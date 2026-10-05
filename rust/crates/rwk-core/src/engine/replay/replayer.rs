//! The Station edge replayer.
//!
//! Port of `RWK.Station.Replay.EdgeReplayer` and the `IEdgeReplayer` contract.
//!
//! **Division of labour.** This type is the plumbing between four pieces that are each
//! testable on their own: [`RwkPaddleFrame`](crate::protocol::edge::RwkPaddleFrame)
//! parses, [`EdgeSequenceTracker`] classifies, [`JitterBuffer`] chooses the delay, and
//! [`ReplayAnchor`] converts a session-relative timestamp to an absolute deadline.
//! Nothing those types decide is re-derived here.
//!
//! **Driving it.** In the .NET build this class owns a `TIME_CRITICAL` thread. Here the
//! engine is synchronous and timestamp-explicit — [`EdgeReplayer::tick`] takes `now` and
//! the keying sink — so every rule is testable without threads or hardware. The driver
//! that calls `tick` on a dedicated task lives above this module, which keeps the
//! safety logic free of scheduler luck.
//!
//! **Fail-safes.** Any condition detected here forces key and PTT up immediately; the
//! *latch policy* is then applied through [`FailSafeCondition::latch_policy`], matching
//! the fail-safe monitor. F4 alone is handled purely here (discard, no latch).
//!
//! **Zero allocation while keying** was a .NET goal (ring buffers, `stackalloc`). The
//! port keeps the bounded queues but allows small allocations on the validation path;
//! the timing-critical wait remains allocation-free in
//! [`ElementKeyer`](crate::engine::keying::ElementKeyer).

use std::collections::VecDeque;
use std::time::Duration;

use crate::engine::serial::{PttSequencer, PttTiming};
use crate::primitives::PathType;
use crate::protocol::edge::RwkPaddleFrame;
use crate::timing::Clock;

use super::anchor::ReplayAnchor;
use super::failsafe::{EdgeReplayerState, FailSafeCondition, LatchPolicy, ReplaySnapshot};
use super::jitter::{EdgeJitterProfile, JitterBuffer, JitterBufferConfig};
use super::tracker::{EdgeSequenceTracker, EdgeValidationOutcome};

/// Inbound datagram queue capacity, in frames.
pub const INBOUND_CAPACITY: usize = 256;
/// Pending scheduled edge capacity, in edges.
pub const PENDING_CAPACITY: usize = 512;
/// Longest the replay loop idles when it has nothing scheduled.
pub const MAX_IDLE_WAIT: Duration = Duration::from_millis(10);

/// The key/PTT output the replayer drives.
pub trait KeyingOutput {
    /// Asserts or releases the key line.
    fn set_key(&mut self, key_down: bool);
    /// Asserts or releases the PTT line. The default is a no-op for outputs with no PTT.
    fn set_ptt(&mut self, _asserted: bool) {}
}

/// A point-in-time snapshot of the replayer's counters and timing.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EdgeReplayerTelemetry {
    /// Datagrams accepted for processing.
    pub frames_received: u64,
    /// Datagrams dropped before validation.
    pub frames_dropped: u64,
    /// Edges validated and scheduled.
    pub edges_applied: u64,
    /// Edges whose deadline was reached and keyed out.
    pub edges_replayed: u64,
    /// Edges discarded as already seen.
    pub duplicate_edges: u64,
    /// Anchors established, including the first.
    pub anchor_count: u64,
    /// Edges whose deadline had already passed when scheduled.
    pub late_edges: u64,
    /// Largest lateness observed at scheduling time.
    pub max_lateness: Duration,
    /// Largest difference between an edge's fire time and its deadline.
    pub max_replay_error: Duration,
    /// Times the pending queue was full when an edge needed scheduling.
    pub pending_overflows: u64,
    /// The jitter buffer delay currently in force.
    pub current_delay: Duration,
    /// Current RTT EWMA.
    pub rtt_ewma: Duration,
    /// Current jitter EWMA.
    pub jitter_ewma: Duration,
}

