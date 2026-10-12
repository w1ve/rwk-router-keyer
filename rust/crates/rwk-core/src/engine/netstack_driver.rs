//! The async concurrency seam over [`NetStack`] — one owner task, many socket users.
//!
//! [`NetStack`] is single-owner and `!Sync`: it holds smoltcp's interface and socket set,
//! which must be stepped by exactly one thread. But the port forwarders and the edge
//! transport are independent async tasks that each want a socket on the tunnel at the same
//! time. This module reconciles the two the way userspace WireGuard front ends usually do:
//! a single **driver task** owns the [`NetStack`] and the
//! [`WireGuardTunnel`](crate::engine::tunnel::WireGuardTunnel), and every other task talks to
//! it through a cloneable [`NetStackHandle`] over channels.
//!
//! ```text
//!   forwarder task ─┐                         ┌─ WireGuardTunnel (encrypt/decrypt)
//!   forwarder task ─┤  NetStackHandle (cmds)  │
//!   edge task      ─┘  ──────────────────────▶│  NetDriver (owns NetStack)
//!                      ◀── per-socket data ───┤   inject → poll → drain → send_inner
//!                                             └─ periodic poll + tunnel timer tick
//! ```
//!
//! The driver never blocks on a socket user: it owns the only `&mut NetStack`, services a
//! command, pumps every open socket between smoltcp and that socket's channels, then moves
//! the resulting packets across the tunnel. A user, in turn, never touches smoltcp — it holds
//! a [`UdpConn`] or [`TcpConn`] whose `send`/`recv` are ordinary `async` over an mpsc channel.
//!
//! ## Why a servicing pass after every event
//!
//! smoltcp is edge-triggered through its device: it only makes progress when polled, and it
//! only has data to encrypt right after a poll. So every wake — a decrypted packet arriving,
//! a user command, or the poll timer — runs the same pass: inject anything received, poll,
//! pump sockets, then drain and send. That keeps the one `&mut NetStack` on a single task
//! without a lock, which is the whole point.

use std::collections::HashMap;
use std::net::IpAddr;
use std::time::Duration;

use tokio::sync::{mpsc, oneshot};

use crate::engine::netstack::{NetStack, TcpHandle, UdpHandle};
use crate::engine::tunnel::WireGuardTunnel;
use crate::Result;

/// Upper bound on how long the driver sleeps between servicing passes when neither smoltcp
/// nor a user has anything pending.
///
/// The driver normally wakes exactly when smoltcp says it next needs servicing
/// ([`NetStack::poll_delay`]); this cap is only the idle ceiling, so the tunnel's own
/// keepalive and rekey timers ([`WireGuardTunnel::tick`]) keep running on a quiet link.
const MAX_IDLE: Duration = Duration::from_millis(100);

/// Shortest sleep the driver will take, so a `poll_delay` of zero cannot spin the loop.
const MIN_SLEEP: Duration = Duration::from_micros(200);

/// Bound on a per-socket data channel, in messages.
///
/// Each message is one datagram (UDP) or one read chunk (TCP). A bound rather than an
/// unbounded channel means a stalled reader applies backpressure instead of letting the
/// driver buffer without limit; the forward's own copy loop keeps it drained.
const CHANNEL_DEPTH: usize = 256;

/// A datagram with its peer address, as it crosses a [`UdpConn`].
pub type Datagram = (Vec<u8>, (IpAddr, u16));

/// A command from a [`NetStackHandle`] to the driver.
///
/// Each variant carries a `oneshot` reply so the caller learns the socket's identity (and
/// any bind/connect error) before it starts using it. The data channels created here are how
/// the socket's traffic flows afterwards, without further commands.
enum Command {
    OpenUdp {
        port: u16,
        /// Datagrams the user wants sent, drained by the driver.
        outbound: mpsc::Receiver<Datagram>,
        /// Datagrams received for the user, filled by the driver.
        inbound: mpsc::Sender<Datagram>,
        reply: oneshot::Sender<Result<()>>,
    },
    ConnectTcp {
        remote: (IpAddr, u16),
        local_port: u16,
        outbound: mpsc::Receiver<Vec<u8>>,
        inbound: mpsc::Sender<Vec<u8>>,
        reply: oneshot::Sender<Result<()>>,
    },
    ListenTcp {
        port: u16,
        outbound: mpsc::Receiver<Vec<u8>>,
        inbound: mpsc::Sender<Vec<u8>>,
        reply: oneshot::Sender<Result<()>>,
    },
}

