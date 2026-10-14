//! Persisted configuration: the keys and addresses that bring a tunnel up.
//!
//! Increments 1–3 built the transport (WireGuard, netstack, relay) but left every key and
//! address to be passed in code. This module is the plumbing that makes the engine usable: a
//! [`Config`] loaded from and saved to a JSON file, holding this node's WireGuard identity,
//! its one peer, an optional relay, and the forward rules — the same shape the .NET app keeps
//! in `config.json`, so a future migration maps field for field.
//!
//! ## Secrets
//!
//! The identity's private key and any pre-shared key are secrets. They are serialised (the
//! file has to persist them to survive a restart) but never printed: every type here has a
//! hand-written [`Debug`] that redacts key material, so a key cannot leak into a log or a
//! panic message. Keys are stored base64-encoded for a human-editable file.
//!
//! The file itself should be created with owner-only permissions by the caller; this module
//! does not set file modes, since that is an OS-specific concern the application layer owns.

use std::net::SocketAddr;
use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::engine::relay::KEY_LEN;
use crate::engine::tunnel::{StaticIdentity, TunnelPeer};
use crate::Result;

/// Which side of the one-Client-one-Station link this node is.
///
/// The role fixes the inner addressing convention and who initiates the WireGuard handshake,
/// so two nodes built from mirror-image configs agree without extra negotiation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    /// The operating position: holds the paddle/keyer, dials the Station.
    Client,
    /// The remote radio site: accepts the Client's connection.
    Station,
}

impl Role {
    /// The inner tunnel address this role answers to, by convention `10.6.0.1` (client) and
    /// `10.6.0.2` (station) — a tiny fixed point-to-point pair, no subnet to configure.
    #[must_use]
    pub fn inner_address(self) -> std::net::Ipv4Addr {
        match self {
            Role::Client => std::net::Ipv4Addr::new(10, 6, 0, 1),
            Role::Station => std::net::Ipv4Addr::new(10, 6, 0, 2),
        }
    }

    /// The peer's inner address — the other role's [`inner_address`](Self::inner_address).
    #[must_use]
    pub fn peer_inner_address(self) -> std::net::Ipv4Addr {
        match self {
            Role::Client => Role::Station.inner_address(),
            Role::Station => Role::Client.inner_address(),
        }
    }
}

/// This node's WireGuard identity: the private key, base64-encoded in the file.
#[derive(Clone, Serialize, Deserialize)]
pub struct IdentityConfig {
    /// Base64 of the 32-byte X25519 private key.
    private_key: String,
}

impl IdentityConfig {
    /// Generates a fresh identity from the OS CSPRNG.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the OS random source is unavailable.
    pub fn generate() -> Result<Self> {
        let id = StaticIdentity::generate()?;
        Ok(Self { private_key: base64_encode(&id.private_bytes()) })
    }

    /// Reconstructs the [`StaticIdentity`] from the stored key.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the stored key is not valid base64 of 32 bytes.
    pub fn to_identity(&self) -> Result<StaticIdentity> {
        let bytes = decode_key(&self.private_key, "identity.private_key")?;
        Ok(StaticIdentity::from_private_bytes(bytes))
    }

    /// The public key to hand the peer, base64-encoded.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the stored private key cannot be decoded.
    pub fn public_key_base64(&self) -> Result<String> {
        Ok(base64_encode(&self.to_identity()?.public_bytes()))
    }
}

impl std::fmt::Debug for IdentityConfig {
    /// Never prints the private key.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("IdentityConfig").field("private_key", &"<redacted>").finish()
    }
}

/// The single remote peer.
#[derive(Clone, Serialize, Deserialize)]
pub struct PeerConfig {
    /// Base64 of the peer's 32-byte public key.
    pub public_key: String,
    /// The peer's direct UDP endpoint, if known. `None` forces the relay.
    #[serde(default)]
    pub endpoint: Option<SocketAddr>,
    /// Base64 of an optional 32-byte pre-shared key; both ends must match.
    #[serde(default)]
    pub preshared_key: Option<String>,
    /// WireGuard persistent-keepalive period, in seconds.
    #[serde(default)]
    pub persistent_keepalive: Option<u16>,
}