/// Something the replayer wants the UI or a monitor to know about.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReplayEvent {
    /// The operating state or SAFE latch changed.
    StateChanged {
        /// The new state.
        state: EdgeReplayerState,
        /// Whether the SAFE latch is now set.
        safe_latched: bool,
    },
    /// A fail-safe condition fired; key output was already forced up.
    FailSafe {
        /// Which condition fired.
        condition: FailSafeCondition,
    },
}

/// One parsed datagram waiting to be validated, with its arrival stamp.
#[derive(Debug, Clone, Copy)]
struct InboundFrame {
    frame: RwkPaddleFrame,
    arrival: i64,
}

/// One validated edge waiting for its deadline.
#[derive(Debug, Clone, Copy)]
struct ScheduledEdge {
    deadline: i64,
    key_down: bool,
}

/// Receives edge datagrams, buffers them for jitter, and schedules precise replay.
pub struct EdgeReplayer {
    frequency: u64,
    jitter: JitterBuffer,
    anchor: ReplayAnchor,
    tracker: Option<EdgeSequenceTracker>,
    pending: VecDeque<ScheduledEdge>,
    inbound: VecDeque<InboundFrame>,
    state: EdgeReplayerState,
    safe_latched: bool,
    session_active: bool,
    last_scheduled_deadline: Option<i64>,
    last_heartbeat: i64,
    last_inbound: i64,
    last_edge_fired: i64,
    last_key_down_flag: bool,
    ptt: Option<PttSequencer>,
    ptt_lead_ticks: i64,
    ptt_asserted: bool,
    events: Vec<ReplayEvent>,
    frames_received: u64,
    frames_dropped: u64,
    edges_applied: u64,
    edges_replayed: u64,
    duplicate_edges: u64,
    late_edges: u64,
    max_lateness: i64,
    max_replay_error: i64,
    pending_overflows: u64,
}

impl EdgeReplayer {
    /// Creates a replayer for a clock running at `frequency` ticks per second.
    ///
    /// `ptt_timing` of [`None`] mirrors a PTT line of `None`: the lead/tail sequencing
    /// is skipped entirely.
    #[must_use]
    pub fn new(
        frequency: u64,
        jitter_config: JitterBufferConfig,
        profile: EdgeJitterProfile,
        path: PathType,
        ptt_timing: Option<PttTiming>,
    ) -> Self {
        let frequency = frequency.max(1);
        let ptt = ptt_timing.map(|t| {
            let lead = t.lead;
            // `PttSequencer::new` only fails for a zero tick rate, which `max(1)` excludes.
            let seq = PttSequencer::new(t, frequency).expect("non-zero tick rate");
            (seq, lead)
        });
        let (ptt_sequencer, ptt_lead) = match ptt {
            Some((seq, lead)) => (Some(seq), ReplayAnchor::ticks_for_milliseconds(lead.as_millis() as i64, frequency)),
            None => (None, 0),
        };

        Self {
            frequency,
            jitter: JitterBuffer::new(jitter_config, profile, path),
            anchor: ReplayAnchor::new(frequency, None),
            tracker: None,
            pending: VecDeque::new(),
            inbound: VecDeque::new(),
            state: EdgeReplayerState::Stopped,
            safe_latched: false,
            session_active: false,
            last_scheduled_deadline: None,
            last_heartbeat: 0,
            last_inbound: 0,
            last_edge_fired: 0,
            last_key_down_flag: false,
            ptt: ptt_sequencer,
            ptt_lead_ticks: ptt_lead,
            ptt_asserted: false,
            events: Vec::new(),
            frames_received: 0,
            frames_dropped: 0,
            edges_applied: 0,
            edges_replayed: 0,
            duplicate_edges: 0,
            late_edges: 0,
            max_lateness: 0,
            max_replay_error: 0,
            pending_overflows: 0,
        }
    }

    /// Creates a replayer bound to a [`Clock`], using its frequency.
    #[must_use]
    pub fn with_clock(clock: &dyn Clock, profile: EdgeJitterProfile, path: PathType, ptt_timing: Option<PttTiming>) -> Self {
        Self::new(clock.frequency(), JitterBufferConfig::default(), profile, path, ptt_timing)
    }

    // ─── State ───────────────────────────────────────────────────────────────

    /// The current operating state.
    #[must_use]
    pub fn state(&self) -> EdgeReplayerState {
        self.state
    }

