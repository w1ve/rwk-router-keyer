//! The Client controller: the headless engine owner behind the UI.
//!
//! This mirrors the C# `ClientController`. The UI never touches the engine directly; it
//! sends commands here and renders [`ClientEvent`]s this controller emits. All blocking and
//! async work (CW playback timing, the WireGuard link, keying over the tunnel) happens on
//! controller-owned threads, so the UI thread only marshals small messages.
//!
//! Slice 1 scope (the Keyer tab): keyer timing settings, local sidetone, CW text keying,
//! optional serial key-line output, pairing-code connect, and keying over the tunnel. Paddle
//! serial input and the WinKeyer host are later increments; the port pickers accept a
//! selection and persist it, but driving a physical paddle is not wired yet (see
//! [`ClientController::select_paddle_port`]).

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};

use rwk_core::config::{Config, Role};
use rwk_core::engine::audio::SidetoneEngine;
use rwk_core::engine::debounce::ContactDebouncer;
use rwk_core::engine::edge_sender::EdgeSender;
use rwk_core::engine::keying::{EdgeEvent, EdgeSchedule, EdgeScheduleBuilder};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::paddle::PaddleKeyer;
use rwk_core::engine::paddle_input::PaddleInputPort;
use rwk_core::engine::serial::{enumerate_ports, KeyingOutputConfig, SerialKeyingOutput};
use rwk_core::engine::session::Session;
use rwk_core::engine::tunnel;
use rwk_core::platform::{ThreadPriorityGuard, TimerResolutionGuard};
use rwk_core::primitives::{EdgeSource, KeyerMode, KeyingLine};
use rwk_core::timing::{Clock, HybridWaiter, MonotonicClock};

/// The inner UDP port the keying edge rides over the tunnel, both ends agreeing.
const EDGE_PORT: u16 = 5555;

/// A command to the dedicated sidetone thread.
///
/// `cpal::Stream` is not `Send`, so the audio engine can never leave the thread that built
/// it. This channel is the only way to touch it: the controller (and the keying thread) send
/// these messages, and one owner thread services them. The handle the controller holds is a
/// plain `Sender`, which is `Send + Sync`, so the controller itself stays shareable.
enum ToneCmd {
    /// Key the tone down (true) or up (false).
    Key(bool),
    /// Rebuild the stream for a new frequency (Hz) and volume (0..=100).
    Reconfigure { hz: i32, volume: u32 },
}

/// A handle to the sidetone thread: cheap to clone, `Send + Sync`.
#[derive(Clone)]
struct Sidetone {
    tx: Sender<ToneCmd>,
}

impl Sidetone {
    /// Spawns the audio-owning thread with an initial tone/volume, reporting device status
    /// (or a warning) through `shared`.
    fn spawn(hz: i32, volume: u32, shared: Arc<Shared>) -> Self {
        let (tx, rx) = mpsc::channel::<ToneCmd>();
        std::thread::Builder::new()
            .name("rwk-sidetone".into())
            .spawn(move || {
                // The engine (and its cpal stream) lives entirely on this thread.
                let mut engine = build_engine(hz, volume, &shared);
                while let Ok(cmd) = rx.recv() {
                    match cmd {
                        ToneCmd::Key(true) => {
                            if let Some(e) = engine.as_ref() {
                                e.key_down();
                            }
                        }
                        ToneCmd::Key(false) => {
                            if let Some(e) = engine.as_ref() {
                                e.key_up();
                            }
                        }
                        ToneCmd::Reconfigure { hz, volume } => {
                            engine = build_engine(hz, volume, &shared);
                        }
                    }
                }
                // Channel closed: the controller is gone. Dropping the engine stops audio.
            })
            .expect("spawn sidetone thread");
        Self { tx }
    }

    fn key(&self, down: bool) {
        let _ = self.tx.send(ToneCmd::Key(down));
    }

    fn reconfigure(&self, hz: i32, volume: u32) {
        let _ = self.tx.send(ToneCmd::Reconfigure { hz, volume });
    }
}

