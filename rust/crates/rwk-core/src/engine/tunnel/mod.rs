//! Native WireGuard transport — the encrypted half of what the `tsnet` sidecar provided.
//!
//! The sidecar embedded Tailscale's `tsnet`, which bundled four separate capabilities. This
//! module replaces the first two; the other two are separate increments:
//!
//! | `tsnet` capability | Here | Status |
//! |---|---|---|
//! | WireGuard encryption, handshake, keepalives, roaming | [`WireGuardTunnel`] | this module |
//! | Userspace IP stack (no TUN adapter, no admin rights) | — | next increment |
//! | Coordination: key exchange, peer discovery, ACLs | [`TunnelPeer`] static peers | by design |
//! | NAT traversal and DERP relay fallback | — | later increment |
//!
//! ## Why static peers
//!
//! Tailscale's coordination server hands each node its peers' keys, addresses and ACLs. A
//! Rust node cannot join a tailnet: tailnet peers only accept keys the coordination server
//! issued, so there is no "bring your own WireGuard key" path. Replacing `tsnet` therefore
//! means running an independent WireGuard mesh, and RWK's topology makes that cheap — one
//! Client talks to one Station, whose address the operator already types into
//! `config.json`. Two fixed endpoints need no discovery and no ACL service, so the keys and
//! endpoint are configured directly ([`TunnelPeer`]).
//!
//! ## What is deliberately not here
//!
//! [`WireGuardPeer`](Self) carries *IP packets*, exactly as boringtun emits them. Turning
//! those into the UDP and TCP sockets the keying path and the port forwarders want is the
//! userspace-netstack increment; until it lands this type is the transport under test, not
//! yet a socket provider.
//!
//! See `rust/docs/NATIVE-NETWORK-SPIKE.md` for the overall plan.

pub mod connect;
pub mod wire;

pub use connect::{
    connect, connect_paired, lan_await_initiator, lan_bind, lan_connect_to, Connected, Path,
};

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use boringtun::noise::{Tunn, TunnResult};
use boringtun::x25519::{PublicKey, StaticSecret};
use tokio::net::UdpSocket;

use crate::engine::relay::RelayClient;
use crate::engine::network::{PathHealth, MAX_CONSECUTIVE_SEND_ERRORS};
use wire::Wire;
use crate::Result;

/// Buffer size for a WireGuard datagram on the wire.
///
/// WireGuard adds a 16-byte header and a 16-byte authentication tag to every transport
/// message, so this is the inner MTU plus that overhead and headroom.
pub const MAX_WIREGUARD_DATAGRAM: usize = 2048;

/// Largest inner IP packet this tunnel will carry.
///
/// A standard 1500-byte Ethernet MTU minus the tunnel's own overhead is the practical
/// figure; the extra room keeps a jumbo-ish control message from being refused outright.
pub const MAX_INNER_PACKET: usize = 1600;

/// How long an initiate-and-wait handshake may take before it is reported as failed.
///
/// WireGuard's own retry schedule re-initiates after 5 seconds, so exceeding this means the
/// peer is not answering at all rather than that one datagram was lost.
pub const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);

/// A WireGuard pre-shared key: a symmetric secret mixed into the handshake for
/// post-quantum defence in depth.
pub type PresharedKey = [u8; 32];

/// This node's static WireGuard identity.
///
/// Holds only the private key; the public key is derived on demand, so there is exactly one
/// representation of the secret in memory.
#[derive(Clone)]
pub struct StaticIdentity {
    private: [u8; 32],
}

impl StaticIdentity {
    /// Creates an identity from the 32 raw private-key bytes an operator would paste in.
    ///
    /// Any 32 bytes are a valid X25519 private key: the clamping that makes a key well
    /// formed is applied when the key is used, not when it is stored.
    #[must_use]
    pub fn from_private_bytes(private: [u8; 32]) -> Self {
        Self { private }
    }

    /// Generates a fresh identity from the operating system's CSPRNG.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the OS random source is unavailable, since a
    /// predictable WireGuard key would silently defeat the tunnel.
    pub fn generate() -> Result<Self> {
        let mut private = [0u8; 32];
        getrandom::fill(&mut private).map_err(|e| {
            crate::Error::Config(format!("no OS randomness available for a WireGuard key: {e}"))
        })?;
        Ok(Self { private })
    }

    /// The raw private-key bytes, for persisting the identity in `config.json`.
    #[must_use]
    pub fn private_bytes(&self) -> [u8; 32] {
        self.private
    }

    /// The public key peers must be configured with.
    #[must_use]
    pub fn public_bytes(&self) -> [u8; 32] {
        let secret = StaticSecret::from(self.private);
        PublicKey::from(&secret).to_bytes()
    }
}

impl std::fmt::Debug for StaticIdentity {
    /// Prints the public key only: a private key must never reach a log or a panic message.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaticIdentity")
            .field("public", &hex(&self.public_bytes()))
            .finish_non_exhaustive()
    }
}