    /// Whether key output is locked by the SAFE latch.
    #[must_use]
    pub fn is_safe_latched(&self) -> bool {
        self.safe_latched
    }

    /// Whether a session epoch is bound.
    #[must_use]
    pub fn is_session_active(&self) -> bool {
        self.session_active
    }

    /// Whether the key line is currently asserted.
    #[must_use]
    pub fn is_key_down(&self) -> bool {
        self.last_key_down_flag
    }

    /// Whether PTT is currently asserted.
    #[must_use]
    pub fn is_ptt_asserted(&self) -> bool {
        self.ptt_asserted
    }

    /// Scheduled edges still waiting to fire.
    #[must_use]
    pub fn pending_count(&self) -> usize {
        self.pending.len()
    }

    /// Clock value of the last accepted datagram, or 0 if none.
    #[must_use]
    pub fn last_inbound(&self) -> i64 {
        self.last_inbound
    }

    /// Clock value of the last heartbeat, or 0 if none.
    #[must_use]
    pub fn last_heartbeat(&self) -> i64 {
        self.last_heartbeat
    }

    /// The jitter buffer, exposed so path type and RTT samples can be fed to it.
    #[must_use]
    pub fn jitter(&self) -> &JitterBuffer {
        &self.jitter
    }

    /// Mutable access to the jitter buffer, for RTT and path updates.
    pub fn jitter_mut(&mut self) -> &mut JitterBuffer {
        &mut self.jitter
    }

    /// Drains the events recorded since the last call.
    pub fn take_events(&mut self) -> Vec<ReplayEvent> {
        std::mem::take(&mut self.events)
    }

    /// A snapshot for the fail-safe watchdogs.
    #[must_use]
    pub fn snapshot(&self) -> ReplaySnapshot {
        ReplaySnapshot {
            session_active: self.session_active,
            safe_latched: self.safe_latched,
            key_down: self.last_key_down_flag,
            last_heartbeat: self.last_heartbeat,
            last_inbound: self.last_inbound,
            has_pending_edges: !self.pending.is_empty(),
            last_edge_fired: self.last_edge_fired,
        }
    }

    /// A telemetry snapshot.
    #[must_use]
    pub fn telemetry(&self) -> EdgeReplayerTelemetry {
        let freq = self.frequency;
        EdgeReplayerTelemetry {
            frames_received: self.frames_received,
            frames_dropped: self.frames_dropped,
            edges_applied: self.edges_applied,
            edges_replayed: self.edges_replayed,
            duplicate_edges: self.duplicate_edges,
            anchor_count: self.anchor.anchor_count(),
            late_edges: self.late_edges,
            max_lateness: Duration::from_secs_f64(ReplayAnchor::milliseconds_for_ticks(self.max_lateness, freq) / 1000.0),
            max_replay_error: Duration::from_secs_f64(
                ReplayAnchor::milliseconds_for_ticks(self.max_replay_error, freq) / 1000.0,
            ),
            pending_overflows: self.pending_overflows,
            current_delay: self.jitter.current_delay(),
            rtt_ewma: Duration::from_secs_f64(self.jitter.rtt_ewma_ms() / 1000.0),
            jitter_ewma: Duration::from_secs_f64(self.jitter.jitter_ewma_ms() / 1000.0),
        }
    }

    /// When the replay loop should next wake.
    #[must_use]
    pub fn next_wake(&self, now: i64) -> i64 {
        let mut wake = now + ReplayAnchor::ticks_for_milliseconds(MAX_IDLE_WAIT.as_millis() as i64, self.frequency);
        if let Some(edge) = self.pending.front() {
            let fire_at = self.effective_fire(*edge);
            if fire_at < wake {
                wake = fire_at;
            }
        }
        wake
    }

    // ─── Session control ─────────────────────────────────────────────────────

