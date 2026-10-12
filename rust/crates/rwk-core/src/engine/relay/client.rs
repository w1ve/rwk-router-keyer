//! The relay client: a datagram channel to one peer, carried over the relay.
//!
//! A [`RelayClient`] connects to a [`RelayServer`](super::server::RelayServer), registers this
//! node's public key, and then behaves like a datagram pipe to one peer: [`send`](RelayClient::send)
//! wraps a WireGuard datagram in a [`Frame::Send`] addressed to the peer's key, and
//! [`recv`](RelayClient::recv) returns the payload of each [`Frame::Recv`] the relay delivers.
//!
//! That send/recv shape is deliberately the same one a UDP socket offers the WireGuard tunnel,
//! so the tunnel can run over the relay exactly as it runs over direct UDP — the relay is a
//! drop-in transport, not a special case the crypto layer has to know about.
//!
//! A background task owns the TCP read half and feeds an inbound queue; `send` writes straight
//! to the write half behind a lock. The connection is one peer's worth of traffic, so a single
//! queue and a single writer are all it needs.

use std::sync::Arc;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::OwnedWriteHalf;
use tokio::net::TcpStream;
use tokio::sync::{mpsc, Mutex};

use super::frame::{parse_header, Frame, HEADER_LEN, KEY_LEN};
use crate::Result;

/// Depth of the inbound datagram queue filled by the reader task.
const INBOUND_DEPTH: usize = 1024;

/// A connection to the relay, scoped to one peer.
pub struct RelayClient {
    /// The write half, shared so `send` can be called from any task.
    writer: Arc<Mutex<OwnedWriteHalf>>,
    /// The peer every `send` is addressed to.
    peer_key: [u8; KEY_LEN],
    /// Datagrams delivered by the relay, filled by the reader task.
    inbound: mpsc::Receiver<Vec<u8>>,
}

/// The peer the relay introduced us to, from a [`pair`] rendezvous.
#[derive(Debug, Clone)]
pub struct PairedPeer {
    /// The peer's WireGuard public key.
    pub key: [u8; KEY_LEN],
    /// The peer's observed address (relay-seen public IP + reported UDP port), for a direct
    /// hole-punch attempt. `None` if the relay could not determine it.
    pub addr: Option<std::net::SocketAddr>,
}

/// Rendezvous: connect to the relay, present `code` + our identity, and wait for the relay to
/// introduce the peer holding the same code.
///
/// This is a one-shot signalling exchange on its own short-lived connection — it does not
/// `Register` for relaying. The caller uses the returned [`PairedPeer`] to attempt a direct
/// WireGuard path, and (if that fails) opens a normal [`RelayClient::connect`] for fallback.
///
/// `udp_port` is the local UDP port this node will run WireGuard on; the relay combines it
/// with our observed public IP so the peer knows where to punch.
///
/// # Errors
///
/// Propagates `std::io::Error` if the relay cannot be reached, and
/// [`crate::Error::Protocol`] if the relay closes before introducing a peer or sends an
/// unexpected frame. Honours `timeout`: a peer that never shows up yields a timeout error.
pub async fn pair(
    relay_addr: std::net::SocketAddr,
    my_key: [u8; KEY_LEN],
    udp_port: u16,
    code: &str,
    timeout: std::time::Duration,
) -> Result<PairedPeer> {
    let stream = TcpStream::connect(relay_addr).await?;
    let _ = stream.set_nodelay(true);
    let (mut read_half, mut write_half) = stream.into_split();

    let pair = Frame::Pair { key: my_key, udp_port, code: code.to_string() };
    write_half.write_all(&pair.encode()).await?;

    // Wait for the relay's introduction, bounded by the timeout.
    let fut = async {
        loop {
            match read_frame(&mut read_half).await? {
                Some(Frame::Paired { peer_key, peer_addr }) => {
                    return Ok(PairedPeer { key: peer_key, addr: peer_addr });
                }
                // The relay sends nothing else on a pairing connection; ignore stragglers.
                Some(_) => {}
                None => {
                    return Err(crate::Error::Protocol(
                        "relay closed before introducing a peer".into(),
                    ))
                }
            }
        }
    };
    match tokio::time::timeout(timeout, fut).await {
        Ok(result) => result,
        Err(_) => Err(crate::Error::Protocol(
            "timed out waiting for the relay to introduce a peer".into(),
        )),
    }
}