/// The single remote peer this tunnel speaks to.
///
/// This is the whole of the "coordination" layer for RWK's one-client-one-station topology:
/// the operator supplies the peer's public key and endpoint, and nothing is discovered.
#[derive(Clone)]
pub struct TunnelPeer {
    /// The peer's static public key.
    pub public_key: [u8; 32],
    /// Optional pre-shared key, which both ends must configure identically.
    pub preshared_key: Option<PresharedKey>,
    /// Where to send WireGuard datagrams. May be updated for a roaming peer.
    pub endpoint: SocketAddr,
    /// Keepalive period in seconds, or `None` to send only on demand.
    pub persistent_keepalive: Option<u16>,
}

impl std::fmt::Debug for TunnelPeer {
    /// Redacts the pre-shared key; the public key and endpoint are not secret.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TunnelPeer")
            .field("public_key", &hex(&self.public_key))
            .field("preshared_key", &self.preshared_key.map(|_| "<set>"))
            .field("endpoint", &self.endpoint)
            .field("persistent_keepalive", &self.persistent_keepalive)
            .finish()
    }
}

/// One inner packet handed up from the tunnel, with the address it came from.
///
/// The address is the inner packet's source, so a caller can attribute the packet without
/// re-parsing the IP header.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TunnelPacket {
    /// An inner IPv4 packet and its source address.
    V4(Vec<u8>, Ipv4Addr),
    /// An inner IPv6 packet and its source address.
    V6(Vec<u8>, Ipv6Addr),
}

impl TunnelPacket {
    /// The inner packet bytes, without the source address.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        match self {
            Self::V4(bytes, _) | Self::V6(bytes, _) => bytes,
        }
    }

    /// The inner packet's source address.
    #[must_use]
    pub fn source(&self) -> IpAddr {
        match self {
            Self::V4(_, addr) => IpAddr::V4(*addr),
            Self::V6(_, addr) => IpAddr::V6(*addr),
        }
    }
}

/// Counters for the tunnel, surfaced to the status panel and consulted for fail-safe F9.
#[derive(Debug, Default)]
pub struct TunnelStats {
    sent_packets: AtomicU64,
    received_packets: AtomicU64,
    received_datagrams: AtomicU64,
    handshakes: AtomicU64,
    send_errors: AtomicU64,
    receive_errors: AtomicU64,
    decapsulation_errors: AtomicU64,
    consecutive_send_errors: AtomicU32,
}

impl TunnelStats {
    /// Inner packets handed to the tunnel for encryption.
    #[must_use]
    pub fn sent_packets(&self) -> u64 {
        self.sent_packets.load(Ordering::Relaxed)
    }

    /// Inner packets decrypted out of the tunnel.
    #[must_use]
    pub fn received_packets(&self) -> u64 {
        self.received_packets.load(Ordering::Relaxed)
    }

    /// WireGuard datagrams received, including handshake and keepalive traffic.
    #[must_use]
    pub fn received_datagrams(&self) -> u64 {
        self.received_datagrams.load(Ordering::Relaxed)
    }

    /// Times this tunnel became established since it was created.
    ///
    /// Counted on the transition itself, so it is accurate whether a tunnel task or a caller
    /// hand-stepping the exchange drove the handshake.
    #[must_use]
    pub fn handshakes(&self) -> u64 {
        self.handshakes.load(Ordering::Relaxed)
    }

    /// Datagrams that could not be put on the wire.
    #[must_use]
    pub fn send_errors(&self) -> u64 {
        self.send_errors.load(Ordering::Relaxed)
    }

    /// Datagrams that could not be read from the socket.
    #[must_use]
    pub fn receive_errors(&self) -> u64 {
        self.receive_errors.load(Ordering::Relaxed)
    }

    /// Datagrams the peer could not authenticate or decrypt — a wrong key, or an attacker.
    #[must_use]
    pub fn decapsulation_errors(&self) -> u64 {
        self.decapsulation_errors.load(Ordering::Relaxed)
    }

    /// Outbound failures since the last successful send.
    #[must_use]
    pub fn consecutive_send_errors(&self) -> u32 {
        self.consecutive_send_errors.load(Ordering::Relaxed)
    }
}

/// What one pass through the tunnel produced.
enum Step {
    /// The datagram yielded nothing further to do.
    Idle,
    /// A protocol datagram to put on the wire (handshake or keepalive).
    Send(Vec<u8>),
    /// An inner packet for the tunnel interface.
    Inner(TunnelPacket),
    /// The peer's datagram could not be authenticated or decrypted.
    Rejected,
}

/// One end of a point-to-point WireGuard link, driven over a UDP socket or the relay.
///
/// The type owns both the crypto state ([`Tunn`]) and the underlying transport ([`Wire`]), so
/// a caller never has to reason about which datagrams are protocol traffic and which carry
/// payload: [`Self::poll`] and [`Self::recv_inner`] answer protocol traffic on your behalf and
/// return only inner packets. The same crypto drives either transport — [`Self::bind`] for a
/// direct UDP path, [`Self::bind_relay`] for the NAT-traversal fallback.
pub struct WireGuardTunnel {
    tunn: Tunn,
    wire: Wire,
    health: PathHealth,
    stats: Arc<TunnelStats>,
}

