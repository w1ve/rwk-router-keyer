//! FlexRadio VITA-49 discovery packet parsing and endpoint rewriting.
//!
//! A faithful port of the C# `RWK.Shared.Discovery.FlexVitaDiscoveryCodec`. FlexRadio
//! 6000/8000-series radios announce themselves on the LAN with a UDP broadcast to port 4992,
//! in a VITA-49 framing: a fixed 28-byte binary preamble followed by an ASCII payload of
//! space-separated `key=value` pairs (`model`, `serial`, `ip`, `port`, `status`, …).
//!
//! The router relays that discovery across the tunnel so a SmartSDR client at the operating
//! position finds the remote radio as if it were on the local LAN. Two operations are needed:
//!
//! * [`parse`] — read a captured broadcast into a [`DiscoveredRadio`] (the Station side, to
//!   learn the radio's real LAN address and tell the Client about it).
//! * [`rewrite_endpoint`] — replace the `ip=`/`port=` fields with the address the SmartSDR
//!   client should actually dial (the Client side, before re-broadcasting on its own LAN).
//!
//! This is the one place that knows the packet layout; nothing else encodes offsets or field
//! names. The layout constants come from the FlexRadio community documentation and are
//! identical to the C# codec — do not "tidy" them.

use std::net::Ipv4Addr;

/// VITA-49 preamble size in bytes (7 × 32-bit words).
const PREAMBLE_SIZE: usize = 28;

/// Stream ID that marks a Flex *discovery* packet (word 1).
const DISCOVERY_STREAM_ID: u32 = 0x0000_0800;

/// Class-ID words identifying a Flex discovery packet (words 2–3). Two formats are seen in
/// the wild: the original SmartSDR framing and the one SmartUnlink / newer firmware emits.
const CLASS_ID_HIGH: u32 = 0x001C_2D53;
const CLASS_ID_LOW: u32 = 0x4CFF_FF00;
const CLASS_ID_HIGH_ALT: u32 = 0x0000_1C2D;
const CLASS_ID_LOW_ALT: u32 = 0x534C_FFFF;

/// The UDP port Flex discovery broadcasts use.
pub const DISCOVERY_PORT: u16 = 4992;

/// A FlexRadio discovered from a VITA-49 broadcast.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoveredRadio {
    /// Radio serial number (the `serial` field).
    pub serial: String,
    /// Radio model (the `model` field), or `"Unknown"` if absent.
    pub model: String,
    /// The radio's advertised LAN address (the `ip` field).
    pub address: Ipv4Addr,
    /// The radio's SmartSDR command port (the `port` field).
    pub command_port: u16,
}

/// Reads a big-endian `u32` from `data` at `offset`.
fn read_u32_be(data: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes([data[offset], data[offset + 1], data[offset + 2], data[offset + 3]])
}

/// Validates the VITA-49 preamble (length, stream ID, class ID). Returns the ASCII payload
/// slice (everything after the 28-byte preamble) on success.
fn validate_preamble(payload: &[u8]) -> Result<&[u8], String> {
    if payload.len() < PREAMBLE_SIZE + 4 {
        return Err(format!(
            "payload too short ({} bytes, need at least {})",
            payload.len(),
            PREAMBLE_SIZE + 4
        ));
    }

    let stream_id = read_u32_be(payload, 4);
    if stream_id != DISCOVERY_STREAM_ID {
        return Err(format!(
            "stream ID mismatch: expected {DISCOVERY_STREAM_ID:#010x}, got {stream_id:#010x}"
        ));
    }

    let class_high = read_u32_be(payload, 8);
    let class_low = read_u32_be(payload, 12);
    let class_match = (class_high == CLASS_ID_HIGH && class_low == CLASS_ID_LOW)
        || (class_high == CLASS_ID_HIGH_ALT && class_low == CLASS_ID_LOW_ALT);
    if !class_match {
        return Err(format!("class ID mismatch: got {class_high:08x}:{class_low:08x}"));
    }

    Ok(&payload[PREAMBLE_SIZE..])
}

/// Extracts the trimmed ASCII payload text from the bytes after the preamble.
fn ascii_payload(after_preamble: &[u8]) -> String {
    // The payload is ASCII, padded with NULs/spaces to a 4-byte boundary.
    let text: String = after_preamble.iter().map(|&b| b as char).collect();
    text.trim_end_matches(['\0', ' ']).to_string()
}

/// Parses the space-separated `key=value` pairs of the ASCII payload (case-insensitive keys).
fn parse_fields(ascii: &str) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for token in ascii.split(' ').filter(|t| !t.is_empty()) {
        if let Some(eq) = token.find('=') {
            if eq > 0 && eq < token.len() - 1 {
                out.push((token[..eq].to_ascii_lowercase(), token[eq + 1..].to_string()));
            }
        }
    }
    out
}

