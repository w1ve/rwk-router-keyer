//! Port forwarders that ride the tunnel — the local side on a host socket, the remote side
//! on a [`NetStackHandle`] socket.
//!
//! The forwarders in [`network`](crate::engine::network) relay between two host-OS sockets:
//! they are the LAN / hardware-VPN path, and the shape the sidecar's `out` / `in` kinds had.
//! These two types are their tunnel-riding counterparts. The local application still connects
//! to a real `127.0.0.1:port` socket — it is a real process on the operator's PC — but the
//! far leg is a socket on the userspace stack, so its bytes travel encrypted over WireGuard
//! to the Station, which dials the actual LAN device.
//!
//! ```text
//!   local app ──TCP/UDP──▶ 127.0.0.1:port   (host OS socket, this module binds it)
//!                              │ copy loop
//!                              ▼
//!                      NetStackHandle socket ──▶ driver ──▶ WireGuard ──▶ Station LAN
//! ```
//!
//! This module has both ends of a forward:
//!
//! * [`TunnelTcpForward`] / [`TunnelUdpForward`] — the **Client** side: bind a local host
//!   socket, carry its traffic over the tunnel to the Station's inner address.
//! * [`StationTcpResponder`] / [`StationUdpResponder`] — the **Station** side: listen on the
//!   tunnel's inner address and dial the real LAN device, so a forwarded connection reaches
//!   the radio/rotator/switch it is meant for.
//!
//! ```text
//!   local app ─▶ 127.0.0.1:port ─▶ TunnelForward ─▶ tunnel ─▶ StationResponder ─▶ LAN device
//! ```

use std::net::SocketAddr;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream, UdpSocket};
use tokio::sync::watch;

use crate::engine::netstack_driver::NetStackHandle;
use crate::engine::network::{MAX_FORWARD_DATAGRAM, TCP_ACCEPT_BACKOFF, TCP_FORWARD_BUFFER};
use crate::Result;

/// An outbound TCP forward: a local listener whose connections are relayed, over the tunnel,
/// to `(station_ip, station_port)` on the Station's inner network.
///
/// This is the sidecar's `out` kind once the tunnel is native: bind `127.0.0.1:client_port`,
/// and for each accepted connection open a tunnel socket to the Station and pump both ways.
pub struct TunnelTcpForward {
    listener: TcpListener,
    handle: NetStackHandle,
    station_ip: std::net::IpAddr,
    station_port: u16,
}

impl TunnelTcpForward {
    /// Binds the local listener; connections are forwarded to the Station over `handle`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the local listener cannot be bound.
    pub async fn bind(
        local: SocketAddr,
        handle: NetStackHandle,
        station: (std::net::IpAddr, u16),
    ) -> Result<Self> {
        let listener = TcpListener::bind(local).await?;
        Ok(Self { listener, handle, station_ip: station.0, station_port: station.1 })
    }

    /// The local address an application should connect to.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the local address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.listener.local_addr()?)
    }

    /// Accepts and relays connections until `shutdown` flips to `true`.
    ///
    /// Each accepted connection is pumped on its own task, so a long-lived control channel
    /// never blocks a new connection — the same property the host-socket `TcpForwarder` has.
    ///
    /// # Errors
    ///
    /// Infallible for a bound listener; a transient `accept` failure is backed off and retried.
    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<()> {
        loop {
            tokio::select! {
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        return Ok(());
                    }
                }
                accepted = self.listener.accept() => {
                    match accepted {
                        Ok((stream, _peer)) => {
                            let handle = self.handle.clone();
                            let station = (self.station_ip, self.station_port);
                            tokio::spawn(async move {
                                let _ = relay_tcp(stream, handle, station).await;
                            });
                        }
                        Err(_) => tokio::time::sleep(TCP_ACCEPT_BACKOFF).await,
                    }
                }
            }
        }
    }
}