/// Builds and starts a sidetone engine on the calling (audio) thread, logging the outcome.
fn build_engine(hz: i32, volume: u32, shared: &Arc<Shared>) -> Option<SidetoneEngine> {
    match SidetoneEngine::new(hz, volume as f64 / 100.0).and_then(|mut e| e.start().map(|()| e)) {
        Ok(engine) => {
            let name = engine.device_name().unwrap_or("output").to_string();
            shared.log("info", format!("Sidetone on {name}."));
            Some(engine)
        }
        Err(e) => {
            shared.log("warn", format!("Sidetone unavailable: {e}"));
            None
        }
    }
}

/// An event the controller emits for the UI to render. Serialized to the webview as-is.
#[derive(Debug, Clone, serde::Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum ClientEvent {
    /// The key line changed state (for the KEY indicator and status strip).
    KeyState { down: bool },
    /// The link to the station changed. `path` is "none" | "direct" | "relay".
    Connection { state: String, path: String },
    /// A human-readable log line for the Log tab.
    Log { level: String, message: String },
    /// A keyer/sidetone setting changed and the UI should reflect it (echo for confirmation).
    Settings {
        wpm: u32,
        weight: u32,
        mode: String,
        paddle_reverse: bool,
        tone_hz: i32,
        tone_volume: u32,
    },
}

/// A sink the host (Tauri) registers to receive [`ClientEvent`]s. Boxed so the controller
/// does not depend on the UI framework.
pub type EventSink = Arc<dyn Fn(ClientEvent) + Send + Sync>;

/// Live keyer/sidetone settings, shared with the keying thread.
#[derive(Debug, Clone)]
struct Settings {
    wpm: u32,
    weight: u32,
    mode: KeyerMode,
    paddle_reverse: bool,
    tone_hz: i32,
    tone_volume: u32,
    keying_port: Option<String>,
    paddle_port: Option<String>,
    winkeyer_port: Option<String>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            wpm: 20,
            weight: 50,
            mode: KeyerMode::IambicB,
            paddle_reverse: false,
            tone_hz: 600,
            tone_volume: 70,
            keying_port: None,
            paddle_port: None,
            winkeyer_port: None,
        }
    }
}

/// Shared state the controller and its background work read and write.
///
/// Everything here is `Send + Sync`: the sidetone is a channel handle (the non-`Send`
/// `cpal::Stream` lives on its own thread), the serial output is `Send`, and the transport is
/// an `Arc`. That is what lets the controller be stored in Tauri's managed state and touched
/// from command handlers on any thread.
struct Shared {
    settings: Mutex<Settings>,
    /// Set once the sidetone thread is spawned (during `start`).
    sidetone: Mutex<Option<Sidetone>>,
    keying: Mutex<Option<SerialKeyingOutput>>,
    /// The edge transport over the tunnel, present only while paired.
    transport: Mutex<Option<Arc<EdgeTransport>>>,
    /// Session epoch, incremented on each new pairing so the station re-anchors.
    epoch: Mutex<u16>,
    /// The running serial paddle poller, present only while a paddle port is selected.
    paddle: Mutex<Option<PaddlePollerHandle>>,
    /// The persisted config, held so pairing reuses a stable WireGuard identity across runs.
    config: Mutex<Config>,
    /// Where the config persists; `None` disables saving (tests).
    config_path: Option<std::path::PathBuf>,
    sink: EventSink,
}

impl Shared {
    fn emit(&self, event: ClientEvent) {
        (self.sink)(event);
    }

    fn log(&self, level: &str, message: impl Into<String>) {
        self.emit(ClientEvent::Log { level: level.into(), message: message.into() });
    }

    fn emit_settings(&self) {
        let s = self.settings.lock().unwrap();
        self.emit(ClientEvent::Settings {
            wpm: s.wpm,
            weight: s.weight,
            mode: mode_name(s.mode).into(),
            paddle_reverse: s.paddle_reverse,
            tone_hz: s.tone_hz,
            tone_volume: s.tone_volume,
        });
    }

