//! LAN peer discovery: relay-free pairing on a local network.
//!
//! On a LAN the two ends do not need the relay to find each other — they can hear each other
//! directly over UDP broadcast. The model is deliberately asymmetric and matches how an
//! operator thinks about it:
//!
//! * The **operating-position** app (the one holding the paddle — a Client) *advertises*: it
//!   periodically broadcasts a [`Beacon`] announcing "I am keyer `W1VE-20m`, my WireGuard key
//!   is K, reach me at UDP port P". It stops advertising once it is paired, so it drops off
//!   everyone's list and a second radio site cannot grab it.
//! * The **radio-site** app (the one that keys the rig — a Station/Server) *browses*: it
//!   collects beacons into a live, de-duplicated, freshness-pruned list and shows the operator
//!   a dropdown of available keyers. The operator picks one, and the radio site brings up a
//!   direct WireGuard link to that beacon's key and address.
//!
//! Because each beacon carries the advertiser's **public key**, the radio site has everything
//! WireGuard needs (boringtun requires the peer key before the handshake) with no relay and no
//! typed code. Multiple keyer/radio pairs coexist on one LAN: beacons are keyed by public key,
//! the human disambiguates by the `callsign-keyerId` label, and each advertiser uses its own
//! ephemeral WireGuard port (carried in the beacon), so nothing collides.
//!
//! The discovery socket itself uses the same `socket2` reuse+broadcast setup as
//! [`flex_discovery`](crate::engine::flex_discovery), bound to [`DISCOVERY_PORT`], so several
//! apps on one machine can share it.

use std::collections::HashMap;
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};
use std::time::{Duration, Instant};

use socket2::{Domain, Protocol, Socket, Type};
use tokio::net::UdpSocket;

use crate::Result;

/// The UDP port LAN discovery beacons are broadcast to. Adjacent to Flex's 4992, and distinct
/// so the two never interfere.
pub const DISCOVERY_PORT: u16 = 4993;

/// A four-byte magic prefix marking an RWK LAN beacon, so a stray datagram on the port is
/// ignored rather than mis-parsed.
const MAGIC: [u8; 4] = *b"RWKB";

/// Beacons older than this are pruned from a browser's list — an advertiser that stopped
/// (quit, paired, or lost power) disappears within a few seconds.
const PEER_TTL: Duration = Duration::from_secs(5);

/// How often an advertiser re-broadcasts its beacon. Comfortably inside [`PEER_TTL`] so a live
/// advertiser never flickers out of a browser's list between broadcasts.
pub const ADVERTISE_INTERVAL: Duration = Duration::from_millis(1500);

/// A keyer's self-announcement on the LAN.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Beacon {
    /// Operator callsign, e.g. `W1VE`.
    pub callsign: String,
    /// Optional position label, e.g. `20m`. Empty when unset.
    pub keyer_id: String,
    /// The advertiser's WireGuard public key — what the radio site needs to handshake.
    pub public_key: [u8; 32],
    /// The UDP port the advertiser's WireGuard socket is bound to.
    pub wg_port: u16,
}

impl Beacon {
    /// The human-facing name shown in the radio site's dropdown: `callsign-keyerId`, or just
    /// `callsign` when no id is set, or `(unnamed)` when neither is.
    #[must_use]
    pub fn display_name(&self) -> String {
        match (self.callsign.trim(), self.keyer_id.trim()) {
            ("", "") => "(unnamed)".to_string(),
            (call, "") => call.to_string(),
            ("", id) => id.to_string(),
            (call, id) => format!("{call}-{id}"),
        }
    }

    /// Encodes the beacon to its wire bytes: `MAGIC | callsign(len+bytes) | keyer_id(len+bytes)
    /// | public_key(32) | wg_port(2)`. Strings are length-prefixed with one byte (clamped to
    /// 255), which is plenty for a callsign/label.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        let call = self.callsign.as_bytes();
        let id = self.keyer_id.as_bytes();
        let call_len = call.len().min(255);
        let id_len = id.len().min(255);
        let mut out = Vec::with_capacity(4 + 1 + call_len + 1 + id_len + 32 + 2);
        out.extend_from_slice(&MAGIC);
        out.push(call_len as u8);
        out.extend_from_slice(&call[..call_len]);
        out.push(id_len as u8);
        out.extend_from_slice(&id[..id_len]);
        out.extend_from_slice(&self.public_key);
        out.extend_from_slice(&self.wg_port.to_be_bytes());
        out
    }

    /// Parses a beacon from received bytes, returning [`None`] for anything that is not a
    /// well-formed RWK beacon (wrong magic, truncated, or non-UTF-8 label).
    #[must_use]
    pub fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 4 || bytes[..4] != MAGIC {
            return None;
        }
        let mut pos = 4;
        let callsign = take_string(bytes, &mut pos)?;
        let keyer_id = take_string(bytes, &mut pos)?;
        // 32-byte key + 2-byte port must remain.
        if bytes.len() < pos + 34 {
            return None;
        }
        let mut public_key = [0u8; 32];
        public_key.copy_from_slice(&bytes[pos..pos + 32]);
        pos += 32;
        let wg_port = u16::from_be_bytes([bytes[pos], bytes[pos + 1]]);
        Some(Self { callsign, keyer_id, public_key, wg_port })
    }
}

