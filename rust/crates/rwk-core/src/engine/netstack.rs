//! Userspace IP stack — the piece that turns the tunnel's inner IP packets into sockets.
//!
//! [`WireGuardTunnel`](crate::engine::tunnel::WireGuardTunnel) carries *inner IP packets*:
//! `send_inner` takes a raw IP packet and `poll` hands one back. That is a transport, not a
//! socket. The sidecar's `tsnet` closed this gap with gVisor's userspace netstack, which
//! presented ordinary sockets over the WireGuard link with no TUN adapter and no admin
//! rights. This module is the Rust equivalent of that layer, built on
//! [`smoltcp`](https://docs.rs/smoltcp) — a `no_std`-capable TCP/IP stack — so the keying
//! path and the port forwarders can speak UDP and TCP over the tunnel exactly as they do
//! over a plain socket today.
//!
//! ## The seam
//!
//! smoltcp drives itself through a [`smoltcp::phy::Device`]: it pulls received frames from
//! the device and pushes frames it wants transmitted back to it. [`TunnelDevice`] is that
//! device, but its "wire" is the tunnel rather than a NIC:
//!
//! ```text
//!   UDP / TCP sockets  (NetStack: smoltcp Interface + SocketSet)
//!        │  smoltcp polls the device
//!        ▼
//!   TunnelDevice ── rx_queue: inner packets the tunnel decrypted  (inject)
//!                └─ tx_queue: inner packets smoltcp wants sent     (drain)
//!        │
//!        ▼
//!   WireGuardTunnel.send_inner / .poll  (encryption, handshake, keepalives)
//! ```
//!
//! A tunnel task owns both halves: it reads a decrypted packet off the tunnel and
//! [`NetStack::inject`]s it, then [`NetStack::drain_outbound`]s whatever smoltcp produced and
//! hands each packet to `send_inner`. Nothing in this module performs I/O or encryption; it
//! is pure packet-in/packet-out, which keeps it synchronous and trivially testable on
//! loopback without a socket or a second host.
//!
//! ## Medium
//!
//! The device uses [`Medium::Ip`], not Ethernet: WireGuard carries bare IP packets, so there
//! are no MAC addresses, no ARP and no Ethernet framing to model. That matches what
//! `tunnel.rs` hands up ([`TunnelPacket`](crate::engine::tunnel::TunnelPacket) is an IP
//! packet) and keeps the overhead off the keying path.

use std::collections::VecDeque;
use std::net::IpAddr;
use std::time::Duration;

use smoltcp::iface::{Config, Interface, SocketHandle, SocketSet};
use smoltcp::phy::{Checksum, Device, DeviceCapabilities, Medium, RxToken, TxToken};
use smoltcp::socket::{tcp, udp};
use smoltcp::time::Instant as SmolInstant;
use smoltcp::wire::{HardwareAddress, IpAddress, IpCidr, IpEndpoint};

use crate::Result;

/// Largest inner IP packet the stack will frame, matching the tunnel's inner MTU.
///
/// Kept equal to [`tunnel::MAX_INNER_PACKET`](crate::engine::tunnel::MAX_INNER_PACKET) so a
/// packet smoltcp emits always fits a single `send_inner`, and one the tunnel accepts always
/// fits a device buffer. A mismatch here would silently truncate a forwarded TCP segment.
pub const MTU: usize = 1600;

/// Per-socket receive/transmit buffer, in bytes.
///
/// The control channel and Flex/VITA-49 traffic these sockets carry are modest; this is
/// large enough to hold several in-flight segments without starving a `no_std` target of
/// memory. UDP additionally needs a packet-metadata ring, sized below.
const SOCKET_BUFFER: usize = 64 * 1024;

/// How many datagrams a UDP socket may hold queued in each direction.
///
/// smoltcp stores UDP boundaries separately from payload, so this bounds the datagram count
/// independently of [`SOCKET_BUFFER`].
const UDP_PACKET_SLOTS: usize = 32;

