//! The W1VE Remote Keyer-Router desktop applications (Tauri v2).
//!
//! This crate is the operator-facing shell over the native [`rwk_core`] engine. It ships as
//! **two separate executables** that share this one library:
//!
//! * `rwk-app` — the **Client** (operating position): the paddle/keyer UI, driven by
//!   [`ClientController`]. Entry point [`run_client`].
//! * `rwk-station` — the **Station** (remote radio site): the replay/keying UI, driven by
//!   [`StationController`]. Entry point [`run_station`].
//!
//! They are distinct binaries, not one app in two modes, because the two sides run on
//! different machines and have no overlapping UI: the Client sends keyed edges, the Station
//! receives and replays them onto a radio. Sharing the crate keeps the Tauri bootstrap, the
//! event bridge, and the web asset bundle in one place; each binary selects its controller,
//! its command set, and its window.
//!
//! The split mirrors the C# apps: a headless controller does the work on background threads,
//! and the UI only sends commands (`invoke`) and renders events (`emit` on `rwk://event`).

mod app_paths;
mod controller;
mod station_controller;
mod station_winkeyer;

use std::sync::Arc;

use controller::{ClientController, ClientEvent};
use station_controller::{StationController, StationEvent};
use tauri::{Emitter, Manager, State};

// ═══════════════════════════════════════════════════════════════════════════════
//  Client application (`rwk-app`)
// ═══════════════════════════════════════════════════════════════════════════════

/// The Tauri-managed state for the Client app: the live Client controller.
struct ClientState {
    controller: ClientController,
}

/// Launches the Client (operator) application and blocks until the window closes.
pub fn run_client() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            // Bridge controller events to the webview: every ClientEvent becomes a
            // "rwk://event" the frontend listens for.
            let handle = app.handle().clone();
            let sink: controller::EventSink = Arc::new(move |event: ClientEvent| {
                let _ = handle.emit("rwk://event", &event);
            });
            let controller = ClientController::start(sink);
            app.manage(ClientState { controller });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            ping,
            set_speed,
            set_weight,
            set_mode,
            set_paddle_reverse,
            set_tone_frequency,
            set_tone_volume,
            audio_devices,
            serial_ports,
            select_keying_port,
            select_paddle_port,
            select_winkeyer_port,
            send_cw,
            test_tx,
            connect,
            disconnect,
            forward_rules_refresh,
            add_forward_rule,
            remove_forward_rule,
            set_forward_rule_enabled,
            set_flex_discovery,
        ])
        .run(tauri::generate_context!())
        .expect("error while running the RWK Client application");
}

/// Backwards-compatible alias: the original single-binary entry point launched the Client.
pub fn run() {
    run_client();
}

/// A trivial liveness command, used to confirm the UI↔Rust bridge is wired.
#[tauri::command]
fn ping() -> String {
    format!("rwk-app {} ready", env!("CARGO_PKG_VERSION"))
}

// ─── Client: keyer settings ──────────────────────────────────────────────────

#[tauri::command]
fn set_speed(state: State<'_, ClientState>, wpm: u32) {
    state.controller.set_speed(wpm);
}

#[tauri::command]
fn set_weight(state: State<'_, ClientState>, weight: u32) {
    state.controller.set_weight(weight);
}

#[tauri::command]
fn set_mode(state: State<'_, ClientState>, mode: String) {
    state.controller.set_mode(&mode);
}

#[tauri::command]
fn set_paddle_reverse(state: State<'_, ClientState>, reverse: bool) {
    state.controller.set_paddle_reverse(reverse);
}

// ─── Client: sidetone ─────────────────────────────────────────────────────────

#[tauri::command]
fn set_tone_frequency(state: State<'_, ClientState>, hz: i32) {
    state.controller.set_tone_frequency(hz);
}

#[tauri::command]
fn set_tone_volume(state: State<'_, ClientState>, percent: u32) {
    state.controller.set_tone_volume(percent);
}

#[tauri::command]
fn audio_devices() -> Vec<String> {
    ClientController::audio_devices()
}

// ─── Client: ports ────────────────────────────────────────────────────────────

#[tauri::command]
fn serial_ports() -> Vec<String> {
    ClientController::serial_ports()
}

#[tauri::command]
fn select_keying_port(state: State<'_, ClientState>, port: Option<String>) {
    state.controller.select_keying_port(port);
}

#[tauri::command]
fn select_paddle_port(state: State<'_, ClientState>, port: Option<String>) {
    state.controller.select_paddle_port(port);
}

#[tauri::command]
fn select_winkeyer_port(state: State<'_, ClientState>, port: Option<String>) {
    state.controller.select_winkeyer_port(port);
}

// ─── Client: keying ───────────────────────────────────────────────────────────