impl WireGuardTunnel {
    /// Binds a local UDP socket and prepares a tunnel to `peer`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if `local_addr` cannot be bound (use port 0 for an
    /// ephemeral port).
    pub async fn bind(
        local_addr: SocketAddr,
        identity: &StaticIdentity,
        peer: &TunnelPeer,
    ) -> Result<Self> {
        let socket = UdpSocket::bind(local_addr).await?;
        Ok(Self::with_wire(
            identity,
            peer,
            Wire::Direct { socket, peer: peer.endpoint },
        ))
    }

    /// Builds a direct tunnel over an **already-bound** UDP socket.
    ///
    /// This is the hole-punch path: a caller binds the socket first, learns its local port,
    /// reports that port to the relay so the peer knows where to punch, and only then — once
    /// the peer's key and address are known — wraps the same socket in a tunnel. Binding the
    /// socket up front is what lets the reported port and the socket's NAT mapping be the same.
    #[must_use]
    pub fn from_socket(socket: UdpSocket, identity: &StaticIdentity, peer: &TunnelPeer) -> Self {
        Self::with_wire(identity, peer, Wire::Direct { socket, peer: peer.endpoint })
    }

    /// Prepares a tunnel that reaches its peer through an already-connected relay.
    ///
    /// Use this when a direct path cannot be opened: the `client` is a
    /// [`RelayClient`](crate::engine::relay::RelayClient) already connected to the relay,
    /// registered under this node's key and scoped to the peer's key. The crypto is identical
    /// to [`Self::bind`] — only the datagrams' route differs.
    #[must_use]
    pub fn bind_relay(identity: &StaticIdentity, peer: &TunnelPeer, client: RelayClient) -> Self {
        Self::with_wire(identity, peer, Wire::Relay(client))
    }

    /// Shared constructor: builds the boringtun state and wraps the chosen transport.
    fn with_wire(identity: &StaticIdentity, peer: &TunnelPeer, wire: Wire) -> Self {
        // WireGuard has no meaningful notion of a "well-known" session index; it only has to
        // be distinct per peer in this process. Deriving it from our public key makes it
        // stable across restarts and different for two identities on one host.
        let public = identity.public_bytes();
        let index = u32::from_le_bytes([public[0], public[1], public[2], public[3]]);

        let tunn = Tunn::new(
            StaticSecret::from(identity.private_bytes()),
            PublicKey::from(peer.public_key),
            peer.preshared_key,
            peer.persistent_keepalive,
            index,
            None,
        );

        Self { tunn, wire, health: PathHealth::new(), stats: Arc::new(TunnelStats::default()) }
    }