/// The in-memory "network interface" smoltcp drives, bridging to the WireGuard tunnel.
///
/// It owns two queues of raw IP packets:
///
/// * `rx_queue` — packets the tunnel decrypted, waiting for smoltcp to parse. Fed by
///   [`push_inbound`](Self::push_inbound).
/// * `tx_queue` — packets smoltcp produced, waiting to be encrypted and sent. Drained by
///   [`pop_outbound`](Self::pop_outbound).
///
/// Both are bounded only by how fast the owning task services them; on the keying path that
/// task runs continuously, so neither grows unbounded in practice. The device is deliberately
/// not `Clone` and performs no locking — it is single-owner, stepped by one task.
pub struct TunnelDevice {
    rx_queue: VecDeque<Vec<u8>>,
    tx_queue: VecDeque<Vec<u8>>,
}

impl Default for TunnelDevice {
    fn default() -> Self {
        Self::new()
    }
}

impl TunnelDevice {
    /// Creates an empty device with no packets queued in either direction.
    #[must_use]
    pub fn new() -> Self {
        Self { rx_queue: VecDeque::new(), tx_queue: VecDeque::new() }
    }

    /// Queues one decrypted inner IP packet for smoltcp to receive on the next poll.
    ///
    /// Call this with the bytes from [`TunnelPacket::bytes`](crate::engine::tunnel::TunnelPacket::bytes).
    pub fn push_inbound(&mut self, packet: Vec<u8>) {
        self.rx_queue.push_back(packet);
    }

    /// Takes the next IP packet smoltcp wants transmitted, if any.
    ///
    /// Hand the returned bytes to [`WireGuardTunnel::send_inner`](crate::engine::tunnel::WireGuardTunnel::send_inner).
    pub fn pop_outbound(&mut self) -> Option<Vec<u8>> {
        self.tx_queue.pop_front()
    }

    /// Number of outbound packets waiting to be sent, for tests and backpressure checks.
    #[must_use]
    pub fn outbound_len(&self) -> usize {
        self.tx_queue.len()
    }

    /// Number of inbound packets waiting to be parsed.
    #[must_use]
    pub fn inbound_len(&self) -> usize {
        self.rx_queue.len()
    }
}

impl Device for TunnelDevice {
    type RxToken<'a> = TunnelRxToken;
    type TxToken<'a> = TunnelTxToken<'a>;

    fn receive(&mut self, _timestamp: SmolInstant) -> Option<(Self::RxToken<'_>, Self::TxToken<'_>)> {
        // smoltcp pairs every receive with a transmit token so a reply can be framed in the
        // same poll without an allocation of its own; we hand back a token that pushes onto
        // the same tx_queue a standalone transmit would.
        let packet = self.rx_queue.pop_front()?;
        Some((TunnelRxToken { packet }, TunnelTxToken { tx_queue: &mut self.tx_queue }))
    }

    fn transmit(&mut self, _timestamp: SmolInstant) -> Option<Self::TxToken<'_>> {
        Some(TunnelTxToken { tx_queue: &mut self.tx_queue })
    }

    fn capabilities(&self) -> DeviceCapabilities {
        let mut caps = DeviceCapabilities::default();
        caps.medium = Medium::Ip;
        caps.max_transmission_unit = MTU;
        // WireGuard delivers an intact IP packet or none at all — a corrupt datagram fails
        // authentication and never reaches us — so the inner checksums are already known
        // good on receive. We still emit correct checksums so the peer's own stack accepts
        // our packets regardless of how it is configured.
        caps.checksum.ipv4 = Checksum::Tx;
        caps.checksum.udp = Checksum::Tx;
        caps.checksum.tcp = Checksum::Tx;
        caps
    }
}

/// A received-packet token: hands the queued bytes to smoltcp's parser once.
pub struct TunnelRxToken {
    packet: Vec<u8>,
}

impl RxToken for TunnelRxToken {
    fn consume<R, F>(self, f: F) -> R
    where
        F: FnOnce(&[u8]) -> R,
    {
        f(&self.packet)
    }
}

/// A transmit token: smoltcp frames one packet into the buffer, which we queue for the tunnel.
pub struct TunnelTxToken<'a> {
    tx_queue: &'a mut VecDeque<Vec<u8>>,
}