impl RelayClient {
    /// Connects to the relay at `relay_addr`, registers `my_key`, and scopes the channel to
    /// `peer_key`.
    ///
    /// Registration is a single [`Frame::Register`]; there is no reply to await, since the
    /// relay has no login step. The first datagram may be sent immediately.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the relay cannot be reached or the registration cannot
    /// be written.
    pub async fn connect(
        relay_addr: std::net::SocketAddr,
        my_key: [u8; KEY_LEN],
        peer_key: [u8; KEY_LEN],
    ) -> Result<Self> {
        let stream = TcpStream::connect(relay_addr).await?;
        let _ = stream.set_nodelay(true);
        let (mut read_half, mut write_half) = stream.into_split();

        // Announce our key so the relay delivers our peer's traffic to this connection.
        write_half.write_all(&Frame::Register { key: my_key }.encode()).await?;

        let (in_tx, in_rx) = mpsc::channel::<Vec<u8>>(INBOUND_DEPTH);
        tokio::spawn(async move {
            loop {
                match read_frame(&mut read_half).await {
                    // Deliver a received datagram; stop if the consumer is gone.
                    Ok(Some(Frame::Recv { payload, .. })) => {
                        if in_tx.send(payload).await.is_err() {
                            break;
                        }
                    }
                    // The relay only sends Recv (and, in future, keepalives); ignore the rest.
                    Ok(Some(_)) => {}
                    // Clean disconnect or a read/parse error both end the reader.
                    Ok(None) | Err(_) => break,
                }
            }
        });

        Ok(Self { writer: Arc::new(Mutex::new(write_half)), peer_key, inbound: in_rx })
    }

    /// Sends one datagram to the peer, over the relay.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the frame cannot be written to the relay.
    pub async fn send(&self, datagram: &[u8]) -> Result<()> {
        let frame = Frame::Send { dst: self.peer_key, payload: datagram.to_vec() };
        let mut writer = self.writer.lock().await;
        writer.write_all(&frame.encode()).await?;
        Ok(())
    }

    /// Awaits the next datagram the relay delivers, or [`None`] once the connection has ended.
    pub async fn recv(&mut self) -> Option<Vec<u8>> {
        self.inbound.recv().await
    }

    /// A cloneable sender for this connection, so a tunnel task can send from one place while
    /// another owns `recv`.
    #[must_use]
    pub fn sender(&self) -> RelaySender {
        RelaySender { writer: Arc::clone(&self.writer), peer_key: self.peer_key }
    }
}

/// The send half of a [`RelayClient`], cheap to clone.
#[derive(Clone)]
pub struct RelaySender {
    writer: Arc<Mutex<OwnedWriteHalf>>,
    peer_key: [u8; KEY_LEN],
}

impl RelaySender {
    /// Sends one datagram to the peer. See [`RelayClient::send`].
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the frame cannot be written.
    pub async fn send(&self, datagram: &[u8]) -> Result<()> {
        let frame = Frame::Send { dst: self.peer_key, payload: datagram.to_vec() };
        let mut writer = self.writer.lock().await;
        writer.write_all(&frame.encode()).await?;
        Ok(())
    }
}