    /// Remembers the relay + pairing code in the persisted config and writes it to disk, so
    /// the operator does not re-enter them next launch. A write failure is logged, not fatal.
    fn persist_pairing(&self, relay: std::net::SocketAddr, code: &str) {
        let Some(path) = &self.config_path else { return };
        let config = {
            let mut config = self.config.lock().unwrap();
            config.relay = Some(rwk_core::config::RelayConfig { address: relay });
            config.pairing_code = Some(code.to_string());
            config.clone()
        };
        if let Err(e) = config.save(path) {
            self.log("warn", format!("Could not save config: {e}"));
        }
    }
}

/// The Client controller. Construct with [`ClientController::start`], then drive it with the
/// command methods; it emits [`ClientEvent`]s to the sink for the UI to render.
pub struct ClientController {
    shared: Arc<Shared>,
    runtime: tokio::runtime::Runtime,
    /// The live session, held so the link stays up until unpaired or dropped. An `Arc` so a
    /// spawned pairing task can install the session when the handshake completes.
    session: Arc<Mutex<Option<Session>>>,
    _clock: Arc<dyn Clock>,
}

impl ClientController {
    /// Starts the controller: loads (or creates) the persisted config so the WireGuard
    /// identity is stable across runs, brings up the local sidetone (best effort), and emits
    /// the initial settings. A missing audio device is a logged warning, not a failure.
    pub fn start(sink: EventSink) -> Self {
        let path = crate::app_paths::config_path(Role::Client);
        Self::start_with_config(sink, Some(path))
    }

    /// Starts the controller against an explicit config path (or `None` to disable
    /// persistence, for tests). Loads the config if present, otherwise generates a fresh
    /// identity and saves it so the next run reuses the same key.
    pub fn start_with_config(sink: EventSink, config_path: Option<std::path::PathBuf>) -> Self {
        let settings = Settings::default();
        let config = match &config_path {
            Some(p) if p.exists() => {
                Config::load(p).unwrap_or_else(|_| Config::new(Role::Client).expect("identity"))
            }
            _ => {
                let config = Config::new(Role::Client).expect("generate client identity");
                if let Some(p) = &config_path {
                    let _ = config.save(p);
                }
                config
            }
        };
        let shared = Arc::new(Shared {
            settings: Mutex::new(settings.clone()),
            sidetone: Mutex::new(None),
            keying: Mutex::new(None),
            transport: Mutex::new(None),
            epoch: Mutex::new(1),
            paddle: Mutex::new(None),
            config: Mutex::new(config),
            config_path,
            sink,
        });

        // Start the local sidetone on its own audio-owning thread so the operator can
        // practice before pairing. The thread holds the non-`Send` cpal stream.
        let sidetone = Sidetone::spawn(settings.tone_hz, settings.tone_volume, Arc::clone(&shared));
        *shared.sidetone.lock().unwrap() = Some(sidetone);

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("tokio runtime");

        let controller = Self {
            shared,
            runtime,
            session: Arc::new(Mutex::new(None)),
            _clock: Arc::new(MonotonicClock),
        };
        controller.shared.emit_settings();
        controller
    }

    // ─── Keyer settings ────────────────────────────────────────────────────────

    /// Sets the keyer speed in WPM (clamped 5..=60 downstream).
    pub fn set_speed(&self, wpm: u32) {
        self.shared.settings.lock().unwrap().wpm = wpm;
        self.shared.log("debug", format!("Speed {wpm} WPM."));
    }

    /// Sets the keyer weight percentage (clamped 25..=75 downstream).
    pub fn set_weight(&self, weight: u32) {
        self.shared.settings.lock().unwrap().weight = weight;
    }

    /// Sets the keyer mode from a UI name ("iambicB", "iambicA", "ultimatic", "bug", "straight").
    pub fn set_mode(&self, mode: &str) {
        let parsed = parse_mode(mode);
        self.shared.settings.lock().unwrap().mode = parsed;
        self.shared.log("debug", format!("Mode {}.", mode_name(parsed)));
    }