    /// The local address the tunnel's socket is bound to, for a direct path.
    ///
    /// Returns a relayed tunnel's error-free [`None`] via the inner `Ok(None)`; most callers
    /// use this only on the direct path where they expect `Some`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if a direct socket's address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        // Preserve the original contract for the direct path (the only caller today); a
        // relayed tunnel has no peer-facing socket, so report that as an I/O error rather
        // than inventing an address.
        self.wire.local_addr()?.ok_or_else(|| {
            std::io::Error::new(
                std::io::ErrorKind::AddrNotAvailable,
                "a relayed tunnel has no local peer socket",
            )
            .into()
        })
    }

    /// Shared counters.
    #[must_use]
    pub fn stats(&self) -> Arc<TunnelStats> {
        Arc::clone(&self.stats)
    }

    /// The path-health flag, so a caller can hand it to the replay driver's F9 source.
    #[must_use]
    pub fn health(&self) -> PathHealth {
        self.health.clone()
    }

    /// The current peer endpoint on a direct path, or [`None`] for a relayed tunnel.
    #[must_use]
    pub fn peer(&self) -> Option<SocketAddr> {
        self.wire.peer()
    }

    /// Moves the peer endpoint, as a roaming peer would when its address changes. A no-op for
    /// a relayed tunnel, which addresses by key and has no endpoint to roam.
    pub fn set_peer(&mut self, endpoint: SocketAddr) {
        self.wire.set_peer(endpoint);
    }

    /// Whether a session is current, so transport packets can flow.
    ///
    /// The two roles reach this state at different moments, as WireGuard's state machine
    /// defines: the *initiator* is established the instant it processes the peer's response,
    /// while the *responder* only once it has accepted a transport packet from the initiator
    /// (boringtun sends that confirming keepalive immediately after the response). So a
    /// responder has to run [`Self::poll`] once more after answering before this turns
    /// `true` — reading status too early on a station would otherwise report an idle link.
    #[must_use]
    pub fn is_established(&self) -> bool {
        self.tunn.time_since_last_handshake().is_some()
    }

    /// How long ago the last handshake completed.
    #[must_use]
    pub fn time_since_last_handshake(&self) -> Option<Duration> {
        self.tunn.time_since_last_handshake()
    }

    /// Sends a handshake initiation to the peer.
    ///
    /// Completing the handshake is not this call's job: the peer's reply arrives on the
    /// socket, so [`Self::ensure_session`] or [`Self::poll`] must run for the link to come up.
    ///
    /// Forces a fresh initiation even if one is already in flight, matching what the name
    /// promises — boringtun otherwise reports "nothing to send" for a handshake in progress,
    /// which would make a caller's re-invite silently do nothing.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the handshake message cannot be built, or
    /// propagates an `std::io::Error` if it cannot be sent.
    pub async fn initiate(&mut self) -> Result<()> {
        let mut dst = vec![0u8; MAX_WIREGUARD_DATAGRAM];
        let initiation = match self.tunn.format_handshake_initiation(&mut dst, true) {
            TunnResult::WriteToNetwork(bytes) => bytes.to_vec(),
            TunnResult::Err(e) => {
                return Err(crate::Error::Config(format!(
                    "WireGuard handshake initiation failed: {e:?}"
                )))
            }
            _ => {
                return Err(crate::Error::Config(
                    "WireGuard handshake initiation produced no datagram".into(),
                ))
            }
        };
        self.send_on_wire(&initiation).await
    }

    /// Brings the link up if it is not already: initiates, then answers protocol traffic
    /// until the handshake completes or [`HANDSHAKE_TIMEOUT`] elapses.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] on timeout or a failed initiation, and propagates
    /// `std::io::Error` from the socket.
    pub async fn ensure_session(&mut self) -> Result<()> {
        self.ensure_session_within(HANDSHAKE_TIMEOUT).await
    }

    /// Like [`Self::ensure_session`] but with a caller-chosen overall timeout.
    ///
    /// The relay-fallback path uses a longer window than the direct path: both ends leave
    /// their hole-punch phase at nearly the same moment, so the initiator must keep trying
    /// long enough for the responder's relay registration to settle, rather than giving up on
    /// the short direct-path budget.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] on timeout or a failed initiation, and propagates
    /// `std::io::Error` from the socket.
    pub async fn ensure_session_within(&mut self, timeout: Duration) -> Result<()> {
        if self.is_established() {
            return Ok(());
        }

        // Re-initiate on an interval rather than once: on the paired/relay path the two ends
        // leave their hole-punch phase at nearly the same instant, so the responder's relay
        // registration can land a beat after the initiator's first handshake. A single
        // initiation would be lost in that window and the initiator would wait out the whole
        // timeout for a peer that is in fact there. Re-firing every `REINITIATE_EVERY` makes
        // the handshake robust to the peer coming up slightly late, which is exactly the
        // schedule WireGuard itself uses.
        const REINITIATE_EVERY: Duration = Duration::from_millis(1000);

        let deadline = Instant::now() + timeout;
        let mut next_initiation = Instant::now();
        while !self.is_established() {
            let now = Instant::now();
            if now >= deadline {
                return Err(crate::Error::Config(
                    "WireGuard handshake timed out; the peer did not answer".into(),
                ));
            }
            if now >= next_initiation {
                self.initiate().await?;
                next_initiation = now + REINITIATE_EVERY;
            }

            // Wait for protocol traffic, but never past the next scheduled re-initiation or
            // the overall deadline. The peer's response is protocol traffic, so `poll` may
            // return no inner packet: the loop condition, not the packet, ends the handshake.
            let wake = next_initiation.min(deadline);
            let remaining = wake.saturating_duration_since(now);
            match tokio::time::timeout(remaining, self.poll()).await {
                Ok(Ok(_)) => {}
                Ok(Err(e)) => return Err(e),
                // A timeout here just means "no traffic yet"; loop back to re-initiate.
                Err(_) => {}
            }
        }
        Ok(())
    }

    /// Waits for the peer to bring the link up, polling without ever initiating.
    ///
    /// This is the responder's counterpart to [`Self::ensure_session`]: the Station should not
    /// initiate — it answers the Client's handshake. Polling alone drives that, since
    /// [`Self::poll`] replies to the initiation and the confirming keepalive on its own. Waits
    /// up to `timeout` for the session to establish.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] on timeout, and propagates a transport error from the
    /// socket or relay.
    pub async fn await_session(&mut self, timeout: Duration) -> Result<()> {
        if self.is_established() {
            return Ok(());
        }
        let deadline = Instant::now() + timeout;
        while !self.is_established() {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(crate::Error::Config(
                    "timed out waiting for the peer to open the WireGuard link".into(),
                ));
            }
            match tokio::time::timeout(remaining, self.poll()).await {
                Ok(Ok(_)) => {}
                Ok(Err(e)) => return Err(e),
                Err(_) => {
                    return Err(crate::Error::Config(
                        "timed out waiting for the peer to open the WireGuard link".into(),
                    ))
                }
            }
        }
        Ok(())
    }

    /// Opens the link by NAT hole-punching: both ends initiate repeatedly while polling.
    ///
    /// Unlike [`Self::ensure_session`] (one initiator) and [`Self::await_session`] (one
    /// responder), a punched direct path needs **both** ends to send outbound handshake
    /// datagrams so each NAT opens a mapping for the other's address. So this re-initiates on
    /// a short interval — each initiation is an outbound packet that punches our NAT — while
    /// polling for the peer's reply, until the session establishes or `timeout` elapses. A
    /// lost first packet (common mid-punch, before both mappings exist) is simply covered by
    /// the next re-initiation.
    ///
    /// A send error during punching is *not* fatal: before the peer's NAT has a mapping, our
    /// datagrams may bounce, which is expected. Only the timeout ends the attempt.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the link does not come up within `timeout`.
    pub async fn punch_session(&mut self, timeout: Duration) -> Result<()> {
        /// How often to re-fire an initiation while punching.
        const REINITIATE_EVERY: Duration = Duration::from_millis(250);

        if self.is_established() {
            return Ok(());
        }
        let deadline = Instant::now() + timeout;
        let mut next_initiate = Instant::now(); // fire immediately on entry

        while !self.is_established() {
            let now = Instant::now();
            if now >= deadline {
                return Err(crate::Error::Config(
                    "hole-punch handshake timed out; no direct path formed".into(),
                ));
            }
            // Re-initiate on the interval; a send failure mid-punch is expected, not fatal.
            if now >= next_initiate {
                let _ = self.initiate().await;
                next_initiate = now + REINITIATE_EVERY;
            }
            // Poll for the peer's handshake, but never longer than the next re-initiate tick
            // (or the deadline), so re-initiations keep the NAT mapping fresh.
            let wake = next_initiate.min(deadline);
            let slice = wake.saturating_duration_since(Instant::now());
            match tokio::time::timeout(slice, self.poll()).await {
                Ok(Ok(_)) => {}
                // A transport error (e.g. ICMP port-unreachable before the peer is ready) is
                // part of punching; keep trying until the deadline.
                Ok(Err(_)) => {}
                // The slice elapsed: loop to re-initiate.
                Err(_) => {}
            }
        }
        Ok(())
    }

    /// Encrypts and sends one inner IP packet.
    ///
    /// Fails immediately when the link is not up rather than handshaking inline. A caller on
    /// the keying path must never block for a handshake — the keying engine holds deadlines of
    /// well under a millisecond, and a stall of seconds would corrupt the element timing this
    /// whole port exists to protect. boringtun drops a payload it has no session for, so
    /// without this check keying traffic would vanish silently instead. Bringing the link up is
    /// [`Self::ensure_session`]'s job, driven by a tunnel task, not the send path.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when the packet is too large for the tunnel or when no
    /// session is established, and propagates an `std::io::Error` if the datagram cannot be
    /// sent.
    pub async fn send_inner(&mut self, packet: &[u8]) -> Result<()> {
        if packet.len() > MAX_INNER_PACKET {
            return Err(crate::Error::Config(format!(
                "inner packet of {} bytes exceeds the {MAX_INNER_PACKET}-byte tunnel MTU",
                packet.len()
            )));
        }
        if !self.is_established() {
            return Err(crate::Error::Config(
                "WireGuard link is not established; bring the session up before sending".into(),
            ));
        }

        let mut dst = vec![0u8; MAX_WIREGUARD_DATAGRAM];
        let datagram = match self.tunn.encapsulate(packet, &mut dst) {
            TunnResult::WriteToNetwork(bytes) => bytes.to_vec(),
            TunnResult::Err(e) => {
                return Err(crate::Error::Config(format!(
                    "WireGuard encapsulation failed: {e:?}"
                )))
            }
            // `Done` with an established session means the packet was not encodable, which
            // would otherwise drop keying traffic silently.
            _ => {
                return Err(crate::Error::Config(
                    "WireGuard encapsulation produced no datagram".into(),
                ))
            }
        };
        self.send_on_wire(&datagram).await?;
        self.stats.sent_packets.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    /// Reads one datagram and returns the inner packet it carried, if any.
    ///
    /// Handshake and keepalive datagrams are answered on this call and reported as
    /// [`None`], so a caller polling in a loop makes progress on the link itself without
    /// knowing the WireGuard protocol.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` when the socket read or a protocol reply fails.
    pub async fn poll(&mut self) -> Result<Option<TunnelPacket>> {
        let (datagram, src) = match self.wire.recv().await {
            Ok(received) => received,
            Err(e) => {
                self.stats.receive_errors.fetch_add(1, Ordering::Relaxed);
                return Err(e);
            }
        };
        self.stats.received_datagrams.fetch_add(1, Ordering::Relaxed);
        // `src` is the sender's socket address on the direct path and `None` over the relay;
        // boringtun uses it only to learn a roaming endpoint, which the relay does not have.
        self.consume(src, &datagram).await
    }

    /// Blocks until an inner packet arrives, answering protocol traffic as it goes.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the socket read or a protocol reply fails.
    pub async fn recv_inner(&mut self) -> Result<TunnelPacket> {
        loop {
            if let Some(packet) = self.poll().await? {
                return Ok(packet);
            }
        }
    }

    /// Runs WireGuard's timers: handshake retries, rekeying, keepalives and expiry.
    ///
    /// Call this on a timer (roughly 100 ms is plenty) so a link recovers on its own after a
    /// network change without waiting for the next payload to fail.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if a keepalive or rekey datagram cannot be sent.
    pub async fn tick(&mut self) -> Result<()> {
        let mut dst = vec![0u8; MAX_WIREGUARD_DATAGRAM];
        let pending = match self.tunn.update_timers(&mut dst) {
            TunnResult::WriteToNetwork(bytes) => Some(bytes.to_vec()),
            TunnResult::Err(_) => {
                self.stats.decapsulation_errors.fetch_add(1, Ordering::Relaxed);
                None
            }
            _ => None,
        };
        if let Some(datagram) = pending {
            self.send_on_wire(&datagram).await?;
        }
        Ok(())
    }

    /// Runs one decapsulation pass, then drains any further protocol datagrams boringtun
    /// queues behind it (its contract is to call again with an empty datagram until `Done`).
    async fn consume(
        &mut self,
        src: Option<IpAddr>,
        datagram: &[u8],
    ) -> Result<Option<TunnelPacket>> {
        let was_established = self.is_established();
        let mut inner = None;
        let mut next_src = src;
        let mut next_datagram = datagram;
        loop {
            match self.step(next_src, next_datagram) {
                Step::Idle | Step::Rejected => break,
                Step::Inner(packet) => inner = Some(packet),
                Step::Send(bytes) => self.send_on_wire(&bytes).await?,
            }
            next_src = None;
            next_datagram = &[];
        }
        if inner.is_some() {
            self.stats.received_packets.fetch_add(1, Ordering::Relaxed);
        }
        if !was_established && self.is_established() {
            self.stats.handshakes.fetch_add(1, Ordering::Relaxed);
        }
        Ok(inner)
    }

    /// One `decapsulate` call, with the borrowed output copied out so the lifetime of the
    /// destination buffer does not escape into the caller.
    fn step(&mut self, src: Option<IpAddr>, datagram: &[u8]) -> Step {
        let mut dst = vec![0u8; MAX_WIREGUARD_DATAGRAM];
        match self.tunn.decapsulate(src, datagram, &mut dst) {
            TunnResult::Done => Step::Idle,
            TunnResult::Err(_) => {
                self.stats.decapsulation_errors.fetch_add(1, Ordering::Relaxed);
                Step::Rejected
            }
            TunnResult::WriteToNetwork(bytes) => Step::Send(bytes.to_vec()),
            TunnResult::WriteToTunnelV4(bytes, addr) => {
                Step::Inner(TunnelPacket::V4(bytes.to_vec(), addr))
            }
            TunnResult::WriteToTunnelV6(bytes, addr) => {
                Step::Inner(TunnelPacket::V6(bytes.to_vec(), addr))
            }
        }
    }

    /// Puts one datagram on the wire, maintaining the sustained-failure signal F9 reads.
    async fn send_on_wire(&mut self, datagram: &[u8]) -> Result<()> {
        match self.wire.send(datagram).await {
            Ok(_) => {
                self.stats.consecutive_send_errors.store(0, Ordering::Relaxed);
                Ok(())
            }
            Err(e) => {
                self.stats.send_errors.fetch_add(1, Ordering::Relaxed);
                // As on the edge transport: one lost datagram is not a lost path, a run of
                // them is. This is the tunnel's own F9 source.
                let consecutive =
                    self.stats.consecutive_send_errors.fetch_add(1, Ordering::Relaxed) + 1;
                if consecutive >= MAX_CONSECUTIVE_SEND_ERRORS {
                    self.health.report_lost();
                }
                Err(e)
            }
        }
    }
}

