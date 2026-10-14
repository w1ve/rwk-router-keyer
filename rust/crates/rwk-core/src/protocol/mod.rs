//! Wire codecs.
//!
//! * [`edge`] — the `RWK-PADDLE` binary frame carried in one UDP datagram.
//! * [`winkeyer`] — the WinKeyer command-byte set and status decoding.
//! * [`winkeyer_host`] — the inbound WinKeyer host-stream decoder (a logger's byte stream).
//! * [`flex_vita`] — FlexRadio VITA-49 discovery packet parsing and endpoint rewriting.
//! * [`morse`] — the ITU Morse table used by the host text path.

pub mod edge;
pub mod flex_vita;
pub mod morse;
pub mod winkeyer;
pub mod winkeyer_host;

pub use edge::{EdgeEntry, RwkPaddleFrame};
pub use flex_vita::{DiscoveredRadio, DISCOVERY_PORT as FLEX_DISCOVERY_PORT};
pub use morse::MorseTable;
pub use winkeyer_host::{Decoder as WinKeyerHostDecoder, HostEvent as WinKeyerHostEvent};
