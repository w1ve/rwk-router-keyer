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
//! ## What this slice covers
//!
//! * **Keying output**: pick the serial port + KEY/PTT control lines that key the radio.
//! * **Pairing**: connect to the Client by shared relay + code (the Station is the responder
//!   — it brings its side up and waits for the Client to initiate), using a **persisted**
//!   WireGuard identity so the key is stable across restarts.
//! * **Logger input**: an optional second serial port on which a local logging program speaks
//!   WinKeyer2; its CW keys the same radio through a [`KeyingArbiter`] with priority over the
//!   remote replay.
//! * **Persistence**: identity, relay, pairing code and the port selections are saved to a
//!   per-user `config.json` so a restart resumes where the operator left off.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use rwk_core::config::{base64_encode, Config, Role};
use rwk_core::engine::bus::{CoreEvent, EventBus};
use rwk_core::engine::control::{ControlLink, ControlMessage};
use rwk_core::engine::edge_station::{pump_edges, tunnel_replayer};
use rwk_core::engine::flex_discovery::DiscoveryListener;
use rwk_core::engine::keying_arbiter::{KeyingArbiter, LineObserver, LineUpdate};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::replay::{spawn_driver, EdgeJitterProfile, SerialKeyingAdapter};
use rwk_core::engine::serial::{enumerate_ports, KeyingOutputConfig, SerialKeyingOutput};
use rwk_core::engine::session::Session;
use rwk_core::engine::tunnel;
use rwk_core::primitives::KeyingLine;
use rwk_core::timing::{Clock, MonotonicClock};

use crate::app_paths;
use crate::station_winkeyer::{HostReport, HostReporter, StationWinKeyerHost};

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
    /// The local logger is actively sending CW (`true`) or idle (`false`).
    LoggerSending { active: bool },
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
        logger_port: Option<String>,
        logger_enabled: bool,
        relay: Option<String>,
        pairing_code: Option<String>,
    },
}

/// A sink the host (Tauri) registers to receive [`StationEvent`]s. Boxed so the controller
/// does not depend on the UI framework.
pub type EventSink = Arc<dyn Fn(StationEvent) + Send + Sync>;

/// Live Station settings, shared with the pairing task and persisted to `config.json`.
#[derive(Debug, Clone)]
struct Settings {
    keying_port: Option<String>,
    key_line: KeyingLine,
    /// `None` means PTT is not driven (QSK / hardware-handled).
    ptt_line: Option<KeyingLine>,
    logger_port: Option<String>,
    logger_enabled: bool,
    relay: Option<String>,
    pairing_code: Option<String>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            keying_port: None,
            key_line: KeyingLine::Dtr,
            ptt_line: Some(KeyingLine::Rts),
            logger_port: None,
            logger_enabled: false,
            relay: None,
            pairing_code: None,
        }
    }
}

/// Shared state the controller and its background work read and write. Everything here is
/// `Send + Sync`, which is what lets the controller be stored in Tauri's managed state.
struct Shared {
    settings: Mutex<Settings>,
    /// The persisted config (identity, relay, code, station settings). Held so pairing reuses
    /// the stable identity, and so UI changes can be written back to disk.
    config: Mutex<Config>,
    /// Where the config persists; `None` disables saving (used only in tests).
    config_path: Option<std::path::PathBuf>,
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
            logger_port: s.logger_port.clone(),
            logger_enabled: s.logger_enabled,
            relay: s.relay.clone(),
            pairing_code: s.pairing_code.clone(),
        });
    }

    /// Copies the live UI settings into the persisted config and writes it to disk. A write
    /// failure is logged, not fatal — the app still runs, it just will not remember.
    fn persist(&self) {
        let Some(path) = &self.config_path else { return };
        {
            let s = self.settings.lock().unwrap();
            let mut config = self.config.lock().unwrap();
            config.relay = s
                .relay
                .as_deref()
                .and_then(|r| r.parse().ok())
                .map(|address| rwk_core::config::RelayConfig { address });
            config.pairing_code = s.pairing_code.clone();
            config.station.keying_port = s.keying_port.clone();
            config.station.key_line = Some(line_name(s.key_line).to_string());
            config.station.ptt_line =
                Some(s.ptt_line.map(|l| line_name(l).to_string()).unwrap_or_else(|| "none".into()));
            config.station.logger_port = s.logger_port.clone();
            config.station.logger_enabled = s.logger_enabled;
        }
        let config = self.config.lock().unwrap();
        if let Err(e) = config.save(path) {
            self.log("warn", format!("Could not save config: {e}"));
        }
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
    /// The live session, shared with the control task so pushed forward rules can be applied
    /// over the same tunnel without tearing the link down.
    session: Arc<tokio::sync::Mutex<Session>>,
    /// Pump task that forwards tunnel datagrams into the replay driver. Aborting it stops
    /// the inflow; the driver's own threads stop when its handle drops.
    pump: tokio::task::JoinHandle<rwk_core::Result<()>>,
    /// The control-channel task (receives forward-rule pushes + sends discovery announces).
    control: tokio::task::JoinHandle<()>,
    /// A flag the bus-forwarding and control tasks watch so they exit when the session ends.
    stop_bus: Arc<AtomicBool>,
    /// The logger-WinKeyer host, if the operator enabled it for this session. Dropped (and
    /// its port released) when the session ends.
    logger: Option<StationWinKeyerHost>,
    /// Held so the arbiter (and the serial port it owns) lives for the session.
    _arbiter: Arc<KeyingArbiter>,
}