    /// Sets whether the paddle dit/dah contacts are reversed.
    pub fn set_paddle_reverse(&self, reverse: bool) {
        self.shared.settings.lock().unwrap().paddle_reverse = reverse;
    }

    // ─── Sidetone ──────────────────────────────────────────────────────────────

    /// Sets the sidetone frequency in Hz and rebuilds the stream to apply it.
    pub fn set_tone_frequency(&self, hz: i32) {
        self.shared.settings.lock().unwrap().tone_hz = hz;
        self.restart_sidetone();
    }

    /// Sets the sidetone volume (0..=100) and rebuilds the stream to apply it.
    pub fn set_tone_volume(&self, percent: u32) {
        self.shared.settings.lock().unwrap().tone_volume = percent.min(100);
        self.restart_sidetone();
    }

    fn restart_sidetone(&self) {
        let (hz, vol) = {
            let s = self.shared.settings.lock().unwrap();
            (s.tone_hz, s.tone_volume)
        };
        if let Some(tone) = self.shared.sidetone.lock().unwrap().as_ref() {
            tone.reconfigure(hz, vol);
        }
    }

    /// Lists output audio device names for the sidetone picker.
    #[must_use]
    pub fn audio_devices() -> Vec<String> {
        SidetoneEngine::output_devices()
    }

    // ─── Ports ───────────────────────────────────────────────────────────────

    /// Lists available serial ports for the device pickers.
    #[must_use]
    pub fn serial_ports() -> Vec<String> {
        enumerate_ports()
            .map(|ports| ports.into_iter().map(|p| p.port_name).collect())
            .unwrap_or_default()
    }

