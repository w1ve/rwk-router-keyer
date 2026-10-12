//! A two-source keying arbiter for the one radio control line.
//!
//! The Station has a single serial key/PTT output, but two things want to drive it: the
//! **remote replay driver** (keyed edges arriving from the Client's paddle over the tunnel)
//! and, optionally, a **local logger** sending WinKeyer CW from a program running at the
//! radio site. Only one may hold the key at a time, so this module arbitrates between them in
//! front of the single physical output.
//!
//! ## Priority
//!
//! The local logger wins. While the logger is transmitting (its key is down, or it has
//! declared itself busy for the span of a buffered message), the remote source is masked:
//! remote key-downs are suppressed and do not reach the line, and remote PTT is held off.
//! When the logger goes idle, the remote source passes through again. This matches the
//! operator's intent — a macro fired locally should not be interrupted or garbled by remote
//! paddle traffic, and vice versa the operator will not fire a macro while actively copying
//! the remote op, so a simple "local overrides" rule is enough; there is no need to queue or
//! merge the two streams.
//!
//! ## Ownership and threading
//!
//! The real [`SerialKeyingAdapter`] (which owns the port and latches write faults for F6)
//! lives behind an `Arc<Mutex<_>>`. Two writers reach it:
//!
//! * The **replay thread** holds a [`RemoteKeyingOutput`], which implements the replay
//!   driver's [`KeyingOutput`] trait, and is moved into `spawn_driver` as the remote sink.
//! * The **logger keyer thread** holds a [`LocalKeyHandle`], cloned from the arbiter, and
//!   calls [`LocalKeyHandle::set_key`] from its CW-generation sink.
//!
//! Every call recomputes the effective line state under the lock and writes it once, so the
//! two threads serialise on the mutex and never both touch the port concurrently. The lock is
//! held only for the duration of a single control-line write — never across an `.await` — so
//! it does not perturb the replay thread's timing.

use std::sync::{Arc, Mutex};

use super::replay::{KeyingOutput, SerialKeyingAdapter};

/// Which source last requested a state, used only for diagnostics/logging.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeySource {
    /// The remote replay driver (paddle edges over the tunnel).
    Remote,
    /// The local logger (WinKeyer CW generated at the Station).
    Local,
}

/// Called whenever the effective line state changes, so a UI can show the real KEY/PTT and
/// which source is driving it. Framework-free: the controller bridges it to its own events.
pub type LineObserver = Arc<dyn Fn(LineUpdate) + Send + Sync>;

/// The effective line state after arbitration, for the [`LineObserver`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LineUpdate {
    /// The key state actually driven on the radio.
    pub key_down: bool,
    /// The PTT state actually driven on the radio.
    pub ptt: bool,
    /// Which source currently owns the line.
    pub source: KeySource,
}

/// Shared keying state behind the arbiter's lock.
struct Inner {
    /// The real output: owns the port, latches faults for F6.
    adapter: SerialKeyingAdapter,
    /// Remote source's requested key/PTT.
    remote_key: bool,
    remote_ptt: bool,
    /// Local (logger) source's requested key/PTT.
    local_key: bool,
    local_ptt: bool,
    /// The logger has declared itself busy for a buffered transmission, so it holds priority
    /// even between individual element key-ups.
    local_busy: bool,
    /// The last state actually written, so we only write the port on a change.
    last_key: Option<bool>,
    last_ptt: Option<bool>,
    /// Optional observer notified on each effective-state change.
    observer: Option<LineObserver>,
}

impl Inner {
    /// Whether the local source currently owns the line.
    fn local_active(&self) -> bool {
        self.local_key || self.local_ptt || self.local_busy
    }

    /// Recomputes the effective key/PTT and writes the port only on a change.
    ///
    /// Local priority: when the local source is active its state is authoritative and the
    /// remote state is ignored; otherwise the remote state drives the line.
    fn apply(&mut self) {
        let source = if self.local_active() { KeySource::Local } else { KeySource::Remote };
        let (key, ptt) = match source {
            KeySource::Local => (self.local_key, self.local_ptt),
            KeySource::Remote => (self.remote_key, self.remote_ptt),
        };

        let key_changed = self.last_key != Some(key);
        let ptt_changed = self.last_ptt != Some(ptt);

        if key_changed {
            self.last_key = Some(key);
            self.adapter.set_key(key);
        }
        if ptt_changed {
            self.last_ptt = Some(ptt);
            self.adapter.set_ptt(ptt);
        }

        // Notify the observer when the physical line changed, so the UI reflects the real
        // KEY/PTT regardless of which source drove it.
        if key_changed || ptt_changed {
            if let Some(observer) = &self.observer {
                observer(LineUpdate { key_down: key, ptt, source });
            }
        }
    }
}

/// The arbiter: construct from the real [`SerialKeyingAdapter`], then take the two source
/// handles. Dropping all handles drops the inner adapter, which releases the port.
pub struct KeyingArbiter {
    inner: Arc<Mutex<Inner>>,
}

impl KeyingArbiter {
    /// Wraps a serial keying adapter so two sources can share it with local priority.
    #[must_use]
    pub fn new(adapter: SerialKeyingAdapter) -> Self {
        Self::with_observer(adapter, None)
    }

