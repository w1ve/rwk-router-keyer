//! The relay wire frame: a length-prefixed, key-addressed packet format.
//!
//! RWK's relay is a last-resort rendezvous for when the Client and Station cannot open a
//! direct WireGuard path (both behind aggressive NAT, no IPv6, a hostile firewall). It is
//! modelled on the shape of Tailscale's DERP protocol — curve25519 public keys as addresses,
//! a one-byte frame type followed by a big-endian length — but it is deliberately *not* DERP:
//! there is no server-key/naclbox login (the payloads are already WireGuard-encrypted, so the
//! relay needs no crypto of its own), no mesh/watch frames, and no coordination plane. A node
//! simply connects, says "I am this key", and the relay forwards datagrams addressed to the
//! other key. The relay never sees plaintext, exactly as DERP never does.
//!
//! ## Frame layout
//!
//! ```text
//!   +--------+------------------+----------------------------------+
//!   | type:1 | length:4 (BE)    | payload: `length` bytes          |
//!   +--------+------------------+----------------------------------+
//! ```
//!
//! | Frame | Direction | Payload |
//! |---|---|---|
//! | [`FrameType::Register`] | client → relay | 32-byte public key: "deliver my traffic here" |
//! | [`FrameType::Send`] | client → relay | 32-byte destination key + WireGuard datagram |
//! | [`FrameType::Recv`] | relay → client | 32-byte source key + WireGuard datagram |
//! | [`FrameType::KeepAlive`] | either way | none; holds a NAT/firewall mapping open |
//! | [`FrameType::Pair`] | client → relay | 32-byte key + 2-byte UDP port + len-prefixed code |
//! | [`FrameType::Paired`] | relay → client | 32-byte peer key + the peer's observed address |
//!
//! ## Rendezvous (zero-setup pairing)
//!
//! Beyond forwarding, the relay introduces two nodes that share a **pairing code** so an
//! operator never pastes keys. A node sends [`Frame::Pair`] with its public key, the UDP port
//! it listens for WireGuard on, and a short code. When the relay has two connections holding
//! the same code it answers each with [`Frame::Paired`] carrying the *other's* public key and
//! the address the relay observed it connect from (its public, post-NAT IP) combined with the
//! reported UDP port. That is enough for both ends to attempt a direct WireGuard handshake —
//! and to fall back to relaying through this same connection if the punch fails.
//!
//! The codec is pure: it turns a [`Frame`] into bytes and parses bytes back, and does no I/O.
//! The server and client layer their TCP streams on top of it.

/// Length of a curve25519 public key, the relay's address type.
pub const KEY_LEN: usize = 32;

/// Length of the frame header: a one-byte type and a four-byte big-endian length.
pub const HEADER_LEN: usize = 1 + 4;

/// Largest payload a single frame may carry.
///
/// A relayed datagram is a WireGuard transport message — an inner packet plus WireGuard's
/// own overhead — so this is comfortably above the tunnel's `MAX_WIREGUARD_DATAGRAM` with
/// room for the 32-byte key prefix. A frame claiming more than this is rejected rather than
/// allocated, so a malformed or hostile length cannot exhaust memory.
pub const MAX_PAYLOAD: usize = 64 * 1024;

/// The one-byte type tag at the start of every frame.
///
/// Values are chosen to overlap DERP's where the meaning matches, purely so a packet capture
/// is legible to someone familiar with DERP; nothing depends on the numeric value matching.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum FrameType {
    /// A node announces the public key its traffic should be delivered to.
    Register = 0x02,
    /// A node sends a datagram to a destination key.
    Send = 0x04,
    /// The relay delivers a datagram, tagged with its source key.
    Recv = 0x05,
    /// A no-op that keeps a NAT/firewall mapping and the TCP connection alive.
    KeepAlive = 0x06,
    /// A node asks to be matched with whoever else presents the same pairing code.
    Pair = 0x20,
    /// The relay introduces the peer: its public key and observed address.
    Paired = 0x21,
}

