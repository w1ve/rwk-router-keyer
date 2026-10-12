//! The Station controller: the headless engine owner behind the Station UI.
//!
//! This mirrors [`crate::controller::ClientController`], but for the remote radio site. The
//! Client holds the paddle and *sends* keyed edges; the Station *receives* them over the
//! tunnel, replays them through the jitter buffer and fail-safe engine, and keys the radio's
//! control line in real time. The UI never touches the engine directly — it sends commands
//! here and renders the [`StationEvent`]s this controller emits.
//!
//! All timing-critical work (the replay thread, the watchdog, the WireGuard link) runs on
//! engine-owned threads; the controller holds only `Send + Sync` handles, so it can live in
//! Tauri's managed state and be driven from command handlers on any thread.
//!
//! Slice scope (the Station tab): select the serial KEY/PTT output, pair with the Client by
//! shared relay + code (the Station is the responder — it waits for the Client to initiate),
//! replay keyed edges onto the radio, surface KEY/PTT state and the ten fail-safes, and tear
//! down cleanly on unpair. Logger-WinKeyer input and port forwarding are later increments.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use rwk_core::config::{Config, Role};
use rwk_core::engine::bus::{CoreEvent, EventBus};
use rwk_core::engine::edge_station::{pump_edges, tunnel_replayer};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::replay::{spawn_driver, EdgeJitterProfile, KeyingOutput, SerialKeyingAdapter};
use rwk_core::engine::serial::{enumerate_ports, KeyingOutputConfig, SerialKeyingOutput};
use rwk_core::engine::session::Session;
use rwk_core::engine::tunnel;
use rwk_core::primitives::KeyingLine;
use rwk_core::timing::{Clock, MonotonicClock};

/// The inner UDP port the keying edge rides over the tunnel, matching the Client's
/// [`crate::controller`] `EDGE_PORT` so the two ends agree.
const EDGE_PORT: u16 = 5555;

/// An event the Station controller emits for the UI to render. Serialized to the webview
/// as-is on the shared `rwk://event` channel.
#[derive(Debug, Clone, serde::Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum StationEvent {
    /// The radio key line changed state (for the KEY indicator).
    KeyState { down: bool },
    /// The PTT line changed state (for the PTT indicator).
    Ptt { asserted: bool },
    /// The link to the client changed. `path` is "none" | "direct" | "relay".
    Connection { state: String, path: String },
    /// A fail-safe fired (F1..=F10). `code` is the number, `message` the description.
    FailSafe { code: u8, message: String },
    /// A human-readable log line for the Log tab.
    Log { level: String, message: String },
    /// The current Station settings, echoed so the UI can confirm them.
    Settings {
        keying_port: Option<String>,
        key_line: String,
        ptt_line: String,
    },
}

/// A sink the host (Tauri) registers to receive [`StationEvent`]s. Boxed so the controller
/// does not depend on the UI framework.
pub type EventSink = Arc<dyn Fn(StationEvent) + Send + Sync>;

/// Live Station settings, shared with the pairing task.
#[derive(Debug, Clone)]
struct Settings {
    keying_port: Option<String>,
    key_line: KeyingLine,
    /// `None` means PTT is not driven (QSK / hardware-handled).
    ptt_line: Option<KeyingLine>,
}

impl Default for Settings {
    fn default() -> Self {
        Self { keying_port: None, key_line: KeyingLine::Dtr, ptt_line: Some(KeyingLine::Rts) }
    }
}

/// Shared state the controller and its background work read and write. Everything here is
/// `Send + Sync`, which is what lets the controller be stored in Tauri's managed state.
struct Shared {
    settings: Mutex<Settings>,
    sink: EventSink,
}

impl Shared {
    fn emit(&self, event: StationEvent) {
        (self.sink)(event);
    }

    fn log(&self, level: &str, message: impl Into<String>) {
        self.emit(StationEvent::Log { level: level.into(), message: message.into() });
    }

    fn emit_settings(&self) {
        let s = self.settings.lock().unwrap();
        self.emit(StationEvent::Settings {
            keying_port: s.keying_port.clone(),
            key_line: line_name(s.key_line).into(),
            ptt_line: s.ptt_line.map(|l| line_name(l).to_string()).unwrap_or_else(|| "none".into()),
        });
    }
}

/// The Station controller. Construct with [`StationController::start`], then drive it with the
/// command methods; it emits [`StationEvent`]s to the sink for the UI to render.
pub struct StationController {
    shared: Arc<Shared>,
    runtime: tokio::runtime::Runtime,
    /// The live session + replay stack, held so the link and the keying stay up until
    /// unpaired or dropped. An `Arc<Mutex>` so a spawned pairing task can install it when the
    /// handshake completes.
    active: Arc<Mutex<Option<ActiveSession>>>,
}

