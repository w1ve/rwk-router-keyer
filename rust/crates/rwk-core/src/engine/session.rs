//! A running session: the netstack and forwarders wired over a connected tunnel.
//!
//! Increments 1–4 built the transport and the config; this is the piece that turns a
//! [`Connected`](crate::engine::tunnel::Connected) tunnel plus a [`Config`] into live traffic.
//! A [`Session`] owns the [`NetDriver`] that drives the stack over the tunnel, and the set of
//! forward tasks the config's rules describe:
//!
//! * On the **Client**, each enabled rule binds a [`TunnelTcpForward`] / [`TunnelUdpForward`]
//!   on `127.0.0.1:client_port`, carrying local traffic over the tunnel to the Station's
//!   inner address and `station_port`.
//! * On the **Station**, each enabled rule starts a [`StationTcpResponder`] /
//!   [`StationUdpResponder`] listening on the inner address and `station_port`, dialing the
//!   real LAN device at `station_target_address`.
//!
//! So a config pair (mirror-image roles, matching rules) makes `rwk connect` on both ends
//! carry an application's connection end to end: `local app → Client forward → tunnel →
//! Station responder → LAN device`, and back.
//!
//! The session holds a `watch` shutdown channel; [`Session::shutdown`] flips it and every
//! forward task returns, which is what `rwk connect` calls on Ctrl-C.

use std::net::{IpAddr, SocketAddr};

use tokio::sync::watch;
use tokio::task::JoinHandle;

use crate::config::{Config, ForwardRuleConfig, Role};
use crate::engine::netstack::NetStack;
use crate::engine::netstack_driver::{NetDriver, NetStackHandle};
use crate::engine::tunnel::Connected;
use crate::engine::tunnel_forward::{
    StationTcpResponder, StationUdpResponder, TunnelTcpForward, TunnelUdpForward,
};
use crate::Result;

/// A live session over a connected tunnel.
///
/// Keep it alive for as long as the link should carry traffic; dropping it (or calling
/// [`shutdown`](Self::shutdown)) stops every forward task. The underlying [`NetDriver`] runs
/// until the last handle is dropped, which happens when the session is dropped.
pub struct Session {
    role: Role,
    /// Flipped to tear every forward task down.
    shutdown: watch::Sender<bool>,
    /// The forward/responder tasks, kept so they are not detached and can be awaited on stop.
    tasks: Vec<JoinHandle<()>>,
    /// Held so the driver keeps running for the session's lifetime; cloning it starts sockets.
    _handle: NetStackHandle,
    /// How many forward rules were actually started (enabled and well-formed).
    started: usize,
}

impl Session {
    /// Builds a session from an established tunnel and the config that describes its forwards.
    ///
    /// Spawns the [`NetDriver`] on the current tokio runtime, then starts a forward task per
    /// enabled rule, choosing the Client or Station shape by [`Config::role`]. A malformed
    /// rule (an address that will not parse) is skipped rather than failing the whole session,
    /// so one bad entry cannot keep the keyer offline; the count of started rules is reported
    /// by [`started`](Self::started).
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] only for a failure that dooms the whole session (today
    /// none past building the driver); per-rule problems are skipped.
    pub fn start(connected: Connected, config: &Config) -> Result<Self> {
        let role = config.role;
        let inner = IpAddr::V4(role.inner_address());
        let handle = NetDriver::spawn(connected.tunnel, NetStack::new(inner));

        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let mut tasks = Vec::new();
        let mut started = 0usize;

        for rule in config.forwards.iter().filter(|r| r.enabled) {
            match start_rule(role, &handle, rule, &shutdown_rx) {
                Ok(Some(task)) => {
                    tasks.push(task);
                    started += 1;
                }
                // A rule that does not apply to this role (none today) or a skip.
                Ok(None) => {}
                // A malformed rule: skip it, keep the session up for the others and the keyer.
                Err(_) => {}
            }
        }

        Ok(Self { role, shutdown: shutdown_tx, tasks, _handle: handle, started })
    }

    /// This session's role.
    #[must_use]
    pub fn role(&self) -> Role {
        self.role
    }

    /// How many forward rules were started.
    #[must_use]
    pub fn started(&self) -> usize {
        self.started
    }

    /// A handle onto the session's netstack, for a caller that wants to open its own sockets
    /// (e.g. to wire the keying edge transport over the same tunnel later).
    #[must_use]
    pub fn handle(&self) -> NetStackHandle {
        self._handle.clone()
    }

    /// Stops every forward task and waits for them to finish.
    ///
    /// Idempotent: a second call is a no-op. The driver stops once the session (and any cloned
    /// handles) are dropped.
    pub async fn shutdown(&mut self) {
        let _ = self.shutdown.send(true);
        for task in self.tasks.drain(..) {
            let _ = task.await;
        }
    }
}