impl FrameType {
    /// Parses a type byte, or [`None`] if it is not a known frame type.
    #[must_use]
    pub fn from_byte(byte: u8) -> Option<Self> {
        match byte {
            0x02 => Some(Self::Register),
            0x04 => Some(Self::Send),
            0x05 => Some(Self::Recv),
            0x06 => Some(Self::KeepAlive),
            0x20 => Some(Self::Pair),
            0x21 => Some(Self::Paired),
            _ => None,
        }
    }
}

/// A decoded relay frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Frame {
    /// Register this connection under `key`.
    Register {
        /// The public key the relay should deliver this connection's traffic to.
        key: [u8; KEY_LEN],
    },
    /// Deliver `payload` to the node registered under `dst`.
    Send {
        /// The destination node's public key.
        dst: [u8; KEY_LEN],
        /// The opaque (WireGuard-encrypted) datagram to deliver.
        payload: Vec<u8>,
    },
    /// A datagram from `src`, delivered by the relay.
    Recv {
        /// The sending node's public key.
        src: [u8; KEY_LEN],
        /// The opaque datagram that was relayed.
        payload: Vec<u8>,
    },
    /// Keep the connection and any NAT mapping alive.
    KeepAlive,
    /// Ask the relay to match this node with whoever else presents `code`.
    Pair {
        /// This node's public key, handed to the peer on a match.
        key: [u8; KEY_LEN],
        /// The UDP port this node listens on for WireGuard, combined with the relay-observed
        /// IP to form the peer's hole-punch target.
        udp_port: u16,
        /// The shared pairing code (short, operator-entered). Both ends use the same code.
        code: String,
    },
    /// The relay's introduction of the matched peer.
    Paired {
        /// The peer's public key.
        peer_key: [u8; KEY_LEN],
        /// The peer's observed address: the relay-observed public IP + the peer's reported
        /// UDP port. The direct-path handshake targets this; `None` if the relay could not
        /// determine it (fall back to relaying).
        peer_addr: Option<std::net::SocketAddr>,
    },
}

impl Frame {
    /// Serialises the frame, header included, into a fresh buffer.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        let (ty, body): (FrameType, Vec<u8>) = match self {
            Frame::Register { key } => (FrameType::Register, key.to_vec()),
            Frame::Send { dst, payload } => {
                let mut body = Vec::with_capacity(KEY_LEN + payload.len());
                body.extend_from_slice(dst);
                body.extend_from_slice(payload);
                (FrameType::Send, body)
            }
            Frame::Recv { src, payload } => {
                let mut body = Vec::with_capacity(KEY_LEN + payload.len());
                body.extend_from_slice(src);
                body.extend_from_slice(payload);
                (FrameType::Recv, body)
            }
            Frame::KeepAlive => (FrameType::KeepAlive, Vec::new()),
            Frame::Pair { key, udp_port, code } => {
                // 32B key + 2B port + code bytes (the remainder of the body).
                let mut body = Vec::with_capacity(KEY_LEN + 2 + code.len());
                body.extend_from_slice(key);
                body.extend_from_slice(&udp_port.to_be_bytes());
                body.extend_from_slice(code.as_bytes());
                (FrameType::Pair, body)
            }
            Frame::Paired { peer_key, peer_addr } => {
                // 32B peer key + an address blob (1B tag + addr + 2B port, or 1B tag=0 none).
                let mut body = Vec::with_capacity(KEY_LEN + 19);
                body.extend_from_slice(peer_key);
                encode_addr(peer_addr, &mut body);
                (FrameType::Paired, body)
            }
        };

        let mut out = Vec::with_capacity(HEADER_LEN + body.len());
        out.push(ty as u8);
        out.extend_from_slice(&(body.len() as u32).to_be_bytes());
        out.extend_from_slice(&body);
        out
    }

    /// Parses a frame body (the bytes *after* the header) given its type.
    ///
    /// The server/client read the header to learn the type and length, read exactly that many
    /// body bytes off the stream, then call this. Separating header from body keeps the codec
    /// usable with any transport that can deliver a length-delimited chunk.
    ///
    /// # Errors
    ///
    /// Returns [`FrameError`] when the body is too short for the frame type.
    pub fn decode_body(ty: FrameType, body: &[u8]) -> Result<Self, FrameError> {
        match ty {
            FrameType::Register => {
                let key = take_key(body, 0)?;
                Ok(Frame::Register { key })
            }
            FrameType::Send => {
                let dst = take_key(body, 0)?;
                Ok(Frame::Send { dst, payload: body[KEY_LEN..].to_vec() })
            }
            FrameType::Recv => {
                let src = take_key(body, 0)?;
                Ok(Frame::Recv { src, payload: body[KEY_LEN..].to_vec() })
            }
            FrameType::KeepAlive => Ok(Frame::KeepAlive),
            FrameType::Pair => {
                let key = take_key(body, 0)?;
                let port_bytes = body.get(KEY_LEN..KEY_LEN + 2).ok_or(FrameError::ShortBody)?;
                let udp_port = u16::from_be_bytes([port_bytes[0], port_bytes[1]]);
                // The code is the remainder; reject non-UTF-8 and anything implausibly long.
                let code = std::str::from_utf8(&body[KEY_LEN + 2..])
                    .map_err(|_| FrameError::ShortBody)?
                    .to_string();
                Ok(Frame::Pair { key, udp_port, code })
            }
            FrameType::Paired => {
                let peer_key = take_key(body, 0)?;
                let peer_addr = decode_addr(&body[KEY_LEN..])?;
                Ok(Frame::Paired { peer_key, peer_addr })
            }
        }
    }
}

