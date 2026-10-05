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

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use boringtun::noise::{Tunn, TunnResult};
use boringtun::x25519::{PublicKey, StaticSecret};
use tokio::net::UdpSocket;

use crate::engine::network::{PathHealth, MAX_CONSECUTIVE_SEND_ERRORS};
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

/// One end of a point-to-point WireGuard link, driven over a UDP socket.
///
/// The type owns both the crypto state ([`Tunn`]) and the socket, so a caller never has to
/// reason about which datagrams are protocol traffic and which carry payload: [`Self::poll`]
/// and [`Self::recv_inner`] answer protocol traffic on your behalf and return only inner
/// packets.
pub struct WireGuardTunnel {
    tunn: Tunn,
    socket: UdpSocket,
    peer: SocketAddr,
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

        Ok(Self {
            tunn,
            socket,
            peer: peer.endpoint,
            health: PathHealth::new(),
            stats: Arc::new(TunnelStats::default()),
        })
    }

    /// The local address the tunnel's socket is bound to.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.socket.local_addr()?)
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

    /// The current peer endpoint.
    #[must_use]
    pub fn peer(&self) -> SocketAddr {
        self.peer
    }

    /// Moves the peer endpoint, as a roaming peer would when its address changes.
    pub fn set_peer(&mut self, endpoint: SocketAddr) {
        self.peer = endpoint;
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
        if self.is_established() {
            return Ok(());
        }

        self.initiate().await?;

        let deadline = Instant::now() + HANDSHAKE_TIMEOUT;
        while !self.is_established() {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(crate::Error::Config(
                    "WireGuard handshake timed out; the peer did not answer".into(),
                ));
            }
            // The peer's response is protocol traffic, so `poll` may return no inner packet:
            // the loop condition, not the packet, is what ends the handshake.
            match tokio::time::timeout(remaining, self.poll()).await {
                Ok(Ok(_)) => {}
                Ok(Err(e)) => return Err(e),
                Err(_) => {
                    return Err(crate::Error::Config(
                        "WireGuard handshake timed out; the peer did not answer".into(),
                    ))
                }
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
        let mut buf = vec![0u8; MAX_WIREGUARD_DATAGRAM];
        let (len, src) = match self.socket.recv_from(&mut buf).await {
            Ok(received) => received,
            Err(e) => {
                self.stats.receive_errors.fetch_add(1, Ordering::Relaxed);
                return Err(e.into());
            }
        };
        self.stats.received_datagrams.fetch_add(1, Ordering::Relaxed);
        self.consume(Some(src.ip()), &buf[..len]).await
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
        match self.socket.send_to(datagram, self.peer).await {
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
                Err(e.into())
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
}
