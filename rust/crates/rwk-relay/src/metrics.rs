//! Time-series metrics on top of the core relay counters.
//!
//! [`RelayMetrics`](rwk_core::engine::relay::RelayMetrics) exposes cumulative atomics. The
//! dashboard wants two more things: *rates* (bytes/sec, not just a total) and *history* (a
//! chart, not just a number). This module samples the core counters on a fixed interval into
//! a bounded ring buffer, computing the per-interval deltas as rates. The HTTP layer reads a
//! [`Snapshot`] of the current values plus the recent history.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use rwk_core::engine::relay::RelayMetrics;
use serde::Serialize;

/// How often the sampler records a point.
pub const SAMPLE_SECS: u64 = 2;

/// How many samples of history to keep (≈ this many × `SAMPLE_SECS` seconds on the chart).
const HISTORY_LEN: usize = 300; // 10 minutes at 2s cadence

/// One sampled point: the instantaneous rates and gauges at a moment.
#[derive(Debug, Clone, Serialize)]
pub struct Sample {
    /// Unix epoch seconds.
    pub t: u64,
    /// Nodes registered at this instant.
    pub active: usize,
    /// Payload bytes/second relayed over the last interval.
    pub bytes_per_sec: u64,
    /// Datagrams/second relayed over the last interval.
    pub frames_per_sec: u64,
}

/// The dashboard payload: current cumulative counters plus recent history.
#[derive(Debug, Clone, Serialize)]
pub struct Snapshot {
    /// Seconds the relay has been running.
    pub uptime_secs: u64,
    /// Nodes currently registered.
    pub active: usize,
    /// Connections accepted and registered since start.
    pub total_connections: u64,
    /// Distinct conversations relayed since start.
    pub total_pairs_served: u64,
    /// Total payload bytes relayed since start.
    pub bytes_relayed: u64,
    /// Total datagrams relayed since start.
    pub frames_relayed: u64,
    /// Datagrams dropped for want of a route.
    pub frames_dropped_no_route: u64,
    /// Datagrams dropped by the per-connection rate cap.
    pub frames_dropped_rate: u64,
    /// Registrations refused at the registration cap.
    pub rejected_full: u64,
    /// Connections closed for a bad first frame.
    pub rejected_bad_register: u64,
    /// Instantaneous relayed throughput, bytes/second (last interval).
    pub bytes_per_sec: u64,
    /// Recent samples, oldest first, for charting.
    pub history: Vec<Sample>,
}

/// Samples the core metrics into a shared ring buffer.
///
/// Clone-cheap (`Arc`): the sampler task holds one, the HTTP handlers another.
#[derive(Clone)]
pub struct MetricsHistory {
    core: Arc<RelayMetrics>,
    inner: Arc<Mutex<Inner>>,
}

struct Inner {
    started: SystemTime,
    history: VecDeque<Sample>,
    last_bytes: u64,
    last_frames: u64,
}

impl MetricsHistory {
    /// Wraps the relay's core counters.
    #[must_use]
    pub fn new(core: Arc<RelayMetrics>) -> Self {
        Self {
            core,
            inner: Arc::new(Mutex::new(Inner {
                started: SystemTime::now(),
                history: VecDeque::with_capacity(HISTORY_LEN),
                last_bytes: 0,
                last_frames: 0,
            })),
        }
    }

    /// Records one sample from the current counters. Call every [`SAMPLE_SECS`] seconds.
    pub fn sample(&self) {
        let bytes = self.core.bytes_relayed();
        let frames = self.core.frames_relayed();
        let active = self.core.active();

        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        // Deltas since the last sample become per-interval rates. `saturating_sub` guards the
        // first sample and any (impossible) counter reset.
        let d_bytes = bytes.saturating_sub(inner.last_bytes);
        let d_frames = frames.saturating_sub(inner.last_frames);
        inner.last_bytes = bytes;
        inner.last_frames = frames;

        let sample = Sample {
            t: now_secs(),
            active,
            bytes_per_sec: d_bytes / SAMPLE_SECS,
            frames_per_sec: d_frames / SAMPLE_SECS,
        };
        if inner.history.len() == HISTORY_LEN {
            inner.history.pop_front();
        }
        inner.history.push_back(sample);
    }

    /// Builds the current snapshot for the dashboard.
    #[must_use]
    pub fn snapshot(&self) -> Snapshot {
        let inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        let uptime_secs = inner.started.elapsed().map(|d| d.as_secs()).unwrap_or(0);
        let bytes_per_sec = inner.history.back().map(|s| s.bytes_per_sec).unwrap_or(0);
        Snapshot {
            uptime_secs,
            active: self.core.active(),
            total_connections: self.core.total_connections(),
            total_pairs_served: self.core.total_pairs_served(),
            bytes_relayed: self.core.bytes_relayed(),
            frames_relayed: self.core.frames_relayed(),
            frames_dropped_no_route: self.core.frames_dropped_no_route(),
            frames_dropped_rate: self.core.frames_dropped_rate(),
            rejected_full: self.core.rejected_full(),
            rejected_bad_register: self.core.rejected_bad_register(),
            bytes_per_sec,
            history: inner.history.iter().cloned().collect(),
        }
    }
}

/// Current Unix epoch seconds.
fn now_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}