    /// Binds the replayer to a session epoch, clearing sequence, anchor and adaptation state.
    ///
    /// Call only for genuine session establishment or reconnect: it discards the verified
    /// sequence baseline, so the next edge (key-down included) is applied as a fresh
    /// baseline instead of raising F5.
    pub fn begin_session(&mut self, epoch: u16) {
        match &mut self.tracker {
            Some(tracker) => tracker.begin_session(epoch),
            None => self.tracker = Some(EdgeSequenceTracker::new(epoch)),
        }
        self.inbound.clear();
        self.pending.clear();
        self.anchor.reset();
        self.last_scheduled_deadline = None;
        self.jitter.reset_samples();
        self.session_active = true;
        self.safe_latched = false;
        self.last_key_down_flag = false;
        self.last_heartbeat = 0;
        self.last_inbound = 0;
        self.set_state(EdgeReplayerState::Idle);
    }

    /// Ends the session: forces key and PTT up and stops accepting edges.
    pub fn end_session(&mut self, keying: &mut dyn KeyingOutput) {
        self.session_active = false;
        self.tracker = None;
        self.inbound.clear();
        self.force_key_up(keying);
        self.jitter.reset_samples();
        self.set_state(EdgeReplayerState::Stopped);
    }

    /// Marks the replay thread as started, so the state is Idle rather than Stopped.
    pub fn start(&mut self) {
        if self.state == EdgeReplayerState::Stopped {
            self.set_state(EdgeReplayerState::Idle);
        }
    }

    /// Stops the replayer, forcing the key up first.
    pub fn stop(&mut self, keying: &mut dyn KeyingOutput) {
        self.force_key_up(keying);
        self.session_active = false;
        self.set_state(EdgeReplayerState::Stopped);
    }

    // ─── Inbound ─────────────────────────────────────────────────────────────

    /// Hands one received `RWK-PADDLE` datagram to the replayer.
    ///
    /// Returns immediately; validation and scheduling happen in [`Self::tick`]. Never
    /// panics for malformed input.
    pub fn process_datagram(&mut self, data: &[u8], arrival: i64) {
        if !self.session_active || self.safe_latched {
            self.frames_dropped += 1;
            return;
        }

        let Some((frame, _)) = RwkPaddleFrame::read_from(data) else {
            self.frames_dropped += 1;
            return;
        };

        if self.inbound.len() >= INBOUND_CAPACITY {
            self.frames_dropped += 1;
            return;
        }

        self.inbound.push_back(InboundFrame { frame, arrival });
        self.frames_received += 1;
        self.last_inbound = arrival;
    }

    /// Records that a heartbeat arrived; feeds F1 and F2.
    pub fn process_heartbeat(&mut self, now: i64) {
        self.last_heartbeat = now;
        self.last_inbound = now;
    }

    /// Sets the SAFE latch and forces key and PTT up.
    pub fn latch_safe(&mut self, condition: FailSafeCondition, keying: &mut dyn KeyingOutput) {
        self.safe_latched = true;
        self.force_key_up(keying);
        self.events.push(ReplayEvent::FailSafe { condition });
        self.set_state(EdgeReplayerState::SafeLatched);
    }

    /// Clears the SAFE latch so keying can resume (the Re-Arm action).
    pub fn clear_safe_latch(&mut self) {
        if !self.safe_latched {
            return;
        }
        self.safe_latched = false;
        // Timing state from before the latch is meaningless: re-anchor on the next edge.
        // The sequence baseline is deliberately left alone, or a key-down behind a gap
        // would be applied as a fresh baseline instead of raising F5.
        self.anchor.reset();
        self.last_scheduled_deadline = None;
        self.set_state(if self.session_active { EdgeReplayerState::Idle } else { EdgeReplayerState::Stopped });
    }

    /// Marks the session degraded for an auto-clearing condition (F1, F9).
    ///
    /// The key has already been forced up; normal operation resumes when valid edges
    /// arrive, so no latch is set.
    pub fn mark_degraded(&mut self) {
        if self.session_active && !self.safe_latched {
            self.set_state(EdgeReplayerState::Degraded);
        }
    }

    /// Forces key and PTT up immediately, discarding pending edges and the anchor.
    pub fn force_key_up(&mut self, keying: &mut dyn KeyingOutput) {
        keying.set_key(false);
        keying.set_ptt(false);
        if let Some(ptt) = &mut self.ptt {
            let _ = ptt.force_release();
        }
        self.ptt_asserted = false;
        self.last_key_down_flag = false;
        self.pending.clear();
        self.anchor.reset();
        self.last_scheduled_deadline = None;
    }

