//! The W1VE Remote Keyer-Router desktop applications (Tauri v2).
//!
//! This crate is the operator-facing shell over the native [`rwk_core`] engine. It ships as
//! **two separate executables** that share this one library:
//!
//! * `w1ve-rkr` — the **Client** (operating position): the paddle/keyer UI, driven by
//!   [`ClientController`]. Entry point [`run_client`].
//! * `w1ve-rkr-station` — the **Station** (remote radio site): the replay/keying UI, driven
//!   by [`StationController`]. Entry point [`run_station`].
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
mod device_watch;
mod station_controller;
mod station_winkeyer;

use std::sync::Arc;

use controller::{ClientController, ClientEvent};
use device_watch::DeviceChangeWatcher;
use station_controller::{StationController, StationEvent};
use tauri::{Emitter, Manager, State};

/// Extracts the native window handle as an `isize` for the serial-PnP device watcher.
///
/// Returns `None` off Windows (the watcher then polls) or if the handle is unavailable.
#[allow(unused_variables)]
fn window_handle_isize(window: &tauri::WebviewWindow) -> Option<isize> {
    #[cfg(windows)]
    {
        return window.hwnd().ok().map(|h| h.0 as isize);
    }
    #[cfg(not(windows))]
    {
        None
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Client application (`w1ve-rkr`)
// ═══════════════════════════════════════════════════════════════════════════════

/// The Tauri-managed state for the Client app: the live Client controller plus the serial
/// PnP watcher (kept here so it lives for the whole app and is not dropped).
struct ClientState {
    controller: ClientController,
    _watcher: DeviceChangeWatcher,
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

            // Install the serial PnP watcher. On Windows it subclasses this window for
            // WM_DEVICECHANGE; elsewhere it polls. Either way it fires the controller's port
            // reconciliation, which pushes a fresh Ports event and drops vanished selections.
            let raw_window =
                app.get_webview_window("main").and_then(|w| window_handle_isize(&w));
            let app_handle = app.handle().clone();
            let on_change: device_watch::OnChange = Arc::new(move || {
                if let Some(state) = app_handle.try_state::<ClientState>() {
                    state.controller.reconcile_ports();
                }
            });
            let watcher = DeviceChangeWatcher::start(raw_window, on_change);
            app.manage(ClientState { controller, _watcher: watcher });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            ping,
            request_state,
            set_speed,
            set_weight,
            set_mode,
            set_paddle_reverse,
            set_transport,
            set_callsign,
            set_keyer_id,
            lan_advertise,
            set_tone_frequency,
            set_tone_volume,
            select_audio_device,
            audio_devices,
            refresh_audio_devices,
            serial_ports,
            select_keying_port,
            select_paddle_port,
            select_winkeyer_port,
            send_cw,
            test_tx,
            abort_send,
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
    format!("w1ve-rkr {} ready", env!("CARGO_PKG_VERSION"))
}

/// Re-emits settings, forward rules, flex state, ports, and audio devices. The UI calls this
/// right after subscribing to the event stream to beat the startup race.
#[tauri::command]
fn request_state(state: State<'_, ClientState>) {
    state.controller.request_state();
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

#[tauri::command]
fn set_transport(state: State<'_, ClientState>, transport: String) {
    state.controller.set_transport(&transport);
}

#[tauri::command]
fn set_callsign(state: State<'_, ClientState>, callsign: String) {
    state.controller.set_callsign(&callsign);
}

#[tauri::command]
fn set_keyer_id(state: State<'_, ClientState>, keyer_id: String) {
    state.controller.set_keyer_id(&keyer_id);
}

#[tauri::command]
fn lan_advertise(state: State<'_, ClientState>, on: bool) {
    state.controller.lan_advertise(on);
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
fn select_audio_device(state: State<'_, ClientState>, device: Option<String>) {
    state.controller.select_audio_device(device);
}

#[tauri::command]
fn audio_devices() -> Vec<controller::AudioDeviceView> {
    ClientController::audio_devices()
}

#[tauri::command]
fn refresh_audio_devices(state: State<'_, ClientState>) {
    state.controller.refresh_audio_devices();
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

#[tauri::command]
fn abort_send(state: State<'_, ClientState>) {
    state.controller.abort_send();
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
//  Station application (`w1ve-rkr-station`)
// ═══════════════════════════════════════════════════════════════════════════════

/// The Tauri-managed state for the Station app: the live Station controller plus the serial
/// PnP watcher (kept here so it lives for the whole app and is not dropped).
struct StationState {
    controller: StationController,
    _watcher: DeviceChangeWatcher,
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

            // Retitle the window for the Station, and capture its handle for the PnP watcher.
            // Both executables share one web bundle and one tauri.conf.json window; the front
            // end probes the `station_app_role` command, which exists only in this binary, and
            // mounts the Station UI when it succeeds (and the Client UI when it does not).
            let raw_window = if let Some(window) = app.get_webview_window("main") {
                let _ = window.set_title(
                    "W1VE Remote Keyer-Router Station — Any Rig, Any Internet, Anytime",
                );
                window_handle_isize(&window)
            } else {
                None
            };

            // Install the serial PnP watcher (Windows: WM_DEVICECHANGE on this window;
            // elsewhere: polling), which fires the Station's port reconciliation.
            let app_handle = app.handle().clone();
            let on_change: device_watch::OnChange = Arc::new(move || {
                if let Some(state) = app_handle.try_state::<StationState>() {
                    state.controller.reconcile_ports();
                }
            });
            let watcher = DeviceChangeWatcher::start(raw_window, on_change);
            app.manage(StationState { controller, _watcher: watcher });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            station_ping,
            station_app_role,
            station_request_state,
            station_serial_ports,
            station_select_keying_port,
            station_set_key_line,
            station_set_ptt_line,
            station_set_logger_port,
            station_set_logger_enabled,
            station_set_transport,
            station_lan_browse,
            station_connect_lan,
            station_listen,
            station_disconnect,
        ])
        .run(tauri::generate_context!())
        .expect("error while running the RWK Station application");
}

/// Station liveness command.
#[tauri::command]
fn station_ping() -> String {
    format!("w1ve-rkr-station {} ready", env!("CARGO_PKG_VERSION"))
}

/// Which role this executable is. The shared web bundle calls this once at startup and mounts
/// the Station UI when it returns `"station"`.
#[tauri::command]
fn station_app_role() -> String {
    "station".to_string()
}

/// Re-emits the Station settings + ports once the UI has subscribed (beats the startup race).
#[tauri::command]
fn station_request_state(state: State<'_, StationState>) {
    state.controller.request_state();
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

#[tauri::command]
fn station_set_transport(state: State<'_, StationState>, transport: String) {
    state.controller.set_transport(&transport);
}

#[tauri::command]
fn station_lan_browse(state: State<'_, StationState>) {
    state.controller.lan_browse();
}

#[tauri::command]
fn station_connect_lan(state: State<'_, StationState>, key: String, endpoint: String) {
    state.controller.connect_lan(&key, &endpoint);
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
