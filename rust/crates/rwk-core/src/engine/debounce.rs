//! Per-contact paddle debounce.
//!
//! A faithful port of the C# `ContactDebouncer`. Mechanical paddle and key contacts bounce
//! for a few milliseconds when they open or close; without filtering, a single press can
//! read as several, producing spurious extra dits. This debouncer accepts a contact's new
//! state only once it has been stable (differing from the last accepted state) for at least
//! the debounce window.
//!
//! Each of the three contacts (dit, dah, straight) is debounced **independently**: a filtered
//! dit bounce never delays a genuine dah transition. The first transition ever seen on a
//! contact is always accepted, so opening the port does not swallow the operator's first
//! press.
//!
//! The type is pure — no clock, no I/O. The caller supplies the raw sample and the timestamp
//! (monotonic ticks, same domain as [`Clock::now`](crate::timing::Clock::now)), which is what
//! makes the debounce rule testable without a paddle. It is not thread-safe for concurrent
//! `accept` calls; the single polling thread is the only caller.

use crate::engine::paddle_input::ContactSample;

/// The three debounced contacts, indexed for the per-contact state arrays.
const CONTACT_COUNT: usize = 3;
const DIT: usize = 0;
const DAH: usize = 1;
const STRAIGHT: usize = 2;

/// A per-contact debounce state machine over a monotonic tick clock.
#[derive(Debug, Clone)]
pub struct ContactDebouncer {
    /// Debounce window, in clock ticks.
    window_ticks: u64,
    /// The currently accepted state of each contact.
    accepted: [bool; CONTACT_COUNT],
    /// The tick at which each contact last had an accepted transition.
    last_accepted: [u64; CONTACT_COUNT],
    /// Whether each contact has ever had an accepted transition.
    has_accepted: [bool; CONTACT_COUNT],
}

impl ContactDebouncer {
    /// The default debounce window: 5 ms, matching the C# default.
    pub const DEFAULT_WINDOW_MS: u64 = 5;

    /// Creates a debouncer with a window of `window_ms` milliseconds over a clock running at
    /// `tick_frequency` ticks per second. A zero window accepts every transition.
    #[must_use]
    pub fn new(window_ms: u64, tick_frequency: u64) -> Self {
        let window_ticks = window_ms.saturating_mul(tick_frequency) / 1000;
        Self {
            window_ticks,
            accepted: [false; CONTACT_COUNT],
            last_accepted: [0; CONTACT_COUNT],
            has_accepted: [false; CONTACT_COUNT],
        }
    }

    /// Creates a debouncer with the default 5 ms window.
    #[must_use]
    pub fn with_default_window(tick_frequency: u64) -> Self {
        Self::new(Self::DEFAULT_WINDOW_MS, tick_frequency)
    }

    /// The currently accepted (debounced) contact states.
    #[must_use]
    pub fn accepted(&self) -> ContactSample {
        ContactSample {
            dit: self.accepted[DIT],
            dah: self.accepted[DAH],
            straight: self.accepted[STRAIGHT],
        }
    }

    /// Offers one raw sample taken at `timestamp_ticks`.
    ///
    /// Returns `Some(accepted)` when at least one contact transition was accepted (the caller
    /// should act on the new state), or `None` when the sample matched the accepted state or
    /// was filtered as bounce.
    pub fn accept(&mut self, raw: ContactSample, timestamp_ticks: u64) -> Option<ContactSample> {
        let raw_contacts = [raw.dit, raw.dah, raw.straight];
        let mut changed = false;

        for i in 0..CONTACT_COUNT {
            if raw_contacts[i] == self.accepted[i] {
                continue;
            }
            // First-ever transition is always accepted; afterwards the contact's own window
            // governs, independently of the other contacts.
            if self.has_accepted[i]
                && timestamp_ticks.saturating_sub(self.last_accepted[i]) < self.window_ticks
            {
                continue;
            }
            self.accepted[i] = raw_contacts[i];
            self.last_accepted[i] = timestamp_ticks;
            self.has_accepted[i] = true;
            changed = true;
        }

        changed.then(|| self.accepted())
    }

    /// Clears all accepted state and history, as though no sample had been seen. Call on open
    /// or close so a stale window cannot suppress the first contact of a new session.
    pub fn reset(&mut self) {
        self.accepted = [false; CONTACT_COUNT];
        self.last_accepted = [0; CONTACT_COUNT];
        self.has_accepted = [false; CONTACT_COUNT];
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A 1 MHz tick clock makes 1 tick == 1 µs, so a 5 ms window == 5000 ticks.
    const HZ: u64 = 1_000_000;

    fn sample(dit: bool, dah: bool, straight: bool) -> ContactSample {
        ContactSample { dit, dah, straight }
    }

    #[test]
    fn first_transition_is_always_accepted() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        let out = d.accept(sample(true, false, false), 0);
        assert_eq!(out, Some(sample(true, false, false)));
    }

    #[test]
    fn a_bounce_within_the_window_is_filtered() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some()); // dit down accepted
        // Dit tries to open 2 ms later (< 5 ms window): filtered.
        assert!(d.accept(sample(false, false, false), 2_000).is_none());
        // Still reads as pressed.
        assert!(d.accepted().dit);
    }

    #[test]
    fn a_change_after_the_window_is_accepted() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some());
        // Open 6 ms later (> 5 ms): accepted.
        let out = d.accept(sample(false, false, false), 6_000);
        assert_eq!(out, Some(sample(false, false, false)));
        assert!(!d.accepted().dit);
    }

    #[test]
    fn contacts_are_debounced_independently() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some()); // dit at t=0
        // 2 ms later a dah press arrives (dit still bouncing-eligible, but dah is independent
        // and has never transitioned, so it is accepted immediately).
        let out = d.accept(sample(true, true, false), 2_000);
        assert_eq!(out, Some(sample(true, true, false)));
    }

    #[test]
    fn zero_window_accepts_every_transition() {
        let mut d = ContactDebouncer::new(0, HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some());
        assert!(d.accept(sample(false, false, false), 1).is_some());
        assert!(d.accept(sample(true, false, false), 2).is_some());
    }

    #[test]
    fn reset_clears_history_so_the_next_press_is_accepted() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some());
        d.reset();
        // After reset, the same state is a fresh first-ever transition and is accepted.
        assert!(d.accept(sample(true, false, false), 100).is_some());
    }

    #[test]
    fn an_unchanged_sample_reports_no_change() {
        let mut d = ContactDebouncer::with_default_window(HZ);
        assert!(d.accept(sample(true, false, false), 0).is_some());
        assert!(d.accept(sample(true, false, false), 10_000).is_none());
    }
}