impl StationController {
    /// Starts the controller: loads (or creates) the persisted config, applies the saved
    /// settings, and emits them. Unlike the Client there is no sidetone to bring up — the
    /// Station keys a radio, it does not generate practice audio.
    pub fn start(sink: EventSink) -> Self {
        let path = app_paths::config_path(Role::Station);
        Self::start_with_config(sink, Some(path))
    }

    /// Starts the controller against an explicit config path (or `None` to disable
    /// persistence, for tests). Loads the config if the file exists, otherwise generates a
    /// fresh identity and saves it so the next run reuses the same key.
    pub fn start_with_config(sink: EventSink, config_path: Option<std::path::PathBuf>) -> Self {
        let (config, settings) = load_or_create(&config_path);

        let shared = Arc::new(Shared {
            settings: Mutex::new(settings),
            config: Mutex::new(config),
            config_path,
            sink,
        });

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("tokio runtime");

        let controller = Self { shared, runtime, active: Arc::new(Mutex::new(None)) };
        controller.shared.emit_settings();
        controller
    }

    // ─── Ports / keying configuration ──────────────────────────────────────────

    /// Lists available serial ports for the keying-output and logger pickers.
    #[must_use]
    pub fn serial_ports() -> Vec<String> {
        enumerate_ports()
            .map(|ports| ports.into_iter().map(|p| p.port_name).collect())
            .unwrap_or_default()
    }