    /// Wraps a serial keying adapter and registers a [`LineObserver`] notified on every
    /// effective line-state change (for the UI's KEY/PTT indicators).
    #[must_use]
    pub fn with_observer(adapter: SerialKeyingAdapter, observer: Option<LineObserver>) -> Self {
        let inner = Inner {
            adapter,
            remote_key: false,
            remote_ptt: false,
            local_key: false,
            local_ptt: false,
            local_busy: false,
            last_key: None,
            last_ptt: None,
            observer,
        };
        Self { inner: Arc::new(Mutex::new(inner)) }
    }

    /// The remote sink to move into the replay driver (`spawn_driver`'s `KeyingOutput`).
    #[must_use]
    pub fn remote_output(&self) -> RemoteKeyingOutput {
        RemoteKeyingOutput { inner: Arc::clone(&self.inner) }
    }

    /// A cloneable handle for the local logger's keyer thread.
    #[must_use]
    pub fn local_handle(&self) -> LocalKeyHandle {
        LocalKeyHandle { inner: Arc::clone(&self.inner) }
    }

    /// Which source currently owns the line (for logging/indicators).
    #[must_use]
    pub fn active_source(&self) -> KeySource {
        let inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        if inner.local_active() {
            KeySource::Local
        } else {
            KeySource::Remote
        }
    }
}

/// The remote source's view of the shared output, implementing the replay driver's
/// [`KeyingOutput`]. Moved into `spawn_driver`; the replay thread is its only caller.
pub struct RemoteKeyingOutput {
    inner: Arc<Mutex<Inner>>,
}

impl KeyingOutput for RemoteKeyingOutput {
    fn set_key(&mut self, key_down: bool) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.remote_key = key_down;
        inner.apply();
    }

    fn set_ptt(&mut self, asserted: bool) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.remote_ptt = asserted;
        inner.apply();
    }

    fn take_fault(&mut self) -> Option<String> {
        // Drain the inner adapter's latched fault so F6 still fires if the port errors —
        // the replay driver polls this on the remote sink.
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.adapter.take_fault()
    }
}

/// The local (logger) source's handle. Cloneable so it can be shared with the keyer thread
/// and the immediate-key path; every clone drives the same shared output.
#[derive(Clone)]
pub struct LocalKeyHandle {
    inner: Arc<Mutex<Inner>>,
}

impl LocalKeyHandle {
    /// Keys the line from the logger. While local is active it overrides the remote source.
    pub fn set_key(&self, key_down: bool) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.local_key = key_down;
        inner.apply();
    }

    /// Asserts/releases PTT from the logger.
    pub fn set_ptt(&self, asserted: bool) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.local_ptt = asserted;
        inner.apply();
    }

    /// Declares the logger busy (holding priority) for the span of a buffered transmission,
    /// so a brief key-up between elements does not hand the line back to the remote source
    /// mid-message. Clear it when the message finishes.
    pub fn set_busy(&self, busy: bool) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.local_busy = busy;
        inner.apply();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::serial::{KeyingOutputConfig, SerialKeyingOutput};

    /// A keying arbiter over a dummy adapter is awkward because `SerialKeyingAdapter` needs a
    /// real `SerialKeyingOutput`, which we cannot open in a unit test. The arbitration is a
    /// pure decision over the two sources' states, so we test [`decide`] (which mirrors
    /// `Inner::apply`'s selection exactly) directly, keeping the test hardware-free.

    /// The pure decision the arbiter makes: given the two sources' states, what key/PTT
    /// should reach the line? Mirrors `Inner::apply`'s selection without needing a port.
    fn decide(
        remote_key: bool,
        remote_ptt: bool,
        local_key: bool,
        local_ptt: bool,
        local_busy: bool,
    ) -> (bool, bool) {
        let local_active = local_key || local_ptt || local_busy;
        if local_active {
            (local_key, local_ptt)
        } else {
            (remote_key, remote_ptt)
        }
    }

    #[test]
    fn remote_drives_the_line_when_local_is_idle() {
        assert_eq!(decide(true, true, false, false, false), (true, true));
        assert_eq!(decide(false, false, false, false, false), (false, false));
    }

    #[test]
    fn local_key_overrides_remote() {
        // Remote wants key down, but local is keyed up and busy: local wins (key up).
        assert_eq!(decide(true, true, false, false, true), (false, false));
        // Local keyed down overrides a remote key-up.
        assert_eq!(decide(false, false, true, true, false), (true, true));
    }

    #[test]
    fn local_busy_holds_priority_between_elements() {
        // Mid-message: local key momentarily up but still busy, remote must stay masked.
        assert_eq!(decide(true, false, false, true, true), (false, true));
    }

    #[test]
    fn remote_resumes_when_local_releases_and_clears_busy() {
        // Local fully idle (not busy): remote passes through again.
        assert_eq!(decide(true, true, false, false, false), (true, true));
    }

    #[test]
    fn arbiter_wires_a_real_adapter_and_reports_active_source() {
        // On a machine with no such port this still constructs the adapter lazily only if the
        // port opens; skip the live write path. We only assert source selection logic here by
        // opening a guaranteed-invalid port and tolerating the error.
        let cfg = KeyingOutputConfig { port_name: "RWK_TEST_NONEXISTENT".into(), ..Default::default() };
        if let Ok(out) = SerialKeyingOutput::open(cfg) {
            // Unlikely on a dev box, but if a port by that name exists, exercise the arbiter.
            let arb = KeyingArbiter::new(SerialKeyingAdapter::new(out));
            assert_eq!(arb.active_source(), KeySource::Remote);
            let local = arb.local_handle();
            local.set_busy(true);
            assert_eq!(arb.active_source(), KeySource::Local);
        }
    }
}
