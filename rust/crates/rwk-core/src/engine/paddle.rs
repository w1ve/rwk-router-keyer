//! The live paddle keyer: turning paddle contacts into timed CW edges in real time.
//!
//! The text path ([`EdgeScheduleBuilder`](crate::engine::keying::EdgeScheduleBuilder)) builds
//! a whole schedule up front, because the message is known. A paddle cannot work that way —
//! the operator decides the next element by what they are pressing *now*, including mid-
//! element. This keyer therefore runs a live loop: decide an element, key it for the right
//! duration, insert the inter-element gap, and decide again, sampling the paddles throughout
//! so iambic squeeze and mode semantics come out right.
//!
//! It wraps [`PaddleElementEngine`](crate::engine::keying::PaddleElementEngine) (the stateless
//! "which element now?" decider) and adds the two things a real keyer needs on top:
//!
//! * **Timing** via [`KeyerElementTiming`](crate::engine::keying::KeyerElementTiming), paced
//!   by [`HybridWaiter`](crate::timing::HybridWaiter) so element boundaries are sub-millisecond
//!   accurate even on a busy desktop.
//! * **Iambic B squeeze memory.** In Iambic B, if the operator squeezes both paddles and
//!   releases during an element, the keyer sends **one more** element of the opposite kind.
//!   The bare decider only reads the instantaneous contacts, so this latch lives here: the
//!   opposite paddle seen closed at any point during the current element is remembered and
//!   forced as the next element once. Iambic A omits the latch.
//!
//! The loop is driven by two closures the caller supplies, which keeps it testable without a
//! serial port or threads:
//!
//! * `paddle()` returns the current debounced [`PaddleState`] (from the serial reader +
//!   debouncer in the real app, or a scripted sequence in tests).
//! * `emit(EdgeEvent)` is called at each key transition — the same per-edge sink the text
//!   path uses, so the caller fans it to sidetone, the key line, the UI, and the tunnel.
//!
//! `run_once` plays exactly one element (or returns immediately when idle), so the caller's
//! thread stays responsive to a stop signal between elements.

use crate::engine::keying::{EdgeEvent, KeyerElement, KeyerElementTiming, PaddleElementEngine, PaddleState};
use crate::primitives::{EdgeSource, KeyerMode};
use crate::timing::{Clock, HybridWaiter};

/// A live paddle keyer: wraps the element decider with timing and iambic memory.
pub struct PaddleKeyer {
    engine: PaddleElementEngine,
    timing: KeyerElementTiming,
    mode: KeyerMode,
    /// The opposite element latched by an Iambic B squeeze, to force next.
    latched: Option<KeyerElement>,
}

impl PaddleKeyer {
    /// Creates a keyer for `mode` at `wpm`/`weight` against `tick_frequency`.
    ///
    /// # Errors
    ///
    /// Propagates [`KeyerElementTiming::from_speed`] errors (zero tick frequency).
    pub fn new(mode: KeyerMode, wpm: u32, weight: u32, tick_frequency: u64) -> crate::Result<Self> {
        Ok(Self {
            engine: PaddleElementEngine::new(mode),
            timing: KeyerElementTiming::from_speed(wpm, weight, tick_frequency)?,
            mode,
            latched: None,
        })
    }

    /// Re-tunes speed/weight live (e.g. the operator moved the WPM slider mid-stream).
    ///
    /// # Errors
    ///
    /// Propagates [`KeyerElementTiming::from_speed`] errors.
    pub fn retune(&mut self, wpm: u32, weight: u32, tick_frequency: u64) -> crate::Result<()> {
        self.timing = KeyerElementTiming::from_speed(wpm, weight, tick_frequency)?;
        Ok(())
    }

    /// Changes the keyer mode live, resetting latch and alternation history.
    pub fn set_mode(&mut self, mode: KeyerMode) {
        self.mode = mode;
        self.engine.set_mode(mode);
        self.latched = None;
    }

    /// The keyer mode in use.
    #[must_use]
    pub fn mode(&self) -> KeyerMode {
        self.mode
    }

