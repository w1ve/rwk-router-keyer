//! Replay anchor: session-relative timestamps to absolute deadlines.
//!
//! Port of `RWK.Station.Replay.ReplayAnchor`.
//!
//! [`ReplayAnchor::anchor_qpc`] is the clock value that edge timestamp 0 of the current
//! burst maps to, so a deadline is a single multiply-add:
//! `deadline = anchor + ticks_for_ms(timestamp_ms)`. It is established from the burst's
//! first edge as `arrival + D - ticks_for_ms(first_timestamp_ms)`, which makes that edge
//! replay at `arrival + D` while every later edge inherits the Client's spacing.
//!
//! Adding `D` to each edge's own arrival instead would import that datagram's network
//! jitter into the keying, which is precisely what the buffer exists to remove.
//!
//! A new anchor is established when no edge has arrived for [`ReplayAnchor::DEFAULT_IDLE_RESET`],
//! which stops Client/Station clock drift accumulating across an operating session.

use std::time::Duration;

/// Turns an edge's session-relative timestamp into an absolute deadline.
#[derive(Debug, Clone)]
pub struct ReplayAnchor {
    frequency: u64,
    idle_reset_ticks: i64,
    anchored: bool,
    anchor_qpc: i64,
    last_arrival_qpc: i64,
    anchor_count: u64,
}

impl ReplayAnchor {
    /// Idle period after which the next edge re-anchors the burst.
    pub const DEFAULT_IDLE_RESET: Duration = Duration::from_secs(2);

    /// Creates an anchor for a clock running at `frequency` ticks per second.
    ///
    /// An `idle_reset` of zero is treated as the default rather than as
    /// "re-anchor every edge", which would reintroduce per-datagram jitter.
    #[must_use]
    pub fn new(frequency: u64, idle_reset: Option<Duration>) -> Self {
        let reset = match idle_reset {
            Some(value) if value > Duration::ZERO => value,
            _ => Self::DEFAULT_IDLE_RESET,
        };
        let idle_reset_ticks = Self::ticks_for_milliseconds(reset.as_millis() as i64, frequency);
        Self { frequency, idle_reset_ticks, anchored: false, anchor_qpc: 0, last_arrival_qpc: 0, anchor_count: 0 }
    }

    /// Whether an anchor is currently established.
    #[must_use]
    pub fn is_anchored(&self) -> bool {
        self.anchored
    }

    /// The clock value that edge timestamp 0 maps to; meaningless unless anchored.
    #[must_use]
    pub fn anchor_qpc(&self) -> i64 {
        self.anchor_qpc
    }

    /// Arrival timestamp of the most recently scheduled edge.
    #[must_use]
    pub fn last_arrival_qpc(&self) -> i64 {
        self.last_arrival_qpc
    }

    /// Idle period that forces re-anchoring, in ticks.
    #[must_use]
    pub fn idle_reset_ticks(&self) -> i64 {
        self.idle_reset_ticks
    }

    /// Idle period that forces re-anchoring.
    #[must_use]
    pub fn idle_reset(&self) -> Duration {
        Duration::from_secs_f64(self.idle_reset_ticks as f64 / self.frequency as f64)
    }

    /// How many anchors have been established, including the first.
    #[must_use]
    pub fn anchor_count(&self) -> u64 {
        self.anchor_count
    }

    /// Whether an edge arriving at `arrival_qpc` would establish a new anchor.
    #[must_use]
    pub fn would_reanchor(&self, arrival_qpc: i64) -> bool {
        !self.anchored || (arrival_qpc - self.last_arrival_qpc) >= self.idle_reset_ticks
    }

    /// Returns the absolute deadline at which an edge should be replayed.
    ///
    /// Returns the deadline and whether this call established a new anchor.
    pub fn schedule(&mut self, arrival_qpc: i64, timestamp_ms: u32, delay_ticks: i64) -> (i64, bool) {
        let delay_ticks = delay_ticks.max(0);
        let relative = Self::ticks_for_milliseconds(i64::from(timestamp_ms), self.frequency);

        let reanchored = self.would_reanchor(arrival_qpc);
        if reanchored {
            // Anchor so this edge lands exactly at arrival + D.
            self.anchor_qpc = arrival_qpc + delay_ticks - relative;
            self.anchored = true;
            self.anchor_count += 1;
        }

        self.last_arrival_qpc = arrival_qpc;
        (self.anchor_qpc + relative, reanchored)
    }

    /// Drops the anchor, so the next edge anchors afresh.
    pub fn reset(&mut self) {
        self.anchored = false;
        self.anchor_qpc = 0;
        self.last_arrival_qpc = 0;
    }