impl TxToken for TunnelTxToken<'_> {
    fn consume<R, F>(self, len: usize, f: F) -> R
    where
        F: FnOnce(&mut [u8]) -> R,
    {
        let mut buffer = vec![0u8; len];
        let result = f(&mut buffer);
        // smoltcp has written a complete IP packet into `buffer`; queue it verbatim for the
        // owning task to encrypt. A zero-length frame would be a smoltcp bug, but guarding it
        // costs nothing and keeps a stray empty datagram off the tunnel.
        if len > 0 {
            self.tx_queue.push_back(buffer);
        }
        result
    }
}

/// A userspace TCP/IP stack bound to the tunnel's inner address.
///
/// Owns the smoltcp [`Interface`], its [`SocketSet`] and the [`TunnelDevice`]. The owning
/// task's loop is:
///
/// 1. [`inject`](Self::inject) each inner packet read from the tunnel;
/// 2. [`poll`](Self::poll) to let smoltcp advance every socket;
/// 3. [`drain_outbound`](Self::drain_outbound) and `send_inner` each produced packet.
///
/// All socket operations go through handles returned by [`open_udp`](Self::open_udp) and
/// [`open_tcp`](Self::open_tcp); the accessors borrow the socket out of the set so a caller
/// never holds the whole stack locked.
pub struct NetStack {
    iface: Interface,
    device: TunnelDevice,
    sockets: SocketSet<'static>,
    /// Milliseconds of synthetic time advanced on each poll; smoltcp needs a monotonic clock
    /// but this stack is driven by packet arrival, not wall time, so a simple counter keeps
    /// its timers moving without binding the module to a real clock.
    clock_ms: i64,
}

impl NetStack {
    /// Builds a stack whose interface answers to `address` — the tunnel's inner IP.
    ///
    /// The address is the one the peer sends inner packets *to*; sockets opened on this stack
    /// are reachable at it.
    ///
    /// The interface prefix is deliberately **not** a `/32`: smoltcp consults the interface's
    /// on-link prefix to select a source address and a next hop for an outbound segment, so a
    /// `/32` would leave the peer's inner address off-link with no route — the stack could
    /// neither emit a correctly-sourced SYN to the peer nor route a reply back, and a
    /// connection between the two inner addresses would silently never establish. The two
    /// inner addresses are a fixed point-to-point pair in `10.6.0.0/24` (`fd6b::/64` for v6),
    /// so a `/24` (`/64`) prefix puts the peer on-link with no separate route needed. There is
    /// still exactly one host at each address; the wider prefix only tells smoltcp the two can
    /// reach each other directly.
    #[must_use]
    pub fn new(address: IpAddr) -> Self {
        let mut device = TunnelDevice::new();
        let config = Config::new(HardwareAddress::Ip);
        let mut iface = Interface::new(config, &mut device, SmolInstant::from_millis(0));

        let cidr = match address {
            IpAddr::V4(v4) => IpCidr::new(IpAddress::Ipv4(v4), 24),
            IpAddr::V6(v6) => IpCidr::new(IpAddress::Ipv6(v6), 64),
        };
        iface.update_ip_addrs(|addrs| {
            // The address list is fixed-capacity; the single push cannot overflow it.
            let _ = addrs.push(cidr);
        });

        Self {
            iface,
            device,
            sockets: SocketSet::new(Vec::new()),
            clock_ms: 0,
        }
    }

    /// Hands one decrypted inner IP packet to the stack to be parsed on the next poll.
    pub fn inject(&mut self, packet: Vec<u8>) {
        self.device.push_inbound(packet);
    }

    /// Advances every socket: parses queued inbound packets and frames any replies.
    ///
    /// Returns `true` if smoltcp reported it changed socket state, which is a hint the caller
    /// may now have readable data or sendable room; it is only a hint, so a caller may also
    /// just check the sockets it cares about.
    pub fn poll(&mut self) -> bool {
        // One millisecond per poll is enough resolution for TCP's timers at the latencies
        // this link runs at, and keeps retransmit/delayed-ack logic ticking forward.
        self.clock_ms += 1;
        let now = SmolInstant::from_millis(self.clock_ms);
        matches!(
            self.iface.poll(now, &mut self.device, &mut self.sockets),
            smoltcp::iface::PollResult::SocketStateChanged
        )
    }

