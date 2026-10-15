//! Serial-port plug/unplug detection (shared by the Keyer-Router Client and Station).
//!
//! The operator expects the port dropdowns to track reality: plug in a USB-serial adapter and
//! it appears; pull one and any selection using it drops to `(None)`. This module provides the
//! *trigger* for that reconciliation; each controller owns the actual re-enumerate-and-reconcile
//! logic and passes it in as a closure.
//!
//! Two backends behind one contract:
//!
//! * **Windows** — event-driven. We subclass the Tauri window and watch for `WM_DEVICECHANGE`
//!   (`DBT_DEVICEARRIVAL` / `DBT_DEVICEREMOVECOMPLETE`). The message is only a hint ("something
//!   changed"); on it we fire the closure, which re-scans. Changes are reflected within a few
//!   hundred milliseconds of the plug event.
//! * **Everywhere else** — a timer thread fires the closure every [`POLL_INTERVAL`]. macOS
//!   (IOKit) and Linux (udev) have real event sources, but polling is simple and cheap and is
//!   the agreed fallback until those backends are added.
//!
//! On Windows a slow safety-net poll also runs, so a missed message cannot leave the lists
//! stale indefinitely.
//!
//! The watcher holds the callback for the life of the app; it is created once at startup and
//! never torn down, so there is no unsubscribe path.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

/// How often the polling backend (and the Windows safety net) re-scans.
const POLL_INTERVAL: Duration = Duration::from_millis(1500);
/// The slower safety-net poll used on Windows alongside the event hook.
const WINDOWS_SAFETY_POLL: Duration = Duration::from_secs(5);

/// The reconciliation callback: "the device set may have changed — re-scan now."
pub type OnChange = Arc<dyn Fn() + Send + Sync>;

/// A running device-change watcher. Keep it alive for the life of the window.
pub struct DeviceChangeWatcher {
    _stop: Arc<AtomicBool>,
}

impl DeviceChangeWatcher {
    /// Starts watching. `raw_window` is the platform window handle used by the event backend
    /// (the Win32 `HWND` as an `isize` on Windows); it is ignored by the polling backend and
    /// may be `None`, in which case polling is always used.
    #[must_use]
    pub fn start(raw_window: Option<isize>, on_change: OnChange) -> Self {
        let stop = Arc::new(AtomicBool::new(false));

        #[cfg(windows)]
        {
            if let Some(hwnd) = raw_window {
                windows_backend::install(hwnd, Arc::clone(&on_change));
                // Safety-net poll in case a message is ever missed.
                spawn_poller(Arc::clone(&stop), Arc::clone(&on_change), WINDOWS_SAFETY_POLL);
                return Self { _stop: stop };
            }
        }

        // Polling backend (non-Windows, or Windows without a usable HWND).
        let _ = raw_window;
        let _ = WINDOWS_SAFETY_POLL;
        spawn_poller(Arc::clone(&stop), on_change, POLL_INTERVAL);
        Self { _stop: stop }
    }
}

/// Spawns a thread that fires `on_change` every `interval` until stopped.
fn spawn_poller(stop: Arc<AtomicBool>, on_change: OnChange, interval: Duration) {
    std::thread::Builder::new()
        .name("rkr-port-poll".into())
        .spawn(move || {
            while !stop.load(Ordering::SeqCst) {
                std::thread::sleep(interval);
                if stop.load(Ordering::SeqCst) {
                    break;
                }
                on_change();
            }
        })
        .expect("spawn port poller");
}

#[cfg(windows)]
mod windows_backend {
    //! The `WM_DEVICECHANGE` subclass. Isolated here so the FFI is in one place.

    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{Duration, Instant};

    use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows_sys::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
    use windows_sys::Win32::UI::WindowsAndMessaging::WM_DEVICECHANGE;

    use super::OnChange;

    /// `DBT_DEVICEARRIVAL` from `dbt.h`.
    const DBT_DEVICEARRIVAL: usize = 0x8000;
    /// `DBT_DEVICEREMOVECOMPLETE` from `dbt.h`.
    const DBT_DEVICEREMOVECOMPLETE: usize = 0x8004;
    /// A fixed subclass id for our handler on this window.
    const SUBCLASS_ID: usize = 0x5756_4b31; // "WVK1"
    /// Debounce window: a single plug can emit several messages in a burst.
    const DEBOUNCE: Duration = Duration::from_millis(250);

    /// Boxed state the subclass proc owns for the life of the window. Leaked deliberately:
    /// the window (and thus the subclass) lives until the process exits.
    struct State {
        on_change: OnChange,
        /// Last time we fired, as millis since an arbitrary epoch, for debouncing.
        last_fire: AtomicU64,
        epoch: Instant,
    }

    /// Installs the subclass on `hwnd` so `on_change` fires (debounced) on device changes.
    pub fn install(hwnd: isize, on_change: OnChange) {
        let state = Box::new(State {
            on_change,
            last_fire: AtomicU64::new(0),
            epoch: Instant::now(),
        });
        let state_ptr = Box::into_raw(state) as usize;
        // SAFETY: `hwnd` is the live Tauri window handle; `subclass_proc` has the correct
        // signature and `state_ptr` is a valid pointer we keep alive for the window's life.
        unsafe {
            SetWindowSubclass(hwnd as HWND, Some(subclass_proc), SUBCLASS_ID, state_ptr);
        }
    }

    /// The subclass window procedure: fire on device-change messages, otherwise pass through.
    ///
    /// # Safety
    ///
    /// Called by the OS with valid window-message arguments. `dwrefdata` is the `State`
    /// pointer we installed in [`install`].
    unsafe extern "system" fn subclass_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _subclass_id: usize,
        dwrefdata: usize,
    ) -> LRESULT {
        if msg == WM_DEVICECHANGE
            && (wparam == DBT_DEVICEARRIVAL || wparam == DBT_DEVICEREMOVECOMPLETE)
        {
            let state = &*(dwrefdata as *const State);
            if should_fire(state) {
                (state.on_change)();
            }
        }
        DefSubclassProc(hwnd, msg, wparam, lparam)
    }

    /// Debounce: only fire if `DEBOUNCE` has elapsed since the last fire.
    fn should_fire(state: &State) -> bool {
        let now_ms = state.epoch.elapsed().as_millis() as u64;
        let last = state.last_fire.load(Ordering::Relaxed);
        if now_ms.saturating_sub(last) < DEBOUNCE.as_millis() as u64 {
            return false;
        }
        state.last_fire.store(now_ms, Ordering::Relaxed);
        true
    }
}
