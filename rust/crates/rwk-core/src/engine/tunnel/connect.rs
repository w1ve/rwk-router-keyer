//! Bringing a tunnel up from [`Config`], choosing direct UDP or the relay automatically.
//!
//! Increments 1–3 left the caller to pick a transport by hand (`bind` vs `bind_relay`). This
//! is the glue that decides for them, the way `tsnet` did: try the fast path first, fall back
//! to the relay only when it fails.
//!
//! ```text
//!   peer has a direct endpoint?
//!     ├─ yes → bind UDP, handshake; established?  ──yes──▶ Direct
//!     │                              └─ no, and a relay is configured ─▶ try relay
//!     └─ no  → a relay is configured?  ──yes──▶ try relay
//!                                      └─ no  ──▶ error: nowhere to reach the peer
//! ```
//!
//! The relay attempt connects a [`RelayClient`], registers this node's key, scopes it to the
//! peer's key, and brings the identical WireGuard handshake up over it. Either way the result
//! is one established [`WireGuardTunnel`] plus the [`Path`] that won, so the UI can show
//! "Direct" or "Relay" exactly as the sidecar reported `PathType`.

use std::net::SocketAddr;

use tokio::net::UdpSocket;

use crate::config::{Config, Role};
use crate::engine::relay::{self, RelayClient};
use crate::engine::tunnel::{StaticIdentity, TunnelPeer, WireGuardTunnel};
use crate::primitives::PathType;
use crate::Result;

/// How long to attempt a direct hole-punch before giving up and relaying.
///
/// Long enough for both NATs to open mappings over several re-initiations, short enough that
/// a hard-NAT pair falls back to the (working) relay without a long stall.
const PUNCH_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(6);

/// How long a pairing rendezvous waits for the peer to present the same code.
///
/// Generous because the two operators press "connect" at different moments; the relay parks
/// the first arrival until the second shows up (bounded by the relay's own pairing TTL).
const PAIR_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(90);

/// How the established tunnel reached its peer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Path {
    /// A direct UDP path to the peer's endpoint.
    Direct,
    /// A relayed path through the configured relay.
    Relay,
}

impl Path {
    /// The [`PathType`] the UI reports, so the jitter buffer picks the right delay band.
    #[must_use]
    pub fn path_type(self) -> PathType {
        match self {
            Path::Direct => PathType::Direct,
            Path::Relay => PathType::Derp,
        }
    }
}

/// An established tunnel and the path it came up on.
pub struct Connected {
    /// The live tunnel, session already established.
    pub tunnel: WireGuardTunnel,
    /// Which transport won.
    pub path: Path,
}

/// Brings a tunnel up from `config`, trying direct first and falling back to the relay.
///
/// Intended for the initiating side (the Client); the responder listens with its own tunnel.
/// The direct attempt is bounded by WireGuard's handshake timeout, so a dead endpoint costs a
/// few seconds before the relay is tried, not an indefinite hang.
///
/// # Errors
///
/// Returns [`crate::Error::Config`] when neither path is usable: no direct endpoint and no
/// relay, or a malformed key, or both the direct and the relay attempts failed.
pub async fn connect(config: &Config) -> Result<Connected> {
    // Zero-setup path: a pairing code (with a relay) discovers the peer by rendezvous, no
    // pasted key or endpoint. This is the default experience; the manual peer fields below
    // are the advanced path for operators who prefer to configure keys/endpoints by hand.
    if let Some(code) = config.pairing_code.as_deref().filter(|c| !c.is_empty()) {
        return connect_paired(config, code).await;
    }

    let identity = config.identity.to_identity()?;
    // The Client initiates the handshake; the Station waits and answers. This mirrors the
    // one-Client-one-Station topology and avoids two simultaneous initiators racing.
    let role = config.role;

    // 1. Direct, if an endpoint is configured.
    if let Some(endpoint) = config.peer.endpoint {
        let peer = config.peer.to_tunnel_peer_with_endpoint(endpoint)?;
        match try_direct(&identity, &peer, role).await {
            Ok(tunnel) => return Ok(Connected { tunnel, path: Path::Direct }),
            Err(direct_err) => {
                // Fall through to the relay if one is configured; otherwise surface the direct
                // failure, since there is nowhere else to go.
                if config.relay.is_none() {
                    return Err(direct_err);
                }
            }
        }
    }

    // 2. Relay, if configured.
    if let Some(relay) = &config.relay {
        let tunnel = try_relay(config, &identity, relay.address, role).await?;
        return Ok(Connected { tunnel, path: Path::Relay });
    }

    Err(crate::Error::Config(
        "cannot reach the peer: no direct endpoint and no relay configured".into(),
    ))
}

