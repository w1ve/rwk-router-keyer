//! FlexRadio discovery relay: host-side UDP capture and re-broadcast.
//!
//! This is the LAN half of the Flex relay; [`flex_vita`](crate::protocol::flex_vita) is the
//! packet half. A FlexRadio announces itself with a UDP broadcast to port 4992 on its own
//! LAN, which a SmartSDR client normally hears directly. When the radio is at the remote
//! Station and the operator is elsewhere, those broadcasts never cross the internet — so RWK
//! relays them:
//!
//! 1. **Station** listens for the radio's discovery broadcasts on its LAN (UDP 4992) with
//!    [`DiscoveryListener`], validates each as a Flex packet, and forwards the raw bytes to
//!    the Client over the control channel.
//! 2. **Client** receives the raw packet, rewrites the `ip=`/`port=` fields to point at the
//!    forwarded local endpoint ([`flex_vita::rewrite_endpoint`]), and re-broadcasts it on the
//!    operating-position LAN with [`DiscoveryEmitter`], so SmartSDR there discovers the radio.
//!
//! Both ends use ordinary host-OS UDP sockets with `SO_REUSEADDR` (several apps bind 4992 at
//! once — SmartSDR and this listener coexist), not the tunnel: the discovery traffic is
//! strictly LAN-local on each side, and only the captured packet bytes travel the tunnel (on
//! the control channel). The actual command/stream traffic rides the port forwards.

use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, UdpSocket};

use socket2::{Domain, Protocol, Socket, Type};

use crate::protocol::flex_vita::{self, DISCOVERY_PORT};
use crate::Result;

/// Binds a UDP socket to `port` on all interfaces with address reuse and broadcast enabled.
///
/// `SO_REUSEADDR` (and `SO_REUSEPORT` where it exists) lets this socket share port 4992 with
/// SmartSDR or another listener, which is required: we are a *second* listener on the radio's
/// discovery port, not its owner.
fn bind_reuse_broadcast(port: u16) -> Result<UdpSocket> {
    let socket = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))
        .map_err(crate::Error::from)?;
    socket.set_reuse_address(true).map_err(crate::Error::from)?;
    #[cfg(unix)]
    {
        let _ = socket.set_reuse_port(true);
    }
    socket.set_broadcast(true).map_err(crate::Error::from)?;
    let addr: SocketAddr = SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, port));
    socket.bind(&addr.into()).map_err(crate::Error::from)?;
    Ok(socket.into())
}

/// Listens for FlexRadio discovery broadcasts on the Station LAN (UDP 4992).
///
/// The Station runs one of these while Flex forwarding is enabled, feeding each validated
/// packet to the control channel. It owns a blocking host UDP socket, read on a dedicated
/// thread by the caller (the socket is cloneable so a stop can be signalled by dropping).
pub struct DiscoveryListener {
    socket: UdpSocket,
}

impl DiscoveryListener {
    /// Binds the discovery listener on UDP 4992 (all interfaces, address reuse).
    ///
    /// # Errors
    ///
    /// Returns an error if the socket cannot be created or bound.
    pub fn bind() -> Result<Self> {
        Ok(Self { socket: bind_reuse_broadcast(DISCOVERY_PORT)? })
    }

    /// Sets a read timeout so the listening loop can periodically check a stop flag.
    ///
    /// # Errors
    ///
    /// Returns an error if the timeout cannot be set.
    pub fn set_read_timeout(&self, dur: Option<std::time::Duration>) -> Result<()> {
        self.socket.set_read_timeout(dur).map_err(crate::Error::from)
    }

    /// Blocks for the next datagram and returns it if it is a valid Flex discovery packet.
    ///
    /// Returns `Ok(Some(bytes))` for a valid discovery packet, `Ok(None)` for a datagram that
    /// is not one (ignored), and an error only for a socket failure (including a timeout,
    /// which the caller treats as "loop and re-check the stop flag").
    ///
    /// # Errors
    ///
    /// Propagates the socket read error (a `WouldBlock`/`TimedOut` on timeout).
    pub fn recv_discovery(&self) -> Result<Option<Vec<u8>>> {
        let mut buf = vec![0u8; 2048];
        let (len, _from) = self.socket.recv_from(&mut buf).map_err(crate::Error::from)?;
        buf.truncate(len);
        // Only forward packets that actually parse as Flex discovery.
        if flex_vita::parse(&buf).is_ok() {
            Ok(Some(buf))
        } else {
            Ok(None)
        }
    }
}

/// Re-broadcasts rewritten FlexRadio discovery packets on the operating-position LAN.
///
/// The Client runs one of these while Flex re-emission is enabled. It rewrites each captured
/// packet's endpoint to the local forwarded address and broadcasts it so a SmartSDR client on
/// the Client's LAN discovers the (remote) radio as if it were local.
pub struct DiscoveryEmitter {
    socket: UdpSocket,
}

impl DiscoveryEmitter {
    /// Creates an emitter bound to an ephemeral source port with broadcast enabled.
    ///
    /// # Errors
    ///
    /// Returns an error if the socket cannot be created.
    pub fn new() -> Result<Self> {
        // Ephemeral source port; the destination is the broadcast address:4992.
        let socket = bind_reuse_broadcast(0)?;
        Ok(Self { socket })
    }

    /// Rewrites `packet`'s endpoint to `local:port` and broadcasts it to 255.255.255.255:4992.
    ///
    /// # Errors
    ///
    /// Returns an error if the rewrite fails or the datagram cannot be sent.
    pub fn rebroadcast(&self, packet: &[u8], local: Ipv4Addr, port: u16) -> Result<()> {
        let rewritten = flex_vita::rewrite_endpoint(packet, local, port)
            .map_err(crate::Error::Protocol)?;
        let dest: SocketAddr =
            SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::BROADCAST, DISCOVERY_PORT));
        self.socket.send_to(&rewritten, dest).map_err(crate::Error::from)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_listener_binds_and_sets_a_timeout() {
        // Binding 4992 with reuse should succeed even if something else holds it.
        let listener = DiscoveryListener::bind().expect("bind discovery listener");
        listener
            .set_read_timeout(Some(std::time::Duration::from_millis(10)))
            .expect("set timeout");
        // A read now times out quickly (nothing is broadcasting in the test).
        let r = listener.recv_discovery();
        assert!(r.is_err(), "an idle socket read should time out");
    }

    #[test]
    fn an_emitter_can_be_created() {
        let _ = DiscoveryEmitter::new().expect("create emitter");
    }

    #[test]
    fn emitter_rejects_a_non_flex_packet() {
        let emitter = DiscoveryEmitter::new().unwrap();
        // Garbage is not a valid discovery packet, so the rewrite (and thus rebroadcast) fails.
        let r = emitter.rebroadcast(&[0u8; 40], Ipv4Addr::LOCALHOST, 4992);
        assert!(r.is_err());
    }
}