/// A cloneable handle to the driver's [`NetStack`].
///
/// Cloning is cheap (an mpsc sender), so every forwarder can hold one. Dropping the last
/// handle closes the command channel, which the driver treats as a shutdown signal.
#[derive(Clone)]
pub struct NetStackHandle {
    commands: mpsc::Sender<Command>,
}

impl NetStackHandle {
    /// Opens a UDP socket on `port` and returns a connection for sending and receiving.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the port is invalid or already bound, or if the
    /// driver has stopped.
    pub async fn open_udp(&self, port: u16) -> Result<UdpConn> {
        let (out_tx, out_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (in_tx, in_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (reply_tx, reply_rx) = oneshot::channel();
        self.send(Command::OpenUdp {
            port,
            outbound: out_rx,
            inbound: in_tx,
            reply: reply_tx,
        })
        .await?;
        recv_reply(reply_rx).await??;
        Ok(UdpConn { tx: out_tx, rx: in_rx })
    }

    /// Dials a TCP connection to `remote` from `local_port`.
    ///
    /// Resolves once the connection is *initiated*; the stream is usable as soon as the
    /// handshake completes, which the first successful `recv`/`send` reflects.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the connect cannot be started or the driver stopped.
    pub async fn connect_tcp(&self, remote: (IpAddr, u16), local_port: u16) -> Result<TcpConn> {
        let (out_tx, out_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (in_tx, in_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (reply_tx, reply_rx) = oneshot::channel();
        self.send(Command::ConnectTcp {
            remote,
            local_port,
            outbound: out_rx,
            inbound: in_tx,
            reply: reply_tx,
        })
        .await?;
        recv_reply(reply_rx).await??;
        Ok(TcpConn { tx: out_tx, rx: in_rx })
    }

    /// Listens for one TCP connection on `port`.
    ///
    /// The returned connection streams the first peer that connects; this is the single-
    /// session shape the Flex/VITA-49 and control-channel forwards need, not a general accept
    /// loop.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the socket cannot listen or the driver stopped.
    pub async fn listen_tcp(&self, port: u16) -> Result<TcpConn> {
        let (out_tx, out_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (in_tx, in_rx) = mpsc::channel(CHANNEL_DEPTH);
        let (reply_tx, reply_rx) = oneshot::channel();
        self.send(Command::ListenTcp {
            port,
            outbound: out_rx,
            inbound: in_tx,
            reply: reply_tx,
        })
        .await?;
        recv_reply(reply_rx).await??;
        Ok(TcpConn { tx: out_tx, rx: in_rx })
    }

    async fn send(&self, command: Command) -> Result<()> {
        self.commands
            .send(command)
            .await
            .map_err(|_| crate::Error::Config("netstack driver has stopped".into()))
    }
}

/// Awaits a oneshot reply, mapping a dropped sender to a clear driver-stopped error.
async fn recv_reply<T>(rx: oneshot::Receiver<T>) -> Result<T> {
    rx.await
        .map_err(|_| crate::Error::Config("netstack driver dropped the request".into()))
}

/// A UDP socket living on the driver's stack. `send`/`recv` are async over channels.
pub struct UdpConn {
    tx: mpsc::Sender<Datagram>,
    rx: mpsc::Receiver<Datagram>,
}

impl UdpConn {
    /// Queues one datagram to `to` for the driver to send over the tunnel.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the driver has stopped.
    pub async fn send(&self, data: Vec<u8>, to: (IpAddr, u16)) -> Result<()> {
        self.tx
            .send((data, to))
            .await
            .map_err(|_| crate::Error::Config("netstack driver has stopped".into()))
    }

    /// Awaits the next received datagram, or [`None`] once the driver has stopped.
    pub async fn recv(&mut self) -> Option<Datagram> {
        self.rx.recv().await
    }

    /// Splits the socket into an independent sender and receiver.
    ///
    /// The keying edge transport sends and receives concurrently from a single shared
    /// handle; holding one lock across both would let a blocked `recv` stall outbound
    /// key edges. Splitting gives the send half a `&self` API that clones cheaply and a
    /// receive half that owns the `&mut self` channel, so the two never contend.
    #[must_use]
    pub fn split(self) -> (UdpConnSender, UdpConnReceiver) {
        (UdpConnSender { tx: self.tx }, UdpConnReceiver { rx: self.rx })
    }
}

/// The send half of a split [`UdpConn`].
#[derive(Clone)]
pub struct UdpConnSender {
    tx: mpsc::Sender<Datagram>,
}

impl UdpConnSender {
    /// Queues one datagram to `to` for the driver to send. See [`UdpConn::send`].
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the driver has stopped.
    pub async fn send(&self, data: Vec<u8>, to: (IpAddr, u16)) -> Result<()> {
        self.tx
            .send((data, to))
            .await
            .map_err(|_| crate::Error::Config("netstack driver has stopped".into()))
    }
}

/// The receive half of a split [`UdpConn`].
pub struct UdpConnReceiver {
    rx: mpsc::Receiver<Datagram>,
}

impl UdpConnReceiver {
    /// Awaits the next received datagram, or [`None`] once the driver has stopped.
    pub async fn recv(&mut self) -> Option<Datagram> {
        self.rx.recv().await
    }
}

/// A TCP connection living on the driver's stack. `send`/`recv` are async over channels.
///
/// A `recv` of [`None`] means end-of-stream: the peer closed, or the driver stopped.
pub struct TcpConn {
    tx: mpsc::Sender<Vec<u8>>,
    rx: mpsc::Receiver<Vec<u8>>,
}

impl TcpConn {
    /// Queues bytes for the driver to send on the connection.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the driver has stopped.
    pub async fn send(&self, data: Vec<u8>) -> Result<()> {
        self.tx
            .send(data)
            .await
            .map_err(|_| crate::Error::Config("netstack driver has stopped".into()))
    }

    /// Awaits the next chunk of received bytes, or [`None`] at end-of-stream.
    pub async fn recv(&mut self) -> Option<Vec<u8>> {
        self.rx.recv().await
    }

    /// Splits the connection into an independent sender and receiver.
    ///
    /// A relay copies both directions at once, so it needs to send and receive concurrently;
    /// holding the whole `TcpConn` for each would be a borrow conflict. The sender is cheap
    /// to clone, so the two halves can move into separate tasks.
    #[must_use]
    pub fn split(self) -> (TcpSender, TcpReceiver) {
        (TcpSender { tx: self.tx }, TcpReceiver { rx: self.rx })
    }
}

/// The send half of a split [`TcpConn`].
#[derive(Clone)]
pub struct TcpSender {
    tx: mpsc::Sender<Vec<u8>>,
}

impl TcpSender {
    /// Queues bytes for the driver to send. See [`TcpConn::send`].
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the driver has stopped.
    pub async fn send(&self, data: Vec<u8>) -> Result<()> {
        self.tx
            .send(data)
            .await
            .map_err(|_| crate::Error::Config("netstack driver has stopped".into()))
    }
}

/// The receive half of a split [`TcpConn`].
pub struct TcpReceiver {
    rx: mpsc::Receiver<Vec<u8>>,
}

impl TcpReceiver {
    /// Awaits the next chunk of received bytes, or [`None`] at end-of-stream.
    pub async fn recv(&mut self) -> Option<Vec<u8>> {
        self.rx.recv().await
    }
}

/// A UDP socket the driver tracks: its smoltcp handle and the user's data channels.
struct UdpEntry {
    handle: UdpHandle,
    outbound: mpsc::Receiver<Datagram>,
    inbound: mpsc::Sender<Datagram>,
}

/// A TCP socket the driver tracks.
struct TcpEntry {
    handle: TcpHandle,
    outbound: mpsc::Receiver<Vec<u8>>,
    inbound: mpsc::Sender<Vec<u8>>,
    /// Set once the socket has been seen active (its handshake completed). Until then a
    /// not-active socket is still *connecting*, not closed — distinguishing the two is what
    /// stops the driver from tearing a stream down before it is even up.
    established: bool,
    /// Set once smoltcp reports the socket closed, so its FIN is forwarded exactly once by
    /// dropping the inbound sender.
    closed: bool,
}

/// Owns the [`NetStack`] and tunnel, and services every socket on one task.
pub struct NetDriver {
    tunnel: WireGuardTunnel,
    stack: NetStack,
    commands: mpsc::Receiver<Command>,
    udp: HashMap<usize, UdpEntry>,
    tcp: HashMap<usize, TcpEntry>,
    next_id: usize,
}

impl NetDriver {
    /// Spawns the driver on the current tokio runtime, returning a handle to it.
    ///
    /// The driver runs until the last [`NetStackHandle`] is dropped. The tunnel's session
    /// should already be established (via [`WireGuardTunnel::ensure_session`]) before traffic
    /// is expected, though the driver will keep its timers running regardless.
    #[must_use]
    pub fn spawn(tunnel: WireGuardTunnel, stack: NetStack) -> NetStackHandle {
        let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
        let driver = Self {
            tunnel,
            stack,
            commands: rx,
            udp: HashMap::new(),
            tcp: HashMap::new(),
            next_id: 0,
        };
        tokio::spawn(driver.run());
        NetStackHandle { commands: tx }
    }

    /// The driver loop. Returns when all handles are dropped or a fatal tunnel error occurs.
    ///
    /// It wakes on three things: a decrypted packet from the tunnel, a user command, or a
    /// timer. The timer is not fixed — it is whatever smoltcp says it next needs
    /// ([`NetStack::poll_delay`]), capped at [`MAX_IDLE`] so the tunnel's keepalive still
    /// runs on an otherwise silent link. Waking exactly when the stack needs it keeps a
    /// handshake moving even when the host is busy, which a fixed interval does not.
    async fn run(mut self) {
        loop {
            let sleep = self.next_wake();
            tokio::select! {
                // A decrypted inner packet from the peer. `poll` answers handshake/keepalive
                // traffic itself and yields `None` for it, which we simply ignore.
                received = self.tunnel.poll() => {
                    match received {
                        Ok(Some(packet)) => self.stack.inject(packet.bytes().to_vec()),
                        Ok(None) => {}
                        // A socket error on the tunnel is not fatal to the driver: a single
                        // bad read must not tear every forward down. Keep servicing.
                        Err(_) => {}
                    }
                }
                // A new socket request. `None` means every handle was dropped: shut down.
                command = self.commands.recv() => {
                    match command {
                        Some(command) => self.handle_command(command),
                        None => break,
                    }
                }
                // Timer: advance smoltcp's and WireGuard's own timers.
                _ = tokio::time::sleep(sleep) => {
                    let _ = self.tunnel.tick().await;
                }
            }

            // One servicing pass after every wake: advance smoltcp, move bytes between each
            // socket and its channels, then push whatever was framed across the tunnel.
            self.service().await;
        }
    }

    /// How long to sleep before the next timer wake: what smoltcp needs, clamped to a sane
    /// floor and the idle ceiling.
    fn next_wake(&mut self) -> Duration {
        let wanted = self.stack.poll_delay().unwrap_or(MAX_IDLE).min(MAX_IDLE);
        wanted.max(MIN_SLEEP)
    }

    /// Registers a socket command on the stack and records its channels.
    fn handle_command(&mut self, command: Command) {
        match command {
            Command::OpenUdp { port, outbound, inbound, reply } => {
                match self.stack.open_udp(port) {
                    Ok(handle) => {
                        let id = self.alloc_id();
                        self.udp.insert(id, UdpEntry { handle, outbound, inbound });
                        let _ = reply.send(Ok(()));
                    }
                    Err(e) => {
                        let _ = reply.send(Err(e));
                    }
                }
            }
            Command::ConnectTcp { remote, local_port, outbound, inbound, reply } => {
                let handle = self.stack.open_tcp();
                match self.stack.tcp_connect(handle, remote, local_port) {
                    Ok(()) => {
                        let id = self.alloc_id();
                        self.tcp.insert(
                            id,
                            TcpEntry { handle, outbound, inbound, established: false, closed: false },
                        );
                        let _ = reply.send(Ok(()));
                    }
                    Err(e) => {
                        let _ = reply.send(Err(e));
                    }
                }
            }
            Command::ListenTcp { port, outbound, inbound, reply } => {
                let handle = self.stack.open_tcp();
                match self.stack.tcp_listen(handle, port) {
                    Ok(()) => {
                        let id = self.alloc_id();
                        self.tcp.insert(
                            id,
                            TcpEntry { handle, outbound, inbound, established: false, closed: false },
                        );
                        let _ = reply.send(Ok(()));
                    }
                    Err(e) => {
                        let _ = reply.send(Err(e));
                    }
                }
            }
        }
    }

    /// Advances the stack and shuttles bytes both ways, then moves packets over the tunnel.
    async fn service(&mut self) {
        // Poll first so freshly injected packets are parsed before we read sockets, and so a
        // just-queued send is framed before we drain.
        self.stack.poll();
        self.pump_udp();
        self.pump_tcp();
        // smoltcp may have more to emit after the pumps queued sends; poll once more so those
        // segments are framed in this pass rather than waiting for the next wake.
        self.stack.poll();
        self.flush_outbound().await;
    }

    /// Moves datagrams between each UDP socket and its channels.
    fn pump_udp(&mut self) {
        for entry in self.udp.values_mut() {
            // User -> stack: send everything queued, as long as the socket can take it.
            while let Ok((data, to)) = entry.outbound.try_recv() {
                let _ = self.stack.udp_send(entry.handle, to, &data);
            }
            // Stack -> user: drain all received datagrams into the inbound channel.
            while let Some((data, from)) = self.stack.udp_recv(entry.handle) {
                if entry.inbound.try_send((data, from)).is_err() {
                    // The reader is gone or saturated; stop offering until the next pass.
                    break;
                }
            }
        }
    }

    /// Moves bytes between each TCP socket and its channels, and forwards a close.
    fn pump_tcp(&mut self) {
        for entry in self.tcp.values_mut() {
            // User -> stack: write queued bytes while the send buffer has room.
            while let Ok(data) = entry.outbound.try_recv() {
                let mut offset = 0;
                while offset < data.len() {
                    match self.stack.tcp_send(entry.handle, &data[offset..]) {
                        Ok(0) => break, // send buffer full; the rest waits for the next pass.
                        Ok(n) => offset += n,
                        Err(_) => break, // not sendable (closing); drop the remainder.
                    }
                }
            }
            // Stack -> user: hand up whatever can be read.
            loop {
                match self.stack.tcp_recv(entry.handle, 16 * 1024) {
                    Ok(bytes) if !bytes.is_empty() => {
                        if entry.inbound.try_send(bytes).is_err() {
                            break;
                        }
                    }
                    _ => break,
                }
            }
            // Track establishment so a still-connecting socket is never mistaken for a
            // closed one: before the handshake, `is_active()` is false but the stream is not
            // dead. Only a socket that *was* active and is now finished is end-of-stream.
            let socket = self.stack.tcp(entry.handle);
            if socket.is_active() {
                entry.established = true;
            }
            // Forward end-of-stream once: when an established socket is no longer active and
            // cannot receive, drop the inbound sender so the user's `recv` returns `None`.
            if entry.established && !entry.closed && !socket.is_active() && !socket.may_recv() {
                entry.closed = true;
                // Swap in a fresh dropped channel; the user side observes the hang-up.
                let (dead, _) = mpsc::channel(1);
                entry.inbound = dead;
            }
        }
    }

    /// Sends every packet smoltcp framed over the WireGuard tunnel.
    async fn flush_outbound(&mut self) {
        while let Some(packet) = self.stack.drain_outbound() {
            // A send failure (link not up yet, or transient) must not kill the driver; the
            // tunnel counts it and raises F9 on a sustained run. Drop this packet and go on.
            let _ = self.tunnel.send_inner(&packet).await;
        }
    }

    fn alloc_id(&mut self) -> usize {
        let id = self.next_id;
        self.next_id += 1;
        id
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::tunnel::{StaticIdentity, TunnelPeer};
    use std::net::Ipv4Addr;

    fn identity(seed: u8) -> StaticIdentity {
        let mut bytes = [0u8; 32];
        for (i, b) in bytes.iter_mut().enumerate() {
            *b = seed.wrapping_mul(31).wrapping_add(i as u8);
        }
        StaticIdentity::from_private_bytes(bytes)
    }

    fn peer(public_key: [u8; 32], endpoint: std::net::SocketAddr) -> TunnelPeer {
        TunnelPeer { public_key, preshared_key: None, endpoint, persistent_keepalive: None }
    }

    const A_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 1);
    const B_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 2);

    /// Brings up two drivers over a real tunnel and returns their handles. Each driver owns
    /// one end of an established WireGuard link, so sockets opened on the handles ride real
    /// encryption — the end-to-end shape the forwarders will use.
    async fn linked_drivers() -> (NetStackHandle, NetStackHandle) {
        let a_id = identity(41);
        let b_id = identity(42);

        let mut a_tun = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &a_id,
            &peer(b_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();
        let mut b_tun = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &b_id,
            &peer(a_id.public_bytes(), a_tun.local_addr().unwrap()),
        )
        .await
        .unwrap();
        a_tun.set_peer(b_tun.local_addr().unwrap());

        // Hand-drive the handshake before the drivers take ownership of the tunnels.
        a_tun.initiate().await.unwrap();
        b_tun.poll().await.unwrap();
        a_tun.poll().await.unwrap();
        b_tun.poll().await.unwrap();
        assert!(a_tun.is_established() && b_tun.is_established());

        let a = NetDriver::spawn(a_tun, NetStack::new(IpAddr::V4(A_IP)));
        let b = NetDriver::spawn(b_tun, NetStack::new(IpAddr::V4(B_IP)));
        (a, b)
    }

    #[tokio::test]
    async fn a_udp_datagram_crosses_two_drivers_over_the_tunnel() {
        let (a, b) = linked_drivers().await;

        let a_udp = a.open_udp(5000).await.unwrap();
        let mut b_udp = b.open_udp(4991).await.unwrap();

        a_udp
            .send(b"vita-49".to_vec(), (IpAddr::V4(B_IP), 4991))
            .await
            .unwrap();

        let (payload, from) = tokio::time::timeout(Duration::from_secs(5), b_udp.recv())
            .await
            .expect("datagram did not cross the tunnel")
            .expect("channel closed");
        assert_eq!(payload, b"vita-49");
        assert_eq!(from.1, 5000, "the source port is preserved end to end");

        // Reply direction.
        b_udp.send(b"ack".to_vec(), from).await.unwrap();
        let mut a_udp = a_udp;
        let (reply, _) = tokio::time::timeout(Duration::from_secs(5), a_udp.recv())
            .await
            .expect("reply did not cross back")
            .expect("channel closed");
        assert_eq!(reply, b"ack");
    }

    #[tokio::test]
    async fn a_tcp_stream_crosses_two_drivers_over_the_tunnel() {
        let (a, b) = linked_drivers().await;

        // B listens; A dials. The listen must be registered before the connect's SYN arrives,
        // which the handle's await-before-return guarantees.
        let mut server = b.listen_tcp(7300).await.unwrap();
        let client = a.connect_tcp((IpAddr::V4(B_IP), 7300), 50000).await.unwrap();

        client.send(b"CQ TEST".to_vec()).await.unwrap();
        let got = tokio::time::timeout(Duration::from_secs(5), server.recv())
            .await
            .expect("server never received")
            .expect("stream closed");
        assert_eq!(got, b"CQ TEST");

        server.send(b"QSL".to_vec()).await.unwrap();
        let mut client = client;
        let reply = tokio::time::timeout(Duration::from_secs(5), client.recv())
            .await
            .expect("client never received the reply")
            .expect("stream closed");
        assert_eq!(reply, b"QSL");
    }

    #[tokio::test]
    async fn an_invalid_bind_is_reported_through_the_handle() {
        let (a, _b) = linked_drivers().await;
        assert!(a.open_udp(0).await.is_err(), "port 0 must be rejected via the handle");
    }
}
