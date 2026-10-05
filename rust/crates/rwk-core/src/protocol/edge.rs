//! The `RWK-PADDLE` edge frame codec.
//!
//! Faithful port of `RWK.Shared.Protocol.Edge.EdgeEntry` and `RwkPaddleFrame`.
//!
//! Wire layout, little-endian:
//!
//! ```text
//! Frame:  epoch   u16       (2 bytes, offset 0)
//!         count   u16       (2 bytes, offset 2)
//!         edges   count × 12 bytes
//!
//! Entry:  sequence u32       (offset 0)
//!         timestamp_ms u32  (offset 4)
//!         state    u8       (offset 8)  0 = key up, 1 = key down
//!         flags    u8       (offset 9)
//!         reserved u16      (offset 10)
//! ```
//!
//! A frame carries the current edge plus up to three previous edges for redundancy,
//! so `count` is 1..=4. Entry size is **12**, not 8: the field widths above and
//! Requirement 6.3 agree on 12, and only an inline design comment says 8. This type
//! sits on the keying path, so nothing here allocates or panics on hostile input —
//! parse failures are reported through the `TryFrom`/`Option` return values.

use std::time::Duration;

/// Size in bytes of one serialized edge entry.
pub const EDGE_ENTRY_SIZE: usize = 12;

/// Size in bytes of the frame header (epoch + edge count).
pub const FRAME_HEADER_SIZE: usize = 4;

/// Smallest legal edge count.
pub const MIN_EDGE_COUNT: usize = 1;

/// Largest legal edge count: current edge plus three redundant copies.
pub const MAX_EDGE_COUNT: usize = 4;

/// Smallest legal serialized frame size, in bytes.
pub const MIN_FRAME_SIZE: usize = FRAME_HEADER_SIZE + MIN_EDGE_COUNT * EDGE_ENTRY_SIZE;

/// Largest legal serialized frame size, in bytes.
pub const MAX_FRAME_SIZE: usize = FRAME_HEADER_SIZE + MAX_EDGE_COUNT * EDGE_ENTRY_SIZE;

/// `state` value meaning key up.
pub const STATE_KEY_UP: u8 = 0;

/// `state` value meaning key down.
pub const STATE_KEY_DOWN: u8 = 1;

/// A single timestamped key-state transition inside an `RWK-PADDLE` frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EdgeEntry {
    /// Monotonic sequence number assigned by the Client.
    pub sequence: u32,
    /// Monotonic milliseconds since session start.
    pub timestamp_ms: u32,
    /// Key state: [`STATE_KEY_UP`] or [`STATE_KEY_DOWN`].
    pub state: u8,
    /// Reserved flag bits (PTT and future use).
    pub flags: u8,
    /// Padding / future use.
    pub reserved: u16,
}

impl EdgeEntry {
    /// Creates a key-down entry.
    #[must_use]
    pub fn key_down_at(sequence: u32, timestamp_ms: u32, flags: u8) -> Self {
        Self { sequence, timestamp_ms, state: STATE_KEY_DOWN, flags, reserved: 0 }
    }

    /// Creates a key-up entry.
    #[must_use]
    pub fn key_up_at(sequence: u32, timestamp_ms: u32, flags: u8) -> Self {
        Self { sequence, timestamp_ms, state: STATE_KEY_UP, flags, reserved: 0 }
    }

    /// True when [`Self::state`] is non-zero (key down).
    #[must_use]
    pub fn key_down(&self) -> bool {
        self.state != STATE_KEY_UP
    }

    /// Serializes this entry into `dst`, returning the number of bytes written.
    ///
    /// Returns [`None`] when `dst` is smaller than [`EDGE_ENTRY_SIZE`].
    #[must_use]
    pub fn write_to(&self, dst: &mut [u8]) -> Option<usize> {
        if dst.len() < EDGE_ENTRY_SIZE {
            return None;
        }
        dst[0..4].copy_from_slice(&self.sequence.to_le_bytes());
        dst[4..8].copy_from_slice(&self.timestamp_ms.to_le_bytes());
        dst[8] = self.state;
        dst[9] = self.flags;
        dst[10..12].copy_from_slice(&self.reserved.to_le_bytes());
        Some(EDGE_ENTRY_SIZE)
    }

    /// Serializes this entry into a fixed-size array.
    #[must_use]
    pub fn to_bytes(&self) -> [u8; EDGE_ENTRY_SIZE] {
        let mut out = [0u8; EDGE_ENTRY_SIZE];
        // Infallible: the buffer is exactly EDGE_ENTRY_SIZE bytes.
        let _ = self.write_to(&mut out);
        out
    }

    /// Parses one entry from the front of `src`, returning it and the bytes consumed.
    ///
    /// Returns [`None`] when `src` is shorter than [`EDGE_ENTRY_SIZE`].
    #[must_use]
    pub fn read_from(src: &[u8]) -> Option<(Self, usize)> {
        if src.len() < EDGE_ENTRY_SIZE {
            return None;
        }
        let sequence = u32::from_le_bytes([src[0], src[1], src[2], src[3]]);
        let timestamp_ms = u32::from_le_bytes([src[4], src[5], src[6], src[7]]);
        let state = src[8];
        let flags = src[9];
        let reserved = u16::from_le_bytes([src[10], src[11]]);
        Some((Self { sequence, timestamp_ms, state, flags, reserved }, EDGE_ENTRY_SIZE))
    }
}