    /// How long until the stack next needs a [`poll`](Self::poll), per smoltcp's own timers.
    ///
    /// Returns [`None`] when nothing is pending (the driver can sleep until traffic or a
    /// command wakes it) and a bounded delay when a retransmit, delayed-ACK or close is due.
    /// A driver that waits this long instead of a fixed interval stays responsive under load
    /// without busy-polling — the fixed-interval alternative stalls a handshake when the host
    /// is busy and the tick is late.
    #[must_use]
    pub fn poll_delay(&mut self) -> Option<Duration> {
        let now = SmolInstant::from_millis(self.clock_ms);
        self.iface
            .poll_delay(now, &self.sockets)
            .map(|d| Duration::from_micros(d.total_micros()))
    }

    /// Takes the next IP packet the stack wants to send over the tunnel, if any.
    pub fn drain_outbound(&mut self) -> Option<Vec<u8>> {
        self.device.pop_outbound()
    }

    /// Whether any outbound packet is waiting, so a task can skip an idle `send_inner` pass.
    #[must_use]
    pub fn has_outbound(&self) -> bool {
        self.device.outbound_len() > 0
    }

    /// Opens a UDP socket bound to `port` on the stack's inner address.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the port is already bound or zero.
    pub fn open_udp(&mut self, port: u16) -> Result<UdpHandle> {
        if port == 0 {
            return Err(crate::Error::Config("UDP port must be non-zero".into()));
        }
        let rx = udp::PacketBuffer::new(
            vec![udp::PacketMetadata::EMPTY; UDP_PACKET_SLOTS],
            vec![0u8; SOCKET_BUFFER],
        );
        let tx = udp::PacketBuffer::new(
            vec![udp::PacketMetadata::EMPTY; UDP_PACKET_SLOTS],
            vec![0u8; SOCKET_BUFFER],
        );
        let mut socket = udp::Socket::new(rx, tx);
        socket
            .bind(port)
            .map_err(|e| crate::Error::Config(format!("UDP bind on port {port} failed: {e:?}")))?;
        Ok(UdpHandle(self.sockets.add(socket)))
    }

    /// Opens an unconnected TCP socket, for either [`listen`](Self::tcp_listen) or
    /// [`connect`](Self::tcp_connect).
    #[must_use]
    pub fn open_tcp(&mut self) -> TcpHandle {
        let rx = tcp::SocketBuffer::new(vec![0u8; SOCKET_BUFFER]);
        let tx = tcp::SocketBuffer::new(vec![0u8; SOCKET_BUFFER]);
        TcpHandle(self.sockets.add(tcp::Socket::new(rx, tx)))
    }

    /// Puts a TCP socket into listening state on `port`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the socket cannot listen (already open, or port 0).
    pub fn tcp_listen(&mut self, handle: TcpHandle, port: u16) -> Result<()> {
        let socket = self.sockets.get_mut::<tcp::Socket>(handle.0);
        socket
            .listen(port)
            .map_err(|e| crate::Error::Config(format!("TCP listen on port {port} failed: {e:?}")))
    }

    /// Initiates a TCP connection to `remote`, using `local_port` as the source port.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the connect cannot be started.
    pub fn tcp_connect(
        &mut self,
        handle: TcpHandle,
        remote: (IpAddr, u16),
        local_port: u16,
    ) -> Result<()> {
        let endpoint = ip_endpoint(remote.0, remote.1);
        let cx = self.iface.context();
        let socket = self.sockets.get_mut::<tcp::Socket>(handle.0);
        socket
            .connect(cx, endpoint, local_port)
            .map_err(|e| crate::Error::Config(format!("TCP connect failed: {e:?}")))
    }

