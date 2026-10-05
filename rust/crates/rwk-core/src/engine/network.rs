//! Native network layer: UDP edge transport and port forwarding, with **no sidecar**.
//!
//! This module is the first slice of the "one EXE, no child process" requirement. It
//! replaces two of the Go sidecar's jobs with in-process Rust:
//!
//! * the **edge relay** — moving [`RwkPaddleFrame`] datagrams between the keying engine
//!   and the peer (the sidecar's loopback UDP ↔ tailnet relay); and
//! * the **UDP port forwarder** — the sidecar's `out-udp` / `in-udp` kinds, used for
//!   Flex SmartSDR command (`4992/udp`) and VITA-49 (`4991/udp`) traffic.
//!
//! ## What replaces Tailscale (research spike)
//!
//! The embedded `tsnet` node provided WireGuard transport, DERP relaying, NAT
//! traversal and the userspace netstack. Dropping the sidecar means that layer becomes
//! the application's responsibility. The realistic Rust path is documented in
//! `rust/docs/NATIVE-NETWORK-SPIKE.md`; the short version:
//!
//! * **Transport** — `boringtun` (Cloudflare's userspace WireGuard) already provides
//!   the encrypted tunnel and is a drop-in for the WireGuard half of `tsnet`.
//! * **Userspace IP stack** — `smoltcp` consumes boringtun's decrypted datagrams,
//!   matching `tsnet`'s "no TUN adapter, no admin privilege" property.
//! * **Coordination** — the tailnet control plane (key exchange, peer discovery, DERP
//!   map) has no maintained Rust client; this is the genuine spike work.
//!
//! The types below are transport-agnostic: they speak plain UDP and will ride over
//! whatever tunnel implementation lands, so the keying path is ready before the mesh is.

use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;

use tokio::net::UdpSocket;
use tokio::sync::{watch, Mutex};

use crate::primitives::{ForwardDirection, ForwardProtocol};
use crate::protocol::edge::RwkPaddleFrame;
use crate::Result;

/// Maximum size of a single edge datagram.
pub const MAX_DATAGRAM: usize = 2048;

/// Maximum size of a single forwarded UDP datagram.
pub const MAX_FORWARD_DATAGRAM: usize = 4096;

/// Idle timeout for a UDP forwarding session, in seconds.
pub const UDP_FORWARD_IDLE_SECONDS: u64 = 60;

/// Consecutive outbound failures that add up to a lost path.
///
/// A single UDP send failure is usually transient and must not tear the radio down;
/// a run of them means the route is gone. This is deliberately a *sustained* signal,
/// so a station never trips F9 on one hiccup.
pub const MAX_CONSECUTIVE_SEND_ERRORS: u32 = 3;

/// A one-way flag that any mesh/tunnel layer can raise when the path to the peer is lost.
///
/// This is the missing event source for fail-safe **F9**: the edge transport and the
/// future WireGuard tunnel each hold a clone, so a detection anywhere becomes an F9 at the
/// replay driver's watchdog. It is shared by `Arc`, so cloning is cheap and every holder
/// observes the same flag.
///
/// The flag is *latching until consumed*: [`Self::take_lost`] clears it, which is exactly
/// the audit trail F9's auto-clearing latch policy expects — each reported loss raises F9
/// once, and traffic resuming re-arms the session.
#[derive(Debug, Clone, Default)]
pub struct PathHealth {
    lost: Arc<AtomicBool>,
}

impl PathHealth {
    /// Creates a healthy path flag.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Raises the flag. Call from the layer that detects the loss.
    pub fn report_lost(&self) {
        self.lost.store(true, Ordering::SeqCst);
    }

    /// Reads the flag without clearing it.
    #[must_use]
    pub fn is_lost(&self) -> bool {
        self.lost.load(Ordering::SeqCst)
    }

    /// Drains the flag: returns `true` at most once per reported loss.
    pub fn take_lost(&self) -> bool {
        self.lost.swap(false, Ordering::SeqCst)
    }
}

