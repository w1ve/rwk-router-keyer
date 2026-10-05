//! Fail-safe conditions, latch policy, and the two health watchdogs.
//!
//! Port of `RWK.Shared.Primitives.FailSafeCondition`, `EdgeReplayerState`,
//! `RWK.Station.Replay.FailSafeMonitor` and `SchedulerWatchdog`.
//!
//! Every one of the ten conditions forces key-up. They differ in whether the SAFE latch
//! is set and how it clears:
//!
//! | Latch policy | Conditions |
//! |---|---|
//! | Manual Re-Arm required | F2, F5, F6, F7, F10 |
//! | Degraded, clears when valid edges resume | F1, F9 |
//! | No latch | F3 (key-up only), F4 (frame discarded), F8 (shutdown) |
//!
//! The checks here are pure functions of a [`ReplaySnapshot`], so they can be tested
//! without threads or hardware. The thread that calls them lives in the driver layer.

use std::time::Duration;

/// The ten enumerated fail-safe conditions. Numeric values match the F-number.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailSafeCondition {
    /// F1: no heartbeat or edge for 750 ms while key-down. Degraded, auto-clears.
    F1 = 1,
    /// F2: no heartbeat for 3 s while idle. Closes the session; manual Re-Arm.
    F2 = 2,
    /// F3: key down continuously beyond 10 s. Forces key-up, no latch.
    F3 = 3,
    /// F4: frame epoch does not match the session. Discard; no latch.
    F4 = 4,
    /// F5: sequence gap that cannot be inferred. Manual Re-Arm.
    F5 = 5,
    /// F6: serial port error or device removal. Manual Re-Arm.
    F6 = 6,
    /// F7: unhandled exception on the keying thread. Manual Re-Arm.
    F7 = 7,
    /// F8: application closing while the key is down. No latch.
    F8 = 8,
    /// F9: mesh path lost. Degraded, auto-clears.
    F9 = 9,
    /// F10: scheduler timing overrun beyond 250 ms. Manual Re-Arm.
    F10 = 10,
}

/// How a condition's SAFE latch behaves.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LatchPolicy {
    /// Set the SAFE latch; key stays locked until the operator re-arms.
    ManualReArm,
    /// Force key-up and mark the session degraded; clears when valid edges resume.
    AutoClear,
    /// Force key-up only; no latch.
    None,
}

impl FailSafeCondition {
    /// The latch policy for this condition.
    #[must_use]
    pub fn latch_policy(self) -> LatchPolicy {
        match self {
            Self::F2 | Self::F5 | Self::F6 | Self::F7 | Self::F10 => LatchPolicy::ManualReArm,
            Self::F1 | Self::F9 => LatchPolicy::AutoClear,
            Self::F3 | Self::F4 | Self::F8 => LatchPolicy::None,
        }
    }

    /// A short operator-facing description.
    #[must_use]
    pub fn description(self) -> &'static str {
        match self {
            Self::F1 => "No heartbeat or edge for 750ms while key-down; key forced up, session degraded (F1).",
            Self::F2 => "No heartbeat for 3 seconds while idle; session closed, SAFE latched (F2).",
            Self::F3 => "Key down continuously for >10 seconds; key forced up (F3).",
            Self::F4 => "Frame epoch does not match the session; frame discarded, key forced up (F4).",
            Self::F5 => "Sequence gap or timestamp regression; key forced up and SAFE latched (F5).",
            Self::F6 => "Serial port error or device removal; key forced up and SAFE latched (F6).",
            Self::F7 => "Unhandled exception on the keying thread; key forced up and SAFE latched (F7).",
            Self::F8 => "Application closing while keyed; key forced up (F8).",
            Self::F9 => "Mesh path lost; key forced up, session degraded (F9).",
            Self::F10 => "Scheduler timing overrun >250ms; key forced up and SAFE latched (F10).",
        }
    }
}

/// Operating state of the Station edge replayer.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EdgeReplayerState {
    /// Not started, or stopped. No replay thread running.
    #[default]
    Stopped = 0,
    /// Started and armed, with no edge traffic scheduled.
    Idle = 1,
    /// Scheduling and replaying edges normally.
    Active = 2,
    /// Impaired by F1 or F9; key forced up, resumes automatically.
    Degraded = 3,
    /// SAFE latch set; key output stays locked until a manual Re-Arm.
    SafeLatched = 4,
}

/// A point-in-time view of the replayer that the watchdogs evaluate.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ReplaySnapshot {
    /// Whether a session epoch is bound.
    pub session_active: bool,
    /// Whether the SAFE latch is set.
    pub safe_latched: bool,
    /// Whether the key line is currently asserted.
    pub key_down: bool,
    /// Clock value of the last heartbeat, or 0 if none.
    pub last_heartbeat: i64,
    /// Clock value of the last accepted datagram, or 0 if none.
    pub last_inbound: i64,
    /// Whether scheduled edges are still waiting to fire.
    pub has_pending_edges: bool,
    /// Clock value at which an edge was last keyed out, or 0 if none.
    pub last_edge_fired: i64,
}