/// Renders a key as lowercase hex for logs and `Debug` output.
fn hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(char::from_digit(u32::from(byte >> 4), 16).unwrap_or('?'));
        out.push(char::from_digit(u32::from(byte & 0x0f), 16).unwrap_or('?'));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A deterministic identity, so a test failure is reproducible.
    fn identity(seed: u8) -> StaticIdentity {
        let mut bytes = [0u8; 32];
        for (i, byte) in bytes.iter_mut().enumerate() {
            *byte = seed.wrapping_mul(31).wrapping_add(i as u8);
        }
        StaticIdentity::from_private_bytes(bytes)
    }

    fn peer(public_key: [u8; 32], endpoint: SocketAddr) -> TunnelPeer {
        TunnelPeer { public_key, preshared_key: None, endpoint, persistent_keepalive: None }
    }

    /// A minimal but well-formed IPv4 packet, so the tunnel sees something realistic.
    fn ipv4_packet(src: [u8; 4], dst: [u8; 4], payload: &[u8]) -> Vec<u8> {
        let mut packet = vec![0u8; 20];
        packet[0] = 0x45; // IPv4, 5-word header.
        packet[2..4].copy_from_slice(&((20 + payload.len()) as u16).to_be_bytes());
        packet[8] = 64; // TTL
        packet[9] = 17; // UDP
        packet[12..16].copy_from_slice(&src);
        packet[16..20].copy_from_slice(&dst);
        packet.extend_from_slice(payload);
        packet
    }

    #[test]
    fn a_generated_identity_derives_a_stable_public_key() {
        let first = StaticIdentity::generate().unwrap();
        let second = StaticIdentity::generate().unwrap();
        assert_ne!(first.private_bytes(), second.private_bytes());
        assert_ne!(first.public_bytes(), second.public_bytes());

        // Rebuilding from the stored private bytes must reproduce the same public key, or a
        // restarted process would present a key its peer does not trust.
        let rebuilt = StaticIdentity::from_private_bytes(first.private_bytes());
        assert_eq!(rebuilt.public_bytes(), first.public_bytes());
    }

    #[test]
    fn debug_output_never_leaks_a_private_key_or_the_preshared_key() {
        let id = identity(9);
        let rendered = format!("{id:?}");
        assert!(rendered.contains(&hex(&id.public_bytes())));
        assert!(!rendered.contains(&hex(&id.private_bytes())));

        let peer = TunnelPeer {
            public_key: identity(10).public_bytes(),
            preshared_key: Some([7u8; 32]),
            endpoint: "127.0.0.1:1".parse().unwrap(),
            persistent_keepalive: Some(25),
        };
        let rendered = format!("{peer:?}");
        assert!(rendered.contains("<set>"));
        assert!(!rendered.contains(&hex(&[7u8; 32])));
    }

    #[tokio::test]
    async fn two_peers_handshake_and_carry_an_inner_packet() {
        let a_id = identity(1);
        let b_id = identity(2);

        let mut a = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &a_id,
            // The peer endpoint is unknown until both ends are bound; it is set below.
            &peer(b_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();
        let mut b = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &b_id,
            &peer(a_id.public_bytes(), a.local_addr().unwrap()),
        )
        .await
        .unwrap();
        a.set_peer(b.local_addr().unwrap());

        // The full exchange: initiation, response, and the keepalive that confirms the
        // responder's new session. A strictly sequential hand-off suffices on loopback.
        a.initiate().await.unwrap();
        assert!(b.poll().await.unwrap().is_none(), "an initiation carries no inner packet");
        assert!(
            !b.is_established(),
            "the responder is not established until it accepts a transport packet"
        );
        assert!(a.poll().await.unwrap().is_none(), "a response carries no inner packet");
        assert!(a.is_established(), "the initiator is up as soon as it reads the response");
        assert!(b.poll().await.unwrap().is_none(), "the confirming keepalive has no payload");
        assert!(b.is_established(), "the responder is up once the keepalive is accepted");
        assert_eq!(a.stats().handshakes(), 1, "the establishment is counted on both ends");
        assert_eq!(b.stats().handshakes(), 1);

        let payload = b"RWK-PADDLE";
        let inner = ipv4_packet([10, 0, 0, 1], [10, 0, 0, 2], payload);
        a.send_inner(&inner).await.unwrap();

        let received = b.poll().await.unwrap().expect("an inner packet must arrive");
        let TunnelPacket::V4(bytes, source) = received else {
            panic!("expected an IPv4 packet, got {received:?}");
        };
        assert_eq!(source, Ipv4Addr::new(10, 0, 0, 1));
        assert_eq!(bytes, inner, "the inner packet must round-trip byte for byte");

        assert_eq!(a.stats().sent_packets(), 1);
        assert_eq!(b.stats().received_packets(), 1);
        assert_eq!(b.stats().decapsulation_errors(), 0);
        assert!(!b.health().is_lost(), "a healthy path must not raise F9");

        // Timers must be safe to run on a live link.
        a.tick().await.unwrap();
        b.tick().await.unwrap();
    }

    #[tokio::test]
    async fn a_payload_larger_than_the_tunnel_mtu_is_refused() {
        let mut a = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &identity(11),
            &peer(identity(12).public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();

        let oversized = vec![0u8; MAX_INNER_PACKET + 1];
        let error = a.send_inner(&oversized).await.unwrap_err();
        assert!(matches!(error, crate::Error::Config(_)));
        assert_eq!(a.stats().sent_packets(), 0);
    }

    #[tokio::test]
    async fn sending_before_the_handshake_fails_fast() {
        let mut a = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &identity(13),
            &peer(identity(14).public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();

        let inner = ipv4_packet([10, 0, 0, 1], [10, 0, 0, 2], b"x");
        // Must return at once: the keying path cannot wait seconds for a link to come up.
        let error = a.send_inner(&inner).await.unwrap_err();
        assert!(matches!(error, crate::Error::Config(_)));
        assert_eq!(a.stats().sent_packets(), 0);
    }

    #[tokio::test]
    async fn a_peer_with_the_wrong_key_cannot_establish_a_session() {
        let a_id = identity(3);
        let b_id = identity(4);
        let stranger = identity(5);

        // A encrypts to the stranger's key, so B cannot authenticate the initiation.
        let mut a = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &a_id,
            &peer(stranger.public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();
        let mut b = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &b_id,
            &peer(a_id.public_bytes(), a.local_addr().unwrap()),
        )
        .await
        .unwrap();
        a.set_peer(b.local_addr().unwrap());

        a.initiate().await.unwrap();
        assert!(
            b.poll().await.unwrap().is_none(),
            "an undecryptable initiation must yield no inner packet"
        );
        assert!(!b.is_established(), "the wrong key must not bring the link up");
        assert!(!a.is_established());
        assert_eq!(b.stats().decapsulation_errors(), 1);
    }

    #[tokio::test]
    async fn sustained_send_failures_raise_path_loss() {
        let a_id = identity(7);
        // An IPv4 socket cannot send to an IPv6 peer, so every datagram fails deterministically.
        let mut a = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &a_id,
            &peer(identity(8).public_bytes(), "[::1]:9".parse().unwrap()),
        )
        .await
        .unwrap();
        let health = a.health();

        for i in 1..MAX_CONSECUTIVE_SEND_ERRORS {
            assert!(a.initiate().await.is_err());
            assert!(!health.is_lost(), "must not trip after {i} failure(s)");
        }
        assert!(a.initiate().await.is_err());
        assert!(health.is_lost(), "a sustained failure run must raise path loss");
        assert!(health.take_lost(), "the flag is consumable");
        assert!(!health.is_lost(), "taking it clears it");
        assert_eq!(a.stats().consecutive_send_errors(), MAX_CONSECUTIVE_SEND_ERRORS);
    }

    /// The NAT-traversal fallback end to end: a full WireGuard handshake and an encrypted
    /// inner packet carried entirely **through the relay**, not over direct UDP. This is what
    /// makes the relay a true drop-in transport — the identical crypto that the direct tests
    /// exercise runs here with every datagram routed by public key through a `RelayServer`.
    #[tokio::test]
    async fn a_tunnel_handshakes_and_carries_a_packet_over_the_relay() {
        use crate::engine::relay::{RelayClient, RelayServer};

        let a_id = identity(31);
        let b_id = identity(32);

        // Stand up the relay on an ephemeral port.
        let server = RelayServer::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let relay_addr = server.local_addr().unwrap();
        tokio::spawn(async move {
            let _ = server.run().await;
        });

        // Each end connects to the relay, registering its own key and scoped to the peer's.
        let a_client =
            RelayClient::connect(relay_addr, a_id.public_bytes(), b_id.public_bytes()).await.unwrap();
        let b_client =
            RelayClient::connect(relay_addr, b_id.public_bytes(), a_id.public_bytes()).await.unwrap();

        // Let both registrations reach the relay so the routes exist before the handshake.
        tokio::time::sleep(Duration::from_millis(50)).await;

        let mut a = WireGuardTunnel::bind_relay(
            &a_id,
            &peer(b_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
            a_client,
        );
        let mut b = WireGuardTunnel::bind_relay(
            &b_id,
            &peer(a_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
            b_client,
        );

        // A relayed tunnel has no peer-facing local socket.
        assert!(a.local_addr().is_err(), "a relayed tunnel exposes no local peer socket");
        assert_eq!(a.peer(), None);

        // The identical handshake exchange as the direct test, but every datagram is now
        // relayed. `timeout` guards against a lost route hanging the test.
        let exchange = async {
            a.initiate().await?;
            b.poll().await?;
            a.poll().await?;
            b.poll().await?;
            Ok::<(), crate::Error>(())
        };
        tokio::time::timeout(Duration::from_secs(5), exchange)
            .await
            .expect("relayed handshake timed out")
            .expect("relayed handshake failed");
        assert!(a.is_established() && b.is_established(), "the link must come up over the relay");

        // And an inner packet survives the relayed, encrypted path.
        let payload = b"RWK-PADDLE";
        let inner = ipv4_packet([10, 0, 0, 1], [10, 0, 0, 2], payload);
        a.send_inner(&inner).await.unwrap();

        let received = tokio::time::timeout(Duration::from_secs(5), b.poll())
            .await
            .expect("relayed inner packet timed out")
            .unwrap()
            .expect("an inner packet must arrive over the relay");
        assert_eq!(received.bytes(), inner.as_slice(), "the inner packet must round-trip");
        assert_eq!(b.stats().decapsulation_errors(), 0, "no datagram failed to decrypt");
    }
}