    /// Selects the serial port that keys the radio, or `None` to clear it. The port is opened
    /// when pairing starts, so the replay driver owns it for the session; changing it while
    /// paired takes effect on the next pairing.
    pub fn select_keying_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().keying_port = port.clone();
        match port {
            Some(name) => self.shared.log("info", format!("Keying output set to {name}.")),
            None => self.shared.log("info", "Keying output cleared."),
        }
        self.shared.persist();
        self.shared.emit_settings();
    }

    /// Sets which control line keys the radio ("dtr" or "rts").
    pub fn set_key_line(&self, line: &str) {
        let parsed = parse_line(line).unwrap_or(KeyingLine::Dtr);
        self.shared.settings.lock().unwrap().key_line = parsed;
        self.shared.persist();
        self.shared.emit_settings();
    }

    /// Sets which control line asserts PTT ("dtr", "rts", or "none").
    pub fn set_ptt_line(&self, line: &str) {
        self.shared.settings.lock().unwrap().ptt_line = parse_line(line);
        self.shared.persist();
        self.shared.emit_settings();
    }

    /// Selects the serial port the local logger sends WinKeyer protocol on, or `None`.
    pub fn set_logger_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().logger_port = port.clone();
        match port {
            Some(name) => self.shared.log("info", format!("Logger input port set to {name}.")),
            None => self.shared.log("info", "Logger input port cleared."),
        }
        self.shared.persist();
        self.shared.emit_settings();
    }

    /// Enables or disables the logger input. Takes effect on the next pairing (the logger
    /// host starts alongside the replay driver so both share the one keying output).
    pub fn set_logger_enabled(&self, enabled: bool) {
        self.shared.settings.lock().unwrap().logger_enabled = enabled;
        self.shared.log("info", if enabled { "Logger input enabled." } else { "Logger input disabled." });
        self.shared.persist();
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

        // Remember the relay + code the operator used, so a restart can resume.
        {
            let mut s = self.shared.settings.lock().unwrap();
            s.relay = Some(relay.to_string());
            s.pairing_code = Some(code.to_string());
        }
        self.shared.persist();

        let (keying_port, logger_port, logger_enabled) = {
            let s = self.shared.settings.lock().unwrap();
            (s.keying_port.clone(), s.logger_port.clone(), s.logger_enabled)
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
            // Clone the PERSISTED config so the WireGuard identity is stable, then layer the
            // live relay + code on top for this pairing.
            let mut config = shared.config.lock().unwrap().clone();
            config.role = Role::Station;
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

            // Open the serial keying output.
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

            // Build the arbiter in front of the single keying output: the replay driver keys
            // the remote source, and (optionally) the logger keys the local source with
            // priority. A line observer reports the effective KEY/PTT to the UI.
            let arbiter = Arc::new(KeyingArbiter::with_observer(
                SerialKeyingAdapter::new(serial),
                Some(make_line_observer(Arc::clone(&shared))),
            ));

            // The replay stack: a replayer tuned for the tunnel (DERP band) and the dedicated
            // replay + watchdog threads, keying the arbiter's remote source.
            let clock: Arc<dyn Clock> = Arc::new(MonotonicClock);
            let mut replayer =
                tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
            replayer.begin_session(1);

            let bus = EventBus::new();
            let path_health = rwk_core::engine::network::PathHealth::new();
            let driver = spawn_driver(
                replayer,
                Box::new(arbiter.remote_output()),
                Arc::clone(&clock),
                Some(bus.clone()),
                Some(path_health),
            );

            // Optionally start the logger-WinKeyer host on the local source.
            let logger = if logger_enabled {
                match logger_port {
                    Some(lp) => {
                        let reporter: HostReporter = make_host_reporter(Arc::clone(&shared));
                        match StationWinKeyerHost::open(&lp, arbiter.local_handle(), reporter) {
                            Ok(host) => {
                                shared.log(
                                    "info",
                                    format!("Logger input active on {}.", host.port_name()),
                                );
                                Some(host)
                            }
                            Err(e) => {
                                shared.log("warn", format!("Logger input unavailable: {e}"));
                                None
                            }
                        }
                    }
                    None => {
                        shared.log("warn", "Logger input enabled but no port selected.");
                        None
                    }
                }
            } else {
                None
            };

            // Forward fail-safe events from the bus to the UI until the session stops.
            let stop_bus = Arc::new(AtomicBool::new(false));
            spawn_bus_forwarder(bus, Arc::clone(&shared), Arc::clone(&stop_bus));

            // Pump received edge datagrams into the replay driver.
            let pump = tokio::spawn(pump_edges(Arc::clone(&transport), driver));

            // Share the session so the control task can apply pushed forward rules live.
            let session = Arc::new(tokio::sync::Mutex::new(session));
            let control = spawn_control_task(
                Arc::clone(&shared),
                net.clone(),
                Arc::clone(&session),
                Arc::clone(&stop_bus),
            );

            *active_slot.lock().unwrap() = Some(ActiveSession {
                session,
                pump,
                control,
                stop_bus,
                logger,
                _arbiter: arbiter,
            });

            shared
                .emit(StationEvent::Connection { state: "connected".into(), path: path.clone() });
            shared.log("info", format!("Paired with the client over the {path} path."));
        });
    }

    /// Tears the session, replay stack, logger host and keying output down, returning to the
    /// unpaired state. Dropping the driver's handle stops the replay/watchdog threads and
    /// forces the key up (F8); dropping the logger host releases its port.
    pub fn disconnect(&self) {
        let active = self.active.lock().unwrap().take();
        if let Some(mut active) = active {
            active.stop_bus.store(true, Ordering::SeqCst);
            if let Some(mut logger) = active.logger.take() {
                logger.stop();
            }
            active.pump.abort();
            active.control.abort();
            let session = active.session;
            self.runtime.block_on(async {
                session.lock().await.shutdown().await;
            });
            self.shared.emit(StationEvent::KeyState { down: false });
            self.shared.emit(StationEvent::Ptt { asserted: false });
            self.shared.emit(StationEvent::LoggerSending { active: false });
            self.shared
                .emit(StationEvent::Connection { state: "disconnected".into(), path: "none".into() });
            self.shared.log("info", "Unpaired.");
        }
    }
}