    // ─── Replay ──────────────────────────────────────────────────────────────

    /// Validates queued datagrams, schedules edges, fires everything due at `now`.
    ///
    /// This is the replay loop body. Fail-safe conditions detected here are applied
    /// immediately and reported through the returned events.
    pub fn tick(&mut self, now: i64, keying: &mut dyn KeyingOutput) {
        self.drain_inbound(now, keying);
        self.fire_due(now, keying);
        if let Some(ptt) = &mut self.ptt {
            if let Some(action) = ptt.poll(now.max(0) as u64) {
                match action {
                    crate::engine::serial::PttAction::Release { .. } => {
                        self.ptt_asserted = false;
                        keying.set_ptt(false);
                    }
                    crate::engine::serial::PttAction::Assert { .. } => {
                        self.ptt_asserted = true;
                        keying.set_ptt(true);
                    }
                }
            }
        }
    }

    fn drain_inbound(&mut self, now: i64, keying: &mut dyn KeyingOutput) {
        let Some(mut tracker) = self.tracker.take() else {
            self.inbound.clear();
            return;
        };

        while let Some(inbound) = self.inbound.pop_front() {
            if self.safe_latched {
                self.frames_dropped += 1;
                continue;
            }

            let results = tracker.validate_frame(&inbound.frame);
            for result in results {
                self.handle_validation(result, inbound.arrival, now, keying);
                if self.safe_latched {
                    break;
                }
            }
        }

        self.tracker = Some(tracker);
    }

    fn handle_validation(
        &mut self,
        result: super::tracker::EdgeValidationResult,
        arrival: i64,
        now: i64,
        keying: &mut dyn KeyingOutput,
    ) {
        match result.outcome {
            EdgeValidationOutcome::Accepted => self.schedule_edge(result.edge, arrival, now, keying),
            EdgeValidationOutcome::DuplicateDiscarded => self.duplicate_edges += 1,
            EdgeValidationOutcome::SequenceGap if result.applied => {
                // A key-up across a gap is safe: the transmitter ends up unkeyed.
                self.schedule_edge(result.edge, arrival, now, keying);
            }
            EdgeValidationOutcome::EpochMismatch => {
                // F4: discard the frame and force key-up if keyed. No latch.
                self.force_key_up(keying);
                self.report_fail_safe(FailSafeCondition::F4, keying);
            }
            EdgeValidationOutcome::SequenceGap => {
                // F5: an unhealed gap ending in a key-down. Never guess a key-down.
                self.latch_safe(FailSafeCondition::F5, keying);
            }
            EdgeValidationOutcome::TimestampRegression => {
                self.latch_safe(FailSafeCondition::F5, keying);
            }
        }
    }

    fn schedule_edge(&mut self, edge: crate::protocol::edge::EdgeEntry, arrival: i64, now: i64, keying: &mut dyn KeyingOutput) {
        let delay_ticks = self.jitter.current_delay_in(self.frequency) as i64;
        let (mut deadline, reanchored) = self.anchor.schedule(arrival, edge.timestamp_ms, delay_ticks);

        if reanchored {
            self.jitter.apply_pending_path_change();
            self.last_scheduled_deadline = None;
        }

        // Scheduled edges keep monotonic order; clamping is safer than reordering the
        // key stream, and within a session the tracker already guarantees non-decreasing
        // timestamps, so this only matters across a re-anchor.
        if let Some(last) = self.last_scheduled_deadline {
            if deadline < last {
                deadline = last;
            }
        }
        self.last_scheduled_deadline = Some(deadline);

        if deadline < now {
            // Late: the buffer delay was smaller than this datagram's excess latency.
            // Replay at once — dropping it could strand the key down — and let telemetry
            // show the lateness rather than hiding a mistimed edge.
            let lateness = now - deadline;
            self.late_edges += 1;
            self.max_lateness = self.max_lateness.max(lateness);
            let now_ms = ReplayAnchor::milliseconds_for_ticks(now, self.frequency) as u64;
            self.jitter.report_late_edge(now_ms);
        }

        if self.pending.len() >= PENDING_CAPACITY {
            // The replay thread is starved. Anything still queued is stale, so the safe
            // response is key-up, not best-effort catch-up.
            self.pending_overflows += 1;
            self.force_key_up(keying);
            self.report_fail_safe(FailSafeCondition::F10, keying);
            return;
        }

        self.pending.push_back(ScheduledEdge { deadline, key_down: edge.key_down() });
        self.edges_applied += 1;

        if self.state == EdgeReplayerState::Idle {
            self.set_state(EdgeReplayerState::Active);
        }
    }

