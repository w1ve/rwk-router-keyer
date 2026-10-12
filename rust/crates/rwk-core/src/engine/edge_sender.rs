//! The Client-side edge orchestrator: [`EdgeEvent`]s → [`RwkPaddleFrame`]s.
//!
//! The keyer and paddle decider ([`crate::engine::keying`]) produce [`EdgeEvent`]s
//! stamped in raw clock ticks. The wire protocol ([`RwkPaddleFrame`]) instead carries a
//! session `epoch`, a monotonic per-edge `sequence`, and a `timestamp_ms` measured from
//! the start of the session. This module is the single place that bridges the two, so the
//! transport ([`crate::engine::network::EdgeTransport`]) only ever sees finished frames.
//!
//! ## Redundancy
//!
//! CW keying is unforgiving of a dropped edge: a lost key-up leaves the far key stuck
//! down. The protocol defends against single-datagram loss by letting every frame carry
//! the current edge **plus up to three of the most recent prior edges**
//! ([`MAX_EDGE_COUNT`]). The replayer deduplicates by `sequence`, so re-sending an edge is
//! free of side effects: a receiver that already applied sequence *n* ignores the copy,
//! and one that missed the datagram carrying *n* recovers it from the next frame. This
//! orchestrator keeps a small ring of recent edges and attaches them to each new frame.
//!
//! ## Timing fidelity
//!
//! `timestamp_ms` is derived from the edge's tick timestamp relative to the session
//! start, using the clock's own frequency. This is the value the Station replayer uses to
//! reconstruct inter-edge spacing, so the ±2ms keying budget rides on this conversion
//! being exact (round-to-nearest, not truncating) — a consistent half-millisecond bias on
//! every edge would otherwise accumulate into audible timing error.

use crate::engine::keying::EdgeEvent;
use crate::protocol::edge::{
    EdgeEntry, RwkPaddleFrame, MAX_EDGE_COUNT, STATE_KEY_DOWN, STATE_KEY_UP,
};

/// How many prior edges, at most, ride along with the current one for redundancy.
///
/// One current edge plus this many previous edges must fit [`MAX_EDGE_COUNT`].
pub const REDUNDANT_EDGES: usize = MAX_EDGE_COUNT - 1;

/// `flags` bit set on an entry whose transition also asserts PTT.
///
/// Reserved in the wire format; carried here so a future PTT-over-edge path has a defined
/// bit rather than inventing one later.
pub const FLAG_PTT: u8 = 0b0000_0001;

/// Converts a stream of [`EdgeEvent`]s into redundant [`RwkPaddleFrame`]s for one session.
///
/// Construct one per session (per `epoch`); feed it edges as the keyer produces them, and
/// hand each returned frame straight to [`EdgeTransport::send_frame`]. The orchestrator is
/// deliberately synchronous and allocation-light: it owns a fixed-size ring of recent
/// edges and assigns sequence numbers, nothing more, so it can live on the keyer thread
/// without touching the network.
///
/// [`EdgeTransport::send_frame`]: crate::engine::network::EdgeTransport::send_frame
#[derive(Debug, Clone)]
pub struct EdgeSender {
    epoch: u16,
    frequency: u64,
    session_start_ticks: u64,
    next_sequence: u32,
    /// Most-recent edges, newest at the back. Capped at [`MAX_EDGE_COUNT`] entries so the
    /// front edge plus [`REDUNDANT_EDGES`] prior ones always fit a frame.
    recent: Vec<EdgeEntry>,
}

impl EdgeSender {
    /// Starts a sender for `epoch`, timing edges relative to `session_start_ticks` on a
    /// clock running at `frequency` ticks per second.
    ///
    /// `frequency` is clamped to at least 1 to make the millisecond conversion total; a
    /// zero-frequency clock is a programming error the keyer path must never hit, and
    /// clamping keeps this constructor infallible on the keying path.
    #[must_use]
    pub fn new(epoch: u16, frequency: u64, session_start_ticks: u64) -> Self {
        Self {
            epoch,
            frequency: frequency.max(1),
            session_start_ticks,
            next_sequence: 0,
            recent: Vec::with_capacity(MAX_EDGE_COUNT),
        }
    }