/// Starts one forward rule, returning its task (or `None` if it does not apply here).
fn start_rule(
    role: Role,
    handle: &NetStackHandle,
    rule: &ForwardRuleConfig,
    shutdown: &watch::Receiver<bool>,
) -> Result<Option<JoinHandle<()>>> {
    let is_udp = rule.protocol.eq_ignore_ascii_case("udp");
    let peer_inner = IpAddr::V4(role.peer_inner_address());

    let task = match role {
        Role::Client => {
            // Bind the local host socket and carry it to the Station's inner address.
            let local: SocketAddr = format!("127.0.0.1:{}", rule.client_port)
                .parse()
                .map_err(|e| crate::Error::Config(format!("bad client bind: {e}")))?;
            let station = (peer_inner, rule.station_port);
            let handle = handle.clone();
            let shutdown = shutdown.clone();
            if is_udp {
                spawn_async(async move {
                    if let Ok(fwd) = TunnelUdpForward::bind(local, handle, station).await {
                        let _ = fwd.run(shutdown).await;
                    }
                })
            } else {
                spawn_async(async move {
                    if let Ok(fwd) = TunnelTcpForward::bind(local, handle, station).await {
                        let _ = fwd.run(shutdown).await;
                    }
                })
            }
        }
        Role::Station => {
            // Listen on the inner address and dial the real LAN device.
            let target: SocketAddr = format!("{}:{}", rule.station_target_address, rule.station_port)
                .parse()
                .map_err(|e| crate::Error::Config(format!("bad station target: {e}")))?;
            let handle = handle.clone();
            let shutdown = shutdown.clone();
            let port = rule.station_port;
            if is_udp {
                spawn_async(async move {
                    let _ = StationUdpResponder::new(handle, port, target).run(shutdown).await;
                })
            } else {
                spawn_async(async move {
                    let _ = StationTcpResponder::new(handle, port, target).run(shutdown).await;
                })
            }
        }
    };
    Ok(Some(task))
}