    /// Plays one element if a paddle is pressed, pacing against `clock` and emitting edges
    /// through `emit`. Returns `true` if an element was played, `false` if the paddles were
    /// idle (the caller should keep polling).
    ///
    /// `paddle` is sampled at the decision point and again throughout the element so an
    /// Iambic B squeeze latches the opposite element. `should_abort` interrupts a wait (e.g.
    /// the poller is stopping); an aborted element still forces the key up.
    ///
    /// Straight-key mode is handled specially: there is no element timing — the key simply
    /// follows the contact, so the caller is expected to drive the straight path directly
    /// (see [`Self::run_straight`]). In the other modes this plays one timed element.
    pub fn run_once(
        &mut self,
        clock: &dyn Clock,
        paddle: &dyn Fn() -> PaddleState,
        emit: &mut dyn FnMut(EdgeEvent),
        should_abort: &dyn Fn() -> bool,
    ) -> bool {
        let now = clock.now();
        let current = paddle();
        self.engine.on_contacts_changed(current);

        // Pick the element: a latched Iambic B element wins, otherwise ask the decider.
        let element = match self.latched.take() {
            Some(latched) => latched,
            None => self.engine.next_element(current),
        };
        if element == KeyerElement::None {
            return false;
        }

        // Straight mode is contact-follow, not element timing.
        if self.mode == KeyerMode::Straight {
            self.run_straight(clock, paddle, emit, should_abort);
            return true;
        }

        let element_ticks = self.timing.ticks_for(element);
        let gap_ticks = self.timing.gap_ticks;

        // Key down for the element duration.
        emit(EdgeEvent { timestamp_ticks: now, key_down: true, source: EdgeSource::Paddle });
        let key_up_at = now + element_ticks;
        let opposite = other(element);
        // While the element plays, watch for the opposite paddle (Iambic B squeeze memory).
        let mut squeezed = false;
        self.wait_watching(clock, key_up_at, paddle, opposite, &mut squeezed, should_abort);
        emit(EdgeEvent { timestamp_ticks: clock.now(), key_down: false, source: EdgeSource::Paddle });

        self.engine.on_element_sent(element);

        // Inter-element gap (key up), still watching for the squeeze.
        let gap_until = key_up_at + gap_ticks;
        self.wait_watching(clock, gap_until, paddle, opposite, &mut squeezed, should_abort);

        // Iambic B: a squeeze seen during this element queues one opposite element, even if
        // both paddles are now released. Iambic A does not latch.
        if self.mode == KeyerMode::IambicB && squeezed {
            self.latched = Some(opposite);
        }

        true
    }

    /// Straight-key loop: the key follows the (dit) contact exactly until it opens. Emits a
    /// key-down on entry and a key-up when the contact releases or `should_abort` fires.
    fn run_straight(
        &mut self,
        clock: &dyn Clock,
        paddle: &dyn Fn() -> PaddleState,
        emit: &mut dyn FnMut(EdgeEvent),
        should_abort: &dyn Fn() -> bool,
    ) {
        emit(EdgeEvent { timestamp_ticks: clock.now(), key_down: true, source: EdgeSource::Paddle });
        // Hold the key down while the contact stays closed. Poll finely so release latency is
        // sub-millisecond without a busy spin.
        while paddle().dit && !should_abort() {
            let next = clock.now() + clock.frequency() / 2000; // ~0.5 ms
            HybridWaiter::wait_until_ticks(clock, next, should_abort);
        }
        emit(EdgeEvent { timestamp_ticks: clock.now(), key_down: false, source: EdgeSource::Paddle });
    }

    /// Waits until `target` ticks, sampling the paddle meanwhile; sets `squeezed` if the
    /// `opposite` paddle is seen closed at any point (the Iambic B latch trigger).
    fn wait_watching(
        &self,
        clock: &dyn Clock,
        target: u64,
        paddle: &dyn Fn() -> PaddleState,
        opposite: KeyerElement,
        squeezed: &mut bool,
        should_abort: &dyn Fn() -> bool,
    ) {
        while clock.now() < target {
            if should_abort() {
                return;
            }
            let p = paddle();
            if (opposite == KeyerElement::Dah && p.dah) || (opposite == KeyerElement::Dit && p.dit)
            {
                *squeezed = true;
            }
            // Poll roughly every 0.5 ms so a brief squeeze tap is not missed, then let the
            // hybrid waiter take us precisely to the boundary on the final step.
            let step = clock.now() + clock.frequency() / 2000;
            let next = step.min(target);
            HybridWaiter::wait_until_ticks(clock, next, should_abort);
        }
    }
}