    /// The session epoch these frames carry.
    #[must_use]
    pub fn epoch(&self) -> u16 {
        self.epoch
    }

    /// The sequence number the next edge will be assigned.
    #[must_use]
    pub fn next_sequence(&self) -> u32 {
        self.next_sequence
    }

    /// Converts an edge's absolute tick timestamp into milliseconds since session start.
    ///
    /// Rounds to the nearest millisecond rather than truncating, so the per-edge
    /// conversion error is bounded by ±0.5ms and does not bias one way across a long
    /// string of edges. Edges before the session start (clock skew on reconnect) saturate
    /// to zero rather than wrapping.
    #[must_use]
    pub fn timestamp_ms(&self, timestamp_ticks: u64) -> u32 {
        let elapsed = timestamp_ticks.saturating_sub(self.session_start_ticks);
        // round(elapsed * 1000 / frequency) == (elapsed * 1000 + frequency/2) / frequency,
        // in u128 so the intermediate product cannot overflow for any real clock.
        let ms = (u128::from(elapsed) * 1000 + u128::from(self.frequency) / 2)
            / u128::from(self.frequency);
        u32::try_from(ms).unwrap_or(u32::MAX)
    }

    /// Builds the next frame for `event`, assigning it a sequence number and attaching up
    /// to [`REDUNDANT_EDGES`] of the most recent prior edges for loss resilience.
    ///
    /// The current edge is always first in the frame, matching the protocol's
    /// "current edge first" ordering; the redundant copies follow in newest-to-oldest
    /// order. The returned frame is ready for [`EdgeTransport::send_frame`].
    ///
    /// [`EdgeTransport::send_frame`]: crate::engine::network::EdgeTransport::send_frame
    pub fn frame_for(&mut self, event: EdgeEvent) -> RwkPaddleFrame {
        let sequence = self.next_sequence;
        // Wrapping is correct: sequence is a u32 counter the replayer compares modulo the
        // type, and a session will never emit 4 billion edges, but defining the wrap keeps
        // the arithmetic total.
        self.next_sequence = self.next_sequence.wrapping_add(1);

        let state = if event.key_down { STATE_KEY_DOWN } else { STATE_KEY_UP };
        let entry = EdgeEntry {
            sequence,
            timestamp_ms: self.timestamp_ms(event.timestamp_ticks),
            state,
            flags: 0,
            reserved: 0,
        };

        // Current edge first, then the most recent prior edges newest-first.
        let mut edges = Vec::with_capacity(MAX_EDGE_COUNT);
        edges.push(entry);
        for prior in self.recent.iter().rev().take(REDUNDANT_EDGES) {
            edges.push(*prior);
        }

        // Record this edge as the newest in the ring, trimming the oldest so the ring
        // never exceeds what a single frame can carry.
        self.recent.push(entry);
        if self.recent.len() > MAX_EDGE_COUNT {
            self.recent.remove(0);
        }

        // Infallible: `edges` holds 1..=MAX_EDGE_COUNT entries by construction.
        RwkPaddleFrame::try_new(self.epoch, &edges)
            .expect("1..=MAX_EDGE_COUNT edges always build a frame")
    }