    /// Selects the serial port used to key a local rig's control line (DTR by default), or
    /// `None` to release it. This drives the physical key line in lock-step with the keyer,
    /// so a local rig keys alongside the remote one.
    pub fn select_keying_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().keying_port = port.clone();
        let mut guard = self.shared.keying.lock().unwrap();
        *guard = None; // release any previous port first
        if let Some(name) = port {
            let cfg = KeyingOutputConfig {
                port_name: name.clone(),
                key_line: KeyingLine::Dtr,
                ..Default::default()
            };
            match SerialKeyingOutput::open(cfg) {
                Ok(out) => {
                    *guard = Some(out);
                    self.shared.log("info", format!("Keying output on {name}."));
                }
                Err(e) => self.shared.log("warn", format!("Keying port {name} failed: {e}")),
            }
        }
    }

    /// Selects the serial port a paddle/key is wired to, or `None` to release it.
    ///
    /// Opening starts a dedicated high-priority poller thread that reads the port's modem-
    /// status lines (CTS=dit, DSR=dah, DCD=straight), debounces them, runs the live keyer for
    /// the current mode/speed/weight, and keys the local sidetone, the local key line, the UI
    /// indicator, and (when paired) the tunnel — exactly the output path the type-ahead CW
    /// uses. This matches the C# `PaddleInputPoller`: 1 ms poll, raised priority, DTR asserted
    /// as the contact voltage source, so it works on native UARTs and USB-to-serial alike.
    pub fn select_paddle_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().paddle_port = port.clone();
        // Stop any running poller first (changing ports is a normal operation).
        *self.shared.paddle.lock().unwrap() = None;

        match port {
            Some(name) => match PaddleInputPort::open(&name) {
                Ok(input) => {
                    let handle = PaddlePollerHandle::spawn(
                        input,
                        Arc::clone(&self.shared),
                        self.runtime.handle().clone(),
                    );
                    *self.shared.paddle.lock().unwrap() = Some(handle);
                    self.shared.log("info", format!("Paddle input on {name} (CTS=dit, DSR=dah, DCD=straight)."));
                }
                Err(e) => self.shared.log("warn", format!("Paddle port {name} failed: {e}")),
            },
            None => self.shared.log("info", "Paddle input cleared."),
        }
    }

    /// Records the selected WinKeyer port. NOTE: the WinKeyer protocol host is a later
    /// increment; this persists the choice only.
    pub fn select_winkeyer_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().winkeyer_port = port.clone();
        match port {
            Some(name) => self.shared.log(
                "info",
                format!("WinKeyer port {name} selected (protocol host not yet wired)."),
            ),
            None => self.shared.log("info", "WinKeyer port cleared."),
        }
    }

    // ─── Keying ──────────────────────────────────────────────────────────────

    /// Keys `text` as CW: plays the local sidetone and, when paired, sends the edges over
    /// the tunnel to the station. Runs the schedule on a dedicated thread so the UI never
    /// blocks on CW timing.
    pub fn send_cw(&self, text: &str) {
        let text = text.to_string();
        let shared = Arc::clone(&self.shared);
        let handle = self.runtime.handle().clone();
        std::thread::spawn(move || play_cw(&shared, &handle, &text));
    }

    /// Sends the standard test string, matching the C# "Test TX" button.
    pub fn test_tx(&self) {
        self.send_cw("VVV TESTING");
    }

    // ─── Pairing ──────────────────────────────────────────────────────────────

    /// Pairs with the station using a shared pairing code over the configured relay, brings
    /// the keying edge up over the tunnel, and reports the path that won.
    ///
    /// Non-blocking: the (slow) rendezvous and handshake run on the controller's own
    /// runtime, and the outcome is reported through [`ClientEvent::Connection`] /
    /// [`ClientEvent::Log`] rather than a return value, so the UI thread never stalls.
    pub fn connect(&self, relay: &str, code: &str) {
        let relay_addr: std::net::SocketAddr = match relay.parse() {
            Ok(a) => a,
            Err(e) => {
                self.shared.log("warn", format!("Invalid relay address '{relay}': {e}"));
                self.shared.emit(ClientEvent::Connection {
                    state: "disconnected".into(),
                    path: "none".into(),
                });
                return;
            }
        };
        let code = code.to_string();
        let shared = Arc::clone(&self.shared);
        let session_slot = self.session_handle();

        shared.emit(ClientEvent::Connection { state: "connecting".into(), path: "none".into() });

        self.runtime.spawn(async move {
            // Clone the PERSISTED config so the WireGuard identity is stable across runs,
            // then layer the relay and pairing code on top. The peer key is discovered by
            // rendezvous, so no key paste is needed.
            let mut config = shared.config.lock().unwrap().clone();
            config.role = Role::Client;
            config.relay = Some(rwk_core::config::RelayConfig { address: relay_addr });
            config.pairing_code = Some(code.clone());
            // Remember the relay + code for next launch.
            shared.persist_pairing(relay_addr, &code);

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

            // Open the keying edge over the tunnel, pointed at the station's inner address.
            let conn = match net.open_udp(EDGE_PORT).await {
                Ok(c) => c,
                Err(e) => return fail(&shared, format!("edge open failed: {e}")),
            };
            let transport = Arc::new(EdgeTransport::over_tunnel(conn));
            let peer = std::net::SocketAddr::new(
                std::net::IpAddr::V4(Role::Client.peer_inner_address()),
                EDGE_PORT,
            );
            transport.set_peer(peer).await;

            *shared.transport.lock().unwrap() = Some(transport);
            *session_slot.lock().unwrap() = Some(session);
            {
                let mut epoch = shared.epoch.lock().unwrap();
                *epoch = epoch.wrapping_add(1);
            }

            shared
                .emit(ClientEvent::Connection { state: "connected".into(), path: path.clone() });
            shared.log("info", format!("Paired over the {path} path."));
        });
    }

    /// Tears the session and keying edge down, returning to the unpaired state.
    pub fn disconnect(&self) {
        *self.shared.transport.lock().unwrap() = None;
        if let Some(mut session) = self.session.lock().unwrap().take() {
            self.runtime.block_on(async { session.shutdown().await });
        }
        self.shared
            .emit(ClientEvent::Connection { state: "disconnected".into(), path: "none".into() });
        self.shared.log("info", "Unpaired.");
    }

    /// A clone of the session slot `Arc` for use inside spawned tasks.
    fn session_handle(&self) -> Arc<Mutex<Option<Session>>> {
        Arc::clone(&self.session)
    }
}

