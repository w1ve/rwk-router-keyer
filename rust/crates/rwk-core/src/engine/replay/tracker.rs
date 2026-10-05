//! Edge sequence validation.
//!
//! Port of `RWK.Shared.Protocol.Edge.EdgeSequenceTracker`, `EdgeValidationOutcome`
//! and `EdgeValidationResult`. This type only *classifies* an edge; mapping an
//! outcome onto schedule / discard / force-key-up / latch is the replayer's job.
//!
//! Two rules carry the safety of the whole replay path:
//!
//! * **Redundancy heals loss.** Every frame carries the current edge plus up to three
//!   earlier ones, so [`EdgeSequenceTracker::validate_frame`] walks a frame's edges in
//!   ascending sequence order. A copy of a missed edge is applied before the newer edge
//!   is examined, so no gap is observed. A gap only surfaces when redundancy fails.
//! * **Never guess a key-down.** Across an unhealed gap the tracker applies a key-up
//!   but not a key-down: see [`EdgeSequenceTracker::can_infer_state_across_gap`].

use crate::protocol::edge::{EdgeEntry, RwkPaddleFrame};

/// Which fail-safe condition an outcome triggers, if any.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailSafeTrigger {
    /// F4 — a frame from another session: discard and force key-up, no latch.
    F4,
    /// F5 — an uninferable gap or a timestamp regression: force key-up and latch.
    F5,
}

/// How an edge was classified.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EdgeValidationOutcome {
    /// In order and applied; the replayer schedules it.
    Accepted,
    /// Already seen (the common case, thanks to redundancy); discarded quietly.
    DuplicateDiscarded,
    /// The frame's epoch is not the session epoch; no edge was examined.
    EpochMismatch,
    /// At least one edge never arrived. Check `can_infer_state`.
    SequenceGap,
    /// A new sequence with a timestamp earlier than the last applied edge.
    TimestampRegression,
}

/// The outcome of validating one edge, plus what the replayer needs to act on it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EdgeValidationResult {
    /// How the edge was classified.
    pub outcome: EdgeValidationOutcome,
    /// The edge that was validated; default for [`EdgeValidationOutcome::EpochMismatch`].
    pub edge: EdgeEntry,
    /// True when the tracker advanced to this edge, so the replayer should schedule it.
    pub applied: bool,
    /// For [`EdgeValidationOutcome::SequenceGap`]: whether the resulting key state is safe.
    pub can_infer_state: bool,
    /// Number of edges that never arrived; zero for every other outcome.
    pub missed_edge_count: u32,
}

impl EdgeValidationResult {
    /// The fail-safe this outcome triggers, or [`None`] when none is needed.
    #[must_use]
    pub fn fail_safe(&self) -> Option<FailSafeTrigger> {
        match self.outcome {
            EdgeValidationOutcome::EpochMismatch => Some(FailSafeTrigger::F4),
            EdgeValidationOutcome::SequenceGap if !self.can_infer_state => Some(FailSafeTrigger::F5),
            EdgeValidationOutcome::TimestampRegression => Some(FailSafeTrigger::F5),
            _ => None,
        }
    }

    /// True when this outcome requires a fail-safe response.
    #[must_use]
    pub fn requires_fail_safe(&self) -> bool {
        self.fail_safe().is_some()
    }

    fn accepted(edge: EdgeEntry) -> Self {
        Self { outcome: EdgeValidationOutcome::Accepted, edge, applied: true, can_infer_state: false, missed_edge_count: 0 }
    }

    fn duplicate(edge: EdgeEntry) -> Self {
        Self { outcome: EdgeValidationOutcome::DuplicateDiscarded, edge, applied: false, can_infer_state: false, missed_edge_count: 0 }
    }

    fn epoch_mismatch() -> Self {
        Self {
            outcome: EdgeValidationOutcome::EpochMismatch,
            edge: EdgeEntry::default(),
            applied: false,
            can_infer_state: false,
            missed_edge_count: 0,
        }
    }

    fn sequence_gap(edge: EdgeEntry, can_infer_state: bool, missed: u32) -> Self {
        Self { outcome: EdgeValidationOutcome::SequenceGap, edge, applied: can_infer_state, can_infer_state, missed_edge_count: missed }
    }

    fn timestamp_regression(edge: EdgeEntry) -> Self {
        Self {
            outcome: EdgeValidationOutcome::TimestampRegression,
            edge,
            applied: false,
            can_infer_state: false,
            missed_edge_count: 0,
        }
    }
}

/// Validates received edges against the current session.
///
/// Not thread-safe by design: one instance belongs to the replay thread.
#[derive(Debug, Clone)]
pub struct EdgeSequenceTracker {
    epoch: u16,
    has_applied: bool,
    last_sequence: u32,
    last_timestamp_ms: u32,
    last_key_down: bool,
}

