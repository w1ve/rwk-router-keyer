//! Morse element timing, scheduling, and the paddle element decider.
//!
//! Port of `RWK.Shared.Keying.*` (`KeyerElement`, `KeyerElementTiming`,
//! `KeyerElementEngine`) and `RWK.Shared.Timing.EdgeScheduleBuilder`.
//!
//! Timing maths is identical to the .NET build so that a character sent from the host
//! text path and the same character sent on the paddles have identical spacing:
//!
//! ```text
//! base_dit = tick_rate * 1200 / (wpm * 1000)   // one dit, unweighted
//! dit      = base_dit * weight / 50             // weight 50% => dit == base_dit
//! dah      = 3 * dit
//! gap      = base_dit * (100 - weight) / 50
//! ```
//!
//! Element boundaries are driven through [`crate::timing::HybridWaiter`], which sleeps
//! coarsely and spins for the final sub-millisecond, so CW timing does not jitter when
//! the UI or network is busy.

use std::sync::Arc;

use crate::primitives::{limits, EdgeSource, KeyerMode};
use crate::protocol::morse::MorseTable;
use crate::timing::{Clock, HybridWaiter};
use crate::Result;

/// A single Morse element.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum KeyerElement {
    /// No element is wanted: the paddles are idle.
    #[default]
    None,
    /// A dit: one unit of key-down.
    Dit,
    /// A dah: three units of key-down.
    Dah,
}

/// One timestamped key-state transition produced by the keyer.
///
/// Timestamps are raw clock ticks (not milliseconds); scale by
/// [`Clock::frequency`] to convert.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EdgeEvent {
    /// Clock ticks at which the transition was decided.
    pub timestamp_ticks: u64,
    /// True for key-down, false for key-up.
    pub key_down: bool,
    /// Which input path produced this edge.
    pub source: EdgeSource,
}

/// Element and gap durations, in clock ticks, for one speed/weight setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyerElementTiming {
    /// Key-down duration of a dit.
    pub dit_ticks: u64,
    /// Key-down duration of a dah (three times the dit).
    pub dah_ticks: u64,
    /// Key-up duration following every element.
    pub gap_ticks: u64,
}

impl KeyerElementTiming {
    /// Computes element timing for a speed and weight against a clock's tick frequency.
    ///
    /// `wpm` is clamped to 5..=60 and `weight` to 25..=75.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when `tick_frequency` is zero.
    pub fn from_speed(wpm: u32, weight: u32, tick_frequency: u64) -> Result<Self> {
        if tick_frequency == 0 {
            return Err(crate::Error::Config("tick frequency must be positive".into()));
        }

        let wpm = wpm.clamp(limits::MIN_WPM, limits::MAX_WPM);
        let weight = weight.clamp(limits::MIN_WEIGHT, limits::MAX_WEIGHT);

        // dit = 1200 / wpm milliseconds, expressed in ticks.
        let base_dit = tick_frequency * 1200 / (u64::from(wpm) * 1000);

        let dit = base_dit * u64::from(weight) / 50;
        Ok(Self { dit_ticks: dit, dah_ticks: 3 * dit, gap_ticks: base_dit * u64::from(100 - weight) / 50 })
    }

    /// The unweighted dit duration, recovered from the weighted element and gap.
    ///
    /// Weight moves duration between the element and the gap while holding the cycle at
    /// two base dits, so the base dit is always half of element + gap.
    #[must_use]
    pub fn base_dit_ticks(&self) -> u64 {
        (self.dit_ticks + self.gap_ticks) / 2
    }

    /// Key-up duration between two characters, over and above the gap that already
    /// follows the last element of the preceding character: three gap units.
    #[must_use]
    pub fn inter_character_gap_ticks(&self) -> u64 {
        3 * self.gap_ticks
    }

    /// Key-up duration of a space character: seven unweighted dits.
    #[must_use]
    pub fn word_gap_ticks(&self) -> u64 {
        7 * self.base_dit_ticks()
    }

    /// Key-down duration for an element; zero for [`KeyerElement::None`].
    #[must_use]
    pub fn ticks_for(&self, element: KeyerElement) -> u64 {
        match element {
            KeyerElement::Dit => self.dit_ticks,
            KeyerElement::Dah => self.dah_ticks,
            KeyerElement::None => 0,
        }
    }
}

