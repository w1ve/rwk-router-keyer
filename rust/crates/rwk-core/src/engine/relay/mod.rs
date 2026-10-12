//! A minimal, RWK-native packet relay — the NAT-traversal fallback.
//!
//! When the Client and Station cannot open a direct WireGuard path, they both reach a relay
//! that forwards their (already-encrypted) datagrams to each other by public key. This is the
//! same role Tailscale's DERP plays — "both sides behind very aggressive NATs, firewalls, no
//! IPv6? Well, DERP" — but reduced to what RWK's one-Client-one-Station topology needs:
//!
//! * no coordination plane: each node is configured with the relay's address, exactly as the
//!   Station's address is already operator-supplied in `config.json`;
//! * no server crypto: the relay forwards opaque WireGuard datagrams and never holds a key
//!   that could decrypt them;
//! * no mesh, no home-node preference, no login handshake.
//!
//! The pieces:
//!
//! * [`frame`] — the pure wire codec (type + length + payload), key-addressed.
//! * [`server`] — [`RelayServer`]: accepts connections, learns each node's key from its
//!   `Register` frame, and forwards `Send` frames to the destination's connection as `Recv`.
//! * [`client`] — [`RelayClient`]: connects, registers this node's key, and presents a
//!   send-to-peer / recv-from-peer datagram channel the WireGuard tunnel can run over.
//!
//! See `rust/docs/NATIVE-NETWORK-SPIKE.md` (increment 3) for how this fits the overall plan.

pub mod client;
pub mod frame;
pub mod server;

pub use client::{pair, PairedPeer, RelayClient};
pub use frame::{Frame, FrameError, FrameType, KEY_LEN, MAX_PAYLOAD};
pub use server::{RelayLimits, RelayMetrics, RelayServer};