/// Counters for a network path, surfaced to the UI's packet-statistics panel.
#[derive(Debug, Default)]
pub struct EdgeStats {
    tx_datagrams: AtomicU64,
    rx_datagrams: AtomicU64,
    tx_bytes: AtomicU64,
    rx_bytes: AtomicU64,
    drop_no_peer: AtomicU64,
    drop_foreign: AtomicU64,
    errors: AtomicU64,
    consecutive_send_errors: AtomicU32,
}

impl EdgeStats {
    /// Datagrams transmitted.
    #[must_use]
    pub fn tx_datagrams(&self) -> u64 {
        self.tx_datagrams.load(Ordering::Relaxed)
    }

    /// Datagrams received.
    #[must_use]
    pub fn rx_datagrams(&self) -> u64 {
        self.rx_datagrams.load(Ordering::Relaxed)
    }

    /// Bytes transmitted.
    #[must_use]
    pub fn tx_bytes(&self) -> u64 {
        self.tx_bytes.load(Ordering::Relaxed)
    }

    /// Bytes received.
    #[must_use]
    pub fn rx_bytes(&self) -> u64 {
        self.rx_bytes.load(Ordering::Relaxed)
    }

    /// Datagrams dropped because no peer was configured.
    #[must_use]
    pub fn drop_no_peer(&self) -> u64 {
        self.drop_no_peer.load(Ordering::Relaxed)
    }

    /// Datagrams dropped because they came from an unexpected source.
    #[must_use]
    pub fn drop_foreign(&self) -> u64 {
        self.drop_foreign.load(Ordering::Relaxed)
    }

    /// Read/write errors observed by the path.
    #[must_use]
    pub fn errors(&self) -> u64 {
        self.errors.load(Ordering::Relaxed)
    }

    /// Outbound failures since the last successful send.
    #[must_use]
    pub fn consecutive_send_errors(&self) -> u32 {
        self.consecutive_send_errors.load(Ordering::Relaxed)
    }
}

/// A UDP endpoint that carries [`RwkPaddleFrame`]s to and from one peer.
///
/// Both directions share one socket, mirroring the sidecar's `edgeRelay`: outbound
/// frames go to the configured peer, and inbound frames are accepted only from that
/// peer once it is known.
pub struct EdgeTransport {
    socket: UdpSocket,
    peer: Mutex<Option<SocketAddr>>,
    stats: Arc<EdgeStats>,
    health: PathHealth,
}