/// Looks up a field value by (lowercase) key.
fn field<'a>(fields: &'a [(String, String)], key: &str) -> Option<&'a str> {
    fields.iter().find(|(k, _)| k == key).map(|(_, v)| v.as_str())
}

/// Parses a captured VITA-49 discovery broadcast into a [`DiscoveredRadio`].
///
/// # Errors
///
/// Returns a human-readable reason if the packet is not a valid Flex discovery broadcast
/// (wrong length, stream ID, class ID, or missing/invalid `serial`/`ip`/`port` fields).
pub fn parse(payload: &[u8]) -> Result<DiscoveredRadio, String> {
    let after = validate_preamble(payload)?;
    let ascii = ascii_payload(after);
    let fields = parse_fields(&ascii);

    let serial = field(&fields, "serial")
        .filter(|s| !s.is_empty())
        .ok_or("missing or empty 'serial' field")?
        .to_string();

    let ip_str = field(&fields, "ip").ok_or("missing 'ip' field")?;
    let address: Ipv4Addr =
        ip_str.parse().map_err(|_| format!("invalid 'ip' field: '{ip_str}'"))?;

    let port_str = field(&fields, "port").ok_or("missing 'port' field")?;
    let command_port: u16 =
        port_str.parse().map_err(|_| format!("invalid 'port' field: '{port_str}'"))?;

    let model = field(&fields, "model").unwrap_or("Unknown").to_string();

    Ok(DiscoveredRadio { serial, model, address, command_port })
}

/// Replaces the value of `key=` in `ascii` with `new_value`, up to the next space or the end.
/// Returns the string unchanged if the key is absent.
fn replace_field(ascii: &str, key: &str, new_value: &str) -> String {
    let prefix = format!("{key}=");
    let Some(start) = ascii.find(&prefix) else {
        return ascii.to_string();
    };
    let value_start = start + prefix.len();
    let value_end = ascii[value_start..]
        .find(' ')
        .map(|rel| value_start + rel)
        .unwrap_or(ascii.len());
    format!("{}{}{}", &ascii[..value_start], new_value, &ascii[value_end..])
}