/// Detects the timing conditions the replayer cannot see itself: F1, F2, F3.
#[derive(Debug, Clone)]
pub struct FailSafeMonitor {
    frequency: u64,
    f1_ticks: i64,
    f2_ticks: i64,
    f3_ticks: i64,
    // `Option` rather than a 0 sentinel: 0 is a legitimate clock value, and a sentinel
    // that collides with it silently disables F3 when a session starts at tick 0.
    key_down_start: Option<i64>,
    was_key_down: bool,
    path_lost: bool,
}

impl FailSafeMonitor {
    /// F1 threshold: no traffic while key-down.
    pub const F1_TIMEOUT: Duration = Duration::from_millis(750);
    /// F2 threshold: no heartbeat while idle.
    pub const F2_TIMEOUT: Duration = Duration::from_secs(3);
    /// F3 threshold: continuous key-down.
    pub const F3_MAX_DOWN: Duration = Duration::from_secs(10);
    /// Check interval for the monitoring thread.
    pub const CHECK_INTERVAL: Duration = Duration::from_millis(50);

    /// Creates a monitor for a clock running at `frequency` ticks per second.
    #[must_use]
    pub fn new(frequency: u64) -> Self {
        let frequency = frequency.max(1);
        Self {
            frequency,
            f1_ticks: Self::ticks_for(frequency, Self::F1_TIMEOUT),
            f2_ticks: Self::ticks_for(frequency, Self::F2_TIMEOUT),
            f3_ticks: Self::ticks_for(frequency, Self::F3_MAX_DOWN),
            key_down_start: None,
            was_key_down: false,
            path_lost: false,
        }
    }

    /// Records that the mesh path was lost; the next check raises F9.
    pub fn report_path_lost(&mut self) {
        self.path_lost = true;
    }

    /// Evaluates the conditions once, returning the first one that fired.
    pub fn check(&mut self, snapshot: &ReplaySnapshot, now: i64) -> Option<FailSafeCondition> {
        if !snapshot.session_active || snapshot.safe_latched {
            self.was_key_down = false;
            self.key_down_start = None;
            return None;
        }

        let last_traffic = snapshot.last_heartbeat.max(snapshot.last_inbound);

        // F1: no traffic at all while key-down.
        if snapshot.key_down && last_traffic > 0 && now - last_traffic >= self.f1_ticks {
            return Some(FailSafeCondition::F1);
        }

        // F2: no heartbeat while idle.
        if !snapshot.key_down && snapshot.last_heartbeat > 0 && now - snapshot.last_heartbeat >= self.f2_ticks {
            return Some(FailSafeCondition::F2);
        }

        // F3: continuous key-down beyond the maximum.
        if snapshot.key_down {
            if !self.was_key_down {
                self.key_down_start = Some(now);
                self.was_key_down = true;
            } else if let Some(start) = self.key_down_start {
                if now - start >= self.f3_ticks {
                    self.was_key_down = false;
                    self.key_down_start = None;
                    return Some(FailSafeCondition::F3);
                }
            }
        } else {
            self.was_key_down = false;
            self.key_down_start = None;
        }

        // F9: path loss flagged by the mesh layer.
        if self.path_lost {
            self.path_lost = false;
            return Some(FailSafeCondition::F9);
        }

        None
    }

    /// The clock tick rate this monitor was built for.
    #[must_use]
    pub fn frequency(&self) -> u64 {
        self.frequency
    }

    fn ticks_for(frequency: u64, duration: Duration) -> i64 {
        (duration.as_millis() as i64) * frequency as i64 / 1000
    }
}

/// Detects a stalled replay thread (F10).
#[derive(Debug, Clone)]
pub struct SchedulerWatchdog {
    overrun_ticks: i64,
}

impl SchedulerWatchdog {
    /// Overrun threshold.
    pub const OVERRUN_THRESHOLD: Duration = Duration::from_millis(250);
    /// Check interval for the watchdog thread.
    pub const CHECK_INTERVAL: Duration = Duration::from_millis(50);

    /// Creates a watchdog for a clock running at `frequency` ticks per second.
    #[must_use]
    pub fn new(frequency: u64) -> Self {
        let frequency = frequency.max(1);
        Self { overrun_ticks: Self::OVERRUN_THRESHOLD.as_millis() as i64 * frequency as i64 / 1000 }
    }

    /// Evaluates the overrun once, returning F10 when the replay thread has stalled.
    #[must_use]
    pub fn check(&self, snapshot: &ReplaySnapshot, now: i64) -> Option<FailSafeCondition> {
        // An idle replayer cannot overrun.
        if !snapshot.has_pending_edges || !snapshot.session_active || snapshot.safe_latched {
            return None;
        }
        if snapshot.last_edge_fired <= 0 {
            return None;
        }
        if now - snapshot.last_edge_fired >= self.overrun_ticks {
            return Some(FailSafeCondition::F10);
        }
        None
    }