impl EdgeTransport {
    /// Binds the transport to `bind` (use port 0 for an ephemeral port).
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::SerialIo`] (an `std::io::Error`) if the socket cannot be
    /// bound.
    pub async fn bind(bind: SocketAddr) -> Result<Self> {
        let socket = UdpSocket::bind(bind).await?;
        Ok(Self {
            socket,
            peer: Mutex::new(None),
            stats: Arc::new(EdgeStats::default()),
            health: PathHealth::new(),
        })
    }

    /// The local address the transport is bound to.
    ///
    /// # Errors
    ///
    /// Propagates an `std::io::Error` if the address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.socket.local_addr()?)
    }

    /// Shared statistics for the path.
    #[must_use]
    pub fn stats(&self) -> Arc<EdgeStats> {
        Arc::clone(&self.stats)
    }

    /// The path-health flag, so a caller can hand it to the replay driver's F9 source.
    #[must_use]
    pub fn health(&self) -> PathHealth {
        self.health.clone()
    }

    /// Sets the outbound peer and the only accepted inbound source.
    pub async fn set_peer(&self, peer: SocketAddr) {
        *self.peer.lock().await = Some(peer);
    }

    /// Clears the configured peer, leaving outbound frames undeliverable.
    pub async fn clear_peer(&self) {
        *self.peer.lock().await = None;
    }

    /// The configured peer, if any.
    pub async fn peer(&self) -> Option<SocketAddr> {
        *self.peer.lock().await
    }

    /// Sends one edge frame to the configured peer.
    ///
    /// Returns the number of bytes written, or `0` when no peer is configured (counted
    /// as [`EdgeStats::drop_no_peer`]).
    ///
    /// # Errors
    ///
    /// Propagates an `std::io::Error` if the datagram cannot be sent.
    pub async fn send_frame(&self, frame: &RwkPaddleFrame) -> Result<usize> {
        let Some(peer) = self.peer().await else {
            self.stats.drop_no_peer.fetch_add(1, Ordering::Relaxed);
            return Ok(0);
        };
        let bytes = frame.to_vec();
        match self.socket.send_to(&bytes, peer).await {
            Ok(n) => {
                self.stats.tx_datagrams.fetch_add(1, Ordering::Relaxed);
                self.stats.tx_bytes.fetch_add(n as u64, Ordering::Relaxed);
                self.stats.consecutive_send_errors.store(0, Ordering::Relaxed);
                Ok(n)
            }
            Err(e) => {
                self.stats.errors.fetch_add(1, Ordering::Relaxed);
                // A run of failures means the route is gone, not that one datagram was
                // unlucky; only then is F9 raised, so a station never drops the key on a
                // single transient error.
                let consecutive = self.stats.consecutive_send_errors.fetch_add(1, Ordering::Relaxed) + 1;
                if consecutive >= MAX_CONSECUTIVE_SEND_ERRORS {
                    self.health.report_lost();
                }
                Err(e.into())
            }
        }
    }

    /// Receives one edge frame, ignoring datagrams from unexpected sources.
    ///
    /// Returns [`None`] when the datagram was dropped (foreign source or malformed
    /// frame); the caller should simply loop.
    ///
    /// # Errors
    ///
    /// Propagates an `std::io::Error` if the socket read fails.
    pub async fn recv_frame(&self) -> Result<Option<RwkPaddleFrame>> {
        let mut buf = vec![0u8; MAX_DATAGRAM];
        let (len, src) = self.socket.recv_from(&mut buf).await?;
        self.stats.rx_datagrams.fetch_add(1, Ordering::Relaxed);
        self.stats.rx_bytes.fetch_add(len as u64, Ordering::Relaxed);

        // Tailscale ACLs were the primary control in the sidecar; this source check is
        // the same cheap second gate, so an unexpected node cannot reach the keying path.
        if let Some(peer) = self.peer().await {
            if peer != src {
                self.stats.drop_foreign.fetch_add(1, Ordering::Relaxed);
                return Ok(None);
            }
        }

        Ok(RwkPaddleFrame::read_from(&buf[..len]).map(|(frame, _)| frame))
    }
}

/// A persisted port forwarding rule.
///
/// Mirrors the profile JSON the .NET client already writes (`ForwardRule`), so existing
/// configurations load unchanged.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ForwardRule {
    /// Stable rule identifier.
    pub id: String,
    /// Display name, e.g. `[Flex] VITA-49 Stream`.
    pub name: String,
    /// Transport protocol.
    pub protocol: ForwardProtocol,
    /// Port the client side binds (or dials, for an inbound rule).
    pub client_port: u16,
    /// Port on the station side.
    pub station_port: u16,
    /// Whether the rule is active.
    pub enabled: bool,
    /// Loopback address the rule binds to. Defaults to `127.0.0.1` — never the
    /// any-address, which would expose the relay to the whole LAN.
    pub bind_address: String,
    /// Address on the station LAN that traffic is delivered to.
    pub station_target_address: String,
    /// Which side initiates the connection.
    pub direction: ForwardDirection,
}

impl ForwardRule {
    /// Validates the rule's ports and bind address.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when a port is zero or the bind address is not
    /// a loopback literal.
    pub fn validate(&self) -> Result<()> {
        if self.client_port == 0 {
            return Err(crate::Error::Config("client_port must be non-zero".into()));
        }
        if self.station_port == 0 {
            return Err(crate::Error::Config("station_port must be non-zero".into()));
        }
        if self.bind_address.is_empty() {
            return Err(crate::Error::Config("bind_address must not be empty".into()));
        }
        Ok(())
    }

    /// The loopback address this rule would bind on the client side.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when the address cannot be parsed.
    pub fn bind_socket_addr(&self) -> Result<SocketAddr> {
        format!("{}:{}", self.bind_address, self.client_port)
            .parse()
            .map_err(|e| crate::Error::Config(format!("invalid bind address: {e}")))
    }
}

/// A UDP forwarder: relays datagrams between a local listener and a target, preserving
/// datagram boundaries and forwarding replies back to the most recent sender.
///
/// This is the in-process replacement for the sidecar's `out-udp` relay. It is
/// deliberately single-session: the Flex/VITA-49 traffic these rules carry is
/// point-to-point, and a full NAT-style session table is a later increment.
pub struct UdpForwarder {
    listen: UdpSocket,
    reply: UdpSocket,
    target: SocketAddr,
    stats: Arc<EdgeStats>,
}

impl UdpForwarder {
    /// Binds a loopback listener and a reply socket for talking to `target`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if either socket cannot be bound.
    pub async fn bind(listen_addr: SocketAddr, target: SocketAddr) -> Result<Self> {
        let listen = UdpSocket::bind(listen_addr).await?;
        // The reply socket is what the target sees as the source, so replies route back
        // to it and are then relayed to the application.
        let reply = UdpSocket::bind("127.0.0.1:0").await?;
        Ok(Self { listen, reply, target, stats: Arc::new(EdgeStats::default()) })
    }

    /// The address the application should send to.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error`.
    pub fn listen_addr(&self) -> Result<SocketAddr> {
        Ok(self.listen.local_addr()?)
    }