impl EdgeSequenceTracker {
    /// Creates a tracker for `epoch` with no edges applied.
    #[must_use]
    pub fn new(epoch: u16) -> Self {
        Self { epoch, has_applied: false, last_sequence: 0, last_timestamp_ms: 0, last_key_down: false }
    }

    /// The session epoch this tracker accepts.
    #[must_use]
    pub fn epoch(&self) -> u16 {
        self.epoch
    }

    /// True once an edge has been applied in the current session.
    #[must_use]
    pub fn has_applied(&self) -> bool {
        self.has_applied
    }

    /// Sequence of the last applied edge.
    #[must_use]
    pub fn last_sequence(&self) -> u32 {
        self.last_sequence
    }

    /// Timestamp of the last applied edge, in milliseconds.
    #[must_use]
    pub fn last_timestamp_ms(&self) -> u32 {
        self.last_timestamp_ms
    }

    /// Key state of the last applied edge; key-up before any edge.
    #[must_use]
    pub fn last_key_down(&self) -> bool {
        self.last_key_down
    }

    /// The epoch following `epoch`, wrapping past `u16::MAX` back to 0.
    ///
    /// Rollover is harmless: the epoch is only compared for equality, never ordered.
    #[must_use]
    pub fn next_epoch(epoch: u16) -> u16 {
        epoch.wrapping_add(1)
    }

    /// Rebinds to `epoch` and clears all sequence and timestamp state.
    ///
    /// Call only on genuine session establishment or reconnect. Calling it mid-stream
    /// would let a key-down behind a gap be applied as a fresh baseline instead of
    /// raising F5.
    pub fn begin_session(&mut self, epoch: u16) {
        self.epoch = epoch;
        self.has_applied = false;
        self.last_sequence = 0;
        self.last_timestamp_ms = 0;
        self.last_key_down = false;
    }

    /// Whether the key state across an unhealed gap can be established safely.
    ///
    /// True only when the arriving edge is a key-up: the transmitter ends up unkeyed
    /// whatever the missing edges carried. A key-down cannot be inferred.
    #[must_use]
    pub fn can_infer_state_across_gap(edge: &EdgeEntry) -> bool {
        !edge.key_down()
    }

    /// Validates one edge, applying it when the result says so.
    pub fn validate(&mut self, frame_epoch: u16, edge: EdgeEntry) -> EdgeValidationResult {
        if frame_epoch != self.epoch {
            return EdgeValidationResult::epoch_mismatch();
        }

        // The first edge of a session establishes the baseline: its sequence is whatever
        // the Client has reached, and treating it as a gap against 0 would latch SAFE on
        // nothing worse than joining an already-running stream.
        if !self.has_applied {
            self.apply(edge);
            return EdgeValidationResult::accepted(edge);
        }

        // Already seen; the overwhelmingly common case. Compared as a plain unsigned
        // value, deliberately not wrapping: discarding is the key-up-safe failure.
        if edge.sequence <= self.last_sequence {
            return EdgeValidationResult::duplicate(edge);
        }

        // A new sequence whose timestamp precedes the last applied one. Within an epoch
        // the Client advances sequence and timestamp together, so this means corruption.
        if edge.timestamp_ms < self.last_timestamp_ms {
            return EdgeValidationResult::timestamp_regression(edge);
        }

        let missed = edge.sequence - self.last_sequence - 1;
        if missed > 0 {
            let can_infer = Self::can_infer_state_across_gap(&edge);
            if can_infer {
                self.apply(edge);
            }
            return EdgeValidationResult::sequence_gap(edge, can_infer, missed);
        }

        self.apply(edge);
        EdgeValidationResult::accepted(edge)
    }

    /// Validates every edge in `frame` in ascending sequence order.
    ///
    /// Results are oldest-first, ready to schedule. An epoch mismatch is decided from
    /// the frame header and yields a single [`EdgeValidationOutcome::EpochMismatch`].
    #[must_use]
    pub fn validate_frame(&mut self, frame: &RwkPaddleFrame) -> Vec<EdgeValidationResult> {
        if frame.epoch != self.epoch {
            return vec![EdgeValidationResult::epoch_mismatch()];
        }

        let mut ordered: Vec<EdgeEntry> = frame.edges().to_vec();
        // Insertion sort: at most four entries, so this beats anything else and allocates
        // nothing beyond the copy.
        for i in 1..ordered.len() {
            let current = ordered[i];
            let mut j = i;
            while j > 0 && ordered[j - 1].sequence > current.sequence {
                ordered[j] = ordered[j - 1];
                j -= 1;
            }
            ordered[j] = current;
        }

        ordered.into_iter().map(|edge| self.validate(frame.epoch, edge)).collect()
    }