/// Reports a pairing failure through the event sink.
fn fail(shared: &Arc<Shared>, message: String) {
    shared.log("warn", message);
    shared.emit(ClientEvent::Connection { state: "disconnected".into(), path: "none".into() });
}

impl Drop for ClientController {
    fn drop(&mut self) {
        self.disconnect();
    }
}

/// Plays a CW string: builds the schedule, then walks its edges in real time, driving the
/// sidetone and (when paired) the tunnel edge transport.
fn play_cw(shared: &Arc<Shared>, rt: &tokio::runtime::Handle, text: &str) {
    let clock = MonotonicClock;
    let (wpm, weight) = {
        let s = shared.settings.lock().unwrap();
        (s.wpm, s.weight)
    };
    let start = clock.now();
    let schedule: EdgeSchedule =
        match EdgeScheduleBuilder::build(text, wpm, weight, start, clock.frequency()) {
            Ok(s) => s,
            Err(e) => {
                shared.log("warn", format!("CW build failed: {e}"));
                return;
            }
        };

    let epoch = *shared.epoch.lock().unwrap();
    let mut sender = EdgeSender::new(epoch, clock.frequency(), start);
    let tone = shared.sidetone.lock().unwrap().clone();

    for edge in &schedule.edges {
        // Pace to the edge's scheduled tick with a hybrid sleep/spin for sub-ms accuracy.
        rwk_core::timing::HybridWaiter::wait_until_ticks(&clock, edge.timestamp_ticks, &|| false);
        drive_edge(shared, rt, tone.as_ref(), &mut sender, edge.key_down, edge.timestamp_ticks);
    }
    // Ensure the key is up at the end.
    drive_edge(shared, rt, tone.as_ref(), &mut sender, false, clock.now());
}

/// Fans a single key transition to every output: local sidetone, the local serial key line,
/// the UI indicator, and (when paired) the tunnel edge transport. This is the one place the
/// Client turns a decided edge into effect, shared by the text/macro path ([`play_cw`]) and
/// the live paddle poller so both key identically.
fn drive_edge(
    shared: &Arc<Shared>,
    rt: &tokio::runtime::Handle,
    tone: Option<&Sidetone>,
    sender: &mut EdgeSender,
    key_down: bool,
    timestamp_ticks: u64,
) {
    // 1. Local sidetone (via its own audio thread).
    if let Some(tone) = tone {
        tone.key(key_down);
    }
    // 2. Local key line, if a port is open.
    if let Some(out) = shared.keying.lock().unwrap().as_mut() {
        let _ = out.apply(key_down, false);
    }
    // 3. UI indicator.
    shared.emit(ClientEvent::KeyState { down: key_down });
    // 4. Tunnel transport when paired.
    let transport = shared.transport.lock().unwrap().clone();
    if let Some(transport) = transport {
        let event = EdgeEvent { timestamp_ticks, key_down, source: EdgeSource::Paddle };
        let frame = sender.frame_for(event);
        let _ = rt.block_on(async { transport.send_frame(&frame).await });
    }
}

/// Nominal paddle poll interval, matching the C# `PaddleInputPoller` (1 ms).
const PADDLE_POLL_MS: u64 = 1;
/// Debounce window for the paddle contacts (5 ms), matching the C# default.
const PADDLE_DEBOUNCE_MS: u64 = 5;

/// A handle to the running paddle poller thread. Dropping it stops the thread, joins it, and
/// releases the serial port (DTR dropped). Changing or clearing the paddle port drops the
/// previous handle, so there is never more than one poller.
struct PaddlePollerHandle {
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl PaddlePollerHandle {
    /// Spawns the poller thread over an open [`PaddleInputPort`].
    fn spawn(
        mut input: PaddleInputPort,
        shared: Arc<Shared>,
        rt: tokio::runtime::Handle,
    ) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let stop_thread = Arc::clone(&stop);
        let thread = std::thread::Builder::new()
            .name("rwk-paddle".into())
            .spawn(move || paddle_loop(&mut input, &shared, &rt, &stop_thread))
            .expect("spawn paddle poller thread");
        Self { stop, thread: Some(thread) }
    }
}