/// Reads a one-byte-length-prefixed UTF-8 string, advancing `pos`.
fn take_string(bytes: &[u8], pos: &mut usize) -> Option<String> {
    let len = *bytes.get(*pos)? as usize;
    *pos += 1;
    let end = *pos + len;
    let s = std::str::from_utf8(bytes.get(*pos..end)?).ok()?.to_string();
    *pos = end;
    Some(s)
}

/// Binds a UDP socket on `port` for all interfaces with address reuse and broadcast enabled,
/// returning it as a tokio socket. Mirrors [`flex_discovery`](crate::engine::flex_discovery)'s
/// setup so several apps on one host can share the discovery port.
fn bind_reuse_broadcast(port: u16) -> Result<UdpSocket> {
    let socket = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))
        .map_err(crate::Error::from)?;
    socket.set_reuse_address(true).map_err(crate::Error::from)?;
    #[cfg(unix)]
    {
        let _ = socket.set_reuse_port(true);
    }
    socket.set_broadcast(true).map_err(crate::Error::from)?;
    socket.set_nonblocking(true).map_err(crate::Error::from)?;
    let addr: SocketAddr = SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, port));
    socket.bind(&addr.into()).map_err(crate::Error::from)?;
    UdpSocket::from_std(socket.into()).map_err(crate::Error::from)
}

/// Broadcasts a keyer's [`Beacon`] on the LAN until dropped.
///
/// The operating-position app constructs one when LAN pairing is active and not yet paired,
/// then calls [`run`](Self::run) on a task; dropping the task (or the advertiser) stops the
/// announcements and the keyer vanishes from browsers within [`PEER_TTL`].
pub struct LanAdvertiser {
    socket: UdpSocket,
    beacon: Beacon,
}

impl LanAdvertiser {
    /// Creates an advertiser for `beacon`, bound to an ephemeral source port with broadcast.
    ///
    /// # Errors
    ///
    /// Returns an error if the broadcast socket cannot be created.
    pub fn new(beacon: Beacon) -> Result<Self> {
        // Ephemeral source port (0): the destination is the broadcast discovery port.
        Ok(Self { socket: bind_reuse_broadcast(0)?, beacon })
    }

    /// Broadcasts the beacon once, now.
    ///
    /// # Errors
    ///
    /// Propagates a socket send error.
    pub async fn announce_once(&self) -> Result<()> {
        let dest: SocketAddr =
            SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::BROADCAST, DISCOVERY_PORT));
        self.socket.send_to(&self.beacon.encode(), dest).await.map_err(crate::Error::from)?;
        Ok(())
    }

    /// Broadcasts the beacon every [`ADVERTISE_INTERVAL`] until the future is dropped.
    pub async fn run(self) {
        let mut ticker = tokio::time::interval(ADVERTISE_INTERVAL);
        loop {
            ticker.tick().await;
            let _ = self.announce_once().await;
        }
    }
}

/// A keyer the browser has heard on the LAN, with the facts needed to connect and display it.
#[derive(Debug, Clone)]
pub struct LanPeer {
    /// The advertiser's WireGuard public key.
    pub public_key: [u8; 32],
    /// Callsign.
    pub callsign: String,
    /// Position label (may be empty).
    pub keyer_id: String,
    /// The address to handshake with: the beacon's source IP + its advertised WireGuard port.
    pub endpoint: SocketAddr,
    /// When this peer's beacon was last heard, for freshness pruning.
    last_seen: Instant,
}

impl LanPeer {
    /// The human-facing name, `callsign-keyerId`.
    #[must_use]
    pub fn display_name(&self) -> String {
        Beacon {
            callsign: self.callsign.clone(),
            keyer_id: self.keyer_id.clone(),
            public_key: self.public_key,
            wg_port: self.endpoint.port(),
        }
        .display_name()
    }
}

/// Collects LAN beacons into a live list of [`LanPeer`]s for the radio site's dropdown.
///
/// The radio-site app constructs one, binds the discovery port, and loops: [`recv_once`] folds
/// each heard beacon into the list; [`peers`] returns the currently-live set (pruning anything
/// not heard within [`PEER_TTL`]). Beacons carrying `self_key` are ignored so a node never
/// lists its own broadcast echo.
pub struct LanBrowser {
    socket: UdpSocket,
    self_key: [u8; 32],
    peers: HashMap<[u8; 32], LanPeer>,
}

