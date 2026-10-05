//! Station edge replay.
//!
//! Port of the `RWK.Station.Replay` namespace: the Station accepts `RWK-PADDLE`
//! datagrams from the Client, validates them against the session, buffers them to absorb
//! network jitter, and keys the radio at absolute deadlines.
//!
//! The pieces are deliberately separate and each testable alone:
//!
//! * [`tracker`] — epoch, duplicate, gap and timestamp classification.
//! * [`jitter`] — playout delay selection, bands and adaptation.
//! * [`anchor`] — session-relative timestamps to absolute deadlines.
//! * [`failsafe`] — the ten fail-safe conditions, latch policy, and the two watchdogs.
//! * [`replayer`] — the plumbing that ties them together.
//! * [`driver`] — the dedicated replay and watchdog threads that drive the core.
//!
//! The engine is synchronous and timestamp-explicit, so the safety logic does not depend
//! on scheduler luck; the dedicated replay task that calls
//! [`EdgeReplayer::tick`](replayer::EdgeReplayer::tick) lives above this module.

pub mod anchor;
pub mod driver;
pub mod failsafe;
pub mod jitter;
pub mod replayer;
pub mod tracker;

pub use anchor::ReplayAnchor;
pub use driver::{spawn as spawn_driver, DriverHandle, InboundPacket, SerialKeyingAdapter};
pub use failsafe::{
    EdgeReplayerState, FailSafeCondition, FailSafeMonitor, LatchPolicy, ReplaySnapshot,
    SchedulerWatchdog,
};
pub use jitter::{EdgeJitterProfile, JitterBuffer, JitterBufferConfig};
pub use replayer::{EdgeReplayer, EdgeReplayerTelemetry, KeyingOutput, ReplayEvent};
pub use tracker::{EdgeSequenceTracker, EdgeValidationOutcome, EdgeValidationResult, FailSafeTrigger};
