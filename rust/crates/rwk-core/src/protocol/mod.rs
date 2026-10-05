//! Wire codecs.
//!
//! * [`edge`] — the `RWK-PADDLE` binary frame carried in one UDP datagram.
//! * [`winkeyer`] — the WinKeyer command-byte set and status decoding.
//! * [`morse`] — the ITU Morse table used by the host text path.

pub mod edge;
pub mod morse;
pub mod winkeyer;

pub use edge::{EdgeEntry, RwkPaddleFrame};
pub use morse::MorseTable;