/// Relays one accepted host connection to a tunnel socket, copying both ways until either
/// end closes. A local source port of 0 lets smoltcp pick an ephemeral port per connection.
async fn relay_tcp(
    local: TcpStream,
    handle: NetStackHandle,
    station: (std::net::IpAddr, u16),
) -> Result<()> {
    let _ = local.set_nodelay(true);
    // Dial the Station over the tunnel. A unique ephemeral source port per connection keeps
    // concurrent sessions from colliding on the stack's single inner address.
    let local_port = ephemeral_port();
    let conn = handle.connect_tcp(station, local_port).await?;
    // Split so the two copy directions can run as independent tasks without a borrow clash.
    let (sender, mut receiver) = conn.split();
    let (mut local_read, mut local_write) = local.into_split();

    // local -> tunnel
    let up = async {
        let mut buf = vec![0u8; TCP_FORWARD_BUFFER];
        loop {
            match local_read.read(&mut buf).await {
                Ok(0) | Err(_) => break,
                Ok(n) => {
                    if sender.send(buf[..n].to_vec()).await.is_err() {
                        break;
                    }
                }
            }
        }
    };
    // tunnel -> local
    let down = async {
        while let Some(chunk) = receiver.recv().await {
            if local_write.write_all(&chunk).await.is_err() {
                break;
            }
        }
        let _ = local_write.shutdown().await;
    };

    // Both directions run until their own end closes; a half-close on one must not cut the
    // other, so this is a join rather than a select.
    tokio::join!(up, down);
    Ok(())
}

/// An outbound UDP forward: a local socket whose datagrams are relayed, over the tunnel, to
/// `(station_ip, station_port)`, with replies sent back to the last local sender.
///
/// This is the sidecar's `out-udp` kind, used for Flex SmartSDR command (`4992/udp`) and
/// VITA-49 (`4991/udp`). Single-session, matching the point-to-point traffic these carry.
pub struct TunnelUdpForward {
    local: UdpSocket,
    handle: NetStackHandle,
    station: (std::net::IpAddr, u16),
}

impl TunnelUdpForward {
    /// Binds the local UDP socket; datagrams are forwarded to the Station over `handle`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the local socket cannot be bound.
    pub async fn bind(
        local: SocketAddr,
        handle: NetStackHandle,
        station: (std::net::IpAddr, u16),
    ) -> Result<Self> {
        let local = UdpSocket::bind(local).await?;
        Ok(Self { local, handle, station })
    }

    /// The local address an application should send to.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the local address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.local.local_addr()?)
    }

    /// Relays datagrams both ways until `shutdown` flips to `true`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the tunnel socket cannot be opened; otherwise
    /// propagates `std::io::Error` from the local socket.
    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<()> {
        // One tunnel UDP socket serves the whole forward; the local source address is tracked
        // so a reply routes back to whoever last sent, as the host-socket relay does. Port 0
        // lets the stack assign the inner source port.
        let mut conn = self.handle.open_udp(ephemeral_port()).await?;
        let mut last_sender: Option<SocketAddr> = None;
        let mut inbound = vec![0u8; MAX_FORWARD_DATAGRAM];

        loop {
            tokio::select! {
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        return Ok(());
                    }
                }
                read = self.local.recv_from(&mut inbound) => {
                    let (len, src) = read?;
                    last_sender = Some(src);
                    // Drop a datagram the tunnel cannot currently accept rather than stalling
                    // the relay; UDP is lossy by contract and the application will retry.
                    let _ = conn.send(inbound[..len].to_vec(), self.station).await;
                }
                reply = conn.recv() => {
                    match reply {
                        Some((data, _from)) => {
                            if let Some(dest) = last_sender {
                                self.local.send_to(&data, dest).await?;
                            }
                        }
                        // The driver stopped; nothing more will arrive.
                        None => return Ok(()),
                    }
                }
            }
        }
    }
}

/// The Station end of a TCP forward: accept connections arriving on the tunnel's inner
/// address and dial the real LAN device for each.
///
/// This is the mirror of [`TunnelTcpForward`]. Where the Client binds a host socket and dials
/// the tunnel, the Station listens on the tunnel ([`NetStackHandle::listen_tcp`]) and dials a
/// host socket — the actual radio/rotator at `target` on the Station LAN.
pub struct StationTcpResponder {
    handle: NetStackHandle,
    inner_port: u16,
    target: SocketAddr,
}

impl StationTcpResponder {
    /// Prepares a responder that accepts on `inner_port` and dials `target` on the LAN.
    #[must_use]
    pub fn new(handle: NetStackHandle, inner_port: u16, target: SocketAddr) -> Self {
        Self { handle, inner_port, target }
    }

    /// Serves forwarded connections until `shutdown` flips to `true`.
    ///
    /// The netstack listener is single-session (one `listen_tcp` yields one stream), so this
    /// re-arms a fresh listener after each connection ends — matching how a station radio
    /// expects a new control connection after the previous one closes. The point-to-point
    /// traffic these forwards carry is one active connection at a time.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the netstack listener cannot be opened.
    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<()> {
        loop {
            // Open a listener for the next inbound connection, unless we are shutting down.
            let conn = tokio::select! {
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        return Ok(());
                    }
                    continue;
                }
                opened = self.handle.listen_tcp(self.inner_port) => opened?,
            };