    /// A mutable borrow of a UDP socket, for send/receive from the caller.
    #[must_use]
    pub fn udp(&mut self, handle: UdpHandle) -> &mut udp::Socket<'static> {
        self.sockets.get_mut::<udp::Socket>(handle.0)
    }

    /// Sends one UDP datagram from `handle` to a std-typed destination.
    ///
    /// A thin wrapper over the smoltcp socket so a caller need not build an [`IpEndpoint`],
    /// keeping smoltcp an internal detail of this crate.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the socket's send buffer is full or unbound.
    pub fn udp_send(&mut self, handle: UdpHandle, to: (IpAddr, u16), data: &[u8]) -> Result<()> {
        let endpoint = ip_endpoint(to.0, to.1);
        self.udp(handle)
            .send_slice(data, endpoint)
            .map_err(|e| crate::Error::Config(format!("UDP send failed: {e:?}")))
    }

    /// Receives one UDP datagram from `handle`, returning the payload and the sender's
    /// std-typed address, or [`None`] if nothing is queued.
    pub fn udp_recv(&mut self, handle: UdpHandle) -> Option<(Vec<u8>, (IpAddr, u16))> {
        let (payload, meta) = self.udp(handle).recv().ok()?;
        let payload = payload.to_vec();
        let addr = ip_addr(meta.endpoint.addr);
        Some((payload, (addr, meta.endpoint.port)))
    }

    /// A mutable borrow of a TCP socket, for send/receive/close from the caller.
    #[must_use]
    pub fn tcp(&mut self, handle: TcpHandle) -> &mut tcp::Socket<'static> {
        self.sockets.get_mut::<tcp::Socket>(handle.0)
    }

    /// Queues bytes on a TCP socket's send buffer, returning how many were accepted.
    ///
    /// Like a non-blocking `write`: fewer bytes than offered means the send buffer is full,
    /// and the caller should retry the remainder after the next [`poll`](Self::poll).
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the socket is not in a sendable state.
    pub fn tcp_send(&mut self, handle: TcpHandle, data: &[u8]) -> Result<usize> {
        self.tcp(handle)
            .send_slice(data)
            .map_err(|e| crate::Error::Config(format!("TCP send failed: {e:?}")))
    }

    /// Reads up to `max` bytes from a TCP socket, returning what was available (possibly
    /// empty when the socket is open but idle).
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the socket is not in a receivable state.
    pub fn tcp_recv(&mut self, handle: TcpHandle, max: usize) -> Result<Vec<u8>> {
        self.tcp(handle)
            .recv(|buf| {
                let n = buf.len().min(max);
                (n, buf[..n].to_vec())
            })
            .map_err(|e| crate::Error::Config(format!("TCP recv failed: {e:?}")))
    }
}

/// Builds a smoltcp [`IpEndpoint`] from a std address and port.
fn ip_endpoint(addr: IpAddr, port: u16) -> IpEndpoint {
    IpEndpoint::new(ip_address(addr), port)
}

/// Converts a std [`IpAddr`] to a smoltcp [`IpAddress`].
fn ip_address(addr: IpAddr) -> IpAddress {
    // smoltcp re-exports `std::net` address types, so these arms are already the right type;
    // no conversion is needed (older smoltcp wrapped them, which is why `.into()` was here).
    match addr {
        IpAddr::V4(v4) => IpAddress::Ipv4(v4),
        IpAddr::V6(v6) => IpAddress::Ipv6(v6),
    }
}

/// Converts a smoltcp [`IpAddress`] back to a std [`IpAddr`].
fn ip_addr(addr: IpAddress) -> IpAddr {
    match addr {
        IpAddress::Ipv4(v4) => IpAddr::V4(v4),
        IpAddress::Ipv6(v6) => IpAddr::V6(v6),
    }
}

/// Handle to a UDP socket inside a [`NetStack`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UdpHandle(SocketHandle);

