//! OS-level scheduling: thread priority and timer resolution.
//!
//! The .NET Station raised its replay thread to `THREAD_PRIORITY_TIME_CRITICAL` and
//! called `timeBeginPeriod(1)` so that a 1 ms sleep really was 1 ms. Both matter for
//! keying: the default Windows scheduling quantum is ~15.6 ms, so on a busy machine a
//! sub-millisecond element deadline can otherwise be missed by a whole quantum — the
//! [`HybridWaiter`](crate::timing::HybridWaiter) spin window only helps if the thread
//! actually gets scheduled inside it.
//!
//! This is the **one** module in `rwk-core` permitted to use `unsafe`: it wraps platform
//! FFI in RAII guards that restore the previous setting on drop, and every entry point
//! degrades to a reported [`PriorityOutcome`] instead of failing, because raising
//! priority is an optimization rather than a correctness requirement. The crate root
//! therefore uses `deny(unsafe_code)` with a single module-level `allow`, so the rest of
//! the engine stays provably free of FFI.
//!
//! ## Verification status
//!
//! The Windows backend (the MVP target) is exercised on Windows. The Linux backend uses
//! `sched_setscheduler(SCHED_FIFO)` with a `setpriority` fallback and is written to the
//! documented POSIX contract; it is not yet run on hardware (the Linux/Raspberry Pi
//! target is a later milestone).

/// The result of asking the OS to raise the current thread's scheduling priority.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PriorityOutcome {
    /// The OS accepted the request; the thread now runs at elevated priority.
    Applied,
    /// The OS refused for lack of privilege — normal for Linux without
    /// `CAP_SYS_NICE`, and for a Windows thread whose priority is managed by a job
    /// object. Keying continues, just with less scheduling protection.
    NotPermitted,
    /// The platform has no supported mechanism for this; the request was a no-op.
    Unsupported,
    /// The call failed for some other reason; carries the raw OS error code.
    Failed(i32),
}

impl PriorityOutcome {
    /// Whether the priority request took effect.
    #[must_use]
    pub fn is_applied(self) -> bool {
        matches!(self, Self::Applied)
    }

    /// A one-line, operator-facing description.
    #[must_use]
    pub fn describe(self) -> String {
        match self {
            Self::Applied => "time-critical thread priority applied".to_string(),
            Self::NotPermitted => "time-critical priority not permitted; running at normal priority".to_string(),
            Self::Unsupported => "time-critical priority unsupported on this platform".to_string(),
            Self::Failed(code) => format!("time-critical priority request failed (os error {code})"),
        }
    }
}

/// RAII guard that holds an elevated thread priority and restores the previous setting
/// when dropped.
///
/// The guard is intentionally usable even when the request was refused: dropping it is
/// always safe and never panics, so callers can hold it unconditionally.
#[derive(Debug)]
pub struct ThreadPriorityGuard {
    outcome: PriorityOutcome,
    restore: imp::Restore,
}

impl ThreadPriorityGuard {
    /// Raises the *calling* thread to time-critical priority.
    #[must_use]
    pub fn raise_time_critical() -> Self {
        let (outcome, restore) = imp::raise();
        Self { outcome, restore }
    }

    /// What the OS reported for this request.
    #[must_use]
    pub fn outcome(&self) -> PriorityOutcome {
        self.outcome
    }
}

impl Drop for ThreadPriorityGuard {
    fn drop(&mut self) {
        imp::restore(&self.restore);
    }
}

/// RAII guard that requests a finer system timer resolution and releases it on drop.
///
/// Only Windows has a process-visible timer period; on other platforms the guard is a
/// no-op that reports [`PriorityOutcome::Unsupported`] through [`Self::applied`].
#[derive(Debug)]
pub struct TimerResolutionGuard {
    resolution_ms: u32,
    applied: bool,
    _timer: imp::Timer,
}

impl TimerResolutionGuard {
    /// Requests a system timer resolution of `resolution_ms` milliseconds.
    #[must_use]
    pub fn raise(resolution_ms: u32) -> Self {
        let (applied, timer) = imp::Timer::raise(resolution_ms);
        Self { resolution_ms, applied, _timer: timer }
    }

    /// Whether the finer resolution was actually granted.
    #[must_use]
    pub fn applied(&self) -> bool {
        self.applied
    }