            // Dial the LAN device; a refused dial just drops this connection and re-arms.
            let lan = match TcpStream::connect(self.target).await {
                Ok(stream) => stream,
                Err(_) => continue,
            };
            let _ = lan.set_nodelay(true);
            relay_conn_to_host(conn, lan).await;
        }
    }
}

/// Pumps a netstack TCP connection and a host-OS stream both ways until either closes.
async fn relay_conn_to_host(conn: crate::engine::netstack_driver::TcpConn, host: TcpStream) {
    let (sender, mut receiver) = conn.split();
    let (mut host_read, mut host_write) = host.into_split();

    // tunnel -> LAN
    let down = async {
        while let Some(chunk) = receiver.recv().await {
            if host_write.write_all(&chunk).await.is_err() {
                break;
            }
        }
        let _ = host_write.shutdown().await;
    };
    // LAN -> tunnel
    let up = async {
        let mut buf = vec![0u8; TCP_FORWARD_BUFFER];
        loop {
            match host_read.read(&mut buf).await {
                Ok(0) | Err(_) => break,
                Ok(n) => {
                    if sender.send(buf[..n].to_vec()).await.is_err() {
                        break;
                    }
                }
            }
        }
    };
    tokio::join!(down, up);
}

/// The Station end of a UDP forward: relay datagrams arriving on the tunnel's inner address to
/// the real LAN device, and the device's replies back over the tunnel.
///
/// The mirror of [`TunnelUdpForward`]: the Station opens a netstack UDP socket on the inner
/// port and bridges it to a host UDP socket talking to `target`.
pub struct StationUdpResponder {
    handle: NetStackHandle,
    inner_port: u16,
    target: SocketAddr,
}

impl StationUdpResponder {
    /// Prepares a responder that receives on `inner_port` and relays to `target` on the LAN.
    #[must_use]
    pub fn new(handle: NetStackHandle, inner_port: u16, target: SocketAddr) -> Self {
        Self { handle, inner_port, target }
    }

    /// Relays datagrams both ways until `shutdown` flips to `true`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the netstack socket cannot be opened, or propagates
    /// an `std::io::Error` from the host socket.
    pub async fn run(self, mut shutdown: watch::Receiver<bool>) -> Result<()> {
        let mut conn = self.handle.open_udp(self.inner_port).await?;
        // The host socket the LAN device sees as the source of forwarded datagrams.
        let host = UdpSocket::bind("0.0.0.0:0").await?;
        // The inner peer (the Client's forward) to send replies back to; learned on first recv.
        let mut inner_peer: Option<(std::net::IpAddr, u16)> = None;
        let mut from_lan = vec![0u8; MAX_FORWARD_DATAGRAM];

        loop {
            tokio::select! {
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        return Ok(());
                    }
                }
                // From the tunnel → to the LAN device.
                inbound = conn.recv() => {
                    match inbound {
                        Some((data, src)) => {
                            inner_peer = Some(src);
                            let _ = host.send_to(&data, self.target).await;
                        }
                        None => return Ok(()), // driver stopped
                    }
                }
                // From the LAN device → back over the tunnel to the inner peer.
                read = host.recv_from(&mut from_lan) => {
                    let (len, _) = read?;
                    if let Some(peer) = inner_peer {
                        let _ = conn.send(from_lan[..len].to_vec(), peer).await;
                    }
                }
            }
        }
    }
}