/// Zero-setup connect: find the peer via a shared pairing `code` through the relay, attempt a
/// direct hole-punched path, and fall back to relaying if the punch fails.
///
/// This is the "no network setup" path: the operator never pastes a peer key or endpoint —
/// both ends enter the same short code, the relay introduces them (keys + observed
/// addresses), and the tunnel comes up directly when the NATs allow, over the relay when they
/// do not. The config needs only `relay` set.
///
/// # Errors
///
/// Returns [`crate::Error::Config`] when no relay is configured, the rendezvous times out, or
/// both the direct and relayed paths fail.
pub async fn connect_paired(config: &Config, code: &str) -> Result<Connected> {
    let identity = config.identity.to_identity()?;
    let Some(relay) = &config.relay else {
        return Err(crate::Error::Config(
            "pairing requires a relay to be configured".into(),
        ));
    };
    let relay_addr = relay.address;

    // 1. Bind our WireGuard UDP socket up front so we know the local port the NAT will map,
    //    and report that port in the rendezvous. All-interfaces, ephemeral port.
    let socket = UdpSocket::bind("0.0.0.0:0").await.map_err(crate::Error::from)?;
    let local_port = socket.local_addr().map_err(crate::Error::from)?.port();

    // 2. Rendezvous: present our key + UDP port + the code; learn the peer's key + observed
    //    address.
    let peer = relay::pair(relay_addr, identity.public_bytes(), local_port, code, PAIR_TIMEOUT)
        .await?;

    // 3. Try a direct hole-punch to the peer's observed address, reusing the bound socket.
    if let Some(peer_addr) = peer.addr {
        let tunnel_peer = TunnelPeer {
            public_key: peer.key,
            preshared_key: None,
            endpoint: peer_addr,
            persistent_keepalive: config.peer.persistent_keepalive,
        };
        let mut tunnel = WireGuardTunnel::from_socket(socket, &identity, &tunnel_peer);
        if tunnel.punch_session(PUNCH_TIMEOUT).await.is_ok() {
            return Ok(Connected { tunnel, path: Path::Direct });
        }
        // Punch failed (likely symmetric NAT on at least one end): fall through to relay.
        // The socket is consumed by the failed tunnel; the relay path opens its own.
    }

    // 4. Relay fallback: open a data-relay connection and bring the link up over it. The peer
    //    key came from pairing, so no pre-shared key paste was needed.
    let tunnel = try_relay_with_key(&identity, relay_addr, peer.key, config.role).await?;
    Ok(Connected { tunnel, path: Path::Relay })
}

/// Relay fallback for the paired flow: like [`try_relay`] but with the peer key learned from
/// the rendezvous rather than the config.
async fn try_relay_with_key(
    identity: &StaticIdentity,
    relay_addr: SocketAddr,
    peer_key: [u8; relay::KEY_LEN],
    role: Role,
) -> Result<WireGuardTunnel> {
    let client = RelayClient::connect(relay_addr, identity.public_bytes(), peer_key).await?;
    let placeholder: SocketAddr = "0.0.0.0:0".parse().expect("valid literal");
    let peer = TunnelPeer {
        public_key: peer_key,
        preshared_key: None,
        endpoint: placeholder,
        persistent_keepalive: None,
    };
    let mut tunnel = WireGuardTunnel::bind_relay(identity, &peer, client);
    establish_relay(&mut tunnel, role).await?;
    Ok(tunnel)
}