/// A complete `RWK-PADDLE` frame: an epoch plus 1..=4 edge entries.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RwkPaddleFrame {
    /// Session epoch; increments on reconnect so stale frames can be detected.
    pub epoch: u16,
    /// Edges carried by this frame, current edge first.
    pub edges: [EdgeEntry; MAX_EDGE_COUNT],
    /// Number of valid entries in [`Self::edges`], always 1..=[`MAX_EDGE_COUNT`].
    count: usize,
}

impl RwkPaddleFrame {
    /// Builds a frame from up to [`MAX_EDGE_COUNT`] edges (current first).
    ///
    /// Returns [`None`] when `edges` is empty or longer than [`MAX_EDGE_COUNT`].
    #[must_use]
    pub fn try_new(epoch: u16, edges: &[EdgeEntry]) -> Option<Self> {
        if edges.is_empty() || edges.len() > MAX_EDGE_COUNT {
            return None;
        }
        let mut slots = [EdgeEntry::default(); MAX_EDGE_COUNT];
        slots[..edges.len()].copy_from_slice(edges);
        Some(Self { epoch, edges: slots, count: edges.len() })
    }

    /// Number of edges carried by this frame.
    #[must_use]
    pub fn edge_count(&self) -> usize {
        self.count
    }

    /// The frame's edges, current edge first.
    #[must_use]
    pub fn edges(&self) -> &[EdgeEntry] {
        &self.edges[..self.count]
    }

    /// Serialized size of this frame, in bytes.
    #[must_use]
    pub fn serialized_size(&self) -> usize {
        frame_size(self.count)
    }

    /// Serialized size of a frame carrying `edge_count` entries.
    #[must_use]
    pub fn frame_size_of(edge_count: usize) -> usize {
        frame_size(edge_count)
    }

    /// Writes the frame into `dst`, returning the number of bytes written.
    ///
    /// Returns [`None`] when `dst` is too small.
    #[must_use]
    pub fn write_to(&self, dst: &mut [u8]) -> Option<usize> {
        let required = self.serialized_size();
        if dst.len() < required {
            return None;
        }
        dst[0..2].copy_from_slice(&self.epoch.to_le_bytes());
        dst[2..4].copy_from_slice(&(self.count as u16).to_le_bytes());
        let mut offset = FRAME_HEADER_SIZE;
        for edge in self.edges() {
            offset += edge.write_to(&mut dst[offset..])?;
        }
        Some(offset)
    }

    /// Serializes the frame into a caller-provided buffer.
    #[must_use]
    pub fn to_vec(&self) -> Vec<u8> {
        let mut out = vec![0u8; self.serialized_size()];
        // Infallible: the buffer was sized from serialized_size().
        let _ = self.write_to(&mut out);
        out
    }

    /// Parses a frame from the front of `src`, returning it and the bytes consumed.
    ///
    /// Trailing bytes beyond the frame are ignored. Returns [`None`] when the buffer
    /// is too small for the header, the declared count is outside 1..=[`MAX_EDGE_COUNT`],
    /// or the buffer is too small for the declared count.
    #[must_use]
    pub fn read_from(src: &[u8]) -> Option<(Self, usize)> {
        if src.len() < FRAME_HEADER_SIZE {
            return None;
        }
        let epoch = u16::from_le_bytes([src[0], src[1]]);
        let count = u16::from_le_bytes([src[2], src[3]]) as usize;
        if !(MIN_EDGE_COUNT..=MAX_EDGE_COUNT).contains(&count) {
            return None;
        }
        if src.len() < frame_size(count) {
            return None;
        }

        let mut edges = [EdgeEntry::default(); MAX_EDGE_COUNT];
        let mut offset = FRAME_HEADER_SIZE;
        for slot in edges.iter_mut().take(count) {
            let (edge, used) = EdgeEntry::read_from(&src[offset..])?;
            *slot = edge;
            offset += used;
        }
        Some((Self { epoch, edges, count }, offset))
    }
}

/// Serialized size of a frame carrying `edge_count` entries.
#[must_use]
pub fn frame_size(edge_count: usize) -> usize {
    FRAME_HEADER_SIZE + edge_count * EDGE_ENTRY_SIZE
}

/// The station-side replay anchor: which edge the replayer considers "now".
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ReplayAnchor {
    /// Epoch the replayer has locked on to.
    pub epoch: u16,
    /// Highest sequence number observed.
    pub last_sequence: u32,
    /// Stream time of the last applied edge, in milliseconds.
    pub stream_ms: u32,
}

impl ReplayAnchor {
    /// True when `entry` belongs to a newer epoch than this anchor.
    #[must_use]
    pub fn is_new_epoch(&self, _entry: &EdgeEntry, epoch: u16) -> bool {
        epoch != self.epoch
    }