/// A precomputed sequence of key transitions for a string.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EdgeSchedule {
    /// Transitions in ascending timestamp order.
    pub edges: Vec<EdgeEvent>,
}

impl EdgeSchedule {
    /// Total span of the schedule, in ticks (last edge minus start).
    #[must_use]
    pub fn duration_ticks(&self) -> u64 {
        match (self.edges.first(), self.edges.last()) {
            (Some(first), Some(last)) => last.timestamp_ticks.saturating_sub(first.timestamp_ticks),
            _ => 0,
        }
    }

    /// Replays the schedule against `clock`, invoking `sink` at each edge.
    ///
    /// Blocks the calling thread using [`HybridWaiter`], so this must run on a
    /// dedicated keyer thread — never on the UI thread. Returns `true` if the whole
    /// schedule played; `false` if `should_abort` interrupted it.
    pub fn play(
        &self,
        clock: &dyn Clock,
        sink: &mut dyn FnMut(EdgeEvent),
        should_abort: &dyn Fn() -> bool,
    ) -> bool {
        for edge in &self.edges {
            HybridWaiter::wait_until_ticks(clock, edge.timestamp_ticks, should_abort);
            if should_abort() {
                return false;
            }
            sink(*edge);
        }
        true
    }
}

/// Builds an [`EdgeSchedule`] for a string of text.
pub struct EdgeScheduleBuilder;

impl EdgeScheduleBuilder {
    /// Builds the schedule for `text` at `wpm`/`weight`.
    ///
    /// Unknown characters are skipped, matching the WinKeyer host path which simply
    /// does not key characters it cannot encode. Spaces produce a word gap.
    ///
    /// # Errors
    ///
    /// Propagates [`KeyerElementTiming::from_speed`] errors.
    pub fn build(
        text: &str,
        wpm: u32,
        weight: u32,
        start_ticks: u64,
        tick_frequency: u64,
    ) -> Result<EdgeSchedule> {
        let timing = KeyerElementTiming::from_speed(wpm, weight, tick_frequency)?;
        // Gaps are added *before* the thing they separate, exactly as
        // `EdgeScheduleBuilder.Build` does, so a space replaces the inter-character
        // gap rather than stacking on top of it.
        let intra_char_gap = timing.gap_ticks;
        let inter_char_gap = timing.inter_character_gap_ticks();
        let word_gap = timing.word_gap_ticks();

        let mut position = start_ticks;
        let mut previous_char_emitted = false;
        let mut edges = Vec::new();

        for ch in text.chars() {
            if MorseTable::is_word_separator(ch) {
                // Word gap: advance position, no edges emitted. Only counts between
                // words, and it supersedes the inter-character gap for the next char.
                if previous_char_emitted {
                    position += word_gap;
                }
                previous_char_emitted = false;
                continue;
            }

            let Some(pattern) = MorseTable::pattern(ch) else {
                // Unknown character: skip entirely.
                continue;
            };

            if previous_char_emitted {
                position += inter_char_gap;
            }

            for (index, symbol) in pattern.chars().enumerate() {
                if index > 0 {
                    position += intra_char_gap;
                }

                let element = if symbol == '-' { KeyerElement::Dah } else { KeyerElement::Dit };
                let duration = timing.ticks_for(element);

                edges.push(EdgeEvent {
                    timestamp_ticks: position,
                    key_down: true,
                    source: EdgeSource::Host,
                });
                edges.push(EdgeEvent {
                    timestamp_ticks: position + duration,
                    key_down: false,
                    source: EdgeSource::Host,
                });

                position += duration;
            }

            previous_char_emitted = true;
        }

        Ok(EdgeSchedule { edges })
    }
}

/// Owns the timing configuration and clock, and drives element output.
///
/// This is the object the Tauri layer moves onto a dedicated keyer task.
pub struct ElementKeyer {
    timing: KeyerElementTiming,
    clock: Arc<dyn Clock>,
}

impl ElementKeyer {
    /// Creates a keyer for a speed/weight and a clock.
    ///
    /// # Errors
    ///
    /// Propagates [`KeyerElementTiming::from_speed`] errors.
    pub fn new(wpm: u32, weight: u32, clock: Arc<dyn Clock>) -> Result<Self> {
        let timing = KeyerElementTiming::from_speed(wpm, weight, clock.frequency())?;
        Ok(Self { timing, clock })
    }