/// How long the Client keeps re-initiating the handshake over the relay fallback.
///
/// Longer than the direct-path [`HANDSHAKE_TIMEOUT`] because both ends enter the relay path
/// just after their hole-punch windows expire together, so the initiator must out-wait the
/// responder's relay registration settling, re-firing until it answers.
const RELAY_HANDSHAKE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(20);

/// Brings a relay-backed tunnel up: the Client re-initiates on the longer relay budget, the
/// Station waits patiently as always.
async fn establish_relay(tunnel: &mut WireGuardTunnel, role: Role) -> Result<()> {
    match role {
        Role::Client => tunnel.ensure_session_within(RELAY_HANDSHAKE_TIMEOUT).await,
        Role::Station => tunnel.await_session(RESPONDER_WAIT).await,
    }
}

/// How long the Station waits for the Client to open the link.
///
/// Generous because the two ends start independently — the Station may be up well before the
/// Client dials — so it must sit patiently answering handshake retries, not give up in a few
/// seconds the way an initiator does.
const RESPONDER_WAIT: std::time::Duration = std::time::Duration::from_secs(60);

/// Brings a bound tunnel up according to role: the Client initiates, the Station waits.
async fn establish(tunnel: &mut WireGuardTunnel, role: Role) -> Result<()> {
    match role {
        Role::Client => tunnel.ensure_session().await,
        Role::Station => tunnel.await_session(RESPONDER_WAIT).await,
    }
}

/// Binds a direct UDP tunnel and brings the session up for this role.
async fn try_direct(
    identity: &crate::engine::tunnel::StaticIdentity,
    peer: &TunnelPeer,
    role: Role,
) -> Result<WireGuardTunnel> {
    // Bind an ephemeral local port on all interfaces; the OS picks the source address that
    // routes to the peer.
    let local: SocketAddr = "0.0.0.0:0".parse().expect("valid literal");
    let mut tunnel = WireGuardTunnel::bind(local, identity, peer).await?;
    establish(&mut tunnel, role).await?;
    Ok(tunnel)
}

/// Connects to the relay and brings the session up over it for this role.
async fn try_relay(
    config: &Config,
    identity: &crate::engine::tunnel::StaticIdentity,
    relay_addr: SocketAddr,
    role: Role,
) -> Result<WireGuardTunnel> {
    let my_key = identity.public_bytes();
    let peer_key = config.peer.public_key_bytes()?;
    let client = RelayClient::connect(relay_addr, my_key, peer_key).await?;

    // The relay ignores the endpoint (it addresses by key), so a placeholder is fine.
    let placeholder: SocketAddr = "0.0.0.0:0".parse().expect("valid literal");
    let peer = config.peer.to_tunnel_peer_with_endpoint(placeholder)?;

    let mut tunnel = WireGuardTunnel::bind_relay(identity, &peer, client);
    establish(&mut tunnel, role).await?;
    Ok(tunnel)
}

// ─── LAN direct path (relay-free) ──────────────────────────────────────────────
//
// On a LAN the two ends find each other by UDP broadcast (see
// [`crate::engine::lan_discovery`]) instead of a relay. The radio site *browses* the
// advertised keyers and *picks* one, so — unlike WAN, where the Client initiates — on the LAN
// the **picker initiates** and the **advertiser responds**. boringtun needs each end to know
// the other's static key before the handshake: the picker learns the advertiser's key from the
// beacon, and a one-shot plaintext HELLO (the picker's key, sent to the advertiser's WireGuard
// port) tells the advertiser the picker's key. After that the normal WireGuard handshake runs
// with no relay and no NAT hole-punch.

/// How long the advertiser waits for a picker's HELLO before giving up a listen cycle.
const LAN_HELLO_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(120);

/// How long each side allows for the LAN WireGuard handshake once keys are exchanged.
const LAN_HANDSHAKE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10);