impl PeerConfig {
    /// Builds a [`TunnelPeer`] for a direct path.
    ///
    /// The `endpoint` is required here; for a relayed path the caller supplies a placeholder,
    /// since the relay addresses by key and ignores the endpoint.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if a key is malformed or no endpoint is set.
    pub fn to_tunnel_peer(&self) -> Result<TunnelPeer> {
        let endpoint = self.endpoint.ok_or_else(|| {
            crate::Error::Config("peer.endpoint is required for a direct path".into())
        })?;
        self.to_tunnel_peer_with_endpoint(endpoint)
    }

    /// Builds a [`TunnelPeer`] with an explicit endpoint (a real one for direct, or a
    /// placeholder for the relay).
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if a key is malformed.
    pub fn to_tunnel_peer_with_endpoint(&self, endpoint: SocketAddr) -> Result<TunnelPeer> {
        let public_key = decode_key(&self.public_key, "peer.public_key")?;
        let preshared_key = match &self.preshared_key {
            Some(psk) => Some(decode_key(psk, "peer.preshared_key")?),
            None => None,
        };
        Ok(TunnelPeer { public_key, preshared_key, endpoint, persistent_keepalive: self.persistent_keepalive })
    }

    /// The peer's public key bytes.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the stored key is malformed.
    pub fn public_key_bytes(&self) -> Result<[u8; KEY_LEN]> {
        decode_key(&self.public_key, "peer.public_key")
    }
}

impl std::fmt::Debug for PeerConfig {
    /// Prints the public key and endpoint, but redacts the pre-shared key.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PeerConfig")
            .field("public_key", &self.public_key)
            .field("endpoint", &self.endpoint)
            .field("preshared_key", &self.preshared_key.as_ref().map(|_| "<redacted>"))
            .field("persistent_keepalive", &self.persistent_keepalive)
            .finish()
    }
}

/// The relay to fall back to when no direct path can be opened.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RelayConfig {
    /// The relay server's address (host:port), reachable by both ends.
    pub address: SocketAddr,
}

/// The whole persisted configuration for one node.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Config {
    /// Whether this node is the Client or the Station.
    pub role: Role,
    /// This node's WireGuard identity.
    pub identity: IdentityConfig,
    /// The single remote peer.
    pub peer: PeerConfig,
    /// An optional relay for NAT-traversal fallback.
    #[serde(default)]
    pub relay: Option<RelayConfig>,
    /// A shared pairing code for zero-setup discovery. When set (and a relay is configured),
    /// `connect` finds the peer by rendezvous instead of a pasted `peer.public_key`: both ends
    /// enter the same code, the relay introduces them, and the link comes up directly (or over
    /// the relay). The manual `peer` fields remain the advanced path when this is unset.
    #[serde(default)]
    pub pairing_code: Option<String>,
    /// Port forward rules, in the shape the .NET client persists.
    #[serde(default)]
    pub forwards: Vec<ForwardRuleConfig>,
    /// Station-only UI settings (keying output + logger input). Absent in a Client config.
    #[serde(default)]
    pub station: StationSettings,
    /// Operator callsign, used as the LAN discovery beacon name on the operating-position
    /// side (e.g. `W1VE`). Empty when unset.
    #[serde(default)]
    pub callsign: String,
    /// Optional position label that disambiguates several keyers on one LAN (e.g. `20m`); the
    /// LAN beacon advertises `callsign-keyer_id`. Empty when unset.
    #[serde(default)]
    pub keyer_id: String,
}

/// Station-only persisted settings: the serial keying output and the optional logger
/// (WinKeyer) input. Kept here so a Station remembers its port selections across restarts,
/// the same way the identity, relay and pairing code persist.
///
/// Control lines are stored as the UI's lowercase names (`"dtr"`, `"rts"`, `"none"`) rather
/// than the [`crate::primitives::KeyingLine`] enum, so the on-disk format stays stable and
/// human-editable and does not couple to the enum's discriminants.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct StationSettings {
    /// Serial port that keys the radio, if chosen.
    #[serde(default)]
    pub keying_port: Option<String>,
    /// Control line that keys the radio: `"dtr"` or `"rts"`. Empty ⇒ default (`"dtr"`).
    #[serde(default)]
    pub key_line: Option<String>,
    /// Control line that asserts PTT: `"dtr"`, `"rts"`, or `"none"`. Empty ⇒ default.
    #[serde(default)]
    pub ptt_line: Option<String>,
    /// Serial port the local logger sends WinKeyer protocol on, if chosen.
    #[serde(default)]
    pub logger_port: Option<String>,
    /// Whether the logger input is enabled.
    #[serde(default)]
    pub logger_enabled: bool,
}