    /// The keyer's current timing.
    #[must_use]
    pub fn timing(&self) -> KeyerElementTiming {
        self.timing
    }

    /// Recomputes timing for a new speed/weight.
    ///
    /// # Errors
    ///
    /// Propagates [`KeyerElementTiming::from_speed`] errors.
    pub fn retune(&mut self, wpm: u32, weight: u32) -> Result<()> {
        self.timing = KeyerElementTiming::from_speed(wpm, weight, self.clock.frequency())?;
        Ok(())
    }

    /// Builds a schedule for `text` starting at the clock's current time.
    ///
    /// # Errors
    ///
    /// Propagates [`EdgeScheduleBuilder::build`] errors.
    pub fn schedule_text(&self, text: &str, wpm: u32, weight: u32) -> Result<EdgeSchedule> {
        EdgeScheduleBuilder::build(text, wpm, weight, self.clock.now(), self.clock.frequency())
    }

    /// Plays a schedule on the calling thread. See [`EdgeSchedule::play`].
    pub fn play(
        &self,
        schedule: &EdgeSchedule,
        sink: &mut dyn FnMut(EdgeEvent),
        should_abort: &dyn Fn() -> bool,
    ) -> bool {
        schedule.play(self.clock.as_ref(), sink, should_abort)
    }
}

/// The current paddle contact state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PaddleState {
    /// Dit paddle closed.
    pub dit: bool,
    /// Dah paddle closed.
    pub dah: bool,
}

impl PaddleState {
    /// True when neither paddle is closed.
    #[must_use]
    pub fn is_idle(&self) -> bool {
        !self.dit && !self.dah
    }
}

/// Translates paddle contacts into Morse elements for the configured keyer mode.
///
/// The decider is deliberately stateless with respect to time — it answers "which
/// element, if any, should start now?" and the caller advances a
/// [`KeyerElementTiming`] clock. That separation is what makes element counts
/// assertable in tests instead of dependent on scheduler luck.
#[derive(Debug, Clone)]
pub struct PaddleElementEngine {
    mode: KeyerMode,
    last_element: KeyerElement,
    last_pressed: KeyerElement,
}

impl PaddleElementEngine {
    /// Creates a decider for `mode`.
    #[must_use]
    pub fn new(mode: KeyerMode) -> Self {
        Self { mode, last_element: KeyerElement::None, last_pressed: KeyerElement::None }
    }

    /// The keyer mode in use.
    #[must_use]
    pub fn mode(&self) -> KeyerMode {
        self.mode
    }

    /// Changes the keyer mode, resetting alternation history.
    pub fn set_mode(&mut self, mode: KeyerMode) {
        self.mode = mode;
        self.last_element = KeyerElement::None;
        self.last_pressed = KeyerElement::None;
    }

    /// Decides the element that should start now.
    ///
    /// Returns [`KeyerElement::None`] when nothing should be sent and the caller
    /// should idle. In [`KeyerMode::Straight`] the returned element is a plain
    /// unit key-down mirroring the contact; the caller keys while the contact stays
    /// closed rather than timing an element.
    pub fn next_element(&mut self, paddle: PaddleState) -> KeyerElement {
        if paddle.is_idle() {
            return KeyerElement::None;
        }

        match self.mode {
            KeyerMode::Straight => KeyerElement::Dit,
            KeyerMode::Bug => {
                // Dits are automatic; the dah paddle passes straight through.
                if paddle.dah && !paddle.dit {
                    KeyerElement::Dah
                } else {
                    KeyerElement::Dit
                }
            }
            KeyerMode::Ultimatic => {
                if paddle.dit && paddle.dah {
                    // Repeat the element of the paddle pressed last.
                    if self.last_pressed == KeyerElement::Dah {
                        KeyerElement::Dah
                    } else {
                        KeyerElement::Dit
                    }
                } else {
                    self.single_paddle(paddle)
                }
            }
            KeyerMode::IambicA | KeyerMode::IambicB => {
                if paddle.dit && paddle.dah {
                    // Squeeze: alternate away from the last element sent.
                    if self.last_element == KeyerElement::Dah {
                        KeyerElement::Dit
                    } else {
                        KeyerElement::Dah
                    }
                } else {
                    self.single_paddle(paddle)
                }
            }
        }
    }

