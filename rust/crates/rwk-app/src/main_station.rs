// Prevent a second console window on Windows release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Entry point for the Station executable (`w1ve-rkr-station`), the remote radio site app.
//! The operating-position app is the separate `w1ve-rkr` binary; both share the `rwk_app`
//! library and the web bundle but run their own controller, command set, and window.

fn main() {
    rwk_app::run_station();
}