impl Drop for PaddlePollerHandle {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// The paddle poller body. Runs on a dedicated time-critical thread with a 1 ms timer period,
/// polling the serial control lines, debouncing, and driving the live keyer. Each resulting
/// edge is fanned to sidetone / key line / UI / tunnel through [`drive_edge`], the same path
/// the type-ahead CW uses, so a paddle and a typed macro key identically. A port read error
/// (adapter unplugged) ends the loop cleanly after releasing the key.
fn paddle_loop(
    input: &mut PaddleInputPort,
    shared: &Arc<Shared>,
    rt: &tokio::runtime::Handle,
    stop: &Arc<AtomicBool>,
) {
    // Raise priority and timer resolution for the whole session, mirroring the C# poller.
    let _priority = ThreadPriorityGuard::raise_time_critical();
    let _timer = TimerResolutionGuard::raise(1);

    let clock = MonotonicClock;
    let hz = clock.frequency();
    let mut debouncer = ContactDebouncer::new(PADDLE_DEBOUNCE_MS, hz);

    // The live keyer, re-tuned each element from the current UI settings so moving the WPM
    // slider or changing mode takes effect immediately.
    let (mut wpm, mut weight, mut mode, _reverse0) = read_keyer_settings(shared);
    let mut keyer = match PaddleKeyer::new(mode, wpm, weight, hz) {
        Ok(k) => k,
        Err(e) => {
            shared.log("warn", format!("Paddle keyer init failed: {e}"));
            return;
        }
    };

    // A fresh EdgeSender for the tunnel frames, with its own epoch window from now.
    let epoch = *shared.epoch.lock().unwrap();
    let mut sender = EdgeSender::new(epoch, hz, clock.now());

    let poll_step = hz.saturating_mul(PADDLE_POLL_MS) / 1000;
    let should_abort = || stop.load(Ordering::SeqCst);

    while !stop.load(Ordering::SeqCst) {
        // Read + debounce the contacts.
        let raw = match input.read() {
            Ok(sample) => sample,
            Err(e) => {
                shared.log("warn", format!("Paddle port read error: {e}. Input stopped."));
                break;
            }
        };
        let _ = debouncer.accept(raw, clock.now());
        let accepted = debouncer.accepted();

        // Re-read UI settings; re-tune / change mode live if they moved.
        let (nw, ng, nm, reverse) = read_keyer_settings(shared);
        if nw != wpm || ng != weight {
            let _ = keyer.retune(nw, ng, hz);
            wpm = nw;
            weight = ng;
        }
        if nm != mode {
            keyer.set_mode(nm);
            mode = nm;
        }

        // Snapshot of the debounced paddle for the keyer, applying dit/dah reversal.
        let paddle_now = move || accepted.to_paddle_state(reverse);

        let tone = shared.sidetone.lock().unwrap().clone();
        let played = keyer.run_once(
            &clock,
            &paddle_now,
            &mut |edge| {
                drive_edge(shared, rt, tone.as_ref(), &mut sender, edge.key_down, edge.timestamp_ticks);
            },
            &should_abort,
        );

        if !played {
            // Idle: pace the poll at ~1 ms so the thread does not busy-spin.
            let next = clock.now() + poll_step.max(1);
            HybridWaiter::wait_until_ticks(&clock, next, &should_abort);
        }
    }

    // Leave the key up on the way out.
    let tone = shared.sidetone.lock().unwrap().clone();
    drive_edge(shared, rt, tone.as_ref(), &mut sender, false, clock.now());
}

/// Reads the current keyer settings (wpm, weight, mode, paddle-reverse) under the lock.
fn read_keyer_settings(shared: &Arc<Shared>) -> (u32, u32, KeyerMode, bool) {
    let s = shared.settings.lock().unwrap();
    (s.wpm, s.weight, s.mode, s.paddle_reverse)
}

/// Maps a UI mode name to a [`KeyerMode`], defaulting to Iambic B.
fn parse_mode(name: &str) -> KeyerMode {
    match name {
        "iambicA" => KeyerMode::IambicA,
        "ultimatic" => KeyerMode::Ultimatic,
        "bug" => KeyerMode::Bug,
        "straight" => KeyerMode::Straight,
        _ => KeyerMode::IambicB,
    }
}

/// The UI name for a [`KeyerMode`].
fn mode_name(mode: KeyerMode) -> &'static str {
    match mode {
        KeyerMode::IambicB => "iambicB",
        KeyerMode::IambicA => "iambicA",
        KeyerMode::Ultimatic => "ultimatic",
        KeyerMode::Bug => "bug",
        KeyerMode::Straight => "straight",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    /// A sink that records every event for assertions.
    fn recording_sink() -> (EventSink, Arc<Mutex<Vec<ClientEvent>>>) {
        let events = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&events);
        let sink: EventSink = Arc::new(move |e| captured.lock().unwrap().push(e));
        (sink, events)
    }

