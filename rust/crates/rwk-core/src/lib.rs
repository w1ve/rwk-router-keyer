//! # rwk-core
//!
//! Native Rust core engine for **RWK Router Keyer**, replacing the C# `RWK.Shared`
//! library and its Go/Tailscale sidecar with an in-process, deterministic engine.
//!
//! The crate is organised in three layers:
//!
//! * [`primitives`] — the shared value/enum vocabulary (keying lines, modes, path
//!   types, forwarding rules) ported from `RWK.Shared.Primitives`.
//! * [`protocol`] — wire codecs: the `RWK-PADDLE` edge frame ([`protocol::edge`])
//!   and the WinKeyer command-byte set ([`protocol::winkeyer`]).
//! * [`engine`] — hardware and OS integration: [`engine::serial`] (DTR/RTS keying),
//!   [`engine::audio`] (keyed sine sidetone), [`engine::keying`] (Morse element
//!   scheduling), [`engine::network`] (UDP edge transport and UDP/TCP port forwarding) and
//!   [`engine::bus`] (a `tokio::sync::broadcast` event bus feeding the UI).
//! * [`timing`] — the sub-millisecond sleep/spin waiter and the injectable clock.
//! * [`platform`] — the single OS-FFI seam: thread priority and timer resolution.
//!
//! ## Design rules
//!
//! 1. **One process, no sidecar.** Mesh transport is a native Rust concern
//!    ([`engine::network`]); nothing is spawned as a child process.
//! 2. **Deterministic timing.** Element timing never runs on the UI thread; see
//!    [`timing::HybridWaiter`].
//! 3. **Safe concurrency.** Inter-thread state moves over channels
//!    ([`engine::bus`]) or atomics, never a shared `std::sync::Mutex` held across
//!    an `.await`.
//! 4. **Clean shutdown.** Every hardware handle is RAII-managed; dropping the
//!    owning value releases the port/stream/task.
//! 5. **`unsafe` is quarantined.** The crate denies `unsafe_code`; [`platform`] is the
//!    only module that lifts that deny, so every other module is provably FFI-free.

#![deny(unsafe_code)]
#![warn(missing_docs)]

pub mod engine;
#[allow(unsafe_code)]
pub mod platform;
pub mod primitives;
pub mod protocol;
pub mod timing;

pub use primitives::{
    EdgeSource, ForwardDirection, ForwardProtocol, KeyerMode, KeyingLine, PathType, TailscaleState,
};

/// Convenience result alias used throughout the crate.
pub type Result<T> = std::result::Result<T, Error>;

/// Errors surfaced by the core engine.
///
/// The variants deliberately mirror the failure classes the .NET client already
/// reports to its UI (`SidecarFailureKind`, keying exceptions, audio fallback) so
/// the Tauri front end can present the same messages.
#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// A serial port could not be opened, enumerated, or written to.
    #[error("serial error on {port}: {source}")]
    Serial {
        /// Port name that failed (e.g. `COM3`, `/dev/ttyUSB0`).
        port: String,
        /// Underlying `serialport` error.
        #[source]
        source: serialport::Error,
    },

    /// An I/O error occurred while driving a serial control line or a socket.
    #[error("i/o error: {0}")]
    SerialIo(#[from] std::io::Error),

    /// The `serialport` crate reported a port-level error (e.g. a control-line write).
    #[error("serial port error: {0}")]
    SerialPort(#[from] serialport::Error),

    /// The audio subsystem (cpal) rejected a request.
    #[error("audio error: {0}")]
    Audio(String),

    /// A wire frame was malformed and rejected before it could reach the keying path.
    #[error("protocol error: {0}")]
    Protocol(String),

    /// A configuration value was outside its documented range.
    #[error("invalid configuration: {0}")]
    Config(String),
}