/// Rewrites the `ip=` and `port=` fields to `address:port`, returning a new, valid discovery
/// packet.
///
/// The preamble is preserved byte-for-byte except the word-count field in the header, which
/// is recomputed for the new (re-padded) length. The result is verified by re-parsing, so a
/// caller can trust that what it broadcasts will decode to the endpoint it asked for — the
/// same belt-and-braces check the C# codec does.
///
/// # Errors
///
/// Returns a reason if the input is not a valid discovery packet, or (defensively) if the
/// rewritten packet fails its verification re-parse.
pub fn rewrite_endpoint(
    payload: &[u8],
    address: Ipv4Addr,
    port: u16,
) -> Result<Vec<u8>, String> {
    // Must be a valid discovery packet to begin with.
    parse(payload)?;

    let ascii = ascii_payload(&payload[PREAMBLE_SIZE..]);
    let rewritten_ascii = {
        let a = replace_field(&ascii, "ip", &address.to_string());
        replace_field(&a, "port", &port.to_string())
    };

    let ascii_bytes = rewritten_ascii.as_bytes();
    // Pad the ASCII payload up to a 4-byte boundary (VITA-49 words).
    let padded_len = (ascii_bytes.len() + 3) & !3;

    let mut result = vec![0u8; PREAMBLE_SIZE + padded_len];
    // Preamble unchanged.
    result[..PREAMBLE_SIZE].copy_from_slice(&payload[..PREAMBLE_SIZE]);
    // Rewritten ASCII; the remaining padding bytes stay zero.
    result[PREAMBLE_SIZE..PREAMBLE_SIZE + ascii_bytes.len()].copy_from_slice(ascii_bytes);

    // Update the VITA-49 header word count (word 0, low 16 bits = total 32-bit word count).
    let word_count = (result.len() / 4) as u16;
    result[2] = (word_count >> 8) as u8;
    result[3] = (word_count & 0xFF) as u8;

    // Verify by re-parsing, exactly as the C# codec does.
    match parse(&result) {
        Ok(radio) if radio.address == address && radio.command_port == port => Ok(result),
        Ok(_) => Err("rewritten packet has the wrong endpoint after re-parse".into()),
        Err(e) => Err(format!("rewritten packet failed verification parse: {e}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a valid discovery packet: a correct preamble plus an ASCII `key=value` payload,
    /// padded to a 4-byte boundary, with the header word count set.
    fn build_packet(ascii: &str, alt_class: bool) -> Vec<u8> {
        let (high, low) =
            if alt_class { (CLASS_ID_HIGH_ALT, CLASS_ID_LOW_ALT) } else { (CLASS_ID_HIGH, CLASS_ID_LOW) };
        let ascii_bytes = ascii.as_bytes();
        let padded = (ascii_bytes.len() + 3) & !3;
        let mut p = vec![0u8; PREAMBLE_SIZE + padded];
        // word 1: stream ID
        p[4..8].copy_from_slice(&DISCOVERY_STREAM_ID.to_be_bytes());
        // words 2-3: class ID
        p[8..12].copy_from_slice(&high.to_be_bytes());
        p[12..16].copy_from_slice(&low.to_be_bytes());
        // payload
        p[PREAMBLE_SIZE..PREAMBLE_SIZE + ascii_bytes.len()].copy_from_slice(ascii_bytes);
        // word 0 low 16 bits: word count
        let wc = (p.len() / 4) as u16;
        p[2] = (wc >> 8) as u8;
        p[3] = (wc & 0xFF) as u8;
        p
    }

    const SAMPLE: &str = "discovery_protocol_version=3.0.0.2 model=FLEX-6400 serial=1234-5678-9012-3456 version=3.5.9.33046 nickname=Shack ip=192.168.1.50 port=4992 status=Available";

    #[test]
    fn parses_a_valid_discovery_packet() {
        let packet = build_packet(SAMPLE, false);
        let radio = parse(&packet).expect("should parse");
        assert_eq!(radio.serial, "1234-5678-9012-3456");
        assert_eq!(radio.model, "FLEX-6400");
        assert_eq!(radio.address, Ipv4Addr::new(192, 168, 1, 50));
        assert_eq!(radio.command_port, 4992);
    }

    #[test]
    fn accepts_both_class_id_formats() {
        assert!(parse(&build_packet(SAMPLE, false)).is_ok(), "original class ID");
        assert!(parse(&build_packet(SAMPLE, true)).is_ok(), "alternate class ID");
    }

    #[test]
    fn rejects_a_short_packet() {
        assert!(parse(&[0u8; 10]).is_err());
    }

    #[test]
    fn rejects_a_wrong_stream_id() {
        let mut p = build_packet(SAMPLE, false);
        p[4..8].copy_from_slice(&0x1234_5678u32.to_be_bytes());
        assert!(parse(&p).is_err());
    }

    #[test]
    fn rejects_a_wrong_class_id() {
        let mut p = build_packet(SAMPLE, false);
        p[8..12].copy_from_slice(&0xDEAD_BEEFu32.to_be_bytes());
        assert!(parse(&p).is_err());
    }

    #[test]
    fn rejects_missing_required_fields() {
        // No serial.
        let p = build_packet("model=FLEX-6400 ip=192.168.1.50 port=4992", false);
        assert!(parse(&p).is_err());
        // Bad ip.
        let p = build_packet("serial=X ip=not-an-ip port=4992", false);
        assert!(parse(&p).is_err());
        // Bad port.
        let p = build_packet("serial=X ip=192.168.1.50 port=notaport", false);
        assert!(parse(&p).is_err());
    }

    #[test]
    fn rewrites_the_endpoint_and_reparses_to_it() {
        let packet = build_packet(SAMPLE, false);
        let rewritten = rewrite_endpoint(&packet, Ipv4Addr::new(10, 6, 0, 1), 15000)
            .expect("rewrite should succeed");
        let radio = parse(&rewritten).expect("rewritten should parse");
        assert_eq!(radio.address, Ipv4Addr::new(10, 6, 0, 1));
        assert_eq!(radio.command_port, 15000);
        // Non-endpoint fields survive.
        assert_eq!(radio.serial, "1234-5678-9012-3456");
        assert_eq!(radio.model, "FLEX-6400");
    }

    #[test]
    fn rewritten_packet_is_word_aligned_with_a_correct_word_count() {
        let packet = build_packet(SAMPLE, false);
        let rewritten = rewrite_endpoint(&packet, Ipv4Addr::new(100, 64, 1, 2), 7000).unwrap();
        assert_eq!(rewritten.len() % 4, 0, "must be padded to a 4-byte boundary");
        let declared = read_u32_be(&rewritten, 0) & 0xFFFF;
        assert_eq!(declared as usize, rewritten.len() / 4, "header word count matches length");
    }

    #[test]
    fn replace_field_leaves_absent_keys_untouched() {
        assert_eq!(replace_field("a=1 b=2", "c", "9"), "a=1 b=2");
        assert_eq!(replace_field("a=1 b=2", "a", "9"), "a=9 b=2");
        assert_eq!(replace_field("a=1 b=2", "b", "9"), "a=1 b=9");
    }
}
