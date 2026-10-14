//! The control channel: a small message stream the Client and Station exchange over the
//! tunnel, alongside the keying edge and the port forwards.
//!
//! The port forwards ([`Session`](crate::engine::session::Session)) carry *application*
//! traffic. The control channel carries *coordination* between the two RWK ends:
//!
//! * **Forward rules** (Client → Station). The operator manages the forward table on the
//!   Client; the Station learns it over this channel and starts the matching responders,
//!   instead of needing its own copy of the config. This is what lets the wizard and the
//!   FlexRadio auto-forwarding "just work" without touching the Station.
//! * **Discovery announcements** (Station → Client). When the Station captures a FlexRadio
//!   VITA-49 discovery broadcast on its LAN, it forwards the raw packet to the Client, which
//!   rewrites the endpoint and re-broadcasts it on the operating-position LAN so SmartSDR
//!   finds the remote radio.
//!
//! ## Wire format
//!
//! One TCP stream over the tunnel on a fixed inner port ([`CONTROL_PORT`]). Each message is a
//! 4-byte big-endian length prefix followed by that many bytes of UTF-8 JSON
//! ([`ControlMessage`]). Length-prefixing keeps the framing unambiguous when several messages
//! are coalesced into one TCP segment — the bug the C# control channel hit and fixed.
//!
//! The Station [`listen`](listen)s (it is the responder); the Client [`connect`](connect)s.
//! Both get a [`ControlLink`] they can [`send`](ControlLink::send) and [`recv`](ControlLink::recv)
//! on. The link rides the same [`NetStackHandle`] as everything else, so it needs no extra
//! socket on the host and no open port on either LAN.

use crate::config::ForwardRuleConfig;
use crate::engine::netstack_driver::{NetStackHandle, TcpConn};
use crate::Result;

/// The inner tunnel TCP port the control channel uses.
///
/// 7373 is reserved for RWK control traffic (it was the .NET control port too), so it never
/// collides with a user forward rule — [`is_reserved_port`] rejects it in rule validation.
pub const CONTROL_PORT: u16 = 7373;

/// The other reserved port (the .NET discovery/relay port), also refused for user rules.
pub const RESERVED_DISCOVERY_PORT: u16 = 41373;

/// True if `port` is reserved for RWK's own use and must not be used by a forward rule.
#[must_use]
pub fn is_reserved_port(port: u16) -> bool {
    port == CONTROL_PORT || port == RESERVED_DISCOVERY_PORT
}

/// A message on the control channel.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ControlMessage {
    /// The full set of forward rules the Station should run (a full replace, not a delta).
    /// Sent by the Client on connect and whenever the rule table changes.
    ForwardRules {
        /// Every rule the Client currently has; the Station runs the enabled ones.
        rules: Vec<ForwardRuleConfig>,
    },
    /// A FlexRadio VITA-49 discovery packet the Station captured, base64-encoded. Sent by the
    /// Station to the Client, which rewrites and re-broadcasts it.
    DiscoveryAnnounce {
        /// Base64 of the raw discovery packet bytes.
        payload: String,
    },
}

impl ControlMessage {
    /// Encodes the message as a length-prefixed JSON frame.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Protocol`] if serialization fails (practically never).
    pub fn encode(&self) -> Result<Vec<u8>> {
        let json = serde_json::to_vec(self)
            .map_err(|e| crate::Error::Protocol(format!("control encode: {e}")))?;
        let len = u32::try_from(json.len())
            .map_err(|_| crate::Error::Protocol("control message too large".into()))?;
        let mut frame = Vec::with_capacity(4 + json.len());
        frame.extend_from_slice(&len.to_be_bytes());
        frame.extend_from_slice(&json);
        Ok(frame)
    }
}

/// Reassembles length-prefixed JSON frames from a byte stream that may split or coalesce
/// them, decoding each complete frame into a [`ControlMessage`].
#[derive(Debug, Default)]
pub struct FrameDecoder {
    buffer: Vec<u8>,
}

impl FrameDecoder {
    /// A new, empty decoder.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Appends received bytes and returns every complete message now available.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Protocol`] if a framed payload is not valid [`ControlMessage`]
    /// JSON, or declares an implausibly large length (guarding against a desync).
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<ControlMessage>> {
        self.buffer.extend_from_slice(bytes);
        let mut out = Vec::new();
        loop {
            if self.buffer.len() < 4 {
                break;
            }
            let len = u32::from_be_bytes([
                self.buffer[0],
                self.buffer[1],
                self.buffer[2],
                self.buffer[3],
            ]) as usize;
            // A control message is small; a huge length means the stream desynced.
            if len > 1_000_000 {
                return Err(crate::Error::Protocol(format!("control frame too large: {len}")));
            }
            if self.buffer.len() < 4 + len {
                break; // wait for the rest
            }
            let json = self.buffer[4..4 + len].to_vec();
            self.buffer.drain(..4 + len);
            let msg = serde_json::from_slice(&json)
                .map_err(|e| crate::Error::Protocol(format!("control decode: {e}")))?;
            out.push(msg);
        }
        Ok(out)
    }
}