/// Loads the config at `path` (if present) or creates a fresh one and saves it, then derives
/// the live UI [`Settings`] from it. A fresh config persists its new identity immediately so
/// the next run reuses the same WireGuard key.
fn load_or_create(path: &Option<std::path::PathBuf>) -> (Config, Settings) {
    let config = match path {
        Some(p) if p.exists() => Config::load(p).unwrap_or_else(|_| {
            Config::new(Role::Station).expect("generate station identity")
        }),
        _ => {
            let config = Config::new(Role::Station).expect("generate station identity");
            if let Some(p) = path {
                let _ = config.save(p);
            }
            config
        }
    };

    let settings = Settings {
        keying_port: config.station.keying_port.clone(),
        key_line: config
            .station
            .key_line
            .as_deref()
            .and_then(parse_line)
            .unwrap_or(KeyingLine::Dtr),
        ptt_line: config.station.ptt_line.as_deref().and_then(parse_line),
        logger_port: config.station.logger_port.clone(),
        logger_enabled: config.station.logger_enabled,
        relay: config.relay.as_ref().map(|r| r.address.to_string()),
        pairing_code: config.pairing_code.clone(),
    };
    (config, settings)
}

/// Builds the arbiter's line observer: emits KEY/PTT to the UI only on an actual change,
/// using interior state so it satisfies the `Fn` observer contract.
fn make_line_observer(shared: Arc<Shared>) -> LineObserver {
    let last_key = Arc::new(Mutex::new(None::<bool>));
    let last_ptt = Arc::new(Mutex::new(None::<bool>));
    Arc::new(move |u: LineUpdate| {
        {
            let mut lk = last_key.lock().unwrap_or_else(|e| e.into_inner());
            if *lk != Some(u.key_down) {
                *lk = Some(u.key_down);
                shared.emit(StationEvent::KeyState { down: u.key_down });
            }
        }
        {
            let mut lp = last_ptt.lock().unwrap_or_else(|e| e.into_inner());
            if *lp != Some(u.ptt) {
                *lp = Some(u.ptt);
                shared.emit(StationEvent::Ptt { asserted: u.ptt });
            }
        }
    })
}

/// Builds the logger host's reporter: logs its lines and bridges the sending indicator.
fn make_host_reporter(shared: Arc<Shared>) -> HostReporter {
    Arc::new(move |report: HostReport| match report {
        HostReport::Log { level, message } => shared.log(&level, message),
        HostReport::Sending(active) => shared.emit(StationEvent::LoggerSending { active }),
    })
}

/// Reports a pairing failure through the event sink.
fn fail(shared: &Arc<Shared>, message: String) {
    shared.log("warn", message);
    shared.emit(StationEvent::Connection { state: "disconnected".into(), path: "none".into() });
}

/// Spawns the Station's control-channel task: accept the Client's control connection, apply
/// each pushed forward-rule table to the live session, and — when any FlexRadio rule is
/// present — capture the radio's VITA-49 discovery broadcasts and forward them to the Client.
fn spawn_control_task(
    shared: Arc<Shared>,
    net: rwk_core::engine::netstack_driver::NetStackHandle,
    session: Arc<tokio::sync::Mutex<Session>>,
    stop: Arc<AtomicBool>,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        // Accept the Client's control connection over the tunnel.
        let link = match ControlLink::listen(&net).await {
            Ok(l) => l,
            Err(e) => {
                shared.log("warn", format!("Control channel listen failed: {e}"));
                return;
            }
        };
        shared.log("info", "Control channel up.");
        let link = Arc::new(tokio::sync::Mutex::new(link));

        // A handle to a running Flex discovery listener task, so a later rule change can stop
        // it (or leave it running).
        let mut flex_task: Option<tokio::task::JoinHandle<()>> = None;

        loop {
            if stop.load(Ordering::SeqCst) {
                break;
            }
            let message = {
                let mut guard = link.lock().await;
                guard.recv().await
            };
            match message {
                Ok(Some(ControlMessage::ForwardRules { rules })) => {
                    let started = session.lock().await.apply_forwards(&rules).await;
                    shared.log(
                        "info",
                        format!("Applied {started} forward rule(s) pushed from the client."),
                    );
                    // If the Client pushed any Flex rule, make sure the discovery listener is
                    // running so SmartSDR discovery is relayed back.
                    let has_flex = rules.iter().any(|r| r.id.starts_with("flex-"));
                    if has_flex && flex_task.is_none() {
                        flex_task = Some(spawn_flex_listener(
                            Arc::clone(&shared),
                            Arc::clone(&link),
                            Arc::clone(&stop),
                        ));
                        shared.log("info", "FlexRadio discovery capture started.");
                    } else if !has_flex {
                        if let Some(task) = flex_task.take() {
                            task.abort();
                        }
                    }
                }
                Ok(Some(_)) => {} // the Station ignores other message types
                Ok(None) | Err(_) => break, // closed or error
            }
        }
        if let Some(task) = flex_task.take() {
            task.abort();
        }
    })
}