    fn fire_due(&mut self, now: i64, keying: &mut dyn KeyingOutput) {
        // PTT lead: raise PTT before the key lands. The key itself still waits for its
        // deadline, so pre-firing PTT must not pop or key the edge.
        if let Some(edge) = self.pending.front().copied() {
            let fire_at = self.effective_fire(edge);
            if edge.key_down && self.ptt_lead_ticks > 0 && !self.ptt_asserted && now >= fire_at {
                if let Some(ptt) = &mut self.ptt {
                    if matches!(
                        ptt.on_key_edge(true, edge.deadline.max(0) as u64),
                        Some(crate::engine::serial::PttAction::Assert { .. })
                    ) {
                        self.ptt_asserted = true;
                        keying.set_ptt(true);
                    }
                }
            }
        }

        loop {
            let Some(edge) = self.pending.front().copied() else {
                return;
            };
            if now < edge.deadline {
                return;
            }
            self.pending.pop_front();

            if !edge.key_down {
                if let Some(ptt) = &mut self.ptt {
                    let _ = ptt.on_key_edge(false, edge.deadline.max(0) as u64);
                }
            } else if self.ptt.is_some() && !self.ptt_asserted {
                // No lead configured to have pre-asserted PTT: assert it now, with the key.
                if let Some(ptt) = &mut self.ptt {
                    let _ = ptt.on_key_edge(true, edge.deadline.max(0) as u64);
                }
                self.ptt_asserted = true;
                keying.set_ptt(true);
            }

            keying.set_key(edge.key_down);
            self.last_key_down_flag = edge.key_down;

            self.edges_replayed += 1;
            let error = now - edge.deadline;
            self.max_replay_error = self.max_replay_error.max(error);
            self.last_edge_fired = now;
        }
    }

    /// When the sequencer must be called for `edge`: its deadline, less one PTT lead for
    /// a key-down that still needs PTT raised.
    fn effective_fire(&self, edge: ScheduledEdge) -> i64 {
        if edge.key_down && self.ptt_lead_ticks > 0 && !self.ptt_asserted {
            edge.deadline - self.ptt_lead_ticks
        } else {
            edge.deadline
        }
    }

    fn report_fail_safe(&mut self, condition: FailSafeCondition, keying: &mut dyn KeyingOutput) {
        match condition.latch_policy() {
            LatchPolicy::ManualReArm => self.latch_safe(condition, keying),
            LatchPolicy::AutoClear => {
                self.force_key_up(keying);
                self.events.push(ReplayEvent::FailSafe { condition });
                self.set_state(EdgeReplayerState::Degraded);
            }
            LatchPolicy::None => {
                self.events.push(ReplayEvent::FailSafe { condition });
            }
        }
    }