    /// The requested resolution, in milliseconds.
    #[must_use]
    pub fn resolution_ms(&self) -> u32 {
        self.resolution_ms
    }
}

/// Raises the calling thread to time-critical priority.
///
/// Convenience for callers that do not need to keep the guard alive: prefer
/// [`ThreadPriorityGuard::raise_time_critical`] so the setting is restored.
#[must_use]
pub fn raise_current_thread() -> ThreadPriorityGuard {
    ThreadPriorityGuard::raise_time_critical()
}

#[cfg(windows)]
mod imp {
    use super::PriorityOutcome;
    use core::ffi::c_void;

    type Handle = *mut c_void;

    /// `GetThreadPriority` error sentinel; a real priority is a small signed value.
    const THREAD_PRIORITY_ERROR_RETURN: i32 = 0x7FFF_FFFF;
    const THREAD_PRIORITY_NORMAL: i32 = 0;
    const THREAD_PRIORITY_TIME_CRITICAL: i32 = 15;
    /// `TIMERR_NOERROR` from `mmsystem.h`.
    const TIMERR_NOERROR: u32 = 0;

    extern "system" {
        fn GetCurrentThread() -> Handle;
        fn GetThreadPriority(thread: Handle) -> i32;
        fn SetThreadPriority(thread: Handle, priority: i32) -> i32;
        fn GetLastError() -> u32;
    }

    // `timeBeginPeriod`/`timeEndPeriod` live in winmm, which is not linked by default.
    #[link(name = "winmm")]
    extern "system" {
        fn timeBeginPeriod(period: u32) -> u32;
        fn timeEndPeriod(period: u32) -> u32;
    }

    /// The previous priority to restore, or `None` when it could not be read.
    ///
    /// # Safety
    ///
    /// The FFI calls here follow the documented Win32 contract: the pseudo-handle from
    /// `GetCurrentThread` is only ever passed to the thread-priority functions, and the
    /// timer period is balanced by `Timer`'s `Drop`.
    #[derive(Debug)]
    pub struct Restore(Option<i32>);

    pub fn raise() -> (PriorityOutcome, Restore) {
        unsafe {
            let thread = GetCurrentThread();
            let previous = GetThreadPriority(thread);
            if SetThreadPriority(thread, THREAD_PRIORITY_TIME_CRITICAL) != 0 {
                let restore = if previous == THREAD_PRIORITY_ERROR_RETURN { None } else { Some(previous) };
                (PriorityOutcome::Applied, Restore(restore))
            } else {
                (PriorityOutcome::Failed(GetLastError() as i32), Restore(None))
            }
        }
    }

    pub fn restore(state: &Restore) {
        unsafe {
            let priority = state.0.unwrap_or(THREAD_PRIORITY_NORMAL);
            let _ = SetThreadPriority(GetCurrentThread(), priority);
        }
    }

    #[derive(Debug)]
    pub struct Timer {
        period_ms: u32,
        active: bool,
    }

    impl Timer {
        pub fn raise(period_ms: u32) -> (bool, Timer) {
            let active = unsafe { timeBeginPeriod(period_ms) } == TIMERR_NOERROR;
            (active, Timer { period_ms, active })
        }
    }

    impl Drop for Timer {
        fn drop(&mut self) {
            if self.active {
                unsafe {
                    timeEndPeriod(self.period_ms);
                }
            }
        }
    }
}

#[cfg(target_os = "linux")]
mod imp {
    use super::PriorityOutcome;

    #[repr(C)]
    struct SchedParam {
        sched_priority: i32,
    }

    const SCHED_OTHER: i32 = 0;
    const SCHED_FIFO: i32 = 1;
    const PRIO_PROCESS: u32 = 0;
    /// The most favourable `nice` value; reaching it needs `CAP_SYS_NICE` or a raised
    /// `RLIMIT_NICE`.
    const NICE_MIN: i32 = -20;

    extern "C" {
        fn sched_setscheduler(pid: i32, policy: i32, param: *const SchedParam) -> i32;
        fn setpriority(which: u32, who: u32, prio: i32) -> i32;
    }