impl Config {
    /// Creates a config with a freshly generated identity and no peer key yet.
    ///
    /// The peer's public key must be filled in from what the other end prints after its own
    /// `keygen`; the two ends exchange public keys out of band, exactly as the operator
    /// already exchanges the Station address.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if an identity cannot be generated.
    pub fn new(role: Role) -> Result<Self> {
        Ok(Self {
            role,
            identity: IdentityConfig::generate()?,
            peer: PeerConfig {
                public_key: String::new(),
                endpoint: None,
                preshared_key: None,
                persistent_keepalive: Some(25),
            },
            relay: None,
            pairing_code: None,
            forwards: Vec::new(),
            station: StationSettings::default(),
            callsign: String::new(),
            keyer_id: String::new(),
        })
    }

    /// Loads a config from `path`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the file cannot be read or is not valid config JSON.
    pub fn load(path: impl AsRef<Path>) -> Result<Self> {
        let text = std::fs::read_to_string(path.as_ref())
            .map_err(|e| crate::Error::Config(format!("reading config: {e}")))?;
        Self::from_json(&text)
    }

    /// Parses a config from a JSON string.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the JSON is not a valid config.
    pub fn from_json(text: &str) -> Result<Self> {
        serde_json::from_str(text).map_err(|e| crate::Error::Config(format!("parsing config: {e}")))
    }

    /// Serialises the config to pretty JSON.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if serialisation fails (practically never).
    pub fn to_json(&self) -> Result<String> {
        serde_json::to_string_pretty(self)
            .map_err(|e| crate::Error::Config(format!("serialising config: {e}")))
    }

    /// Saves the config to `path`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] if the file cannot be written.
    pub fn save(&self, path: impl AsRef<Path>) -> Result<()> {
        let json = self.to_json()?;
        std::fs::write(path.as_ref(), json)
            .map_err(|e| crate::Error::Config(format!("writing config: {e}")))
    }
}

/// A forward rule as persisted, mirroring [`crate::engine::network::ForwardRule`]'s fields.
///
/// Kept as its own serialisable type rather than deriving on the engine's `ForwardRule` so
/// the on-disk format is owned here and the engine type stays free of serde.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ForwardRuleConfig {
    /// Stable identifier.
    pub id: String,
    /// Display name.
    pub name: String,
    /// `"tcp"` or `"udp"`.
    pub protocol: String,
    /// Local port the Client binds.
    pub client_port: u16,
    /// Port on the Station side.
    pub station_port: u16,
    /// Whether the rule is active.
    pub enabled: bool,
    /// Address on the Station LAN traffic is delivered to.
    pub station_target_address: String,
}

/// The standard base64 alphabet, index → character.
const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Encodes bytes as standard base64 with `=` padding.
///
/// Hand-rolled to avoid a dependency for the one place keys are stringified; the crate
/// already hand-rolls `hex` for the same reason.
#[must_use]
pub fn base64_encode(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(B64[(n >> 18 & 0x3f) as usize] as char);
        out.push(B64[(n >> 12 & 0x3f) as usize] as char);
        out.push(if chunk.len() > 1 { B64[(n >> 6 & 0x3f) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { B64[(n & 0x3f) as usize] as char } else { '=' });
    }
    out
}

/// Decodes standard base64 (with or without padding) into bytes.
///
/// # Errors
///
/// Returns [`crate::Error::Config`] on an invalid character or a truncated group.
pub fn base64_decode(text: &str) -> Result<Vec<u8>> {
    fn val(c: u8) -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }
    let cleaned: Vec<u8> = text.bytes().filter(|b| *b != b'=' && !b.is_ascii_whitespace()).collect();
    let mut out = Vec::with_capacity(cleaned.len() / 4 * 3);
    for group in cleaned.chunks(4) {
        if group.len() < 2 {
            return Err(crate::Error::Config("truncated base64 group".into()));
        }
        let mut n = 0u32;
        for (i, &c) in group.iter().enumerate() {
            let v = val(c).ok_or_else(|| crate::Error::Config("invalid base64 character".into()))?;
            n |= v << (18 - 6 * i);
        }
        out.push((n >> 16 & 0xff) as u8);
        if group.len() >= 3 {
            out.push((n >> 8 & 0xff) as u8);
        }
        if group.len() >= 4 {
            out.push((n & 0xff) as u8);
        }
    }
    Ok(out)
}