#[tauri::command]
fn send_cw(state: State<'_, ClientState>, text: String) {
    state.controller.send_cw(&text);
}

#[tauri::command]
fn test_tx(state: State<'_, ClientState>) {
    state.controller.test_tx();
}

// ─── Client: pairing ──────────────────────────────────────────────────────────

/// Pairs with a station. Returns immediately; the controller runs the rendezvous and
/// handshake on its own runtime and reports the outcome via `rwk://event` (Connection/Log).
#[tauri::command]
fn connect(state: State<'_, ClientState>, relay: String, code: String) {
    state.controller.connect(&relay, &code);
}

#[tauri::command]
fn disconnect(state: State<'_, ClientState>) {
    state.controller.disconnect();
}

// ─── Client: Ham Router (port forwarding) ──────────────────────────────────────

#[tauri::command]
fn forward_rules_refresh(state: State<'_, ClientState>) {
    state.controller.refresh_forward_rules();
}

#[tauri::command]
fn add_forward_rule(
    state: State<'_, ClientState>,
    name: String,
    protocol: String,
    client_port: u16,
    station_port: u16,
    station_target_address: String,
) {
    state.controller.add_forward_rule(
        &name,
        &protocol,
        client_port,
        station_port,
        &station_target_address,
    );
}

#[tauri::command]
fn remove_forward_rule(state: State<'_, ClientState>, id: String) {
    state.controller.remove_forward_rule(&id);
}

#[tauri::command]
fn set_forward_rule_enabled(state: State<'_, ClientState>, id: String, enabled: bool) {
    state.controller.set_forward_rule_enabled(&id, enabled);
}

#[tauri::command]
fn set_flex_discovery(state: State<'_, ClientState>, enabled: bool) {
    state.controller.set_flex_discovery(enabled);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Station application (`rwk-station`)
// ═══════════════════════════════════════════════════════════════════════════════

/// The Tauri-managed state for the Station app: the live Station controller.
struct StationState {
    controller: StationController,
}

/// Launches the Station (remote radio site) application and blocks until the window closes.
pub fn run_station() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let handle = app.handle().clone();
            let sink: station_controller::EventSink = Arc::new(move |event: StationEvent| {
                let _ = handle.emit("rwk://event", &event);
            });
            let controller = StationController::start(sink);
            app.manage(StationState { controller });

            // Retitle the window for the Station. Both executables share one web bundle and
            // one tauri.conf.json window; the front end probes the `station_app_role`
            // command, which exists only in this binary, and mounts the Station UI when it
            // succeeds (and the Client UI when it does not).
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.set_title(
                    "W1VE Remote Keyer-Router Station — Any Rig, Any Internet, Anytime",
                );
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            station_ping,
            station_app_role,
            station_serial_ports,
            station_select_keying_port,
            station_set_key_line,
            station_set_ptt_line,
            station_set_logger_port,
            station_set_logger_enabled,
            station_listen,
            station_disconnect,
        ])
        .run(tauri::generate_context!())
        .expect("error while running the RWK Station application");
}

/// Station liveness command.
#[tauri::command]
fn station_ping() -> String {
    format!("rwk-station {} ready", env!("CARGO_PKG_VERSION"))
}

/// Which role this executable is. The shared web bundle calls this once at startup and mounts
/// the Station UI when it returns `"station"`.
#[tauri::command]
fn station_app_role() -> String {
    "station".to_string()
}

#[tauri::command]
fn station_serial_ports() -> Vec<String> {
    StationController::serial_ports()
}

#[tauri::command]
fn station_select_keying_port(state: State<'_, StationState>, port: Option<String>) {
    state.controller.select_keying_port(port);
}

#[tauri::command]
fn station_set_key_line(state: State<'_, StationState>, line: String) {
    state.controller.set_key_line(&line);
}

#[tauri::command]
fn station_set_ptt_line(state: State<'_, StationState>, line: String) {
    state.controller.set_ptt_line(&line);
}

#[tauri::command]
fn station_set_logger_port(state: State<'_, StationState>, port: Option<String>) {
    state.controller.set_logger_port(port);
}

#[tauri::command]
fn station_set_logger_enabled(state: State<'_, StationState>, enabled: bool) {
    state.controller.set_logger_enabled(enabled);
}

/// Begins pairing: the Station waits for the Client to connect over the shared relay + code,
/// then replays keyed edges onto the radio. Returns immediately; the outcome arrives via
/// `rwk://event`.
#[tauri::command]
fn station_listen(state: State<'_, StationState>, relay: String, code: String) {
    state.controller.listen(&relay, &code);
}

#[tauri::command]
fn station_disconnect(state: State<'_, StationState>) {
    state.controller.disconnect();
}