    /// Applies `entry` to the anchor, advancing the sequence and stream time.
    pub fn apply(&mut self, entry: &EdgeEntry) {
        self.last_sequence = entry.sequence;
        self.stream_ms = entry.timestamp_ms;
    }

    /// Delay between two entries, saturating to zero on reordering.
    #[must_use]
    pub fn gap(entry: &EdgeEntry, previous_ms: u32) -> Duration {
        Duration::from_millis(u64::from(entry.timestamp_ms.saturating_sub(previous_ms)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn entry_round_trips_exactly() {
        let entry = EdgeEntry { sequence: 0xDEAD_BEEF, timestamp_ms: 123_456, state: STATE_KEY_DOWN, flags: 0xA5, reserved: 0x1234 };
        let bytes = entry.to_bytes();
        assert_eq!(bytes.len(), EDGE_ENTRY_SIZE);
        // Little-endian on the wire, explicitly.
        assert_eq!(&bytes[0..4], &0xDEAD_BEEFu32.to_le_bytes());
        assert_eq!(bytes[8], STATE_KEY_DOWN);
        let (parsed, used) = EdgeEntry::read_from(&bytes).expect("parse");
        assert_eq!(used, EDGE_ENTRY_SIZE);
        assert_eq!(parsed, entry);
        assert!(parsed.key_down());
    }

    #[test]
    fn entry_read_from_short_buffer_fails() {
        assert!(EdgeEntry::read_from(&[0u8; EDGE_ENTRY_SIZE - 1]).is_none());
    }

    #[test]
    fn frame_round_trips_with_four_redundant_edges() {
        let edges = [
            EdgeEntry::key_down_at(10, 100, 0),
            EdgeEntry::key_up_at(9, 80, 0),
            EdgeEntry::key_down_at(8, 60, 0),
            EdgeEntry::key_up_at(7, 40, 0),
        ];
        let frame = RwkPaddleFrame::try_new(3, &edges).expect("frame");
        assert_eq!(frame.edge_count(), 4);
        assert_eq!(frame.serialized_size(), MAX_FRAME_SIZE);

        let bytes = frame.to_vec();
        assert_eq!(bytes.len(), MAX_FRAME_SIZE);
        assert_eq!(u16::from_le_bytes([bytes[0], bytes[1]]), 3);

        let (parsed, used) = RwkPaddleFrame::read_from(&bytes).expect("parse");
        assert_eq!(used, MAX_FRAME_SIZE);
        assert_eq!(parsed, frame);
        assert_eq!(parsed.edges(), &edges);
    }

    #[test]
    fn frame_rejects_illegal_edge_counts() {
        assert!(RwkPaddleFrame::try_new(1, &[]).is_none());
        let five = [EdgeEntry::default(); 5];
        assert!(RwkPaddleFrame::try_new(1, &five).is_none());
    }

    #[test]
    fn parser_rejects_zero_and_oversized_counts() {
        // count = 0
        let mut buf = vec![0u8; MIN_FRAME_SIZE];
        buf[2] = 0;
        buf[3] = 0;
        assert!(RwkPaddleFrame::read_from(&buf).is_none());
        // count = 5
        buf[2] = 5;
        buf[3] = 0;
        assert!(RwkPaddleFrame::read_from(&buf).is_none());
    }

    #[test]
    fn parser_rejects_truncated_payload() {
        let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(0, 0, 0)]).unwrap();
        let bytes = frame.to_vec();
        assert!(RwkPaddleFrame::read_from(&bytes[..bytes.len() - 1]).is_none());
    }

    #[test]
    fn parser_ignores_trailing_bytes() {
        let frame = RwkPaddleFrame::try_new(2, &[EdgeEntry::key_up_at(1, 5, 0)]).unwrap();
        let mut bytes = frame.to_vec();
        bytes.extend_from_slice(&[0xFF; 9]);
        let (parsed, used) = RwkPaddleFrame::read_from(&bytes).expect("parse");
        assert_eq!(used, frame.serialized_size());
        assert_eq!(parsed.edge_count(), 1);
    }

    #[test]
    fn replay_anchor_tracks_sequence() {
        let mut anchor = ReplayAnchor { epoch: 1, last_sequence: 0, stream_ms: 0 };
        let entry = EdgeEntry::key_down_at(42, 250, 0);
        anchor.apply(&entry);
        assert_eq!(anchor.last_sequence, 42);
        assert_eq!(anchor.stream_ms, 250);
        assert!(anchor.is_new_epoch(&entry, 2));
        assert!(!anchor.is_new_epoch(&entry, 1));
    }

    #[test]
    fn replay_anchor_gap_saturates_on_reorder() {
        let entry = EdgeEntry::key_up_at(1, 40, 0);
        assert_eq!(ReplayAnchor::gap(&entry, 100), Duration::ZERO);
        assert_eq!(ReplayAnchor::gap(&entry, 10), Duration::from_millis(30));
    }
}
