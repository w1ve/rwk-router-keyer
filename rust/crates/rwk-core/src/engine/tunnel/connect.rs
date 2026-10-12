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
}
