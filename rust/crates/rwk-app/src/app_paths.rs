//! Where each app persists its configuration.
//!
//! Both executables keep a `config.json` so the WireGuard identity (and the relay, pairing
//! code, and the Station's port selections) survive a restart — without this, every launch
//! generated a throwaway key and the operator re-entered everything each time.
//!
//! The file lives in a per-user application-data directory, one file per role so the Client
//! and Station can run on the same machine during testing without clobbering each other:
//!
//! ```text
//! Windows: %APPDATA%\W1VE Remote Keyer-Router\client.json  (or station.json)
//! other:   ~/.config/w1ve-remote-keyer-router/client.json  (or station.json)
//! ```
//!
//! Resolution is dependency-free (no `dirs`/`directories` crate): on Windows it reads
//! `%APPDATA%`, elsewhere `$XDG_CONFIG_HOME` or `~/.config`. If the environment is bare, it
//! falls back to the current directory so the app still starts (with a non-persistent-looking
//! path) rather than failing to launch.

use std::path::PathBuf;

use rwk_core::config::Role;

/// The vendor/product folder name under the per-user config root.
const APP_DIR_WINDOWS: &str = "W1VE Remote Keyer-Router";
const APP_DIR_UNIX: &str = "w1ve-remote-keyer-router";

/// Returns the config-file path for `role`, creating the parent directory if needed.
///
/// The returned path may not exist yet; the caller loads it if present and otherwise creates
/// it. Creating the directory here (not the file) means a first run has somewhere to save.
#[must_use]
pub fn config_path(role: Role) -> PathBuf {
    let dir = config_dir();
    // Best effort: if the directory cannot be created the save will surface the error later.
    let _ = std::fs::create_dir_all(&dir);
    let file = match role {
        Role::Client => "client.json",
        Role::Station => "station.json",
    };
    dir.join(file)
}

/// The per-user directory RWK stores its config in.
fn config_dir() -> PathBuf {
    #[cfg(windows)]
    {
        if let Some(appdata) = std::env::var_os("APPDATA") {
            return PathBuf::from(appdata).join(APP_DIR_WINDOWS);
        }
    }
    #[cfg(not(windows))]
    {
        if let Some(xdg) = std::env::var_os("XDG_CONFIG_HOME") {
            return PathBuf::from(xdg).join(APP_DIR_UNIX);
        }
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home).join(".config").join(APP_DIR_UNIX);
        }
    }
    // Last resort: a folder in the current directory, so the app still runs.
    PathBuf::from(APP_DIR_UNIX)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn client_and_station_resolve_distinct_files() {
        let client = config_path(Role::Client);
        let station = config_path(Role::Station);
        assert_ne!(client, station);
        assert!(client.ends_with("client.json"));
        assert!(station.ends_with("station.json"));
    }

    #[test]
    fn the_path_lives_under_a_named_app_directory() {
        let path = config_path(Role::Client);
        let s = path.to_string_lossy();
        assert!(
            s.contains(APP_DIR_WINDOWS) || s.contains(APP_DIR_UNIX),
            "config path should be under the app directory, got {s}"
        );
    }
}