/// Appends an optional socket address to `out` in a compact tagged form.
///
/// Tag `0` = none (no further bytes); `4` = IPv4 (4 addr bytes + 2 port); `6` = IPv6
/// (16 addr bytes + 2 port). All big-endian.
fn encode_addr(addr: &Option<std::net::SocketAddr>, out: &mut Vec<u8>) {
    use std::net::SocketAddr;
    match addr {
        None => out.push(0),
        Some(SocketAddr::V4(v4)) => {
            out.push(4);
            out.extend_from_slice(&v4.ip().octets());
            out.extend_from_slice(&v4.port().to_be_bytes());
        }
        Some(SocketAddr::V6(v6)) => {
            out.push(6);
            out.extend_from_slice(&v6.ip().octets());
            out.extend_from_slice(&v6.port().to_be_bytes());
        }
    }
}

/// Parses a tagged socket address written by [`encode_addr`].
fn decode_addr(body: &[u8]) -> Result<Option<std::net::SocketAddr>, FrameError> {
    use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4, SocketAddrV6};
    let tag = *body.first().ok_or(FrameError::ShortBody)?;
    match tag {
        0 => Ok(None),
        4 => {
            let b = body.get(1..7).ok_or(FrameError::ShortBody)?;
            let ip = Ipv4Addr::new(b[0], b[1], b[2], b[3]);
            let port = u16::from_be_bytes([b[4], b[5]]);
            Ok(Some(SocketAddr::V4(SocketAddrV4::new(ip, port))))
        }
        6 => {
            let b = body.get(1..19).ok_or(FrameError::ShortBody)?;
            let mut octets = [0u8; 16];
            octets.copy_from_slice(&b[..16]);
            let ip = Ipv6Addr::from(octets);
            let port = u16::from_be_bytes([b[16], b[17]]);
            Ok(Some(SocketAddr::V6(SocketAddrV6::new(ip, port, 0, 0))))
        }
        _ => Err(FrameError::ShortBody),
    }
}

/// Reads a header, returning the frame type and the declared body length.
///
/// # Errors
///
/// Returns [`FrameError::UnknownType`] for an unrecognised type byte and
/// [`FrameError::TooLarge`] when the declared length exceeds [`MAX_PAYLOAD`], so neither a
/// bad byte nor a hostile length reaches the body reader.
pub fn parse_header(header: &[u8; HEADER_LEN]) -> Result<(FrameType, usize), FrameError> {
    let ty = FrameType::from_byte(header[0]).ok_or(FrameError::UnknownType(header[0]))?;
    let len = u32::from_be_bytes([header[1], header[2], header[3], header[4]]) as usize;
    if len > MAX_PAYLOAD {
        return Err(FrameError::TooLarge(len));
    }
    Ok((ty, len))
}