/// The resources a running pairing owns, torn down together on unpair.
struct ActiveSession {
    session: Session,
    /// Pump task that forwards tunnel datagrams into the replay driver. Aborting it stops
    /// the inflow; the driver's own threads stop when its handle (held inside `_driver`) drops.
    pump: tokio::task::JoinHandle<rwk_core::Result<()>>,
    /// A flag the bus-forwarding task watches so it exits when the session ends.
    stop_bus: Arc<AtomicBool>,
}

impl StationController {
    /// Starts the controller and emits the initial settings. Unlike the Client there is no
    /// sidetone to bring up: the Station keys a radio, it does not generate practice audio.
    pub fn start(sink: EventSink) -> Self {
        let shared = Arc::new(Shared { settings: Mutex::new(Settings::default()), sink });

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("tokio runtime");

        let controller =
            Self { shared, runtime, active: Arc::new(Mutex::new(None)) };
        controller.shared.emit_settings();
        controller
    }

    // ─── Ports / keying configuration ──────────────────────────────────────────

    /// Lists available serial ports for the keying-output picker.
    #[must_use]
    pub fn serial_ports() -> Vec<String> {
        enumerate_ports()
            .map(|ports| ports.into_iter().map(|p| p.port_name).collect())
            .unwrap_or_default()
    }

    /// Selects the serial port that keys the radio, or `None` to clear it. The port is not
    /// opened here; it is opened when pairing starts, so the replay driver owns it for the
    /// session's lifetime. Changing the port while paired takes effect on the next pairing.
    pub fn select_keying_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().keying_port = port.clone();
        match port {
            Some(name) => self.shared.log("info", format!("Keying output set to {name}.")),
            None => self.shared.log("info", "Keying output cleared."),
        }
        self.shared.emit_settings();
    }

    /// Sets which control line keys the radio ("dtr" or "rts").
    pub fn set_key_line(&self, line: &str) {
        let parsed = parse_line(line).unwrap_or(KeyingLine::Dtr);
        self.shared.settings.lock().unwrap().key_line = parsed;
        self.shared.emit_settings();
    }

    /// Sets which control line asserts PTT ("dtr", "rts", or "none").
    pub fn set_ptt_line(&self, line: &str) {
        self.shared.settings.lock().unwrap().ptt_line = parse_line(line);
        self.shared.emit_settings();
    }

    // ─── Pairing ──────────────────────────────────────────────────────────────

    /// Pairs with the Client using a shared pairing code over the configured relay, then
    /// starts replaying keyed edges onto the radio. The Station is the responder: it brings
    /// its side of the link up and waits for the Client to initiate the handshake.
    ///
    /// Non-blocking: the rendezvous and handshake run on the controller's runtime, and the
    /// outcome is reported through [`StationEvent::Connection`] / [`StationEvent::Log`].
    pub fn listen(&self, relay: &str, code: &str) {
        let relay_addr: std::net::SocketAddr = match relay.parse() {
            Ok(a) => a,
            Err(e) => {
                self.shared.log("warn", format!("Invalid relay address '{relay}': {e}"));
                self.shared.emit(StationEvent::Connection {
                    state: "disconnected".into(),
                    path: "none".into(),
                });
                return;
            }
        };

        let keying_port = {
            let s = self.shared.settings.lock().unwrap();
            s.keying_port.clone()
        };
        if keying_port.is_none() {
            self.shared.log("warn", "Select a keying output port before pairing.");
            self.shared
                .emit(StationEvent::Connection { state: "disconnected".into(), path: "none".into() });
            return;
        }

        // Only one pairing at a time.
        self.disconnect();

        let code = code.to_string();
        let shared = Arc::clone(&self.shared);
        let active_slot = Arc::clone(&self.active);

        shared.emit(StationEvent::Connection { state: "connecting".into(), path: "none".into() });
        shared.log("info", "Waiting for the client to pair…");

        self.runtime.spawn(async move {
            // Build a station config with a fresh identity, the relay, and the pairing code.
            let mut config = match Config::new(Role::Station) {
                Ok(c) => c,
                Err(e) => return fail(&shared, format!("config error: {e}")),
            };
            config.relay = Some(rwk_core::config::RelayConfig { address: relay_addr });
            config.pairing_code = Some(code);

            let connected = match tunnel::connect(&config).await {
                Ok(c) => c,
                Err(e) => return fail(&shared, format!("link failed: {e}")),
            };
            let path = format!("{:?}", connected.path).to_lowercase();

            let session = match Session::start(connected, &config) {
                Ok(s) => s,
                Err(e) => return fail(&shared, format!("session failed: {e}")),
            };
            let net = session.handle();

            // Open the keying edge over the tunnel, pointed at the client's inner address.
            let conn = match net.open_udp(EDGE_PORT).await {
                Ok(c) => c,
                Err(e) => return fail(&shared, format!("edge open failed: {e}")),
            };
            let transport = Arc::new(EdgeTransport::over_tunnel(conn));
            let peer = std::net::SocketAddr::new(
                std::net::IpAddr::V4(Role::Station.peer_inner_address()),
                EDGE_PORT,
            );
            transport.set_peer(peer).await;

            // Open the serial keying output the replay driver will drive.
            let (key_line, ptt_line) = {
                let s = shared.settings.lock().unwrap();
                (s.key_line, s.ptt_line)
            };
            let port_name = keying_port.expect("checked present before listen");
            let cfg = KeyingOutputConfig {
                port_name: port_name.clone(),
                key_line,
                ptt_line: ptt_line.unwrap_or(KeyingLine::None),
                ..Default::default()
            };
            let serial = match SerialKeyingOutput::open(cfg) {
                Ok(out) => out,
                Err(e) => return fail(&shared, format!("keying port {port_name} failed: {e}")),
            };
            shared.log("info", format!("Keying output open on {port_name}."));

            // Build the replay stack: a replayer tuned for the tunnel (DERP band), the
            // dedicated replay + watchdog threads, and a keying output that both drives the
            // serial line and reports KEY/PTT state to the UI.
            let clock: Arc<dyn Clock> = Arc::new(MonotonicClock);
            let mut replayer =
                tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
            replayer.begin_session(1);

            let bus = EventBus::new();
            let output = UiKeyingOutput::new(SerialKeyingAdapter::new(serial), Arc::clone(&shared));
            let path_health = rwk_core::engine::network::PathHealth::new();
            let driver = spawn_driver(
                replayer,
                Box::new(output),
                Arc::clone(&clock),
                Some(bus.clone()),
                Some(path_health),
            );

            // Forward fail-safe events from the bus to the UI until the session stops.
            let stop_bus = Arc::new(AtomicBool::new(false));
            spawn_bus_forwarder(bus, Arc::clone(&shared), Arc::clone(&stop_bus));

            // Pump received edge datagrams into the replay driver.
            let pump = tokio::spawn(pump_edges(Arc::clone(&transport), driver));

            *active_slot.lock().unwrap() =
                Some(ActiveSession { session, pump, stop_bus });

            shared
                .emit(StationEvent::Connection { state: "connected".into(), path: path.clone() });
            shared.log("info", format!("Paired with the client over the {path} path."));
        });
    }

    /// Tears the session, replay stack and keying output down, returning to the unpaired
    /// state. The replay driver's handle (held by the pump's `pump_edges` call) is dropped
    /// here, which stops the replay and watchdog threads and forces the key up (F8).
    pub fn disconnect(&self) {
        let active = self.active.lock().unwrap().take();
        if let Some(active) = active {
            active.stop_bus.store(true, Ordering::SeqCst);
            active.pump.abort();
            let mut session = active.session;
            self.runtime.block_on(async { session.shutdown().await });
            self.shared.emit(StationEvent::KeyState { down: false });
            self.shared.emit(StationEvent::Ptt { asserted: false });
            self.shared
                .emit(StationEvent::Connection { state: "disconnected".into(), path: "none".into() });
            self.shared.log("info", "Unpaired.");
        }
    }
}