    /// Clears the redundancy ring and resets sequencing for a new epoch.
    ///
    /// Called on reconnect: a fresh epoch means the Station re-anchors, so carrying edges
    /// from the previous epoch forward would only feed it stale sequences.
    pub fn begin_epoch(&mut self, epoch: u16, session_start_ticks: u64) {
        self.epoch = epoch;
        self.session_start_ticks = session_start_ticks;
        self.next_sequence = 0;
        self.recent.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::primitives::EdgeSource;

    const TICK_HZ: u64 = 1_000_000_000;

    fn down_at(ticks: u64) -> EdgeEvent {
        EdgeEvent { timestamp_ticks: ticks, key_down: true, source: EdgeSource::Paddle }
    }

    fn up_at(ticks: u64) -> EdgeEvent {
        EdgeEvent { timestamp_ticks: ticks, key_down: false, source: EdgeSource::Paddle }
    }

    #[test]
    fn first_frame_carries_only_the_current_edge() {
        let mut sender = EdgeSender::new(1, TICK_HZ, 0);
        let frame = sender.frame_for(down_at(0));
        assert_eq!(frame.epoch, 1);
        assert_eq!(frame.edge_count(), 1);
        let edge = frame.edges()[0];
        assert_eq!(edge.sequence, 0);
        assert_eq!(edge.timestamp_ms, 0);
        assert!(edge.key_down());
    }

    #[test]
    fn sequence_numbers_increment_monotonically() {
        let mut sender = EdgeSender::new(1, TICK_HZ, 0);
        let a = sender.frame_for(down_at(0));
        let b = sender.frame_for(up_at(TICK_HZ / 20));
        assert_eq!(a.edges()[0].sequence, 0);
        assert_eq!(b.edges()[0].sequence, 1);
        assert_eq!(sender.next_sequence(), 2);
    }

    #[test]
    fn timestamps_are_milliseconds_from_session_start() {
        // Session starts at 1_000 ticks; an edge 60ms later must read 60ms.
        let start = 1_000;
        let mut sender = EdgeSender::new(1, TICK_HZ, start);
        let sixty_ms = start + 60 * (TICK_HZ / 1000);
        let frame = sender.frame_for(down_at(sixty_ms));
        assert_eq!(frame.edges()[0].timestamp_ms, 60);
    }

    #[test]
    fn timestamp_rounds_to_nearest_millisecond() {
        let sender = EdgeSender::new(1, TICK_HZ, 0);
        // 1.4ms rounds down to 1, 1.6ms rounds up to 2.
        assert_eq!(sender.timestamp_ms(1_400_000), 1);
        assert_eq!(sender.timestamp_ms(1_600_000), 2);
        // Exactly 2.5ms rounds up to 3 (round-half-up).
        assert_eq!(sender.timestamp_ms(2_500_000), 3);
    }

    #[test]
    fn edges_before_session_start_saturate_to_zero() {
        let sender = EdgeSender::new(1, TICK_HZ, 10_000);
        assert_eq!(sender.timestamp_ms(0), 0);
    }

    #[test]
    fn frames_carry_up_to_three_redundant_prior_edges() {
        let mut sender = EdgeSender::new(7, TICK_HZ, 0);
        for i in 0..5u64 {
            let frame = sender.frame_for(down_at(i * TICK_HZ / 20));
            let expected = ((i + 1) as usize).min(MAX_EDGE_COUNT);
            assert_eq!(frame.edge_count(), expected, "frame {i}");
        }

        // The fifth frame carries edge 4 (current) plus 3,2,1 (newest-first).
        let frame = sender.frame_for(down_at(5 * TICK_HZ / 20));
        let seqs: Vec<u32> = frame.edges().iter().map(|e| e.sequence).collect();
        assert_eq!(seqs, vec![5, 4, 3, 2]);
    }

    #[test]
    fn redundant_copies_are_byte_identical_to_their_originals() {
        let mut sender = EdgeSender::new(1, TICK_HZ, 0);
        let first = sender.frame_for(down_at(0));
        let first_edge = first.edges()[0];
        let second = sender.frame_for(up_at(TICK_HZ / 20));
        // The second frame's redundant copy must equal the first frame's current edge,
        // so the replayer's sequence-based dedupe sees them as the same transition.
        assert_eq!(second.edges()[1], first_edge);
    }

    #[test]
    fn begin_epoch_resets_sequence_and_redundancy() {
        let mut sender = EdgeSender::new(1, TICK_HZ, 0);
        let _ = sender.frame_for(down_at(0));
        let _ = sender.frame_for(up_at(TICK_HZ / 20));
        sender.begin_epoch(2, 5_000);
        let frame = sender.frame_for(down_at(5_000));
        assert_eq!(frame.epoch, 2);
        assert_eq!(frame.edge_count(), 1, "redundancy ring must be cleared");
        assert_eq!(frame.edges()[0].sequence, 0, "sequence must restart");
        assert_eq!(frame.edges()[0].timestamp_ms, 0, "timing rebased on new start");
    }
}