/// The opposite element (dit↔dah); [`KeyerElement::None`] maps to itself.
fn other(element: KeyerElement) -> KeyerElement {
    match element {
        KeyerElement::Dit => KeyerElement::Dah,
        KeyerElement::Dah => KeyerElement::Dit,
        KeyerElement::None => KeyerElement::None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::timing::MonotonicClock;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, Mutex};

    const HZ: u64 = 1_000_000_000;

    /// Collects emitted edges for assertions.
    fn collector() -> (Arc<Mutex<Vec<EdgeEvent>>>, impl FnMut(EdgeEvent)) {
        let edges = Arc::new(Mutex::new(Vec::new()));
        let sink_edges = Arc::clone(&edges);
        let sink = move |e: EdgeEvent| sink_edges.lock().unwrap().push(e);
        (edges, sink)
    }

    #[test]
    fn idle_paddle_plays_no_element() {
        let mut keyer = PaddleKeyer::new(KeyerMode::IambicB, 20, 50, HZ).unwrap();
        let clock = MonotonicClock;
        let (edges, mut sink) = collector();
        let played =
            keyer.run_once(&clock, &|| PaddleState::default(), &mut sink, &|| false);
        assert!(!played, "idle paddle should play nothing");
        assert!(edges.lock().unwrap().is_empty());
    }

    #[test]
    fn a_dit_press_emits_one_key_down_then_up_stamped_paddle() {
        let mut keyer = PaddleKeyer::new(KeyerMode::IambicA, 30, 50, HZ).unwrap();
        let clock = MonotonicClock;
        let (edges, mut sink) = collector();
        let played =
            keyer.run_once(&clock, &|| PaddleState { dit: true, dah: false }, &mut sink, &|| false);
        assert!(played);
        let edges = edges.lock().unwrap();
        assert_eq!(edges.len(), 2, "one element = key-down + key-up");
        assert!(edges[0].key_down);
        assert!(!edges[1].key_down);
        assert_eq!(edges[0].source, EdgeSource::Paddle);
    }

    #[test]
    fn dah_is_three_times_a_dit() {
        let dit_len = {
            let mut k = PaddleKeyer::new(KeyerMode::IambicA, 20, 50, HZ).unwrap();
            let clock = MonotonicClock;
            let (edges, mut sink) = collector();
            k.run_once(&clock, &|| PaddleState { dit: true, dah: false }, &mut sink, &|| false);
            let e = edges.lock().unwrap();
            e[1].timestamp_ticks - e[0].timestamp_ticks
        };
        let dah_len = {
            let mut k = PaddleKeyer::new(KeyerMode::IambicA, 20, 50, HZ).unwrap();
            let clock = MonotonicClock;
            let (edges, mut sink) = collector();
            k.run_once(&clock, &|| PaddleState { dit: false, dah: true }, &mut sink, &|| false);
            let e = edges.lock().unwrap();
            e[1].timestamp_ticks - e[0].timestamp_ticks
        };
        // Allow slack for scheduling: dah should be roughly 3x the dit.
        let ratio = dah_len as f64 / dit_len as f64;
        assert!((2.5..3.5).contains(&ratio), "dah/dit ratio {ratio} should be ~3");
    }

    #[test]
    fn straight_mode_follows_the_contact_until_released() {
        let mut keyer = PaddleKeyer::new(KeyerMode::Straight, 20, 50, HZ).unwrap();
        let clock = MonotonicClock;
        let (edges, mut sink) = collector();

        // Release the contact shortly after it is pressed, via a shared flag the closure reads.
        let held = Arc::new(AtomicBool::new(true));
        let flag = Arc::clone(&held);
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(20));
            flag.store(false, Ordering::SeqCst);
        });

        let read = {
            let held = Arc::clone(&held);
            move || PaddleState { dit: held.load(Ordering::SeqCst), dah: false }
        };
        let played = keyer.run_once(&clock, &read, &mut sink, &|| false);
        assert!(played);
        let edges = edges.lock().unwrap();
        assert_eq!(edges.len(), 2, "straight key = one down + one up");
        assert!(edges[0].key_down && !edges[1].key_down);
        // The key was held ~20 ms before release.
        let held_ticks = edges[1].timestamp_ticks - edges[0].timestamp_ticks;
        assert!(held_ticks >= 15 * (HZ / 1000), "key should stay down until contact released");
    }

    #[test]
    fn iambic_b_latches_one_opposite_element_after_a_squeeze() {
        // Squeeze both paddles during a dit; Iambic B should then force a dah even once the
        // paddles are released. We simulate: dit+dah held during the first element, then idle.
        let mut keyer = PaddleKeyer::new(KeyerMode::IambicB, 25, 50, HZ).unwrap();
        let clock = MonotonicClock;

        let squeeze = Arc::new(AtomicBool::new(true));
        let flag = Arc::clone(&squeeze);
        // Release both paddles after the first element is well underway.
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(10));
            flag.store(false, Ordering::SeqCst);
        });
        let read = {
            let squeeze = Arc::clone(&squeeze);
            move || {
                let s = squeeze.load(Ordering::SeqCst);
                PaddleState { dit: s, dah: s }
            }
        };

        let (edges, mut sink) = collector();
        // First element (a dah, since IambicB squeeze alternates away from None->Dah first).
        assert!(keyer.run_once(&clock, &read, &mut sink, &|| false));
        // Second call: paddles are now released, but the squeeze must have latched the
        // opposite element, so another element plays.
        let second = keyer.run_once(&clock, &read, &mut sink, &|| false);
        assert!(second, "Iambic B must play a latched element after the squeeze releases");
    }
}