/// Magic prefix for the LAN connect HELLO datagram (picker → advertiser).
const LAN_HELLO_MAGIC: [u8; 4] = *b"RWKH";

/// Binds a WireGuard UDP socket on an ephemeral port for the LAN path, returning the socket
/// and the port the advertiser must announce in its beacon.
///
/// # Errors
///
/// Propagates a bind error.
pub async fn lan_bind() -> Result<(UdpSocket, u16)> {
    let socket = UdpSocket::bind("0.0.0.0:0").await.map_err(crate::Error::from)?;
    let port = socket.local_addr().map_err(crate::Error::from)?.port();
    Ok((socket, port))
}

/// The advertiser side of a LAN pairing: wait for a picker's HELLO on the already-bound
/// `socket` (whose port was advertised), learn the picker's key, then bring the WireGuard
/// link up as the responder. Returns the established tunnel.
///
/// `socket` must be the one from [`lan_bind`] whose port the beacon advertised, so the picker's
/// HELLO and handshake reach it.
///
/// # Errors
///
/// Returns [`crate::Error::Config`] if no HELLO arrives within [`LAN_HELLO_TIMEOUT`] or the
/// handshake does not complete; propagates socket errors.
pub async fn lan_await_initiator(
    socket: UdpSocket,
    identity: &StaticIdentity,
) -> Result<Connected> {
    // 1. Wait for the picker's HELLO, which carries its public key, and note where it came
    //    from (that is the picker's WireGuard endpoint).
    let (peer_key, peer_addr) = {
        let mut buf = vec![0u8; 64];
        let fut = async {
            loop {
                let (len, src) = socket.recv_from(&mut buf).await.map_err(crate::Error::from)?;
                if len >= 4 + 32 && buf[..4] == LAN_HELLO_MAGIC {
                    let mut key = [0u8; 32];
                    key.copy_from_slice(&buf[4..36]);
                    return Ok::<_, crate::Error>((key, src));
                }
                // Not a HELLO (a stray datagram); keep waiting.
            }
        };
        match tokio::time::timeout(LAN_HELLO_TIMEOUT, fut).await {
            Ok(result) => result?,
            Err(_) => {
                return Err(crate::Error::Config(
                    "no LAN connect request arrived; the picker never connected".into(),
                ))
            }
        }
    };

    // 2. Build the responder tunnel over the same socket, now that we know the picker's key
    //    and address, and answer its handshake.
    let peer = TunnelPeer {
        public_key: peer_key,
        preshared_key: None,
        endpoint: peer_addr,
        persistent_keepalive: Some(25),
    };
    let mut tunnel = WireGuardTunnel::from_socket(socket, identity, &peer);
    tunnel.await_session(LAN_HANDSHAKE_TIMEOUT).await?;
    Ok(Connected { tunnel, path: Path::Direct })
}