impl LanBrowser {
    /// Binds the discovery port and starts browsing, ignoring beacons from `self_key`.
    ///
    /// # Errors
    ///
    /// Returns an error if the discovery socket cannot be bound.
    pub fn bind(self_key: [u8; 32]) -> Result<Self> {
        Ok(Self { socket: bind_reuse_broadcast(DISCOVERY_PORT)?, self_key, peers: HashMap::new() })
    }

    /// Waits for one datagram and folds it into the peer list if it is a valid foreign beacon.
    ///
    /// Returns `true` if a beacon was accepted (the caller may want to re-emit the list),
    /// `false` for a datagram that was ignored (self-echo or not a beacon).
    ///
    /// # Errors
    ///
    /// Propagates a socket receive error.
    pub async fn recv_once(&mut self) -> Result<bool> {
        let mut buf = vec![0u8; 512];
        let (len, src) = self.socket.recv_from(&mut buf).await.map_err(crate::Error::from)?;
        let Some(beacon) = Beacon::decode(&buf[..len]) else {
            return Ok(false);
        };
        // Never list our own echo.
        if beacon.public_key == self.self_key {
            return Ok(false);
        }
        let endpoint = SocketAddr::new(src.ip(), beacon.wg_port);
        self.peers.insert(
            beacon.public_key,
            LanPeer {
                public_key: beacon.public_key,
                callsign: beacon.callsign,
                keyer_id: beacon.keyer_id,
                endpoint,
                last_seen: Instant::now(),
            },
        );
        Ok(true)
    }

    /// The currently-live peers, pruning any not heard within [`PEER_TTL`].
    #[must_use]
    pub fn peers(&mut self) -> Vec<LanPeer> {
        prune_and_sort(&mut self.peers, Instant::now())
    }

    /// Binds a browser and listens for up to `timeout` for the current beacon of `key`,
    /// returning its freshly-heard endpoint.
    ///
    /// The radio site uses this right before connecting so it never handshakes a stale port:
    /// an advertiser that re-armed (and re-bound to a new WireGuard port) is picked up on its
    /// next broadcast — well inside a couple of [`ADVERTISE_INTERVAL`]s — so a reconnect
    /// always targets the keyer's *current* address, not the one cached when the operator
    /// first browsed.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if no beacon for `key` arrives within `timeout`;
    /// propagates socket errors.
    pub async fn resolve(
        self_key: [u8; 32],
        key: [u8; 32],
        timeout: Duration,
    ) -> Result<SocketAddr> {
        let mut browser = Self::bind(self_key)?;
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(tokio::time::Instant::now());
            if remaining.is_zero() {
                return Err(crate::Error::Config(
                    "the selected keyer is no longer advertising on the LAN".into(),
                ));
            }
            match tokio::time::timeout(remaining, browser.recv_once()).await {
                Ok(Ok(_)) => {
                    if let Some(peer) = browser.peers().into_iter().find(|p| p.public_key == key) {
                        return Ok(peer.endpoint);
                    }
                }
                Ok(Err(e)) => return Err(e),
                Err(_) => {
                    return Err(crate::Error::Config(
                        "the selected keyer is no longer advertising on the LAN".into(),
                    ))
                }
            }
        }
    }
}

/// Prunes stale peers (not seen within [`PEER_TTL`]) and returns the rest sorted by name.
/// Factored out so the freshness rule is testable without binding a discovery socket.
fn prune_and_sort(peers: &mut HashMap<[u8; 32], LanPeer>, now: Instant) -> Vec<LanPeer> {
    peers.retain(|_, p| now.duration_since(p.last_seen) < PEER_TTL);
    let mut list: Vec<LanPeer> = peers.values().cloned().collect();
    list.sort_by(|a, b| a.display_name().cmp(&b.display_name()));
    list
}

#[cfg(test)]
mod tests {
    use super::*;

    fn beacon() -> Beacon {
        Beacon { callsign: "W1VE".into(), keyer_id: "20m".into(), public_key: [7u8; 32], wg_port: 51820 }
    }

    #[test]
    fn display_name_composes_callsign_and_id() {
        assert_eq!(beacon().display_name(), "W1VE-20m");
        let no_id = Beacon { keyer_id: String::new(), ..beacon() };
        assert_eq!(no_id.display_name(), "W1VE");
        let neither = Beacon { callsign: String::new(), keyer_id: String::new(), ..beacon() };
        assert_eq!(neither.display_name(), "(unnamed)");
    }

    #[test]
    fn beacon_round_trips_through_the_codec() {
        let b = beacon();
        let bytes = b.encode();
        assert_eq!(Beacon::decode(&bytes), Some(b));
    }