    fn set_state(&mut self, state: EdgeReplayerState) {
        if self.state != state {
            self.state = state;
            self.events.push(ReplayEvent::StateChanged { state, safe_latched: self.safe_latched });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::edge::EdgeEntry;

    const GHZ: u64 = 1_000_000_000;
    const MS: i64 = 1_000_000;

    /// Records every key transition, so tests can assert the keying waveform.
    #[derive(Default)]
    struct RecordingOutput {
        transitions: Vec<(bool, bool)>, // (key_down, ptt)
    }

    impl KeyingOutput for RecordingOutput {
        fn set_key(&mut self, key_down: bool) {
            self.transitions.push((key_down, self.transitions.last().map(|t| t.1).unwrap_or(false)));
        }
        fn set_ptt(&mut self, asserted: bool) {
            let key = self.transitions.last().map(|t| t.0).unwrap_or(false);
            self.transitions.push((key, asserted));
        }
    }

    impl RecordingOutput {
        fn key_states(&self) -> Vec<bool> {
            self.transitions.iter().map(|(k, _)| *k).collect()
        }
    }

    fn frame(epoch: u16, edges: &[EdgeEntry]) -> Vec<u8> {
        RwkPaddleFrame::try_new(epoch, edges).unwrap().to_vec()
    }

    fn replayer() -> EdgeReplayer {
        EdgeReplayer::new(
            GHZ,
            JitterBufferConfig { direct_delay: Duration::from_millis(60), derp_delay: Duration::from_millis(60), adaptive_mode: false },
            EdgeJitterProfile::PathAdaptive,
            PathType::Direct,
            None,
        )
    }

    #[test]
    fn replays_an_edge_exactly_one_buffer_delay_after_arrival() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);

        // One key-down edge at stream time 0, arriving at now = 0.
        let f = frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]);
        rp.process_datagram(&f, 0);

        // Before the delay elapses, nothing is keyed.
        rp.tick(59 * MS, &mut out);
        assert!(out.key_states().is_empty(), "must not key before arrival + D");

        // At exactly 60ms the key goes down.
        rp.tick(60 * MS, &mut out);
        assert_eq!(out.key_states(), vec![true]);
        assert_eq!(rp.telemetry().anchor_count, 1);
    }

    #[test]
    fn two_edges_keep_the_clients_spacing_not_their_arrival_jitter() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);

        // Key-down at stream 0, key-up at stream 100ms.
        let f1 = frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]);
        rp.process_datagram(&f1, 0);
        rp.tick(60 * MS, &mut out);

        // The key-up datagram arrives 20ms late (jitter), but its stream spacing is 100ms.
        let f2 = frame(1, &[EdgeEntry::key_up_at(2, 100, 0)]);
        rp.process_datagram(&f2, 80 * MS);
        rp.tick(160 * MS, &mut out);

        // Fired at 60ms and 160ms: exactly 100ms apart, jitter removed.
        assert_eq!(out.key_states(), vec![true, false]);
    }

    #[test]
    fn duplicate_redundant_edges_are_discarded() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        // The next frame repeats edge 1 redundantly alongside edge 2.
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(2, 50, 0), EdgeEntry::key_down_at(1, 0, 0)]), 60 * MS);
        rp.tick(200 * MS, &mut out);

        let t = rp.telemetry();
        assert_eq!(t.duplicate_edges, 1);
        assert_eq!(t.edges_applied, 2, "the duplicate must not be scheduled again");
    }

    #[test]
    fn epoch_mismatch_forces_key_up_and_reports_f4_without_latching() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        assert!(rp.is_key_down());

        // A frame from another session arrives.
        rp.process_datagram(&frame(99, &[EdgeEntry::key_up_at(1, 0, 0)]), 100 * MS);
        rp.tick(100 * MS, &mut out);

        assert!(!rp.is_safe_latched(), "F4 must not latch");
        assert!(!rp.is_key_down(), "F4 forces key-up");
        let events = rp.take_events();
        assert!(events.contains(&ReplayEvent::FailSafe { condition: FailSafeCondition::F4 }));
    }

    #[test]
    fn key_down_behind_an_unhealed_gap_latches_safe_and_forces_key_up() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        assert!(rp.is_key_down());

        // Edge 2 never arrives anywhere; edge 3 is a key-down.
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(3, 80, 0)]), 70 * MS);
        rp.tick(130 * MS, &mut out);

        assert!(rp.is_safe_latched(), "an uninferable gap must latch SAFE");
        assert_eq!(rp.state(), EdgeReplayerState::SafeLatched);
        assert!(!rp.is_key_down(), "key must be forced up");
        assert!(rp.take_events().contains(&ReplayEvent::FailSafe { condition: FailSafeCondition::F5 }));
    }

    #[test]
    fn key_up_behind_an_unhealed_gap_is_applied_and_does_not_latch() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);

        // Edge 2 lost; edge 3 is a key-up at stream 100ms.
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(3, 100, 0)]), 70 * MS);
        rp.tick(200 * MS, &mut out);

        assert!(!rp.is_safe_latched());
        assert_eq!(out.key_states(), vec![true, false]);
    }

    #[test]
    fn while_latched_arriving_datagrams_are_dropped() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(3, 80, 0)]), 70 * MS);
        rp.tick(130 * MS, &mut out);
        assert!(rp.is_safe_latched());

        let before = rp.telemetry().frames_received;
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(4, 120, 0)]), 200 * MS);
        assert_eq!(rp.telemetry().frames_received, before, "latched replayer drops frames");
        assert!(rp.telemetry().frames_dropped > 0);
    }

    #[test]
    fn clearing_the_latch_resumes_keying() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(3, 80, 0)]), 70 * MS);
        rp.tick(130 * MS, &mut out);
        assert!(rp.is_safe_latched());

        rp.clear_safe_latch();
        assert!(!rp.is_safe_latched());

        // A fresh edge now replays normally (edges 3 and 4 both arrive).
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(4, 200, 0)]), 300 * MS);
        rp.tick(400 * MS, &mut out);
        assert_eq!(rp.telemetry().edges_replayed, 2);
    }

    #[test]
    fn malformed_datagrams_are_dropped_not_fatal() {
        let mut rp = replayer();
        rp.begin_session(1);
        rp.process_datagram(&[0xFF, 0xFF, 0xFF, 0xFF], 0);
        rp.process_datagram(&[], 0);
        let t = rp.telemetry();
        assert_eq!(t.frames_received, 0);
        assert_eq!(t.frames_dropped, 2);
    }

    #[test]
    fn datagrams_without_a_session_are_dropped() {
        let mut rp = replayer();
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        assert_eq!(rp.telemetry().frames_received, 0);
        assert_eq!(rp.telemetry().frames_dropped, 1);
    }

    #[test]
    fn a_late_edge_replays_immediately_and_is_counted() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        // Arrives at 500ms but the anchor would place its deadline at arrival + 60ms.
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 500 * MS);
        rp.tick(560 * MS, &mut out);
        assert_eq!(out.key_states(), vec![true]);
        // Then a second edge arrives so late that its deadline is already past.
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(2, 10, 0)]), 900 * MS);
        rp.tick(900 * MS, &mut out);
        assert!(rp.telemetry().late_edges >= 1, "a past-due deadline must be counted as late");
    }

    #[test]
    fn ptt_is_asserted_ahead_of_key_down_when_configured() {
        let mut rp = EdgeReplayer::new(
            GHZ,
            JitterBufferConfig { direct_delay: Duration::from_millis(60), derp_delay: Duration::from_millis(60), adaptive_mode: false },
            EdgeJitterProfile::PathAdaptive,
            PathType::Direct,
            Some(PttTiming::default()),
        );
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);

        // PTT lead is 15ms, so the sequencer is invoked 15ms before the 60ms deadline.
        rp.tick(45 * MS, &mut out);
        assert!(rp.is_ptt_asserted(), "PTT should be up before the key lands");
        // The key itself must wait for the deadline.
        rp.tick(60 * MS, &mut out);
        assert!(rp.is_key_down());
    }

    #[test]
    fn force_key_up_is_idempotent_and_releases_everything() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        assert!(rp.is_key_down());

        rp.force_key_up(&mut out);
        rp.force_key_up(&mut out);
        assert!(!rp.is_key_down());
        assert_eq!(rp.pending_count(), 0);
    }

    #[test]
    fn next_wake_targets_the_earliest_pending_deadline() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(0, &mut out);
        // Deadline is 60ms; the wake must be no later than that.
        assert!(rp.next_wake(0) <= 60 * MS);
    }

    #[test]
    fn session_end_forces_key_up_and_stops_accepting() {
        let mut rp = replayer();
        let mut out = RecordingOutput::default();
        rp.begin_session(1);
        rp.process_datagram(&frame(1, &[EdgeEntry::key_down_at(1, 0, 0)]), 0);
        rp.tick(60 * MS, &mut out);
        assert!(rp.is_key_down());

        rp.end_session(&mut out);
        assert!(!rp.is_key_down());
        assert_eq!(rp.state(), EdgeReplayerState::Stopped);

        let before = rp.telemetry().frames_received;
        rp.process_datagram(&frame(1, &[EdgeEntry::key_up_at(2, 10, 0)]), 100 * MS);
        assert_eq!(rp.telemetry().frames_received, before);
    }
}