/// Reports a pairing failure through the event sink.
fn fail(shared: &Arc<Shared>, message: String) {
    shared.log("warn", message);
    shared.emit(StationEvent::Connection { state: "disconnected".into(), path: "none".into() });
}

/// Forwards fail-safe events published on the replay driver's bus to the UI sink until
/// `stop` is set. Runs on a blocking thread because the bus receiver is synchronous.
fn spawn_bus_forwarder(bus: EventBus, shared: Arc<Shared>, stop: Arc<AtomicBool>) {
    std::thread::Builder::new()
        .name("rwk-station-bus".into())
        .spawn(move || {
            let mut rx = bus.subscribe();
            while !stop.load(Ordering::SeqCst) {
                match rx.try_recv() {
                    Ok(CoreEvent::FailSafe { code, message }) => {
                        shared.emit(StationEvent::FailSafe { code, message: message.clone() });
                        shared.log("warn", format!("Fail-safe F{code}: {message}"));
                    }
                    Ok(_) => {}
                    Err(_) => std::thread::sleep(std::time::Duration::from_millis(20)),
                }
            }
        })
        .expect("spawn station bus forwarder");
}

impl Drop for StationController {
    fn drop(&mut self) {
        self.disconnect();
    }
}

/// A [`KeyingOutput`] that drives the real serial line *and* reports KEY/PTT transitions to
/// the UI, so the Station's indicators follow the keying in real time. It wraps the engine's
/// [`SerialKeyingAdapter`] (which owns the port and latches faults for F6) and only emits a
/// UI event on an actual state change, matching the C# Station's sticky LED behaviour.
struct UiKeyingOutput {
    inner: SerialKeyingAdapter,
    shared: Arc<Shared>,
    last_key: Option<bool>,
    last_ptt: Option<bool>,
}