/// Picks an ephemeral inner source port in the IANA dynamic range.
///
/// The stack has one inner address, so concurrent forwards must not reuse a source port;
/// drawing from the OS RNG makes a collision between a handful of live forwards vanishingly
/// unlikely without a shared allocator. A future increment can replace this with a counter
/// owned by the driver if the forward count ever grows large.
fn ephemeral_port() -> u16 {
    let mut bytes = [0u8; 2];
    // A failure of the OS RNG is implausible here and not worth propagating through every
    // forward; fall back to a fixed high port, which is still valid.
    if getrandom::fill(&mut bytes).is_err() {
        return 49_152;
    }
    49_152 + (u16::from_le_bytes(bytes) % (65_535 - 49_152))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::netstack::NetStack;
    use crate::engine::netstack_driver::NetDriver;
    use crate::engine::tunnel::{StaticIdentity, TunnelPeer, WireGuardTunnel};
    use std::net::{IpAddr, Ipv4Addr};
    use std::time::Duration;

    fn identity(seed: u8) -> StaticIdentity {
        let mut bytes = [0u8; 32];
        for (i, b) in bytes.iter_mut().enumerate() {
            *b = seed.wrapping_mul(31).wrapping_add(i as u8);
        }
        StaticIdentity::from_private_bytes(bytes)
    }
    fn peer(public_key: [u8; 32], endpoint: SocketAddr) -> TunnelPeer {
        TunnelPeer { public_key, preshared_key: None, endpoint, persistent_keepalive: None }
    }

    const CLIENT_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 1);
    const STATION_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 2);

    /// Two linked drivers over a real tunnel: the Client end and the Station end. Returns a
    /// handle to each.
    async fn linked() -> (NetStackHandle, NetStackHandle) {
        let c_id = identity(51);
        let s_id = identity(52);
        let mut c_tun = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &c_id,
            &peer(s_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
        )
        .await
        .unwrap();
        let mut s_tun = WireGuardTunnel::bind(
            "127.0.0.1:0".parse().unwrap(),
            &s_id,
            &peer(c_id.public_bytes(), c_tun.local_addr().unwrap()),
        )
        .await
        .unwrap();
        c_tun.set_peer(s_tun.local_addr().unwrap());
        c_tun.initiate().await.unwrap();
        s_tun.poll().await.unwrap();
        c_tun.poll().await.unwrap();
        s_tun.poll().await.unwrap();

        let client = NetDriver::spawn(c_tun, NetStack::new(IpAddr::V4(CLIENT_IP)));
        let station = NetDriver::spawn(s_tun, NetStack::new(IpAddr::V4(STATION_IP)));
        (client, station)
    }

    #[tokio::test]
    async fn tcp_forward_bridges_a_host_connection_to_a_tunnel_socket() {
        let (client, station) = linked().await;

        // The Station end: a listener on the inner network standing in for the far radio,
        // reached via a plain netstack socket. It echoes what it reads.
        let mut radio = station.listen_tcp(8000).await.unwrap();
        tokio::spawn(async move {
            if let Some(bytes) = radio.recv().await {
                let _ = radio.send(bytes).await;
            }
        });

        // The Client end: a host-OS listener bridged to the Station's inner (10.0.0.2:8000).
        let forward = TunnelTcpForward::bind(
            "127.0.0.1:0".parse().unwrap(),
            client,
            (IpAddr::V4(STATION_IP), 8000),
        )
        .await
        .unwrap();
        let local = forward.local_addr().unwrap();
        let (tx, rx) = watch::channel(false);
        let task = tokio::spawn(forward.run(rx));

        // A local application connects to the host socket and gets its bytes echoed back from
        // across the tunnel.
        let mut app = TcpStream::connect(local).await.unwrap();
        app.write_all(b"cq test").await.unwrap();
        let mut buf = [0u8; 16];
        let n = tokio::time::timeout(Duration::from_secs(5), app.read(&mut buf))
            .await
            .expect("echo did not return across the tunnel")
            .unwrap();
        assert_eq!(&buf[..n], b"cq test");

        tx.send(true).unwrap();
        let _ = tokio::time::timeout(Duration::from_secs(2), task).await;
    }

    #[tokio::test]
    async fn udp_forward_bridges_a_host_datagram_to_a_tunnel_socket() {
        let (client, station) = linked().await;

        // The Station end: a UDP socket on the inner network that echoes a datagram back.
        let mut radio = station.open_udp(4991).await.unwrap();
        tokio::spawn(async move {
            if let Some((bytes, from)) = radio.recv().await {
                let _ = radio.send(bytes, from).await;
            }
        });

        let forward = TunnelUdpForward::bind(
            "127.0.0.1:0".parse().unwrap(),
            client,
            (IpAddr::V4(STATION_IP), 4991),
        )
        .await
        .unwrap();
        let local = forward.local_addr().unwrap();
        let (tx, rx) = watch::channel(false);
        let task = tokio::spawn(forward.run(rx));

        let app = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        app.send_to(b"vita", local).await.unwrap();
        let mut buf = [0u8; 16];
        let (n, _) = tokio::time::timeout(Duration::from_secs(5), app.recv_from(&mut buf))
            .await
            .expect("reply did not return across the tunnel")
            .unwrap();
        assert_eq!(&buf[..n], b"vita");

        tx.send(true).unwrap();
        let _ = tokio::time::timeout(Duration::from_secs(2), task).await;
    }
}