    /// How the priority was raised, so it can be undone precisely.
    #[derive(Debug)]
    pub enum Restore {
        /// Real-time FIFO scheduling, which must be dropped back to `SCHED_OTHER`.
        Fifo,
        /// Only the `nice` value changed.
        Nice,
        /// Nothing was changed.
        Nothing,
    }

    pub fn raise() -> (PriorityOutcome, Restore) {
        // Prefer real-time FIFO: it is the Linux analogue of THREAD_PRIORITY_TIME_CRITICAL.
        // Priority 1 is the lowest real-time priority, which is still above every
        // SCHED_OTHER thread — enough for a keying deadline without starving the system.
        unsafe {
            let fifo = SchedParam { sched_priority: 1 };
            if sched_setscheduler(0, SCHED_FIFO, &fifo) == 0 {
                return (PriorityOutcome::Applied, Restore::Fifo);
            }
        }
        // Real-time scheduling needs CAP_SYS_NICE; unprivileged processes fall back to
        // the best `nice` value they are allowed to set.
        unsafe {
            if setpriority(PRIO_PROCESS, 0, NICE_MIN) == 0 {
                return (PriorityOutcome::Applied, Restore::Nice);
            }
        }
        (PriorityOutcome::NotPermitted, Restore::Nothing)
    }

    pub fn restore(state: &Restore) {
        unsafe {
            match state {
                Restore::Fifo => {
                    let other = SchedParam { sched_priority: 0 };
                    let _ = sched_setscheduler(0, SCHED_OTHER, &other);
                }
                Restore::Nice => {
                    let _ = setpriority(PRIO_PROCESS, 0, 0);
                }
                Restore::Nothing => {}
            }
        }
    }

    /// Linux has no process-visible timer period; `nanosleep` is already high resolution.
    #[derive(Debug)]
    pub struct Timer;

    impl Timer {
        pub fn raise(_period_ms: u32) -> (bool, Timer) {
            (false, Timer)
        }
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
mod imp {
    use super::PriorityOutcome;

    #[derive(Debug)]
    pub struct Restore;

    pub fn raise() -> (PriorityOutcome, Restore) {
        (PriorityOutcome::Unsupported, Restore)
    }

    pub fn restore(_state: &Restore) {}

    #[derive(Debug)]
    pub struct Timer;

    impl Timer {
        pub fn raise(_period_ms: u32) -> (bool, Timer) {
            (false, Timer)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn raising_and_dropping_priority_never_panics_or_fails_hard() {
        // Whether the OS grants the elevation is environment-dependent (a Linux CI box
        // without CAP_SYS_NICE refuses), so the contract under test is that the request
        // is always safe and always reports an outcome.
        let guard = ThreadPriorityGuard::raise_time_critical();
        let outcome = guard.outcome();
        assert!(
            matches!(
                outcome,
                PriorityOutcome::Applied
                    | PriorityOutcome::NotPermitted
                    | PriorityOutcome::Unsupported
                    | PriorityOutcome::Failed(_)
            ),
            "unexpected outcome {outcome:?}"
        );
        if cfg!(any(windows, target_os = "linux")) {
            assert!(
                !matches!(outcome, PriorityOutcome::Unsupported),
                "a supported platform must not report Unsupported"
            );
        }
        // Dropping restores the previous priority; the test passing is the assertion.
        drop(guard);
    }

    #[test]
    fn outcome_descriptions_are_operator_readable() {
        assert!(PriorityOutcome::Applied.is_applied());
        assert!(!PriorityOutcome::NotPermitted.is_applied());
        assert!(PriorityOutcome::NotPermitted.describe().contains("not permitted"));
        assert!(PriorityOutcome::Failed(5).describe().contains("5"));
    }

    #[test]
    fn timer_resolution_guard_reports_whether_it_applied() {
        let guard = TimerResolutionGuard::raise(1);
        assert_eq!(guard.resolution_ms(), 1);
        if cfg!(windows) {
            assert!(guard.applied(), "Windows should grant a 1ms timer period");
        } else {
            assert!(!guard.applied(), "no timer-period API off Windows");
        }
    }

    #[test]
    fn raising_a_second_time_is_independent_and_restores_cleanly() {
        // Nested guards must not corrupt the restore path.
        let outer = raise_current_thread();
        let inner = raise_current_thread();
        assert_eq!(inner.outcome(), outer.outcome());
        drop(inner);
        drop(outer);
    }
}