/// Spawns a unit future, discarding its (already-logged-or-ignored) result.
fn spawn_async<F>(fut: F) -> JoinHandle<()>
where
    F: std::future::Future<Output = ()> + Send + 'static,
{
    tokio::spawn(fut)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{ForwardRuleConfig, IdentityConfig, PeerConfig, RelayConfig};
    use crate::engine::relay::RelayServer;
    use crate::engine::tunnel::{connect, Path};
    use std::time::Duration;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::{TcpListener, TcpStream, UdpSocket};

    /// A TCP forward rule for `port`, targeting `target` on the Station LAN.
    fn tcp_rule(port: u16, target: &str) -> ForwardRuleConfig {
        ForwardRuleConfig {
            id: format!("tcp-{port}"),
            name: "test tcp".into(),
            protocol: "tcp".into(),
            client_port: port,
            station_port: port,
            enabled: true,
            station_target_address: target.into(),
        }
    }

    /// A UDP forward rule for `port`, targeting `target` on the Station LAN.
    fn udp_rule(port: u16, target: &str) -> ForwardRuleConfig {
        ForwardRuleConfig {
            id: format!("udp-{port}"),
            name: "test udp".into(),
            protocol: "udp".into(),
            client_port: port,
            station_port: port,
            enabled: true,
            station_target_address: target.into(),
        }
    }

    /// Brings up a Client and Station session over a relay, with the given forward rules on
    /// both ends. Returns both sessions so they stay alive for the test.
    async fn linked_sessions(
        client_rules: Vec<ForwardRuleConfig>,
        station_rules: Vec<ForwardRuleConfig>,
    ) -> (Session, Session) {
        // A relay both ends meet at.
        let server = RelayServer::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let relay_addr = server.local_addr().unwrap();
        tokio::spawn(async move {
            let _ = server.run().await;
        });

        let client_id = IdentityConfig::generate().unwrap();
        let station_id = IdentityConfig::generate().unwrap();

        let mut client_cfg = Config::new(Role::Client).unwrap();
        client_cfg.identity = client_id.clone();
        client_cfg.peer = PeerConfig {
            public_key: station_id.public_key_base64().unwrap(),
            endpoint: None,
            preshared_key: None,
            persistent_keepalive: None,
        };
        client_cfg.relay = Some(RelayConfig { address: relay_addr });
        client_cfg.forwards = client_rules;

        let mut station_cfg = Config::new(Role::Station).unwrap();
        station_cfg.identity = station_id.clone();
        station_cfg.peer = PeerConfig {
            public_key: client_id.public_key_base64().unwrap(),
            endpoint: None,
            preshared_key: None,
            persistent_keepalive: None,
        };
        station_cfg.relay = Some(RelayConfig { address: relay_addr });
        station_cfg.forwards = station_rules;

        // Connect both ends concurrently: the Station waits, the Client initiates.
        let station_fut = {
            let cfg = station_cfg.clone();
            tokio::spawn(async move { connect(&cfg).await })
        };
        tokio::time::sleep(Duration::from_millis(50)).await;
        let client_connected = connect(&client_cfg).await.expect("client connect");
        let station_connected = station_fut.await.unwrap().expect("station connect");
        assert_eq!(client_connected.path, Path::Relay);

        let client = Session::start(client_connected, &client_cfg).unwrap();
        let station = Session::start(station_connected, &station_cfg).unwrap();
        (client, station)
    }

    #[tokio::test]
    async fn a_tcp_forward_rule_carries_a_connection_end_to_end() {
        // The "radio" on the Station LAN: a TCP echo server on a real host port.
        let radio = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let radio_port = radio.local_addr().unwrap().port();
        tokio::spawn(async move {
            if let Ok((mut s, _)) = radio.accept().await {
                let mut buf = [0u8; 64];
                if let Ok(n) = s.read(&mut buf).await {
                    let _ = s.write_all(&buf[..n]).await;
                    let _ = s.flush().await;
                }
            }
        });

        // Client binds 127.0.0.1:<radio_port>; Station dials 127.0.0.1:<radio_port> (the echo).
        let rule = tcp_rule(radio_port, "127.0.0.1");
        let (_client, _station) =
            linked_sessions(vec![rule.clone()], vec![rule]).await;

        // A local app connects to the Client's forward and must get its bytes echoed from the
        // Station-side "radio", across the tunnel.
        let app_addr: SocketAddr = format!("127.0.0.1:{radio_port}").parse().unwrap();
        // Retry the connect briefly: the forward's listener comes up just after the session.
        let mut app = None;
        for _ in 0..50 {
            if let Ok(s) = TcpStream::connect(app_addr).await {
                app = Some(s);
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        let mut app = app.expect("could not reach the client forward");
        app.write_all(b"cq de w1ve").await.unwrap();
        let mut buf = [0u8; 64];
        let n = tokio::time::timeout(Duration::from_secs(8), app.read(&mut buf))
            .await
            .expect("echo did not return across the tunnel")
            .unwrap();
        assert_eq!(&buf[..n], b"cq de w1ve");
    }

    #[tokio::test]
    async fn a_udp_forward_rule_carries_a_datagram_end_to_end() {
        // The Station-LAN "radio": a UDP echo socket.
        let radio = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let radio_port = radio.local_addr().unwrap().port();
        tokio::spawn(async move {
            let mut buf = [0u8; 64];
            if let Ok((n, from)) = radio.recv_from(&mut buf).await {
                let _ = radio.send_to(&buf[..n], from).await;
            }
        });

        let rule = udp_rule(radio_port, "127.0.0.1");
        let (_client, _station) = linked_sessions(vec![rule.clone()], vec![rule]).await;

        let app = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let fwd_addr: SocketAddr = format!("127.0.0.1:{radio_port}").parse().unwrap();

        // UDP has no connection to retry; send a few times until the reply comes back, since
        // the forward may still be arming when the first datagram goes out.
        let mut got = None;
        for _ in 0..50 {
            app.send_to(b"599 tu", fwd_addr).await.unwrap();
            let mut buf = [0u8; 64];
            match tokio::time::timeout(Duration::from_millis(200), app.recv_from(&mut buf)).await {
                Ok(Ok((n, _))) => {
                    got = Some(buf[..n].to_vec());
                    break;
                }
                _ => continue,
            }
        }
        assert_eq!(got.expect("no UDP echo returned across the tunnel"), b"599 tu");
    }

    #[tokio::test]
    async fn a_malformed_rule_is_skipped_not_fatal() {
        // One good rule and one with an unparseable target: the session must still start the
        // good one.
        let good = tcp_rule(15001, "127.0.0.1");
        let bad = ForwardRuleConfig {
            station_target_address: "this is not an address".into(),
            ..tcp_rule(15002, "x")
        };
        let (_client, station) = linked_sessions(vec![], vec![good, bad]).await;
        assert_eq!(station.started(), 1, "the good rule starts, the bad one is skipped");
    }
}