    /// The overrun threshold in ticks.
    #[must_use]
    pub fn overrun_ticks(&self) -> i64 {
        self.overrun_ticks
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const GHZ: u64 = 1_000_000_000;
    const MS: i64 = 1_000_000;

    fn active_snapshot() -> ReplaySnapshot {
        ReplaySnapshot {
            session_active: true,
            safe_latched: false,
            key_down: false,
            last_heartbeat: 0,
            last_inbound: 0,
            has_pending_edges: false,
            last_edge_fired: 0,
        }
    }

    #[test]
    fn latch_policy_matches_the_specification() {
        for c in [FailSafeCondition::F2, FailSafeCondition::F5, FailSafeCondition::F6, FailSafeCondition::F7, FailSafeCondition::F10] {
            assert_eq!(c.latch_policy(), LatchPolicy::ManualReArm, "{c:?}");
        }
        for c in [FailSafeCondition::F1, FailSafeCondition::F9] {
            assert_eq!(c.latch_policy(), LatchPolicy::AutoClear, "{c:?}");
        }
        for c in [FailSafeCondition::F3, FailSafeCondition::F4, FailSafeCondition::F8] {
            assert_eq!(c.latch_policy(), LatchPolicy::None, "{c:?}");
        }
    }

    #[test]
    fn f_numbers_match_their_numeric_values() {
        assert_eq!(FailSafeCondition::F1 as u8, 1);
        assert_eq!(FailSafeCondition::F10 as u8, 10);
    }

    #[test]
    fn f1_fires_after_750ms_silence_while_key_down() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.key_down = true;
        snapshot.last_heartbeat = 1_000 * MS;
        snapshot.last_inbound = 1_000 * MS;

        // 500ms later: still fine.
        assert_eq!(monitor.check(&snapshot, 1_500 * MS), None);
        // 750ms later: F1.
        assert_eq!(monitor.check(&snapshot, 1_750 * MS), Some(FailSafeCondition::F1));
    }

    #[test]
    fn f1_does_not_fire_while_key_up() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.key_down = false;
        snapshot.last_heartbeat = 1_000 * MS;
        snapshot.last_inbound = 0;
        // Only F2's threshold (3s) applies here; 1s of silence is fine.
        assert_eq!(monitor.check(&snapshot, 2_000 * MS), None);
    }

    #[test]
    fn f2_fires_after_3s_without_a_heartbeat_while_idle() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.last_heartbeat = 1_000 * MS;
        assert_eq!(monitor.check(&snapshot, 3_900 * MS), None);
        assert_eq!(monitor.check(&snapshot, 4_000 * MS), Some(FailSafeCondition::F2));
    }

    #[test]
    fn f3_fires_after_ten_seconds_of_continuous_key_down() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.key_down = true;
        // Keep traffic fresh so F1 never fires first.
        for i in 0..=10_000i64 {
            snapshot.last_heartbeat = i * MS;
            snapshot.last_inbound = i * MS;
            let fired = monitor.check(&snapshot, i * MS);
            if let Some(c) = fired {
                assert_eq!(c, FailSafeCondition::F3, "only F3 should fire while traffic is fresh");
                assert!(i >= 9_950, "F3 fired far too early at {i}ms");
                return;
            }
        }
        panic!("F3 never fired despite 10s of continuous key-down");
    }

    #[test]
    fn f9_fires_once_after_the_path_is_lost() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let snapshot = active_snapshot();
        monitor.report_path_lost();
        assert_eq!(monitor.check(&snapshot, 1_000 * MS), Some(FailSafeCondition::F9));
        // The flag is consumed: it does not repeat.
        assert_eq!(monitor.check(&snapshot, 1_001 * MS), None);
    }

    #[test]
    fn monitor_is_silent_without_an_active_session_or_when_latched() {
        let mut monitor = FailSafeMonitor::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.last_heartbeat = 1 * MS;
        snapshot.session_active = false;
        assert_eq!(monitor.check(&snapshot, 999_999 * MS), None);

        snapshot.session_active = true;
        snapshot.safe_latched = true;
        assert_eq!(monitor.check(&snapshot, 999_999 * MS), None);
    }

    #[test]
    fn watchdog_fires_f10_when_pending_edges_stall() {
        let watchdog = SchedulerWatchdog::new(GHZ);
        let mut snapshot = active_snapshot();
        snapshot.has_pending_edges = true;
        snapshot.last_edge_fired = 1_000 * MS;

        assert_eq!(watchdog.check(&snapshot, 1_100 * MS), None, "100ms is not an overrun");
        assert_eq!(watchdog.check(&snapshot, 1_300 * MS), Some(FailSafeCondition::F10));
    }

    #[test]
    fn watchdog_is_silent_when_the_replayer_is_legitimately_idle() {
        let watchdog = SchedulerWatchdog::new(GHZ);
        let mut snapshot = active_snapshot();
        // No pending edges: an idle replayer cannot overrun.
        snapshot.last_edge_fired = 1_000 * MS;
        assert_eq!(watchdog.check(&snapshot, 99_000 * MS), None);

        // Pending, but never fired anything yet.
        snapshot.has_pending_edges = true;
        snapshot.last_edge_fired = 0;
        assert_eq!(watchdog.check(&snapshot, 99_000 * MS), None);
    }
}