/// A live control-channel connection over the tunnel.
///
/// Thin wrapper over the tunnel [`TcpConn`] that frames [`ControlMessage`]s. Hold it for the
/// session's lifetime; dropping it closes the control stream (the port forwards and keying
/// edge are unaffected).
pub struct ControlLink {
    conn: TcpConn,
    decoder: FrameDecoder,
    /// Messages decoded from one received chunk but not yet returned by `recv`.
    pending: Pending,
}

impl ControlLink {
    /// The Client side: dials the Station's control port over the tunnel.
    ///
    /// # Errors
    ///
    /// Propagates a dial failure from the netstack.
    pub async fn connect(handle: &NetStackHandle, station_inner: std::net::IpAddr) -> Result<Self> {
        let conn = handle.connect_tcp((station_inner, CONTROL_PORT), CONTROL_PORT).await?;
        Ok(Self { conn, decoder: FrameDecoder::new(), pending: Pending::empty() })
    }

    /// The Station side: accepts the Client's control connection on the control port.
    ///
    /// # Errors
    ///
    /// Propagates a listen failure from the netstack.
    pub async fn listen(handle: &NetStackHandle) -> Result<Self> {
        let conn = handle.listen_tcp(CONTROL_PORT).await?;
        Ok(Self { conn, decoder: FrameDecoder::new(), pending: Pending::empty() })
    }

    /// Sends one control message.
    ///
    /// # Errors
    ///
    /// Propagates an encode error or a stopped-driver error.
    pub async fn send(&self, message: &ControlMessage) -> Result<()> {
        self.conn.send(message.encode()?).await
    }

    /// Awaits the next control message, or [`None`] at end-of-stream (peer closed or driver
    /// stopped). Reassembles frames internally, so a partial read never yields a message.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Protocol`] on a malformed frame.
    pub async fn recv(&mut self) -> Result<Option<ControlMessage>> {
        loop {
            // Any messages already buffered from a previous chunk come out first.
            if let Some(msg) = self.pending.take_first() {
                return Ok(Some(msg));
            }
            match self.conn.recv().await {
                Some(bytes) => {
                    let msgs = self.decoder.push(&bytes)?;
                    if msgs.is_empty() {
                        continue; // need more bytes
                    }
                    self.pending = Pending::from(msgs);
                    // Loop back to pop the first.
                }
                None => return Ok(None),
            }
        }
    }
}

/// A tiny FIFO of decoded-but-not-yet-returned messages, so one TCP chunk carrying several
/// frames yields them across successive `recv` calls.
struct Pending {
    items: std::collections::VecDeque<ControlMessage>,
}

impl Pending {
    fn empty() -> Self {
        Self { items: std::collections::VecDeque::new() }
    }

    fn take_first(&mut self) -> Option<ControlMessage> {
        self.items.pop_front()
    }
}

impl From<Vec<ControlMessage>> for Pending {
    fn from(v: Vec<ControlMessage>) -> Self {
        Self { items: v.into() }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_rules() -> Vec<ForwardRuleConfig> {
        vec![ForwardRuleConfig {
            id: "flex-cmd".into(),
            name: "[Flex] SmartSDR Command".into(),
            protocol: "tcp".into(),
            client_port: 4992,
            station_port: 4992,
            enabled: true,
            station_target_address: "192.168.1.50".into(),
        }]
    }

    #[test]
    fn reserved_ports_are_rejected() {
        assert!(is_reserved_port(CONTROL_PORT));
        assert!(is_reserved_port(RESERVED_DISCOVERY_PORT));
        assert!(!is_reserved_port(4992));
    }

    #[test]
    fn a_forward_rules_message_round_trips() {
        let msg = ControlMessage::ForwardRules { rules: sample_rules() };
        let frame = msg.encode().unwrap();
        let mut dec = FrameDecoder::new();
        let out = dec.push(&frame).unwrap();
        assert_eq!(out, vec![msg]);
    }

    #[test]
    fn a_discovery_announce_message_round_trips() {
        let msg = ControlMessage::DiscoveryAnnounce { payload: "AAEC".into() };
        let frame = msg.encode().unwrap();
        let mut dec = FrameDecoder::new();
        assert_eq!(dec.push(&frame).unwrap(), vec![msg]);
    }

    #[test]
    fn coalesced_frames_are_split_correctly() {
        let a = ControlMessage::ForwardRules { rules: sample_rules() };
        let b = ControlMessage::DiscoveryAnnounce { payload: "Zm9v".into() };
        let mut buf = a.encode().unwrap();
        buf.extend(b.encode().unwrap());
        let mut dec = FrameDecoder::new();
        let out = dec.push(&buf).unwrap();
        assert_eq!(out, vec![a, b]);
    }

    #[test]
    fn a_frame_split_across_pushes_is_reassembled() {
        let msg = ControlMessage::ForwardRules { rules: sample_rules() };
        let frame = msg.encode().unwrap();
        let (head, tail) = frame.split_at(frame.len() / 2);
        let mut dec = FrameDecoder::new();
        assert!(dec.push(head).unwrap().is_empty(), "no message until the rest arrives");
        assert_eq!(dec.push(tail).unwrap(), vec![msg]);
    }

    #[test]
    fn an_oversized_length_prefix_is_rejected() {
        let mut dec = FrameDecoder::new();
        let bogus = [0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        assert!(dec.push(&bogus).is_err());
    }
}