    #[test]
    fn decode_rejects_non_beacons_and_truncation() {
        assert_eq!(Beacon::decode(b"not a beacon"), None);
        assert_eq!(Beacon::decode(&[]), None);
        let mut bytes = beacon().encode();
        bytes.truncate(bytes.len() - 3); // chop the port + key tail
        assert_eq!(Beacon::decode(&bytes), None);
    }

    #[test]
    fn empty_labels_round_trip() {
        let b = Beacon { callsign: String::new(), keyer_id: String::new(), public_key: [1u8; 32], wg_port: 0 };
        assert_eq!(Beacon::decode(&b.encode()), Some(b));
    }

    #[tokio::test]
    async fn browser_lists_a_foreign_beacon_and_ignores_self() {
        // Bind a browser for self_key = [9;32].
        let mut browser = LanBrowser::bind([9u8; 32]).unwrap();
        let browse_port = browser.socket.local_addr().unwrap().port();
        assert_eq!(browse_port, DISCOVERY_PORT);

        // A foreign beacon fed directly through decode+insert via a loopback send. We send to
        // 127.0.0.1:DISCOVERY_PORT so the browser's bound socket receives it.
        let sender = bind_reuse_broadcast(0).unwrap();
        let foreign = Beacon { public_key: [3u8; 32], ..beacon() };
        sender
            .send_to(&foreign.encode(), SocketAddr::from(([127, 0, 0, 1], DISCOVERY_PORT)))
            .await
            .unwrap();

        // Receive it; it should be accepted and listed.
        let accepted = tokio::time::timeout(Duration::from_secs(2), browser.recv_once())
            .await
            .expect("recv timed out")
            .unwrap();
        assert!(accepted, "a foreign beacon should be accepted");
        let peers = browser.peers();
        assert_eq!(peers.len(), 1);
        assert_eq!(peers[0].display_name(), "W1VE-20m");
        assert_eq!(peers[0].endpoint.port(), 51820, "endpoint port is the advertised wg_port");

        // A self beacon (our own key) must be ignored.
        let own = Beacon { public_key: [9u8; 32], ..beacon() };
        sender
            .send_to(&own.encode(), SocketAddr::from(([127, 0, 0, 1], DISCOVERY_PORT)))
            .await
            .unwrap();
        let accepted = tokio::time::timeout(Duration::from_secs(2), browser.recv_once())
            .await
            .expect("recv timed out")
            .unwrap();
        assert!(!accepted, "our own beacon must be ignored");
        assert_eq!(browser.peers().len(), 1, "self-echo did not add a peer");
    }

    #[tokio::test]
    async fn resolve_finds_the_current_endpoint_for_a_key() {
        // An advertiser for key [3;32] broadcasting on wg_port 51830.
        let advertiser = LanAdvertiser::new(Beacon { public_key: [3u8; 32], wg_port: 51830, ..beacon() })
            .unwrap();
        let beacon_task = tokio::spawn(advertiser.run());

        // resolve() from a different self_key should return the advertised endpoint port.
        let endpoint = LanBrowser::resolve([9u8; 32], [3u8; 32], Duration::from_secs(5))
            .await
            .expect("resolve should find the advertiser");
        assert_eq!(endpoint.port(), 51830);
        beacon_task.abort();
    }

    #[tokio::test]
    async fn resolve_times_out_when_the_key_is_absent() {
        // Nothing advertising key [5;32]; resolve must time out rather than hang.
        let r = LanBrowser::resolve([9u8; 32], [5u8; 32], Duration::from_millis(300)).await;
        assert!(r.is_err());
    }

    #[test]
    fn peers_prunes_stale_entries_and_keeps_fresh_ones() {
        // Test the freshness rule directly (no socket bind, so it never contends for the port).
        let mut peers: HashMap<[u8; 32], LanPeer> = HashMap::new();
        let now = Instant::now();
        peers.insert(
            [3u8; 32],
            LanPeer {
                public_key: [3u8; 32],
                callsign: "W1VE".into(),
                keyer_id: "40m".into(),
                endpoint: SocketAddr::from(([192, 168, 1, 5], 51820)),
                last_seen: now - Duration::from_secs(10), // stale
            },
        );
        peers.insert(
            [4u8; 32],
            LanPeer {
                public_key: [4u8; 32],
                callsign: "W1VE".into(),
                keyer_id: "20m".into(),
                endpoint: SocketAddr::from(([192, 168, 1, 6], 51821)),
                last_seen: now, // fresh
            },
        );
        let live = prune_and_sort(&mut peers, now);
        assert_eq!(live.len(), 1, "the stale peer is pruned, the fresh one kept");
        assert_eq!(live[0].display_name(), "W1VE-20m");
    }
}