impl UiKeyingOutput {
    fn new(inner: SerialKeyingAdapter, shared: Arc<Shared>) -> Self {
        Self { inner, shared, last_key: None, last_ptt: None }
    }
}

impl KeyingOutput for UiKeyingOutput {
    fn set_key(&mut self, key_down: bool) {
        self.inner.set_key(key_down);
        if self.last_key != Some(key_down) {
            self.last_key = Some(key_down);
            self.shared.emit(StationEvent::KeyState { down: key_down });
        }
    }

    fn set_ptt(&mut self, asserted: bool) {
        self.inner.set_ptt(asserted);
        if self.last_ptt != Some(asserted) {
            self.last_ptt = Some(asserted);
            self.shared.emit(StationEvent::Ptt { asserted });
        }
    }

    fn take_fault(&mut self) -> Option<String> {
        self.inner.take_fault()
    }
}

/// Parses a UI line name into a [`KeyingLine`]; "none"/empty maps to [`None`].
fn parse_line(name: &str) -> Option<KeyingLine> {
    match name.to_ascii_lowercase().as_str() {
        "dtr" => Some(KeyingLine::Dtr),
        "rts" => Some(KeyingLine::Rts),
        _ => None,
    }
}

/// The UI name for a [`KeyingLine`].
fn line_name(line: KeyingLine) -> &'static str {
    match line {
        KeyingLine::Dtr => "dtr",
        KeyingLine::Rts => "rts",
        KeyingLine::None => "none",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    fn recording_sink() -> (EventSink, Arc<Mutex<Vec<StationEvent>>>) {
        let events = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&events);
        let sink: EventSink = Arc::new(move |e| captured.lock().unwrap().push(e));
        (sink, events)
    }

    fn wait_for(
        events: &Arc<Mutex<Vec<StationEvent>>>,
        timeout: Duration,
        pred: impl Fn(&[StationEvent]) -> bool,
    ) -> bool {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            if pred(&events.lock().unwrap()) {
                return true;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        pred(&events.lock().unwrap())
    }

    #[test]
    fn start_emits_initial_settings() {
        let (sink, events) = recording_sink();
        let _controller = StationController::start(sink);
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| matches!(e, StationEvent::Settings { key_line, .. } if key_line == "dtr"))
        });
        assert!(ok, "start must emit the initial station settings");
    }

    #[test]
    fn selecting_a_keying_port_updates_and_echoes_settings() {
        let (sink, events) = recording_sink();
        let controller = StationController::start(sink);
        controller.select_keying_port(Some("COM7".into()));
        assert_eq!(controller.shared.settings.lock().unwrap().keying_port.as_deref(), Some("COM7"));
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| {
                matches!(e, StationEvent::Settings { keying_port, .. } if keying_port.as_deref() == Some("COM7"))
            })
        });
        assert!(ok, "selecting a port must echo updated settings");
    }

    #[test]
    fn line_setters_parse_and_store() {
        let (sink, _events) = recording_sink();
        let controller = StationController::start(sink);
        controller.set_key_line("rts");
        controller.set_ptt_line("none");
        let s = controller.shared.settings.lock().unwrap();
        assert_eq!(s.key_line, KeyingLine::Rts);
        assert_eq!(s.ptt_line, None);
    }

    #[test]
    fn listen_without_a_keying_port_reports_disconnected() {
        let (sink, events) = recording_sink();
        let controller = StationController::start(sink);
        // No keying port selected: pairing must refuse and stay disconnected.
        controller.listen("127.0.0.1:9999", "code");
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| matches!(e, StationEvent::Connection { state, .. } if state == "disconnected"))
                && evs.iter().any(|e| matches!(e, StationEvent::Log { .. }))
        });
        assert!(ok, "pairing without a keying port must report disconnected + a log line");
    }

    #[test]
    fn listen_rejects_a_bad_relay_address_without_blocking() {
        let (sink, events) = recording_sink();
        let controller = StationController::start(sink);
        controller.select_keying_port(Some("COM1".into()));
        controller.listen("not-an-address", "code");
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| matches!(e, StationEvent::Connection { state, .. } if state == "disconnected"))
        });
        assert!(ok, "a bad relay address must report disconnected, never hang");
    }

    #[test]
    fn line_names_round_trip() {
        assert_eq!(line_name(parse_line("dtr").unwrap()), "dtr");
        assert_eq!(line_name(parse_line("rts").unwrap()), "rts");
        assert_eq!(parse_line("none"), None);
        assert_eq!(parse_line("nonsense"), None);
    }
}