/// The picker side of a LAN pairing: send a HELLO announcing our key to the advertiser's
/// endpoint, then bring the WireGuard link up as the initiator. Returns the established
/// tunnel.
///
/// `peer_key` and `peer_endpoint` come from the chosen [`LanPeer`](crate::engine::lan_discovery::LanPeer).
///
/// # Errors
///
/// Returns [`crate::Error::Config`] if the handshake does not complete; propagates socket
/// errors.
pub async fn lan_connect_to(
    identity: &StaticIdentity,
    peer_key: [u8; relay::KEY_LEN],
    peer_endpoint: SocketAddr,
) -> Result<Connected> {
    let socket = UdpSocket::bind("0.0.0.0:0").await.map_err(crate::Error::from)?;

    // 1. Tell the advertiser our key so it can build its responder tunnel. Send a few times:
    //    the first datagram can race the advertiser arriving at its recv.
    let mut hello = Vec::with_capacity(36);
    hello.extend_from_slice(&LAN_HELLO_MAGIC);
    hello.extend_from_slice(&identity.public_bytes());
    for _ in 0..5 {
        let _ = socket.send_to(&hello, peer_endpoint).await;
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }

    // 2. Bring the link up as the initiator.
    let peer = TunnelPeer {
        public_key: peer_key,
        preshared_key: None,
        endpoint: peer_endpoint,
        persistent_keepalive: Some(25),
    };
    let mut tunnel = WireGuardTunnel::from_socket(socket, identity, &peer);
    tunnel.ensure_session_within(LAN_HANDSHAKE_TIMEOUT).await?;
    Ok(Connected { tunnel, path: Path::Direct })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{IdentityConfig, PeerConfig, RelayConfig};
    use crate::engine::relay::RelayServer;
    use crate::engine::tunnel::WireGuardTunnel;
    use std::time::Duration;

    /// Builds a client config pointing at `peer_pub`, with an optional direct endpoint and an
    /// optional relay.
    fn client_config(
        peer_pub: &str,
        endpoint: Option<SocketAddr>,
        relay: Option<SocketAddr>,
    ) -> Config {
        let mut config = Config::new(Role::Client).unwrap();
        config.peer = PeerConfig {
            public_key: peer_pub.to_string(),
            endpoint,
            preshared_key: None,
            persistent_keepalive: None,
        };
        config.relay = relay.map(|address| RelayConfig { address });
        config
    }

    #[tokio::test]
    async fn connect_errors_when_neither_path_is_configured() {
        let peer_pub = IdentityConfig::generate().unwrap().public_key_base64().unwrap();
        let config = client_config(&peer_pub, None, None);
        match connect(&config).await {
            Err(crate::Error::Config(_)) => {}
            Err(other) => panic!("unexpected error: {other}"),
            Ok(_) => panic!("connect must fail with neither path configured"),
        }
    }

    #[tokio::test]
    async fn connect_falls_back_to_the_relay_when_direct_fails() {
        // Stand up a relay and a responder that answers over it.
        let server = RelayServer::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let relay_addr = server.local_addr().unwrap();
        tokio::spawn(async move {
            let _ = server.run().await;
        });

        // The responder (Station) side: its own identity and a relay client scoped to the
        // initiator. We drive it in the background so the initiator's handshake completes.
        let initiator_id = IdentityConfig::generate().unwrap();
        let responder_id = IdentityConfig::generate().unwrap();
        let initiator_pub = initiator_id.public_key_base64().unwrap();

        // Build the initiator's config: a *dead* direct endpoint (an address that will never
        // answer) plus the working relay, so the direct attempt fails fast and the relay wins.
        let mut config = client_config(
            &responder_id.public_key_base64().unwrap(),
            // An IPv6 discard address: an IPv4 send to it fails quickly on this host, so the
            // direct handshake errors rather than stalling the whole test.
            None,
            Some(relay_addr),
        );
        config.identity = initiator_id.clone();

        // Spawn the responder: connect to the relay, bind_relay, and poll to answer.
        {
            let r_id = responder_id.to_identity().unwrap();
            let r_peer = crate::engine::relay::RelayClient::connect(
                relay_addr,
                r_id.public_bytes(),
                initiator_id.to_identity().unwrap().public_bytes(),
            )
            .await
            .unwrap();
            let peer = PeerConfig {
                public_key: initiator_pub,
                endpoint: None,
                preshared_key: None,
                persistent_keepalive: None,
            }
            .to_tunnel_peer_with_endpoint("0.0.0.0:0".parse().unwrap())
            .unwrap();
            let mut responder = WireGuardTunnel::bind_relay(&r_id, &peer, r_peer);
            tokio::spawn(async move {
                // Answer protocol traffic until the session is up and a bit beyond.
                for _ in 0..200 {
                    if tokio::time::timeout(Duration::from_millis(50), responder.poll())
                        .await
                        .is_err()
                    {
                        // No datagram this slice; keep waiting.
                    }
                }
            });
        }

        // Give both relay registrations a moment.
        tokio::time::sleep(Duration::from_millis(50)).await;

        let outcome = tokio::time::timeout(Duration::from_secs(8), connect(&config))
            .await
            .expect("connect timed out");
        let connected = match outcome {
            Ok(c) => c,
            Err(e) => panic!("connect should fall back to the relay: {e}"),
        };
        assert_eq!(connected.path, Path::Relay);
        assert_eq!(connected.path.path_type(), PathType::Derp);
        assert!(connected.tunnel.is_established());
    }

    #[tokio::test]
    async fn two_ends_pair_by_code_and_connect_directly() {
        // A relay for the rendezvous only; on loopback the "observed address" is 127.0.0.1
        // plus each end's real UDP port, so the direct hole-punch actually reaches the peer
        // and the direct path wins (no relaying needed).
        let server = RelayServer::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let relay_addr = server.local_addr().unwrap();
        tokio::spawn(async move {
            let _ = server.run().await;
        });

        // Two configs sharing a relay and a pairing code — but NO peer key or endpoint set,
        // which is the whole point: zero manual setup.
        let mut client_cfg = Config::new(Role::Client).unwrap();
        client_cfg.relay = Some(RelayConfig { address: relay_addr });
        let mut station_cfg = Config::new(Role::Station).unwrap();
        station_cfg.relay = Some(RelayConfig { address: relay_addr });

        let code = "FIELD-DAY-73";
        // Both call connect_paired concurrently with the same code.
        let c = {
            let cfg = client_cfg.clone();
            let code = code.to_string();
            tokio::spawn(async move { connect_paired(&cfg, &code).await })
        };
        let s = {
            let cfg = station_cfg.clone();
            let code = code.to_string();
            tokio::spawn(async move { connect_paired(&cfg, &code).await })
        };

        let (c_res, s_res) = tokio::join!(c, s);
        let c_conn = match c_res.unwrap() {
            Ok(x) => x,
            Err(e) => panic!("client pairing connect failed: {e}"),
        };
        let s_conn = match s_res.unwrap() {
            Ok(x) => x,
            Err(e) => panic!("station pairing connect failed: {e}"),
        };

        assert!(c_conn.tunnel.is_established(), "client link up");
        assert!(s_conn.tunnel.is_established(), "station link up");
        // On loopback the punch succeeds, so both report a Direct path.
        assert_eq!(c_conn.path, Path::Direct, "loopback punch yields a direct path");
        assert_eq!(s_conn.path, Path::Direct);
    }

    #[tokio::test]
    async fn pairing_connect_without_a_relay_errors() {
        let config = Config::new(Role::Client).unwrap(); // no relay
        assert!(connect_paired(&config, "ANY").await.is_err());
    }

    #[tokio::test]
    async fn lan_picker_and_advertiser_establish_a_direct_link() {
        // The advertiser (operating position) binds its WG socket and waits; the picker (radio
        // site) sends a HELLO to that port and initiates. Both end up with an established
        // direct tunnel over loopback — the relay-free LAN path.
        let advertiser_id = IdentityConfig::generate().unwrap().to_identity().unwrap();
        let picker_id = IdentityConfig::generate().unwrap().to_identity().unwrap();

        let (adv_socket, adv_port) = lan_bind().await.unwrap();
        let adv_endpoint: SocketAddr = format!("127.0.0.1:{adv_port}").parse().unwrap();

        // Advertiser awaits the initiator in the background.
        let adv_fut = {
            let id = advertiser_id.clone();
            tokio::spawn(async move { lan_await_initiator(adv_socket, &id).await })
        };
        // Give the advertiser a beat to reach its recv.
        tokio::time::sleep(Duration::from_millis(50)).await;

        let adv_key = advertiser_id.public_bytes();
        let picker = lan_connect_to(&picker_id, adv_key, adv_endpoint).await;
        let picker = picker.expect("picker should establish the direct link");
        assert_eq!(picker.path, Path::Direct);
        assert!(picker.tunnel.is_established(), "picker link up");

        let advertiser = adv_fut.await.unwrap().expect("advertiser should establish");
        assert_eq!(advertiser.path, Path::Direct);
        assert!(advertiser.tunnel.is_established(), "advertiser link up");
    }
}