    /// Records that `element` finished, so alternation can continue correctly.
    pub fn on_element_sent(&mut self, element: KeyerElement) {
        if element != KeyerElement::None {
            self.last_element = element;
        }
    }

    /// Records a paddle press, tracking which paddle was most recently closed.
    pub fn on_contacts_changed(&mut self, paddle: PaddleState) {
        if paddle.dit && !paddle.dah {
            self.last_pressed = KeyerElement::Dit;
        } else if paddle.dah && !paddle.dit {
            self.last_pressed = KeyerElement::Dah;
        }
    }

    fn single_paddle(&mut self, paddle: PaddleState) -> KeyerElement {
        if paddle.dah {
            KeyerElement::Dah
        } else {
            KeyerElement::Dit
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::timing::MonotonicClock;

    const TICK_HZ: u64 = 1_000_000_000;

    #[test]
    fn timing_matches_documented_formula_at_fifty_percent_weight() {
        // 20 WPM, 50% weight, nanosecond clock.
        let t = KeyerElementTiming::from_speed(20, 50, TICK_HZ).unwrap();
        let ms = TICK_HZ / 1000;
        assert_eq!(t.dit_ticks, 60 * ms, "dit should be 60ms at 20 WPM");
        assert_eq!(t.dah_ticks, 180 * ms, "dah should be 3x dit");
        assert_eq!(t.gap_ticks, 60 * ms, "gap equals dit at 50% weight");
        assert_eq!(t.base_dit_ticks(), 60 * ms);
        assert_eq!(t.inter_character_gap_ticks(), 180 * ms);
        assert_eq!(t.word_gap_ticks(), 420 * ms);
    }

    #[test]
    fn weight_shifts_time_between_element_and_gap() {
        // 75% weight: element 1.5 base dits, gap 0.5 base dits.
        let t = KeyerElementTiming::from_speed(20, 75, TICK_HZ).unwrap();
        let ms = TICK_HZ / 1000;
        assert_eq!(t.dit_ticks, 90 * ms);
        assert_eq!(t.dah_ticks, 270 * ms);
        assert_eq!(t.gap_ticks, 30 * ms);
        // Cycle preserved at two base dits.
        assert_eq!(t.dit_ticks + t.gap_ticks, 120 * ms);
    }

    #[test]
    fn speed_and_weight_are_clamped() {
        let t = KeyerElementTiming::from_speed(0, 0, TICK_HZ).unwrap();
        let clamped = KeyerElementTiming::from_speed(limits::MIN_WPM, limits::MIN_WEIGHT, TICK_HZ).unwrap();
        assert_eq!(t, clamped);
        let fast = KeyerElementTiming::from_speed(9999, 9999, TICK_HZ).unwrap();
        let maxed = KeyerElementTiming::from_speed(limits::MAX_WPM, limits::MAX_WEIGHT, TICK_HZ).unwrap();
        assert_eq!(fast, maxed);
    }

    #[test]
    fn zero_tick_frequency_is_rejected() {
        assert!(KeyerElementTiming::from_speed(20, 50, 0).is_err());
    }

    #[test]
    fn schedule_for_e_has_one_element() {
        // 'E' is a single dit.
        let s = EdgeScheduleBuilder::build("E", 20, 50, 0, TICK_HZ).unwrap();
        assert_eq!(s.edges.len(), 2);
        assert!(s.edges[0].key_down);
        assert_eq!(s.edges[0].timestamp_ticks, 0);
        assert!(!s.edges[1].key_down);
        assert_eq!(s.edges[1].timestamp_ticks, 60_000_000);
    }

    #[test]
    fn schedule_for_s_emits_three_dits_with_even_spacing() {
        // 'S' is "...", at 20 WPM.
        let s = EdgeScheduleBuilder::build("S", 20, 50, 0, TICK_HZ).unwrap();
        assert_eq!(s.edges.len(), 6);
        let downs: Vec<u64> = s.edges.iter().filter(|e| e.key_down).map(|e| e.timestamp_ticks).collect();
        // Each dit starts 120ms after the previous one starts (60ms key + 60ms gap).
        assert_eq!(downs, vec![0, 120_000_000, 240_000_000]);
    }

    #[test]
    fn spaces_produce_a_word_gap() {
        let with_space = EdgeScheduleBuilder::build("E E", 20, 50, 0, TICK_HZ).unwrap();
        // First E: 0..60ms. Word gap 420ms, then second E at 480ms.
        let downs: Vec<u64> =
            with_space.edges.iter().filter(|e| e.key_down).map(|e| e.timestamp_ticks).collect();
        assert_eq!(downs, vec![0, 480_000_000]);
    }

    #[test]
    fn unknown_characters_are_skipped_not_keyed() {
        let s = EdgeScheduleBuilder::build("%E", 20, 50, 0, TICK_HZ).unwrap();
        assert_eq!(s.edges.len(), 2, "only the E should be keyed");
    }

    #[test]
    fn schedule_replays_at_expected_times() {
        let clock = Arc::new(MonotonicClock);
        let keyer = ElementKeyer::new(20, 50, clock.clone()).unwrap();
        let schedule = keyer.schedule_text("E", 20, 50).unwrap();

        let mut observed = Vec::new();
        let completed = keyer.play(&schedule, &mut |e| observed.push(e), &|| false);
        assert!(completed);
        assert_eq!(observed.len(), 2);
        // The key-up landed ~60ms after the key-down (allow scheduler slop).
        let span = observed[1].timestamp_ticks - observed[0].timestamp_ticks;
        assert_eq!(span, 60_000_000);
    }

    #[test]
    fn play_aborts_when_asked() {
        let clock = Arc::new(MonotonicClock);
        let keyer = ElementKeyer::new(20, 50, clock).unwrap();
        let schedule = keyer.schedule_text("PARIS", 20, 50).unwrap();
        let mut count = 0;
        let completed = keyer.play(&schedule, &mut |_| count += 1, &|| true);
        assert!(!completed);
        assert_eq!(count, 0);
    }

    #[test]
    fn iambic_b_alternates_on_squeeze() {
        let mut eng = PaddleElementEngine::new(KeyerMode::IambicB);
        let squeeze = PaddleState { dit: true, dah: true };
        let a = eng.next_element(squeeze);
        eng.on_element_sent(a);
        let b = eng.next_element(squeeze);
        eng.on_element_sent(b);
        assert_ne!(a, b, "squeeze should alternate");
        assert_eq!(a, KeyerElement::Dah);
        assert_eq!(b, KeyerElement::Dit);
    }

    #[test]
    fn single_paddle_selects_correct_element() {
        let mut eng = PaddleElementEngine::new(KeyerMode::IambicB);
        assert_eq!(eng.next_element(PaddleState { dit: true, dah: false }), KeyerElement::Dit);
        assert_eq!(eng.next_element(PaddleState { dit: false, dah: true }), KeyerElement::Dah);
        assert_eq!(eng.next_element(PaddleState { dit: false, dah: false }), KeyerElement::None);
    }

    #[test]
    fn ultimatic_repeats_last_pressed_paddle() {
        let mut eng = PaddleElementEngine::new(KeyerMode::Ultimatic);
        // Press dah first, then squeeze.
        eng.on_contacts_changed(PaddleState { dit: false, dah: true });
        let squeeze = PaddleState { dit: true, dah: true };
        assert_eq!(eng.next_element(squeeze), KeyerElement::Dah);
        assert_eq!(eng.next_element(squeeze), KeyerElement::Dah);
        // Now press dit last; the repeated element flips to dit.
        eng.on_contacts_changed(PaddleState { dit: true, dah: false });
        assert_eq!(eng.next_element(squeeze), KeyerElement::Dit);
    }

    #[test]
    fn straight_key_passes_contacts_through() {
        let mut eng = PaddleElementEngine::new(KeyerMode::Straight);
        assert_eq!(eng.next_element(PaddleState { dit: true, dah: false }), KeyerElement::Dit);
        assert_eq!(eng.next_element(PaddleState { dit: false, dah: true }), KeyerElement::Dit);
        assert_eq!(eng.next_element(PaddleState::default()), KeyerElement::None);
    }

    #[test]
    fn bug_mode_times_dahs_manually() {
        let mut eng = PaddleElementEngine::new(KeyerMode::Bug);
        assert_eq!(eng.next_element(PaddleState { dit: false, dah: true }), KeyerElement::Dah);
        assert_eq!(eng.next_element(PaddleState { dit: true, dah: false }), KeyerElement::Dit);
    }
}