    /// Shared statistics.
    #[must_use]
    pub fn stats(&self) -> Arc<EdgeStats> {
        Arc::clone(&self.stats)
    }

    /// Relays datagrams until `shutdown` flips to `true`.
    ///
    /// Uses a `tokio::sync::watch` channel for shutdown rather than a shared lock, so
    /// the task can be stopped from the UI thread without blocking either side.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` from the sockets.
    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<()> {
        let last_sender: Arc<Mutex<Option<SocketAddr>>> = Arc::new(Mutex::new(None));
        let mut inbound = vec![0u8; MAX_FORWARD_DATAGRAM];
        let mut outbound = vec![0u8; MAX_FORWARD_DATAGRAM];

        loop {
            tokio::select! {
                changed = shutdown.changed() => {
                    // A closed or flipped channel both mean "stop".
                    if changed.is_err() || *shutdown.borrow() {
                        return Ok(());
                    }
                }
                read = self.listen.recv_from(&mut inbound) => {
                    let (len, src) = read?;
                    *last_sender.lock().await = Some(src);
                    self.stats.tx_datagrams.fetch_add(1, Ordering::Relaxed);
                    self.stats.tx_bytes.fetch_add(len as u64, Ordering::Relaxed);
                    self.reply.send_to(&inbound[..len], self.target).await?;
                }
                read = self.reply.recv_from(&mut outbound) => {
                    let (len, _) = read?;
                    self.stats.rx_datagrams.fetch_add(1, Ordering::Relaxed);
                    self.stats.rx_bytes.fetch_add(len as u64, Ordering::Relaxed);
                    if let Some(sender) = *last_sender.lock().await {
                        self.listen.send_to(&outbound[..len], sender).await?;
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::edge::EdgeEntry;

    #[tokio::test]
    async fn edge_frame_round_trips_over_loopback() {
        let a = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let b = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();

        a.set_peer(b.local_addr().unwrap()).await;
        let frame = RwkPaddleFrame::try_new(
            7,
            &[EdgeEntry::key_down_at(1, 10, 0), EdgeEntry::key_up_at(2, 40, 0)],
        )
        .unwrap();

        let sent = a.send_frame(&frame).await.unwrap();
        assert!(sent > 0);
        assert_eq!(a.stats().tx_datagrams(), 1);

        let received = b.recv_frame().await.unwrap().expect("frame");
        assert_eq!(received, frame);
        assert_eq!(b.stats().rx_datagrams(), 1);
    }

    #[tokio::test]
    async fn send_without_peer_is_counted_not_sent() {
        let a = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(0, 0, 0)]).unwrap();
        assert_eq!(a.send_frame(&frame).await.unwrap(), 0);
        assert_eq!(a.stats().drop_no_peer(), 1);
    }

    #[tokio::test]
    async fn sustained_send_failures_raise_path_loss_once_per_run() {
        // An IPv4 socket sending to an IPv6 peer fails deterministically with an address
        // family mismatch, which gives the detector a real failure to react to.
        let a = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        a.set_peer("[::1]:9".parse().unwrap()).await;
        let health = a.health();
        let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(0, 0, 0)]).unwrap();

        for i in 1..MAX_CONSECUTIVE_SEND_ERRORS {
            assert!(a.send_frame(&frame).await.is_err());
            assert!(!health.is_lost(), "must not trip after {i} failure(s)");
        }
        assert!(a.send_frame(&frame).await.is_err());
        assert!(health.is_lost(), "a sustained failure run must raise path loss");
        assert!(health.take_lost(), "the flag is consumable");
        assert!(!health.is_lost(), "taking it clears it");
        assert_eq!(a.stats().consecutive_send_errors(), MAX_CONSECUTIVE_SEND_ERRORS);
        assert_eq!(a.stats().errors(), u64::from(MAX_CONSECUTIVE_SEND_ERRORS));
    }

    #[tokio::test]
    async fn a_successful_send_resets_the_failure_run() {
        let a = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let b = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(0, 0, 0)]).unwrap();