    /// Converts a session-relative millisecond timestamp to clock ticks.
    ///
    /// Evaluated in two parts so a full 32-bit millisecond value cannot overflow.
    #[must_use]
    pub fn ticks_for_milliseconds(milliseconds: i64, frequency: u64) -> i64 {
        if milliseconds <= 0 || frequency == 0 {
            return 0;
        }
        let whole = milliseconds / 1000 * frequency as i64;
        let fraction = milliseconds % 1000 * frequency as i64 / 1000;
        whole + fraction
    }

    /// Converts clock ticks to milliseconds, for telemetry and logging.
    #[must_use]
    pub fn milliseconds_for_ticks(ticks: i64, frequency: u64) -> f64 {
        if frequency == 0 {
            0.0
        } else {
            ticks as f64 * 1000.0 / frequency as f64
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const GHZ: u64 = 1_000_000_000;

    #[test]
    fn first_edge_lands_exactly_at_arrival_plus_delay() {
        let mut anchor = ReplayAnchor::new(GHZ, None);
        // Delay 200ms = 200_000_000 ticks; edge timestamp 5000ms.
        let (deadline, reanchored) = anchor.schedule(1_000_000_000, 5_000, 200_000_000);
        assert!(reanchored);
        let expected = 1_000_000_000 + 200_000_000;
        assert_eq!(deadline, expected, "the anchoring edge replays at arrival + D");
        assert_eq!(anchor.anchor_count(), 1);
    }

    #[test]
    fn later_edges_inherit_the_first_edges_spacing() {
        let mut anchor = ReplayAnchor::new(GHZ, None);
        let (first, _) = anchor.schedule(1_000_000_000, 5_000, 200_000_000);
        // 100ms later in stream time, but arriving 5ms later with jitter.
        let (second, reanchored) = anchor.schedule(1_005_000_000, 5_100, 200_000_000);
        assert!(!reanchored);
        assert_eq!(
            second - first,
            100_000_000,
            "deadlines follow the Client's spacing, not each datagram's arrival jitter"
        );
    }

    #[test]
    fn idle_period_reanchors_and_only_the_burst_is_offset() {
        let mut anchor = ReplayAnchor::new(GHZ, None);
        anchor.schedule(1_000_000_000, 0, 0);
        assert!(!anchor.would_reanchor(1_500_000_000), "within the idle window");
        // Two seconds later: re-anchor.
        assert!(anchor.would_reanchor(4_000_000_000));
        let (_, reanchored) = anchor.schedule(4_000_000_000, 300_000, 0);
        assert!(reanchored);
        assert_eq!(anchor.anchor_count(), 2);
    }

    #[test]
    fn zero_idle_reset_falls_back_to_the_default() {
        let anchor = ReplayAnchor::new(GHZ, Some(Duration::ZERO));
        assert_eq!(anchor.idle_reset(), ReplayAnchor::DEFAULT_IDLE_RESET);
    }

    #[test]
    fn reset_drops_the_anchor_so_the_next_edge_reanchors() {
        let mut anchor = ReplayAnchor::new(GHZ, None);
        anchor.schedule(1_000, 10, 0);
        assert!(anchor.is_anchored());
        anchor.reset();
        assert!(!anchor.is_anchored());
        assert!(anchor.would_reanchor(1_001));
    }

    #[test]
    fn negative_delay_is_treated_as_zero() {
        let mut anchor = ReplayAnchor::new(GHZ, None);
        let (deadline, _) = anchor.schedule(1_000_000_000, 0, -500);
        assert_eq!(deadline, 1_000_000_000);
    }

    #[test]
    fn tick_conversion_handles_full_u32_milliseconds() {
        // 4_294_967_295 ms is the largest uint timestamp; must not overflow.
        let ticks = ReplayAnchor::ticks_for_milliseconds(4_294_967_295, GHZ);
        assert_eq!(ticks, 4_294_967_295_000_000);
        assert_eq!(ReplayAnchor::ticks_for_milliseconds(0, GHZ), 0);
        assert_eq!(ReplayAnchor::ticks_for_milliseconds(-5, GHZ), 0);
        assert_eq!(ReplayAnchor::ticks_for_milliseconds(1000, 0), 0);
    }

    #[test]
    fn milliseconds_for_ticks_is_the_inverse() {
        assert!((ReplayAnchor::milliseconds_for_ticks(60_000_000, GHZ) - 60.0).abs() < 1e-9);
        assert_eq!(ReplayAnchor::milliseconds_for_ticks(60_000_000, 0), 0.0);
    }
}