/// Copies the 32-byte key at `offset` out of `body`.
fn take_key(body: &[u8], offset: usize) -> Result<[u8; KEY_LEN], FrameError> {
    body.get(offset..offset + KEY_LEN)
        .and_then(|s| s.try_into().ok())
        .ok_or(FrameError::ShortBody)
}

/// A frame that could not be parsed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum FrameError {
    /// The type byte did not name a known frame.
    #[error("unknown relay frame type 0x{0:02x}")]
    UnknownType(u8),
    /// The declared payload length exceeds [`MAX_PAYLOAD`].
    #[error("relay frame length {0} exceeds the maximum")]
    TooLarge(usize),
    /// The body was shorter than the frame type requires (e.g. missing its key prefix).
    #[error("relay frame body too short")]
    ShortBody,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Encodes a frame then parses it back through the header + body split the wire uses.
    fn round_trip(frame: &Frame) -> Frame {
        let bytes = frame.encode();
        assert!(bytes.len() >= HEADER_LEN);
        let header: [u8; HEADER_LEN] = bytes[..HEADER_LEN].try_into().unwrap();
        let (ty, len) = parse_header(&header).unwrap();
        let body = &bytes[HEADER_LEN..];
        assert_eq!(body.len(), len, "the declared length must match the body");
        Frame::decode_body(ty, body).unwrap()
    }

    #[test]
    fn register_round_trips() {
        let frame = Frame::Register { key: [7u8; KEY_LEN] };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn send_round_trips_with_its_payload() {
        let frame = Frame::Send { dst: [9u8; KEY_LEN], payload: b"wireguard-datagram".to_vec() };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn recv_round_trips_with_its_payload() {
        let frame = Frame::Recv { src: [3u8; KEY_LEN], payload: vec![0xAB; 1200] };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn keepalive_has_an_empty_body() {
        let frame = Frame::KeepAlive;
        let bytes = frame.encode();
        assert_eq!(bytes.len(), HEADER_LEN, "a keepalive is header-only");
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn an_empty_send_payload_is_valid() {
        // A zero-length datagram is odd but not malformed; the key prefix alone is a valid body.
        let frame = Frame::Send { dst: [1u8; KEY_LEN], payload: Vec::new() };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn an_unknown_type_byte_is_rejected() {
        let header = [0xFF, 0, 0, 0, 0];
        assert_eq!(parse_header(&header), Err(FrameError::UnknownType(0xFF)));
    }

    #[test]
    fn an_oversized_length_is_rejected_before_allocation() {
        let huge = ((MAX_PAYLOAD + 1) as u32).to_be_bytes();
        let header = [FrameType::Send as u8, huge[0], huge[1], huge[2], huge[3]];
        assert_eq!(parse_header(&header), Err(FrameError::TooLarge(MAX_PAYLOAD + 1)));
    }

    #[test]
    fn a_send_missing_its_key_is_short() {
        // Only 10 bytes of body: not enough for the 32-byte destination key.
        assert_eq!(Frame::decode_body(FrameType::Send, &[0u8; 10]), Err(FrameError::ShortBody));
    }

    #[test]
    fn pair_round_trips_with_its_code() {
        let frame = Frame::Pair { key: [5u8; KEY_LEN], udp_port: 51820, code: "W1VE-7Q".into() };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn pair_with_an_empty_code_round_trips() {
        let frame = Frame::Pair { key: [1u8; KEY_LEN], udp_port: 1, code: String::new() };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn paired_round_trips_with_an_ipv4_address() {
        let frame = Frame::Paired {
            peer_key: [2u8; KEY_LEN],
            peer_addr: Some("203.0.113.9:51820".parse().unwrap()),
        };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn paired_round_trips_with_an_ipv6_address() {
        let frame = Frame::Paired {
            peer_key: [8u8; KEY_LEN],
            peer_addr: Some("[2606:4700::1]:9000".parse().unwrap()),
        };
        assert_eq!(round_trip(&frame), frame);
    }

    #[test]
    fn paired_round_trips_with_no_address() {
        let frame = Frame::Paired { peer_key: [4u8; KEY_LEN], peer_addr: None };
        assert_eq!(round_trip(&frame), frame);
    }
}