/// Spawns a blocking task that captures FlexRadio discovery broadcasts on the Station LAN and
/// forwards each raw packet to the Client over `link` as a base64 `DiscoveryAnnounce`.
fn spawn_flex_listener(
    shared: Arc<Shared>,
    link: Arc<tokio::sync::Mutex<ControlLink>>,
    stop: Arc<AtomicBool>,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        let listener = match DiscoveryListener::bind() {
            Ok(l) => l,
            Err(e) => {
                shared.log("warn", format!("Flex discovery listener failed: {e}"));
                return;
            }
        };
        let _ = listener.set_read_timeout(Some(std::time::Duration::from_millis(300)));
        loop {
            if stop.load(Ordering::SeqCst) {
                return;
            }
            // The capture is blocking; run it off the async worker.
            let captured = tokio::task::block_in_place(|| listener.recv_discovery());
            match captured {
                Ok(Some(packet)) => {
                    let payload = base64_encode(&packet);
                    let guard = link.lock().await;
                    let _ = guard.send(&ControlMessage::DiscoveryAnnounce { payload }).await;
                }
                Ok(None) => {} // not a Flex packet, ignore
                Err(_) => {}   // timeout or transient error: loop and re-check the stop flag
            }
        }
    })
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

    /// A controller with persistence disabled (no file writes in tests).
    fn controller(sink: EventSink) -> StationController {
        StationController::start_with_config(sink, None)
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
        let _controller = controller(sink);
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| matches!(e, StationEvent::Settings { key_line, .. } if key_line == "dtr"))
        });
        assert!(ok, "start must emit the initial station settings");
    }

    #[test]
    fn selecting_a_keying_port_updates_and_echoes_settings() {
        let (sink, events) = recording_sink();
        let controller = controller(sink);
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
        let controller = controller(sink);
        controller.set_key_line("rts");
        controller.set_ptt_line("none");
        let s = controller.shared.settings.lock().unwrap();
        assert_eq!(s.key_line, KeyingLine::Rts);
        assert_eq!(s.ptt_line, None);
    }

    #[test]
    fn logger_settings_store() {
        let (sink, _events) = recording_sink();
        let controller = controller(sink);
        controller.set_logger_port(Some("COM9".into()));
        controller.set_logger_enabled(true);
        let s = controller.shared.settings.lock().unwrap();
        assert_eq!(s.logger_port.as_deref(), Some("COM9"));
        assert!(s.logger_enabled);
    }

    #[test]
    fn listen_without_a_keying_port_reports_disconnected() {
        let (sink, events) = recording_sink();
        let controller = controller(sink);
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
        let controller = controller(sink);
        controller.select_keying_port(Some("COM1".into()));
        controller.listen("not-an-address", "code");
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| matches!(e, StationEvent::Connection { state, .. } if state == "disconnected"))
        });
        assert!(ok, "a bad relay address must report disconnected, never hang");
    }

    #[test]
    fn persistence_round_trips_identity_and_settings() {
        // Write a config via one controller, then load it in a second and confirm the
        // identity and settings survived.
        let dir = std::env::temp_dir().join(format!("rwk-test-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let path = dir.join("station.json");
        let _ = std::fs::remove_file(&path);

        let (sink, _events) = recording_sink();
        let first = StationController::start_with_config(sink, Some(path.clone()));
        first.select_keying_port(Some("COM3".into()));
        first.set_logger_port(Some("COM4".into()));
        first.set_logger_enabled(true);
        let first_pub = first.shared.config.lock().unwrap().identity.public_key_base64().unwrap();
        drop(first);

        let (sink2, _e2) = recording_sink();
        let second = StationController::start_with_config(sink2, Some(path.clone()));
        let second_pub = second.shared.config.lock().unwrap().identity.public_key_base64().unwrap();
        assert_eq!(first_pub, second_pub, "identity must persist across restarts");
        let s = second.shared.settings.lock().unwrap();
        assert_eq!(s.keying_port.as_deref(), Some("COM3"));
        assert_eq!(s.logger_port.as_deref(), Some("COM4"));
        assert!(s.logger_enabled);
        drop(s);
        drop(second);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn line_names_round_trip() {
        assert_eq!(line_name(parse_line("dtr").unwrap()), "dtr");
        assert_eq!(line_name(parse_line("rts").unwrap()), "rts");
        assert_eq!(parse_line("none"), None);
        assert_eq!(parse_line("nonsense"), None);
    }
}