/// Reads one complete frame from the relay, or [`None`] at a clean end of stream.
async fn read_frame<R>(read_half: &mut R) -> Result<Option<Frame>>
where
    R: AsyncReadExt + Unpin,
{
    let mut header = [0u8; HEADER_LEN];
    match read_half.read_exact(&mut header).await {
        Ok(_) => {}
        Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e.into()),
    }
    let (ty, len) = parse_header(&header).map_err(|e| crate::Error::Protocol(e.to_string()))?;
    let body = if len == 0 {
        Vec::new()
    } else {
        let mut body = vec![0u8; len];
        read_half.read_exact(&mut body).await?;
        body
    };
    Frame::decode_body(ty, &body)
        .map(Some)
        .map_err(|e| crate::Error::Protocol(e.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::relay::server::RelayServer;
    use std::time::Duration;

    const A_KEY: [u8; KEY_LEN] = [0xA1; KEY_LEN];
    const B_KEY: [u8; KEY_LEN] = [0xB2; KEY_LEN];

    /// Spawns a relay on an ephemeral port and returns its address.
    async fn spawn_relay() -> std::net::SocketAddr {
        let server = RelayServer::bind("127.0.0.1:0".parse().unwrap()).await.unwrap();
        let addr = server.local_addr().unwrap();
        tokio::spawn(async move {
            let _ = server.run().await;
        });
        addr
    }

    #[tokio::test]
    async fn two_clients_exchange_a_datagram_through_the_relay() {
        let relay = spawn_relay().await;

        // A registers as A, talks to B; B registers as B, talks to A.
        let a = RelayClient::connect(relay, A_KEY, B_KEY).await.unwrap();
        let mut b = RelayClient::connect(relay, B_KEY, A_KEY).await.unwrap();

        // Give B's registration a moment to reach the relay before A sends, so the route
        // exists. (A production tunnel retransmits, so this ordering is a test convenience.)
        tokio::time::sleep(Duration::from_millis(50)).await;

        a.send(b"wireguard-handshake").await.unwrap();
        let got = tokio::time::timeout(Duration::from_secs(2), b.recv())
            .await
            .expect("datagram did not arrive through the relay")
            .expect("relay connection closed");
        assert_eq!(got, b"wireguard-handshake");

        // Reply direction.
        b.send(b"handshake-response").await.unwrap();
        let mut a = a;
        let reply = tokio::time::timeout(Duration::from_secs(2), a.recv())
            .await
            .expect("reply did not arrive")
            .expect("relay connection closed");
        assert_eq!(reply, b"handshake-response");
    }

    #[tokio::test]
    async fn a_datagram_to_an_unregistered_peer_is_dropped_not_fatal() {
        let relay = spawn_relay().await;
        // A talks to B, but B never connects. The send must succeed (it reaches the relay);
        // the relay simply has nowhere to deliver it, which must not error the sender.
        let a = RelayClient::connect(relay, A_KEY, B_KEY).await.unwrap();
        assert!(a.send(b"into the void").await.is_ok());
    }

    #[tokio::test]
    async fn two_nodes_with_the_same_code_are_introduced() {
        let relay = spawn_relay().await;
        let code = "W1VE-TEST";

        // Both ends rendezvous on the same code; each learns the other's key.
        let a_fut = pair(relay, A_KEY, 51820, code, Duration::from_secs(5));
        let b_fut = pair(relay, B_KEY, 51821, code, Duration::from_secs(5));
        let (a_res, b_res) = tokio::join!(a_fut, b_fut);

        let a_peer = a_res.expect("A should be introduced to B");
        let b_peer = b_res.expect("B should be introduced to A");

        assert_eq!(a_peer.key, B_KEY, "A learns B's key");
        assert_eq!(b_peer.key, A_KEY, "B learns A's key");
        // The observed address carries the reported UDP port (loopback IP here).
        assert_eq!(a_peer.addr.unwrap().port(), 51821, "A sees B's reported UDP port");
        assert_eq!(b_peer.addr.unwrap().port(), 51820, "B sees A's reported UDP port");
    }

    #[tokio::test]
    async fn a_lone_pairing_request_times_out() {
        let relay = spawn_relay().await;
        // Only one side presents the code; no partner ever arrives.
        let r = pair(relay, A_KEY, 51820, "LONELY", Duration::from_millis(300)).await;
        assert!(r.is_err(), "a pairing with no partner must time out, not hang");
    }

    #[tokio::test]
    async fn different_codes_do_not_match() {
        let relay = spawn_relay().await;
        let a = pair(relay, A_KEY, 1, "CODE-A", Duration::from_millis(400));
        let b = pair(relay, B_KEY, 2, "CODE-B", Duration::from_millis(400));
        let (a_res, b_res) = tokio::join!(a, b);
        assert!(a_res.is_err() && b_res.is_err(), "mismatched codes must not introduce");
    }
}