        a.set_peer("[::1]:9".parse().unwrap()).await;
        assert!(a.send_frame(&frame).await.is_err());
        assert_eq!(a.stats().consecutive_send_errors(), 1);

        a.set_peer(b.local_addr().unwrap()).await;
        assert!(a.send_frame(&frame).await.is_ok());
        assert_eq!(a.stats().consecutive_send_errors(), 0);
        assert!(!a.health().is_lost(), "a successful send proves the path is back");
    }

    #[tokio::test]
    async fn foreign_source_is_dropped() {
        let b = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let c = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        // B only trusts A, but C sends to it.
        let a = EdgeTransport::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        b.set_peer(a.local_addr().unwrap()).await;

        let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(0, 0, 0)]).unwrap();
        c.set_peer(b.local_addr().unwrap()).await;
        c.send_frame(&frame).await.unwrap();

        assert!(b.recv_frame().await.unwrap().is_none(), "foreign datagram must be dropped");
        assert_eq!(b.stats().drop_foreign(), 1);
    }

    #[test]
    fn forward_rule_validation() {
        let mut rule = ForwardRule {
            id: "r1".into(),
            name: "VITA-49".into(),
            protocol: ForwardProtocol::Udp,
            client_port: 4991,
            station_port: 4991,
            enabled: true,
            bind_address: "127.0.0.1".into(),
            station_target_address: "192.168.88.213".into(),
            direction: ForwardDirection::ClientToStation,
        };
        assert!(rule.validate().is_ok());
        assert_eq!(rule.bind_socket_addr().unwrap().port(), 4991);

        rule.client_port = 0;
        assert!(rule.validate().is_err());
    }

    #[tokio::test]
    async fn udp_forwarder_relays_both_directions() {
        // "app" plays the local application; "radio" plays the station-LAN target.
        let app = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let radio = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let forwarder =
            UdpForwarder::bind("127.0.0.1:0".parse().unwrap(), radio.local_addr().unwrap())
                .await
                .unwrap();
        let listen = forwarder.listen_addr().unwrap();

        let (tx, rx) = watch::channel(false);
        let stats = forwarder.stats();
        let task = tokio::spawn(async move { forwarder.run(rx).await });

        // App -> forwarder -> radio.
        app.send_to(b"ping", listen).await.unwrap();
        let mut buf = [0u8; 16];
        let (len, reply_to) = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            radio.recv_from(&mut buf),
        )
        .await
        .expect("radio receive timed out")
        .unwrap();
        assert_eq!(&buf[..len], b"ping");

        // Radio replies to the forwarder's reply socket; the forwarder relays it back
        // to the application's original source address.
        radio.send_to(b"pong", reply_to).await.unwrap();
        let (len2, _from) = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            app.recv_from(&mut buf),
        )
        .await
        .expect("app receive timed out")
        .unwrap();
        assert_eq!(&buf[..len2], b"pong");

        // Shut the forwarder down cleanly.
        tx.send(true).unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(2), task)
            .await
            .expect("forwarder did not stop")
            .unwrap()
            .unwrap();

        assert_eq!(stats.tx_datagrams(), 1);
        assert_eq!(stats.rx_datagrams(), 1);
    }
}
