//! The tunnel's underlying transport: direct UDP, or the relay.
//!
//! [`WireGuardTunnel`](super::WireGuardTunnel) does not care how a WireGuard datagram reaches
//! the peer — only that one can be sent and one can be received. [`Wire`] is that seam: the
//! direct variant is a plain UDP socket to the peer's endpoint (the fast path), and the relay
//! variant is a [`RelayClient`] that carries the same opaque datagrams through a
//! [`RelayServer`](crate::engine::relay::RelayServer) when no direct path exists. The crypto
//! layer above is identical for both — a datagram is a datagram — so NAT-traversal fallback
//! costs the tunnel nothing but this enum.
//!
//! A received datagram carries an optional source address: UDP knows the sender's socket
//! address, which boringtun uses to validate and to learn a roaming endpoint; the relay does
//! not expose one (it addresses by key, not socket), so the relay path hands up [`None`] and
//! boringtun simply skips that check. That is sound here because the relay only ever delivers
//! this node its one configured peer's traffic.

use std::net::{IpAddr, SocketAddr};

use tokio::net::UdpSocket;

use crate::engine::relay::RelayClient;
use crate::engine::tunnel::MAX_WIREGUARD_DATAGRAM;
use crate::Result;

/// How the tunnel reaches its peer.
pub enum Wire {
    /// A direct UDP socket to the peer's endpoint — the fast path.
    Direct {
        /// The bound UDP socket.
        socket: UdpSocket,
        /// Where datagrams are sent; updatable for a roaming peer.
        peer: SocketAddr,
    },
    /// A relayed path: datagrams travel through the relay to the peer's key.
    Relay(RelayClient),
}

impl Wire {
    /// Sends one WireGuard datagram to the peer.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` (direct) or the relay client's error if the datagram
    /// cannot be put on the wire.
    pub async fn send(&self, datagram: &[u8]) -> Result<()> {
        match self {
            Wire::Direct { socket, peer } => {
                socket.send_to(datagram, *peer).await?;
                Ok(())
            }
            Wire::Relay(client) => client.send(datagram).await,
        }
    }

    /// Receives one WireGuard datagram, with the sender's socket address when the transport
    /// exposes one (UDP) or [`None`] when it does not (the relay).
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` on a direct socket read failure, or
    /// [`crate::Error::SerialIo`] if the relay connection has closed.
    pub async fn recv(&mut self) -> Result<(Vec<u8>, Option<IpAddr>)> {
        match self {
            Wire::Direct { socket, .. } => {
                let mut buf = vec![0u8; MAX_WIREGUARD_DATAGRAM];
                let (len, src) = socket.recv_from(&mut buf).await?;
                buf.truncate(len);
                Ok((buf, Some(src.ip())))
            }
            Wire::Relay(client) => match client.recv().await {
                Some(datagram) => Ok((datagram, None)),
                // A closed relay reads as end-of-stream; surface it as an I/O error so the
                // tunnel's receive-error path (and its F9 detector) treats it like a dead
                // socket rather than a silent stall.
                None => Err(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    "relay connection closed",
                )
                .into()),
            },
        }
    }

    /// The local socket address, for the direct path only.
    ///
    /// Returns [`None`] for a relayed tunnel, which has no peer-facing local socket of its own
    /// (its only socket is the TCP connection to the relay).
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if a direct socket's address cannot be read.
    pub fn local_addr(&self) -> Result<Option<SocketAddr>> {
        match self {
            Wire::Direct { socket, .. } => Ok(Some(socket.local_addr()?)),
            Wire::Relay(_) => Ok(None),
        }
    }

    /// The current peer endpoint, for the direct path only ([`None`] for the relay).
    #[must_use]
    pub fn peer(&self) -> Option<SocketAddr> {
        match self {
            Wire::Direct { peer, .. } => Some(*peer),
            Wire::Relay(_) => None,
        }
    }

    /// Moves the peer endpoint on the direct path; a no-op for the relay, which addresses by
    /// key and so has no endpoint to roam.
    pub fn set_peer(&mut self, endpoint: SocketAddr) {
        if let Wire::Direct { peer, .. } = self {
            *peer = endpoint;
        }
    }
}