/// Decodes a base64 string that must be exactly a 32-byte key.
fn decode_key(text: &str, field: &str) -> Result<[u8; KEY_LEN]> {
    let bytes = base64_decode(text)?;
    bytes
        .try_into()
        .map_err(|_| crate::Error::Config(format!("{field} must be exactly {KEY_LEN} bytes")))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_round_trips_arbitrary_lengths() {
        for len in 0..40 {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 7 + 3) as u8).collect();
            let encoded = base64_encode(&bytes);
            assert_eq!(base64_decode(&encoded).unwrap(), bytes, "len {len}");
        }
    }

    #[test]
    fn base64_matches_a_known_vector() {
        // "RWK" -> "UldL" by the standard alphabet.
        assert_eq!(base64_encode(b"RWK"), "UldL");
        assert_eq!(base64_decode("UldL").unwrap(), b"RWK");
    }

    #[test]
    fn base64_rejects_an_invalid_character() {
        assert!(base64_decode("not valid!*").is_err());
    }

    #[test]
    fn a_generated_identity_round_trips_through_config() {
        let id = IdentityConfig::generate().unwrap();
        let identity = id.to_identity().unwrap();
        // The public key derived through the config matches the identity's own.
        assert_eq!(id.public_key_base64().unwrap(), base64_encode(&identity.public_bytes()));
    }

    #[test]
    fn a_config_round_trips_through_json() {
        let mut config = Config::new(Role::Client).unwrap();
        config.peer.public_key = IdentityConfig::generate().unwrap().public_key_base64().unwrap();
        config.peer.endpoint = Some("203.0.113.5:51820".parse().unwrap());
        config.relay = Some(RelayConfig { address: "198.51.100.9:9999".parse().unwrap() });
        config.forwards.push(ForwardRuleConfig {
            id: "flex-cmd".into(),
            name: "[Flex] SmartSDR Command".into(),
            protocol: "tcp".into(),
            client_port: 4992,
            station_port: 4992,
            enabled: true,
            station_target_address: "192.168.1.50".into(),
        });

        let json = config.to_json().unwrap();
        let back = Config::from_json(&json).unwrap();

        assert_eq!(back.role, Role::Client);
        assert_eq!(back.peer.endpoint, config.peer.endpoint);
        assert_eq!(back.relay.unwrap().address, "198.51.100.9:9999".parse().unwrap());
        assert_eq!(back.forwards, config.forwards);
        // The private key survives the round trip and still derives the same public key.
        assert_eq!(
            back.identity.public_key_base64().unwrap(),
            config.identity.public_key_base64().unwrap()
        );
    }

    #[test]
    fn debug_output_redacts_secrets() {
        let mut config = Config::new(Role::Station).unwrap();
        config.peer.preshared_key = Some(base64_encode(&[9u8; KEY_LEN]));
        let rendered = format!("{config:?}");
        // The private key's base64 must not appear.
        let private_b64 = config.identity.private_key.clone();
        assert!(!rendered.contains(&private_b64), "private key leaked into Debug");
        assert!(rendered.contains("<redacted>"));
        // The preshared key's base64 must not appear either.
        assert!(!rendered.contains(&base64_encode(&[9u8; KEY_LEN])));
    }

    #[test]
    fn roles_have_distinct_mirrored_inner_addresses() {
        assert_eq!(Role::Client.inner_address(), Role::Station.peer_inner_address());
        assert_eq!(Role::Station.inner_address(), Role::Client.peer_inner_address());
        assert_ne!(Role::Client.inner_address(), Role::Station.inner_address());
    }

    #[test]
    fn a_peer_without_an_endpoint_is_rejected_for_a_direct_path() {
        let config = Config::new(Role::Client).unwrap();
        // Freshly created: no endpoint, so a direct TunnelPeer cannot be built.
        assert!(config.peer.to_tunnel_peer().is_err());
    }
}