/// Handle to a TCP socket inside a [`NetStack`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TcpHandle(SocketHandle);

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::Ipv4Addr;

    /// Shuttles every packet one stack wants to send into the other stack, both ways, then
    /// polls both. One round of this is one "network tick" between two directly-connected
    /// peers — the loopback stand-in for the tunnel carrying packets between Client and
    /// Station.
    fn exchange(a: &mut NetStack, b: &mut NetStack) {
        for _ in 0..8 {
            a.poll();
            b.poll();
            while let Some(packet) = a.drain_outbound() {
                b.inject(packet);
            }
            while let Some(packet) = b.drain_outbound() {
                a.inject(packet);
            }
            a.poll();
            b.poll();
        }
    }

    const A_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 1);
    const B_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 2);

    #[test]
    fn a_transmit_token_queues_a_framed_packet() {
        let mut device = TunnelDevice::new();
        let token = device.transmit(SmolInstant::from_millis(0)).unwrap();
        token.consume(4, |buf| {
            buf.copy_from_slice(&[1, 2, 3, 4]);
        });
        assert_eq!(device.pop_outbound(), Some(vec![1, 2, 3, 4]));
        assert_eq!(device.pop_outbound(), None);
    }

    #[test]
    fn an_injected_packet_is_offered_to_the_receive_path() {
        let mut device = TunnelDevice::new();
        device.push_inbound(vec![9, 9, 9]);
        assert_eq!(device.inbound_len(), 1);
        let (rx, _tx) = device.receive(SmolInstant::from_millis(0)).unwrap();
        let seen = rx.consume(|bytes| bytes.to_vec());
        assert_eq!(seen, vec![9, 9, 9]);
        // The queue is drained once consumed, so the same packet is not parsed twice.
        assert!(device.receive(SmolInstant::from_millis(0)).is_none());
    }

    #[test]
    fn udp_bind_rejects_port_zero() {
        let mut stack = NetStack::new(IpAddr::V4(A_IP));
        assert!(stack.open_udp(0).is_err());
    }

    #[test]
    fn udp_datagram_crosses_between_two_stacks_over_the_device_seam() {
        let mut a = NetStack::new(IpAddr::V4(A_IP));
        let mut b = NetStack::new(IpAddr::V4(B_IP));

        let a_sock = a.open_udp(4000).unwrap();
        let b_sock = b.open_udp(4991).unwrap();

        // A sends a VITA-49-sized token payload to B:4991, as the Flex forward would.
        a.udp(a_sock)
            .send_slice(b"vita-49", ip_endpoint(IpAddr::V4(B_IP), 4991))
            .unwrap();

        exchange(&mut a, &mut b);

        let (payload, meta) = b.udp(b_sock).recv().expect("B must receive the datagram");
        assert_eq!(payload, b"vita-49");
        assert_eq!(meta.endpoint.port, 4000, "the reply address is A's source port");

        // And the reply direction: B answers, A receives.
        b.udp(b_sock).send_slice(b"ack", meta.endpoint).unwrap();
        exchange(&mut a, &mut b);
        let (reply, _) = a.udp(a_sock).recv().expect("A must receive the reply");
        assert_eq!(reply, b"ack");
    }

    #[test]
    fn tcp_connection_establishes_and_streams_both_directions() {
        let mut client = NetStack::new(IpAddr::V4(A_IP));
        let mut server = NetStack::new(IpAddr::V4(B_IP));

        let listener = server.open_tcp();
        server.tcp_listen(listener, 7300).unwrap();

        let dialer = client.open_tcp();
        client
            .tcp_connect(dialer, (IpAddr::V4(B_IP), 7300), 50000)
            .unwrap();

        // Drive the three-way handshake.
        exchange(&mut client, &mut server);
        assert!(client.tcp(dialer).is_active(), "client side must be connected");
        assert!(server.tcp(listener).is_active(), "server side must have accepted");

        // Client -> server, the control-channel direction.
        assert!(client.tcp(dialer).can_send());
        client.tcp(dialer).send_slice(b"CQ TEST").unwrap();
        exchange(&mut client, &mut server);
        let got = server
            .tcp(listener)
            .recv(|buf| (buf.len(), buf.to_vec()))
            .unwrap();
        assert_eq!(got, b"CQ TEST");

        // Server -> client, the reply direction.
        server.tcp(listener).send_slice(b"QSL").unwrap();
        exchange(&mut client, &mut server);
        let reply = client
            .tcp(dialer)
            .recv(|buf| (buf.len(), buf.to_vec()))
            .unwrap();
        assert_eq!(reply, b"QSL");
    }

    #[test]
    fn a_tcp_half_close_is_propagated_without_cutting_the_reply() {
        let mut client = NetStack::new(IpAddr::V4(A_IP));
        let mut server = NetStack::new(IpAddr::V4(B_IP));

        let listener = server.open_tcp();
        server.tcp_listen(listener, 7301).unwrap();
        let dialer = client.open_tcp();
        client
            .tcp_connect(dialer, (IpAddr::V4(B_IP), 7301), 50001)
            .unwrap();
        exchange(&mut client, &mut server);

        // Client sends, then closes its write side (a half-close), as a forwarded TCP leg
        // does when the application finishes sending but still wants the reply.
        client.tcp(dialer).send_slice(b"bye").unwrap();
        client.tcp(dialer).close();
        exchange(&mut client, &mut server);

        let got = server
            .tcp(listener)
            .recv(|buf| (buf.len(), buf.to_vec()))
            .unwrap();
        assert_eq!(got, b"bye");

        // The server can still send its reply after seeing the client's FIN.
        assert!(server.tcp(listener).can_send(), "reply direction must stay open");
        server.tcp(listener).send_slice(b"73").unwrap();
        exchange(&mut client, &mut server);
        let reply = client
            .tcp(dialer)
            .recv(|buf| (buf.len(), buf.to_vec()))
            .unwrap();
        assert_eq!(reply, b"73");
    }

    #[test]
    fn the_mtu_matches_the_tunnel_inner_mtu() {
        // A silent mismatch here would truncate a forwarded segment; assert the two agree.
        assert_eq!(MTU, crate::engine::tunnel::MAX_INNER_PACKET);
    }

    /// The real thing: a UDP datagram socket→smoltcp→**WireGuard encrypt**→wire→decrypt→
    /// smoltcp→socket. Where the loopback tests shuttle plaintext IP packets between two
    /// stacks directly, this drives both stacks over two live [`WireGuardTunnel`]s so the
    /// netstack is proven to ride the encrypted transport it will ship on — the whole point
    /// of this increment. Two tunnels and two stacks live in one process, so the handshake
    /// never crosses a real network, exactly as the tunnel's own tests note.
    #[tokio::test]
    async fn a_udp_datagram_rides_a_real_wireguard_tunnel_end_to_end() {
        use crate::engine::tunnel::{StaticIdentity, TunnelPeer, WireGuardTunnel};

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

        let a_id = identity(21);
        let b_id = identity(22);

        // Bring up the encrypted link, mirroring the tunnel module's own handshake test.
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

        a_tun.initiate().await.unwrap();
        b_tun.poll().await.unwrap();
        a_tun.poll().await.unwrap();
        b_tun.poll().await.unwrap();
        assert!(a_tun.is_established() && b_tun.is_established(), "the link must come up");

        // One stack per end, each addressed at its inner IP.
        let mut a = NetStack::new(IpAddr::V4(A_IP));
        let mut b = NetStack::new(IpAddr::V4(B_IP));
        let a_sock = a.open_udp(5000).unwrap();
        let b_sock = b.open_udp(4991).unwrap();

        // A's application sends; the datagram now has to survive the full encrypted path.
        a.udp(a_sock)
            .send_slice(b"vita-49", ip_endpoint(IpAddr::V4(B_IP), 4991))
            .unwrap();

        // Carry inner packets A→B over the real tunnel until B's socket has the datagram, or
        // we give up. Each pass: let A frame packets, encrypt+send them, let B decrypt+inject.
        let mut delivered = None;
        for _ in 0..16 {
            a.poll();
            while let Some(packet) = a.drain_outbound() {
                a_tun.send_inner(&packet).await.unwrap();
            }
            // Drain whatever B's tunnel decrypted into B's stack. The outer `Ok` is the
            // timeout not firing; the inner `Ok(Some(..))` is a decrypted inner packet.
            while let Ok(Ok(Some(inner))) =
                tokio::time::timeout(std::time::Duration::from_millis(50), b_tun.poll()).await
            {
                b.inject(inner.bytes().to_vec());
            }
            b.poll();
            if let Some((payload, meta)) = b.udp(b_sock).recv().ok().map(|(p, m)| (p.to_vec(), m)) {
                delivered = Some((payload, meta));
                break;
            }
        }

        let (payload, meta) = delivered.expect("the datagram must survive the encrypted path");
        assert_eq!(payload, b"vita-49");
        assert_eq!(meta.endpoint.port, 5000, "A's source port is preserved end to end");
        assert_eq!(b_tun.stats().decapsulation_errors(), 0, "no datagram failed to decrypt");
        assert!(a_tun.stats().sent_packets() >= 1, "the tunnel carried at least one inner packet");
    }
}