    /// Waits until `pred` sees the recorded events satisfied, or the timeout elapses.
    fn wait_for(
        events: &Arc<Mutex<Vec<ClientEvent>>>,
        timeout: Duration,
        pred: impl Fn(&[ClientEvent]) -> bool,
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
        let _controller = ClientController::start(sink);
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| {
                matches!(e, ClientEvent::Settings { wpm, mode, .. } if *wpm == 20 && mode == "iambicB")
            })
        });
        assert!(ok, "start must emit the initial keyer settings");
    }

    #[test]
    fn settings_commands_update_state() {
        let (sink, _events) = recording_sink();
        let controller = ClientController::start(sink);
        controller.set_speed(30);
        controller.set_weight(60);
        controller.set_mode("straight");
        controller.set_paddle_reverse(true);
        let s = controller.shared.settings.lock().unwrap();
        assert_eq!(s.wpm, 30);
        assert_eq!(s.weight, 60);
        assert_eq!(s.mode, KeyerMode::Straight);
        assert!(s.paddle_reverse);
    }

    #[test]
    fn send_cw_emits_key_down_then_up() {
        let (sink, events) = recording_sink();
        let controller = ClientController::start(sink);
        // "E" is a single dit: exactly one key-down then one key-up.
        controller.send_cw("E");

        let ok = wait_for(&events, Duration::from_secs(2), |evs| {
            let states: Vec<bool> = evs
                .iter()
                .filter_map(|e| match e {
                    ClientEvent::KeyState { down } => Some(*down),
                    _ => None,
                })
                .collect();
            // At least a down followed by an up, and the final state is up (safe).
            states.len() >= 2 && states.first() == Some(&true) && states.last() == Some(&false)
        });
        assert!(ok, "send_cw(\"E\") must key down then up, ending key-up");
    }

    #[test]
    fn connect_rejects_a_bad_relay_address_without_blocking() {
        let (sink, events) = recording_sink();
        let controller = ClientController::start(sink);
        // A malformed relay must fail fast and return to disconnected, never hang.
        controller.connect("not-an-address", "code");
        let ok = wait_for(&events, Duration::from_secs(1), |evs| {
            evs.iter().any(|e| {
                matches!(e, ClientEvent::Connection { state, .. } if state == "disconnected")
            }) && evs.iter().any(|e| matches!(e, ClientEvent::Log { .. }))
        });
        assert!(ok, "a bad relay address must report disconnected + a log line");
    }

    #[test]
    fn mode_names_round_trip() {
        for name in ["iambicB", "iambicA", "ultimatic", "bug", "straight"] {
            assert_eq!(mode_name(parse_mode(name)), name);
        }
        // Unknown names fall back to Iambic B.
        assert_eq!(parse_mode("nonsense"), KeyerMode::IambicB);
    }
}