    fn apply(&mut self, edge: EdgeEntry) {
        self.has_applied = true;
        self.last_sequence = edge.sequence;
        self.last_timestamp_ms = edge.timestamp_ms;
        self.last_key_down = edge.key_down();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::edge::{EdgeEntry, RwkPaddleFrame};

    fn down(seq: u32, ts: u32) -> EdgeEntry {
        EdgeEntry::key_down_at(seq, ts, 0)
    }

    fn up(seq: u32, ts: u32) -> EdgeEntry {
        EdgeEntry::key_up_at(seq, ts, 0)
    }

    #[test]
    fn first_edge_establishes_baseline_regardless_of_sequence() {
        let mut t = EdgeSequenceTracker::new(1);
        let r = t.validate(1, down(500, 9000));
        assert_eq!(r.outcome, EdgeValidationOutcome::Accepted);
        assert!(r.applied);
        assert_eq!(t.last_sequence(), 500);
    }

    #[test]
    fn epoch_mismatch_discards_the_whole_frame_with_f4() {
        let mut t = EdgeSequenceTracker::new(4);
        let frame = RwkPaddleFrame::try_new(5, &[down(1, 1), up(2, 2)]).unwrap();
        let results = t.validate_frame(&frame);
        assert_eq!(results.len(), 1);
        assert_eq!(results[0].outcome, EdgeValidationOutcome::EpochMismatch);
        assert_eq!(results[0].fail_safe(), Some(FailSafeTrigger::F4));
        assert!(!t.has_applied(), "nothing may be applied from a foreign epoch");
    }

    #[test]
    fn duplicate_edges_are_discarded_quietly() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(10, 100)).outcome, EdgeValidationOutcome::Accepted);
        let r = t.validate(1, down(10, 100));
        assert_eq!(r.outcome, EdgeValidationOutcome::DuplicateDiscarded);
        assert!(!r.requires_fail_safe());
    }

    #[test]
    fn redundancy_heals_a_lost_datagram() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(1, 10)).outcome, EdgeValidationOutcome::Accepted);
        // Edge 2 was lost in transit, but the next frame carries it redundantly.
        let frame = RwkPaddleFrame::try_new(1, &[up(3, 30), up(2, 20)]).unwrap();
        let results = t.validate_frame(&frame);
        // Ordered oldest-first: edge 2 then edge 3, both in order, no gap observed.
        assert_eq!(results.len(), 2);
        assert_eq!(results[0].outcome, EdgeValidationOutcome::Accepted);
        assert_eq!(results[0].edge.sequence, 2);
        assert_eq!(results[1].outcome, EdgeValidationOutcome::Accepted);
        assert_eq!(results[1].edge.sequence, 3);
    }

    #[test]
    fn key_up_across_an_unhealed_gap_is_applied() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(1, 10)).outcome, EdgeValidationOutcome::Accepted);
        // Edge 2 never arrives anywhere; edge 3 is a key-up.
        let r = t.validate(1, up(3, 30));
        assert_eq!(r.outcome, EdgeValidationOutcome::SequenceGap);
        assert!(r.can_infer_state);
        assert!(r.applied, "a key-up across a gap is safe to apply");
        assert_eq!(r.missed_edge_count, 1);
        assert!(!r.requires_fail_safe());
    }

    #[test]
    fn key_down_across_an_unhealed_gap_latches_safe_with_f5() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(1, 10)).outcome, EdgeValidationOutcome::Accepted);
        let r = t.validate(1, down(3, 30));
        assert_eq!(r.outcome, EdgeValidationOutcome::SequenceGap);
        assert!(!r.can_infer_state);
        assert!(!r.applied, "never guess a key-down");
        assert_eq!(r.fail_safe(), Some(FailSafeTrigger::F5));
    }

    #[test]
    fn timestamp_regression_is_f5() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(1, 100)).outcome, EdgeValidationOutcome::Accepted);
        let r = t.validate(1, up(2, 50));
        assert_eq!(r.outcome, EdgeValidationOutcome::TimestampRegression);
        assert_eq!(r.fail_safe(), Some(FailSafeTrigger::F5));
        assert_eq!(t.last_sequence(), 1, "a regressed edge must not advance the baseline");
    }

    #[test]
    fn epoch_wraps_at_u16_max() {
        assert_eq!(EdgeSequenceTracker::next_epoch(u16::MAX), 0);
        assert_eq!(EdgeSequenceTracker::next_epoch(0), 1);
    }

    #[test]
    fn begin_session_clears_state_so_next_edge_is_a_fresh_baseline() {
        let mut t = EdgeSequenceTracker::new(1);
        assert_eq!(t.validate(1, down(100, 1000)).outcome, EdgeValidationOutcome::Accepted);
        t.begin_session(2);
        assert!(!t.has_applied());
        assert_eq!(t.last_sequence(), 0);
        // A key-down on the new epoch is applied unconditionally.
        assert_eq!(t.validate(2, down(1, 5)).outcome, EdgeValidationOutcome::Accepted);
    }
}
