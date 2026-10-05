//! Hardware and OS integration.
//!
//! * [`serial`] — serial port enumeration and DTR/RTS key/PTT line control.
//! * [`keying`] — Morse element timing, scheduling and the paddle decider.
//! * [`audio`] — the keyed-sine sidetone generator and its cpal output stream.
//! * [`network`] — native UDP edge transport and port forwarding (no sidecar).
//! * [`replay`] — the Station edge replayer: jitter buffer, fail-safes, watchdogs.
//! * [`bus`] — the broadcast event bus that feeds the UI.

pub mod audio;
pub mod bus;
pub mod keying;
pub mod network;
pub mod replay;
pub mod serial;

pub use bus::{CoreEvent, EventBus, EventReceiver};
pub use network::PathHealth;
pub use keying::{
    EdgeEvent, EdgeSchedule, EdgeScheduleBuilder, ElementKeyer, KeyerElement, KeyerElementTiming,
    PaddleElementEngine, PaddleState,
};
pub use serial::{enumerate_ports, KeyingOutputConfig, PttTiming, SerialKeyingOutput};
