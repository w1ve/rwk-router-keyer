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

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Condvar, Mutex};

use rwk_core::config::{base64_decode, Config, ForwardRuleConfig, Role};
use rwk_core::engine::audio::SidetoneEngine;
use rwk_core::engine::control::{is_reserved_port, ControlLink, ControlMessage};
use rwk_core::engine::debounce::ContactDebouncer;
use rwk_core::engine::edge_sender::EdgeSender;
use rwk_core::engine::flex_discovery::DiscoveryEmitter;
use rwk_core::engine::keying::{EdgeEvent, KeyerElement, KeyerElementTiming};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::paddle::PaddleKeyer;
use rwk_core::engine::paddle_input::PaddleInputPort;
use rwk_core::engine::serial::{
    enumerate_ports, natural_port_key, KeyingOutputConfig, SerialKeyingOutput,
};
use rwk_core::engine::session::Session;
use rwk_core::engine::tunnel;
use rwk_core::protocol::flex_vita;
use rwk_core::protocol::morse::MorseTable;
use rwk_core::platform::{ThreadPriorityGuard, TimerResolutionGuard};
use rwk_core::primitives::{EdgeSource, KeyerMode, KeyingLine};
use rwk_core::timing::{Clock, HybridWaiter, MonotonicClock};

/// The inner UDP port the keying edge rides over the tunnel, both ends agreeing.
const EDGE_PORT: u16 = 5555;

/// Control identifiers for inline per-control error messages in the UI.
mod control {
    pub const PADDLE: &str = "paddle";
    pub const WINKEYER: &str = "winkeyer";
    pub const KEYING: &str = "keying";
    pub const AUDIO: &str = "audio";
}

/// A command to the dedicated sidetone thread.
///
/// `cpal::Stream` is not `Send`, so the audio engine can never leave the thread that built
/// it. This channel is the only way to touch it: the controller (and the keying thread) send
/// these messages, and one owner thread services them. The handle the controller holds is a
/// plain `Sender`, which is `Send + Sync`, so the controller itself stays shareable.
enum ToneCmd {
    /// Key the tone down (true) or up (false).
    Key(bool),
    /// Rebuild the stream for a new frequency (Hz), volume (0..=100), and output device.
    Reconfigure { hz: i32, volume: u32, device: ToneDevice },
}

/// How the sidetone device is configured: off, the system default, or a named device.
#[derive(Debug, Clone)]
enum ToneDevice {
    Off,
    Default,
    Named(String),
}

impl ToneDevice {
    /// Interprets the persisted `audio_device` field: `None` = default, `Some("")` = off,
    /// `Some(name)` = that device.
    fn from_setting(setting: &Option<String>) -> Self {
        match setting {
            None => Self::Default,
            Some(s) if s.is_empty() => Self::Off,
            Some(name) => Self::Named(name.clone()),
        }
    }
}

/// A handle to the sidetone thread: cheap to clone, `Send + Sync`.
#[derive(Clone)]
struct Sidetone {
    tx: Sender<ToneCmd>,
}

impl Sidetone {
    /// Spawns the audio-owning thread with an initial tone/volume/device, reporting device
    /// status (or a warning) through `shared`.
    fn spawn(hz: i32, volume: u32, device: ToneDevice, shared: Arc<Shared>) -> Self {
        let (tx, rx) = mpsc::channel::<ToneCmd>();
        std::thread::Builder::new()
            .name("rwk-sidetone".into())
            .spawn(move || {
                // The engine (and its cpal stream) lives entirely on this thread.
                let mut engine = build_engine(hz, volume, &device, &shared);
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
                        ToneCmd::Reconfigure { hz, volume, device } => {
                            engine = build_engine(hz, volume, &device, &shared);
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

    fn reconfigure(&self, hz: i32, volume: u32, device: ToneDevice) {
        let _ = self.tx.send(ToneCmd::Reconfigure { hz, volume, device });
    }
}

/// Builds a started sidetone engine for the requested device, or `None` when the device is
/// `Off` or the audio stream could not be opened (the error is logged and surfaced under the
/// audio control).
fn build_engine(
    hz: i32,
    volume: u32,
    device: &ToneDevice,
    shared: &Arc<Shared>,
) -> Option<SidetoneEngine> {
    if matches!(device, ToneDevice::Off) {
        shared.clear_error(control::AUDIO);
        shared.log("info", "Sidetone off (device set to None).");
        return None;
    }
    let named = match device {
        ToneDevice::Named(n) => Some(n.as_str()),
        _ => None,
    };
    let engine = SidetoneEngine::new(hz, volume as f64 / 100.0)
        .and_then(|mut e| e.start_on(named).map(|()| e));
    match engine {
        Ok(engine) => {
            let name = engine.device_name().unwrap_or("output").to_string();
            shared.clear_error(control::AUDIO);
            shared.log("info", format!("Sidetone on {name}."));
            Some(engine)
        }
        Err(e) => {
            let short = match device {
                ToneDevice::Named(n) => format!("{n} unavailable"),
                _ => "no audio output".to_string(),
            };
            shared.fail(control::AUDIO, short, format!("Sidetone unavailable: {e}"));
            None
        }
    }
}

// ─── Send buffer (type-ahead + macros) ─────────────────────────────────────────

/// The character queue + control flags shared with the send-worker thread.
#[derive(Default)]
struct SendState {
    /// Characters waiting to be keyed, in order.
    queue: VecDeque<char>,
    /// Set by [`ClientController::abort_send`]; the worker drops the queue and the current
    /// schedule immediately when it sees this, then clears it.
    abort: bool,
    /// Set once at shutdown so the worker thread exits.
    shutdown: bool,
}

/// A thread-safe send buffer: a lock plus a condvar the worker waits on. Appending text or
/// requesting an abort notifies the worker.
#[derive(Default)]
struct SendBuffer {
    state: Mutex<SendState>,
    cv: Condvar,
}

impl SendBuffer {
    /// Appends `text` to the queue and wakes the worker.
    fn push(&self, text: &str) {
        let mut st = self.state.lock().unwrap_or_else(|e| e.into_inner());
        st.queue.extend(text.chars());
        self.cv.notify_all();
    }

    /// Flushes the queue and signals the worker to drop the in-progress schedule now.
    fn abort(&self) {
        let mut st = self.state.lock().unwrap_or_else(|e| e.into_inner());
        st.queue.clear();
        st.abort = true;
        self.cv.notify_all();
    }

    /// Signals the worker to exit.
    fn shutdown(&self) {
        let mut st = self.state.lock().unwrap_or_else(|e| e.into_inner());
        st.shutdown = true;
        self.cv.notify_all();
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
    ///
    /// NOTE: struct-variant fields keep their snake_case names in the serialized JSON (the
    /// enum-level `rename_all = "camelCase"` renames only the variant *tag*). The web UI
    /// reads `ev.paddle_reverse`, `ev.tone_hz`, `ev.audio_device`, etc. — keep them snake_case.
    Settings {
        wpm: u32,
        weight: u32,
        mode: String,
        paddle_reverse: bool,
        tone_hz: i32,
        tone_volume: u32,
        /// Sidetone output device: `null` = system default, `""` = off, name = that device.
        audio_device: Option<String>,
        keying_port: Option<String>,
        paddle_port: Option<String>,
        winkeyer_port: Option<String>,
        transport: String,
        callsign: String,
        keyer_id: String,
    },
    /// The current forward-rule table, echoed whenever it changes (for the Ham Router tab).
    ForwardRules { rules: Vec<ForwardRuleView> },
    /// Whether FlexRadio discovery re-emission is enabled (for the Ham Router toggle).
    FlexDiscovery { enabled: bool },
    /// Whether the LAN discovery beacon is currently being advertised.
    Advertising { active: bool },
    /// The type-ahead/macro sender is actively keying CW.
    Sending { active: bool },
    /// The current serial-port inventory (numeric-sorted), pushed on change (PnP).
    Ports { available: Vec<String> },
    /// The current audio output device inventory for the sidetone picker.
    AudioDevices { available: Vec<AudioDeviceView> },
    /// A short per-control error to show in red under the offending control (full detail is
    /// logged too). An empty `message` clears the control's error. `control` is one of
    /// "keying" | "paddle" | "winkeyer" | "audio".
    Error { control: String, message: String },
}

/// One audio output device for the UI picker: its name and whether it is the system default.
#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioDeviceView {
    pub name: String,
    pub is_default: bool,
}

/// Which transport the operator chose for pairing: WAN (relay + code) or LAN (relay-free
/// broadcast discovery). The Keyer-Router's WAN path is the existing relay rendezvous.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Transport {
    /// Over the internet via the relay (the existing `connect(relay, code)` path).
    Wan,
    /// Relay-free on the local network (broadcast discovery; the radio site picks this keyer).
    Lan,
}

impl Transport {
    fn parse(s: &str) -> Self {
        if s.eq_ignore_ascii_case("lan") {
            Transport::Lan
        } else {
            Transport::Wan
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Transport::Wan => "wan",
            Transport::Lan => "lan",
        }
    }
}

/// A forward rule as the UI sees it: the persisted [`ForwardRuleConfig`] fields, serialized
/// in camelCase for the web layer.
#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ForwardRuleView {
    pub id: String,
    pub name: String,
    pub protocol: String,
    pub client_port: u16,
    pub station_port: u16,
    pub enabled: bool,
    pub station_target_address: String,
}

impl From<&ForwardRuleConfig> for ForwardRuleView {
    fn from(r: &ForwardRuleConfig) -> Self {
        Self {
            id: r.id.clone(),
            name: r.name.clone(),
            protocol: r.protocol.clone(),
            client_port: r.client_port,
            station_port: r.station_port,
            enabled: r.enabled,
            station_target_address: r.station_target_address.clone(),
        }
    }
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
    /// Sidetone output device: `None` = system default, `Some("")` = off, `Some(name)` = named.
    audio_device: Option<String>,
    keying_port: Option<String>,
    paddle_port: Option<String>,
    winkeyer_port: Option<String>,
    transport: Transport,
    callsign: String,
    keyer_id: String,
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
            audio_device: None,
            keying_port: None,
            paddle_port: None,
            winkeyer_port: None,
            transport: Transport::Wan,
            callsign: String::new(),
            keyer_id: String::new(),
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
    /// The type-ahead / macro send buffer, drained by the single send-worker thread.
    send: SendBuffer,
    /// The running LAN advertiser task (operating-position side), present while advertising.
    advertise: tokio::sync::Mutex<Option<tokio::task::JoinHandle<()>>>,
    /// The control channel to the Station, present only while paired. Used to push the
    /// forward-rule table and to receive FlexRadio discovery announcements.
    control: tokio::sync::Mutex<Option<ControlLink>>,
    /// The live session, so the Client can restart its own forwards on a rule change. Shared
    /// (rather than held only on the controller) so command handlers reach it.
    session: tokio::sync::Mutex<Option<Session>>,
    /// The host-LAN FlexRadio discovery emitter, present while re-emission is enabled.
    flex_emitter: Mutex<Option<DiscoveryEmitter>>,
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

    /// Logs the full detail and shows a short red message under `control`.
    fn fail(&self, control: &str, short: impl Into<String>, detail: impl Into<String>) {
        self.log("warn", detail);
        self.emit(ClientEvent::Error { control: control.into(), message: short.into() });
    }

    /// Clears any red message under `control`.
    fn clear_error(&self, control: &str) {
        self.emit(ClientEvent::Error { control: control.into(), message: String::new() });
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
            audio_device: s.audio_device.clone(),
            keying_port: s.keying_port.clone(),
            paddle_port: s.paddle_port.clone(),
            winkeyer_port: s.winkeyer_port.clone(),
            transport: s.transport.as_str().into(),
            callsign: s.callsign.clone(),
            keyer_id: s.keyer_id.clone(),
        });
    }

    /// Interprets the persisted audio-device setting into a [`ToneDevice`].
    fn tone_device(&self) -> ToneDevice {
        ToneDevice::from_setting(&self.settings.lock().unwrap().audio_device)
    }

    /// Persists callsign/keyer_id into the config and writes it to disk.
    fn persist_identity_labels(&self) {
        let Some(path) = &self.config_path else { return };
        {
            let s = self.settings.lock().unwrap();
            let mut config = self.config.lock().unwrap();
            config.callsign = s.callsign.clone();
            config.keyer_id = s.keyer_id.clone();
        }
        let config = self.config.lock().unwrap();
        if let Err(e) = config.save(path) {
            self.log("warn", format!("Could not save config: {e}"));
        }
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

    /// Emits the current forward-rule table to the UI.
    fn emit_forward_rules(&self) {
        let config = self.config.lock().unwrap();
        let rules = config.forwards.iter().map(ForwardRuleView::from).collect();
        self.emit(ClientEvent::ForwardRules { rules });
    }

    /// Emits whether FlexRadio discovery re-emission is enabled.
    fn emit_flex_state(&self) {
        let enabled = self.flex_emitter.lock().unwrap().is_some();
        self.emit(ClientEvent::FlexDiscovery { enabled });
    }

    /// Writes the current config (including `forwards`) to disk. Logged, not fatal, on error.
    fn persist_config(&self) {
        let Some(path) = &self.config_path else { return };
        let config = self.config.lock().unwrap();
        if let Err(e) = config.save(path) {
            self.log("warn", format!("Could not save config: {e}"));
        }
    }

    /// Returns a clone of the current forward rules.
    fn forward_rules(&self) -> Vec<ForwardRuleConfig> {
        self.config.lock().unwrap().forwards.clone()
    }
}

/// The Client controller. Construct with [`ClientController::start`], then drive it with the
/// command methods; it emits [`ClientEvent`]s to the sink for the UI to render.
pub struct ClientController {
    shared: Arc<Shared>,
    runtime: tokio::runtime::Runtime,
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
        // Seed callsign/keyer_id from the persisted config so the LAN beacon name survives.
        let mut settings = Settings::default();
        settings.callsign = config.callsign.clone();
        settings.keyer_id = config.keyer_id.clone();
        let shared = Arc::new(Shared {
            settings: Mutex::new(settings.clone()),
            sidetone: Mutex::new(None),
            keying: Mutex::new(None),
            transport: Mutex::new(None),
            epoch: Mutex::new(1),
            paddle: Mutex::new(None),
            send: SendBuffer::default(),
            advertise: tokio::sync::Mutex::new(None),
            control: tokio::sync::Mutex::new(None),
            session: tokio::sync::Mutex::new(None),
            flex_emitter: Mutex::new(None),
            config: Mutex::new(config),
            config_path,
            sink,
        });

        // Start the local sidetone on its own audio-owning thread so the operator can
        // practice before pairing. The thread holds the non-`Send` cpal stream.
        let device = ToneDevice::from_setting(&settings.audio_device);
        let sidetone =
            Sidetone::spawn(settings.tone_hz, settings.tone_volume, device, Arc::clone(&shared));
        *shared.sidetone.lock().unwrap() = Some(sidetone);

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("tokio runtime");

        // The single send-worker thread drains the type-ahead / macro buffer with real CW
        // timing, keying every output through `drive_edge`. Spawned once for the controller's
        // life; it needs the runtime handle to pace tunnel frames.
        spawn_send_worker(Arc::clone(&shared), runtime.handle().clone());

        let controller = Self { shared, runtime, _clock: Arc::new(MonotonicClock) };
        controller.shared.emit_settings();
        controller.shared.emit_forward_rules();
        controller.shared.emit_flex_state();
        controller.emit_ports();
        controller.emit_audio_devices();
        controller
    }

    /// Re-emits settings, forward rules, flex state, ports, and audio devices. The UI calls
    /// this once it has subscribed to the event channel, because the startup emit in
    /// [`Self::start`] races the webview and is otherwise lost — the webview does not exist
    /// yet when `start` runs in Tauri's setup.
    pub fn request_state(&self) {
        self.shared.emit_settings();
        self.shared.emit_forward_rules();
        self.shared.emit_flex_state();
        self.emit_ports();
        self.emit_audio_devices();
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

    // ─── Transport + LAN identity ──────────────────────────────────────────────

    /// Selects the pairing transport: "wan" (relay + code) or "lan" (broadcast discovery).
    pub fn set_transport(&self, transport: &str) {
        self.shared.settings.lock().unwrap().transport = Transport::parse(transport);
        self.shared.emit_settings();
    }

    /// Sets the operator callsign used as the LAN beacon name.
    pub fn set_callsign(&self, callsign: &str) {
        self.shared.settings.lock().unwrap().callsign = callsign.to_string();
        self.shared.persist_identity_labels();
        self.shared.emit_settings();
    }

    /// Sets the position label (e.g. `20m`) appended to the callsign in the LAN beacon.
    pub fn set_keyer_id(&self, keyer_id: &str) {
        self.shared.settings.lock().unwrap().keyer_id = keyer_id.to_string();
        self.shared.persist_identity_labels();
        self.shared.emit_settings();
    }

    /// Starts (or stops) broadcasting this keyer's LAN beacon so a Station can find and pick
    /// it. When a Station picks us, we come up as the responder and the keying edge opens.
    /// Starting stops any WAN pairing first; advertising stops automatically once paired.
    pub fn lan_advertise(&self, on: bool) {
        if !on {
            self.disconnect();
            return;
        }
        self.disconnect();
        let (callsign, keyer_id) = {
            let s = self.shared.settings.lock().unwrap();
            (s.callsign.clone(), s.keyer_id.clone())
        };
        let shared = Arc::clone(&self.shared);
        shared.emit(ClientEvent::Connection { state: "connecting".into(), path: "none".into() });
        shared.emit(ClientEvent::Advertising { active: true });
        shared.log("info", "Advertising on the LAN; waiting for a station to connect…");
        let handle = self.runtime.spawn(async move {
            lan_advertise_and_await(shared).await;
        });
        self.runtime.block_on(async {
            *self.shared.advertise.lock().await = Some(handle);
        });
        // callsign/keyer_id captured for logging symmetry (the beacon reads them in the task).
        let _ = (callsign, keyer_id);
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

    /// Selects the sidetone output device. `None` (JS `null`) = system default; an empty
    /// string = "(None)" = sidetone off; any other string = that device by name.
    pub fn select_audio_device(&self, device: Option<String>) {
        self.shared.settings.lock().unwrap().audio_device = device;
        self.restart_sidetone();
        self.shared.emit_settings();
    }

    fn restart_sidetone(&self) {
        let (hz, vol) = {
            let s = self.shared.settings.lock().unwrap();
            (s.tone_hz, s.tone_volume)
        };
        let device = self.shared.tone_device();
        if let Some(tone) = self.shared.sidetone.lock().unwrap().as_ref() {
            tone.reconfigure(hz, vol, device);
        }
    }

    /// Lists output audio devices (name + default flag) for the sidetone picker.
    #[must_use]
    pub fn audio_devices() -> Vec<AudioDeviceView> {
        SidetoneEngine::output_devices_marked()
            .into_iter()
            .map(|(name, is_default)| AudioDeviceView { name, is_default })
            .collect()
    }

    fn emit_audio_devices(&self) {
        self.shared.emit(ClientEvent::AudioDevices { available: Self::audio_devices() });
    }

    /// Re-enumerates audio devices and pushes the list (for the UI refresh button / PnP).
    pub fn refresh_audio_devices(&self) {
        self.emit_audio_devices();
    }

    // ─── Ports ───────────────────────────────────────────────────────────────

    /// Lists available serial ports for the device pickers.
    #[must_use]
    pub fn serial_ports() -> Vec<String> {
        enumerate_ports()
            .map(|ports| ports.into_iter().map(|p| p.port_name).collect())
            .unwrap_or_default()
    }

    fn emit_ports(&self) {
        self.shared.emit(ClientEvent::Ports { available: Self::serial_ports() });
    }

    /// The serial ports we currently hold open (paddle poller running, keying output open,
    /// WinKeyer port selected). Used so reconciliation does not drop a working, in-use port
    /// that the OS hides from enumeration while it is busy.
    fn held_ports(&self) -> Vec<String> {
        let s = self.shared.settings.lock().unwrap();
        let mut held = Vec::new();
        if self.shared.keying.lock().unwrap().is_some() {
            if let Some(p) = &s.keying_port {
                held.push(p.clone());
            }
        }
        if self.shared.paddle.lock().unwrap().is_some() {
            if let Some(p) = &s.paddle_port {
                held.push(p.clone());
            }
        }
        if let Some(p) = &s.winkeyer_port {
            held.push(p.clone());
        }
        held
    }

    /// Re-enumerates serial ports and reconciles the three selections: any selected port no
    /// longer present (and not held open) is dropped to `None` and its handle released,
    /// without disturbing the others. Always emits the fresh port list; also refreshes audio
    /// devices (a USB sound device may have arrived/left on the same event).
    ///
    /// A port we *currently hold open* is never treated as missing even if it is absent from
    /// the enumeration: on Windows an exclusively-opened COM port is commonly hidden from
    /// `available_ports()` while in use, and dropping a working, in-use selection would snap
    /// it back to `(None)`.
    pub fn reconcile_ports(&self) {
        let mut present = Self::serial_ports();
        for held in self.held_ports() {
            if !present.contains(&held) {
                present.push(held);
            }
        }
        present.sort_by(|a, b| natural_port_key(a).cmp(&natural_port_key(b)));
        self.shared.emit(ClientEvent::Ports { available: present.clone() });
        self.emit_audio_devices();

        let (keying_gone, paddle_gone, winkeyer_gone) = {
            let s = self.shared.settings.lock().unwrap();
            (
                port_missing(&s.keying_port, &present),
                port_missing(&s.paddle_port, &present),
                port_missing(&s.winkeyer_port, &present),
            )
        };
        if !(keying_gone || paddle_gone || winkeyer_gone) {
            return;
        }

        if keying_gone {
            let name = self.shared.settings.lock().unwrap().keying_port.take();
            if let Some(n) = name {
                self.shared.log("warn", format!("Keying port {n} removed; set to (None)."));
            }
            self.select_keying_port(None);
        }
        if paddle_gone {
            let name = self.shared.settings.lock().unwrap().paddle_port.take();
            if let Some(n) = name {
                self.shared.log("warn", format!("Paddle port {n} removed; set to (None)."));
            }
            self.select_paddle_port(None);
        }
        if winkeyer_gone {
            let name = self.shared.settings.lock().unwrap().winkeyer_port.take();
            if let Some(n) = name {
                self.shared.log("warn", format!("WinKeyer port {n} removed; set to (None)."));
            }
            self.select_winkeyer_port(None);
        }
        self.shared.emit_settings();
    }

    /// Selects the serial port used to key a local rig's control line (DTR by default), or
    /// `None` to release it. This drives the physical key line in lock-step with the keyer,
    /// so a local rig keys alongside the remote one.
    pub fn select_keying_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().keying_port = port.clone();
        {
            let mut guard = self.shared.keying.lock().unwrap();
            *guard = None; // release any previous port first
            match port {
                Some(name) => {
                    let cfg = KeyingOutputConfig {
                        port_name: name.clone(),
                        key_line: KeyingLine::Dtr,
                        ..Default::default()
                    };
                    match SerialKeyingOutput::open(cfg) {
                        Ok(out) => {
                            *guard = Some(out);
                            self.shared.clear_error(control::KEYING);
                            self.shared.log("info", format!("Keying output on {name}."));
                        }
                        Err(e) => self.shared.fail(
                            control::KEYING,
                            format!("{name} not available"),
                            format!("Keying port {name} failed: {e}"),
                        ),
                    }
                }
                None => self.shared.clear_error(control::KEYING),
            }
        }
        self.emit_ports();
        self.shared.emit_settings();
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
                    self.shared.clear_error(control::PADDLE);
                    self.shared.log("info", format!("Paddle input on {name} (CTS=dit, DSR=dah, DCD=straight)."));
                }
                Err(e) => self.shared.fail(
                    control::PADDLE,
                    format!("{name} not available"),
                    format!("Paddle port {name} failed: {e}"),
                ),
            },
            None => {
                self.shared.clear_error(control::PADDLE);
                self.shared.log("info", "Paddle input cleared.");
            }
        }
        self.emit_ports();
        self.shared.emit_settings();
    }

    /// Records the selected WinKeyer port. NOTE: the WinKeyer protocol host is a later
    /// increment; this persists the choice only.
    pub fn select_winkeyer_port(&self, port: Option<String>) {
        self.shared.settings.lock().unwrap().winkeyer_port = port.clone();
        self.shared.clear_error(control::WINKEYER);
        match port {
            Some(name) => self.shared.log(
                "info",
                format!("WinKeyer port {name} selected (protocol host not yet wired)."),
            ),
            None => self.shared.log("info", "WinKeyer port cleared."),
        }
        self.emit_ports();
        self.shared.emit_settings();
    }

    // ─── Keying ──────────────────────────────────────────────────────────────

    /// Appends text (a typed character or a macro) to the send buffer. It is keyed with
    /// proper CW spacing by the single persistent send-worker thread, after whatever is
    /// already queued — so characters typed or pasted while a send is in flight chain on
    /// seamlessly. Playback fans to the sidetone, the local key line, the UI indicator, and
    /// (when paired) the tunnel through `drive_edge`.
    pub fn send_cw(&self, text: &str) {
        self.shared.send.push(text);
    }

    /// Sends the standard test string, matching the C# "Test TX" button.
    pub fn test_tx(&self) {
        self.send_cw("VVV TESTING");
    }

    /// Immediately stops sending: flushes the buffer and drops the in-progress character
    /// (ESC / WinKeyer "clear buffer"). The key is forced up.
    pub fn abort_send(&self) {
        self.shared.send.abort();
    }

    // ─── Ham Router: forward rules ─────────────────────────────────────────────

    /// Adds a forward rule. `protocol` is "tcp" or "udp". Ports are validated: a rule using a
    /// reserved RWK port (7373/41373), or a duplicate client port on the same protocol, is
    /// rejected with a logged warning and no change. New rules default to **disabled** so the
    /// operator enables them deliberately (matching the C#).
    #[allow(clippy::too_many_arguments)]
    pub fn add_forward_rule(
        &self,
        name: &str,
        protocol: &str,
        client_port: u16,
        station_port: u16,
        station_target_address: &str,
    ) {
        let proto = if protocol.eq_ignore_ascii_case("udp") { "udp" } else { "tcp" };
        if is_reserved_port(client_port) || is_reserved_port(station_port) {
            self.shared.log("warn", "That port is reserved for RWK control traffic.");
            return;
        }
        {
            let mut config = self.shared.config.lock().unwrap();
            if config
                .forwards
                .iter()
                .any(|r| r.protocol == proto && r.client_port == client_port)
            {
                drop(config);
                self.shared.log("warn", format!("A {proto} rule already uses client port {client_port}."));
                return;
            }
            let id = format!("{proto}-{client_port}-{}", config.forwards.len() + 1);
            config.forwards.push(ForwardRuleConfig {
                id,
                name: name.to_string(),
                protocol: proto.to_string(),
                client_port,
                station_port,
                enabled: false,
                station_target_address: station_target_address.to_string(),
            });
        }
        self.shared.persist_config();
        self.shared.emit_forward_rules();
        self.push_and_apply_rules();
    }

    /// Removes the forward rule with `id`.
    pub fn remove_forward_rule(&self, id: &str) {
        {
            let mut config = self.shared.config.lock().unwrap();
            config.forwards.retain(|r| r.id != id);
        }
        self.shared.persist_config();
        self.shared.emit_forward_rules();
        self.push_and_apply_rules();
    }

    /// Enables or disables the forward rule with `id`.
    pub fn set_forward_rule_enabled(&self, id: &str, enabled: bool) {
        {
            let mut config = self.shared.config.lock().unwrap();
            if let Some(rule) = config.forwards.iter_mut().find(|r| r.id == id) {
                rule.enabled = enabled;
            }
        }
        self.shared.persist_config();
        self.shared.emit_forward_rules();
        self.push_and_apply_rules();
    }

    /// Re-emits the current rule table to the UI (for an explicit refresh).
    pub fn refresh_forward_rules(&self) {
        self.shared.emit_forward_rules();
    }

    /// Pushes the current rules to the Station (if paired) and restarts the Client's own
    /// forwards to match. Runs on the controller runtime; non-blocking for the caller.
    fn push_and_apply_rules(&self) {
        let shared = Arc::clone(&self.shared);
        let rules = self.shared.forward_rules();
        self.runtime.spawn(async move {
            push_rules(&shared, &rules).await;
        });
    }

    // ─── Ham Router: FlexRadio discovery re-emission ───────────────────────────

    /// Enables or disables FlexRadio discovery re-emission on the Client LAN. Enabling creates
    /// the host-side emitter and auto-adds the Flex forward rules (TCP 4992 command, UDP 4991
    /// stream) if absent; disabling removes the emitter and those rules. The real radio IP is
    /// filled into `station_target_address` when the first discovery announcement arrives.
    pub fn set_flex_discovery(&self, enabled: bool) {
        if enabled {
            match DiscoveryEmitter::new() {
                Ok(emitter) => {
                    *self.shared.flex_emitter.lock().unwrap() = Some(emitter);
                    self.ensure_flex_rules();
                    self.shared.log("info", "FlexRadio discovery re-emission enabled.");
                }
                Err(e) => {
                    self.shared.log("warn", format!("Flex re-emission unavailable: {e}"));
                    self.shared.emit_flex_state();
                    return;
                }
            }
        } else {
            *self.shared.flex_emitter.lock().unwrap() = None;
            self.remove_flex_rules();
            self.shared.log("info", "FlexRadio discovery re-emission disabled.");
        }
        self.shared.persist_config();
        self.shared.emit_flex_state();
        self.shared.emit_forward_rules();
        self.push_and_apply_rules();
    }

    /// Ensures the two Flex forward rules exist (TCP 4992, UDP 4991), enabled.
    fn ensure_flex_rules(&self) {
        let mut config = self.shared.config.lock().unwrap();
        for (proto, port, label) in [
            ("tcp", 4992u16, "[Flex] SmartSDR Command"),
            ("udp", 4991u16, "[Flex] VITA-49 Stream"),
        ] {
            if !config.forwards.iter().any(|r| r.protocol == proto && r.client_port == port) {
                config.forwards.push(ForwardRuleConfig {
                    id: format!("flex-{proto}-{port}"),
                    name: label.to_string(),
                    protocol: proto.to_string(),
                    client_port: port,
                    station_port: port,
                    enabled: true,
                    station_target_address: String::new(), // filled from the first announce
                });
            }
        }
    }

    /// Removes the auto-managed Flex forward rules.
    fn remove_flex_rules(&self) {
        let mut config = self.shared.config.lock().unwrap();
        config.forwards.retain(|r| !r.id.starts_with("flex-"));
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

        shared.emit(ClientEvent::Connection { state: "connecting".into(), path: "none".into() });

        self.runtime.spawn(async move {
            // Clone the PERSISTED config so the WireGuard identity is stable across runs,
            // then layer the relay and pairing code on top. The peer key is discovered by
            // rendezvous, so no key paste is needed. The forward rules travel to the Station
            // over the control channel, so they are not needed in the connect config.
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
            bring_up_client(&shared, connected).await;
        });
    }

    /// Tears the session, control channel, LAN advertiser and keying edge down.
    pub fn disconnect(&self) {
        *self.shared.transport.lock().unwrap() = None;
        let shared = Arc::clone(&self.shared);
        self.runtime.block_on(async move {
            if let Some(task) = shared.advertise.lock().await.take() {
                task.abort();
            }
            *shared.control.lock().await = None;
            if let Some(mut session) = shared.session.lock().await.take() {
                session.shutdown().await;
            }
        });
        self.shared.emit(ClientEvent::Advertising { active: false });
        self.shared
            .emit(ClientEvent::Connection { state: "disconnected".into(), path: "none".into() });
        self.shared.log("info", "Unpaired.");
    }
}

/// Shared post-connect wiring for the Client (operating position), used by both the WAN
/// `connect` and the LAN advertise paths: start the session, open the keying edge, open the
/// control channel and push the forward rules, and report connected.
async fn bring_up_client(shared: &Arc<Shared>, connected: tunnel::Connected) {
    let path = format!("{:?}", connected.path).to_lowercase();
    let config = shared.config.lock().unwrap().clone();
    let session = match Session::start(connected, &config) {
        Ok(s) => s,
        Err(e) => return fail(shared, format!("session failed: {e}")),
    };
    let net = session.handle();

    let conn = match net.open_udp(EDGE_PORT).await {
        Ok(c) => c,
        Err(e) => return fail(shared, format!("edge open failed: {e}")),
    };
    let transport = Arc::new(EdgeTransport::over_tunnel(conn));
    let peer = std::net::SocketAddr::new(
        std::net::IpAddr::V4(Role::Client.peer_inner_address()),
        EDGE_PORT,
    );
    transport.set_peer(peer).await;

    // Open the control channel to the Station and push the current forward rules.
    let station_inner = std::net::IpAddr::V4(Role::Client.peer_inner_address());
    match ControlLink::connect(&net, station_inner).await {
        Ok(link) => {
            let rules = shared.config.lock().unwrap().forwards.clone();
            if let Err(e) = link.send(&ControlMessage::ForwardRules { rules }).await {
                shared.log("warn", format!("Could not push forward rules: {e}"));
            }
            *shared.control.lock().await = Some(link);
            spawn_discovery_receiver(Arc::clone(shared));
        }
        Err(e) => shared.log("warn", format!("Control channel unavailable: {e}")),
    }

    *shared.transport.lock().unwrap() = Some(transport);
    *shared.session.lock().await = Some(session);
    // The epoch is intentionally NOT bumped here. Both edge sources (the paddle poller and
    // the type-ahead send worker) sample `shared.epoch` independently, so a bump would make
    // them disagree and the Station would F4 whichever source sent second. The station adopts
    // the first frame's epoch of each fresh session and re-anchors on reconnect (it tears the
    // replayer down and back up per pairing), so a stable epoch is both correct and necessary.
    shared.emit(ClientEvent::Connection { state: "connected".into(), path: path.clone() });
    shared.log("info", format!("Paired over the {path} path."));
}

/// The operating-position LAN flow: a **persistent responder**. The operator arms it once
/// (Advertise); it then keeps answering Stations until unpaired. Each cycle binds a fresh
/// WireGuard socket, broadcasts the beacon with that port, waits for a Station to pick this
/// keyer, and brings the link up as the responder. When a later pick supersedes it (e.g. the
/// Station reconnects after a drop), the loop re-binds and re-advertises — so the keyer
/// reconnects without the operator re-arming. The Station resolves this keyer's *current* port
/// from the live beacon at connect time, so a re-bound port is never stale.
///
/// Runs as one task; aborting it (unpair) stops the beacon and the wait.
async fn lan_advertise_and_await(shared: Arc<Shared>) {
    let (identity, callsign, keyer_id) = {
        let config = shared.config.lock().unwrap();
        let s = shared.settings.lock().unwrap();
        match config.identity.to_identity() {
            Ok(id) => (id, s.callsign.clone(), s.keyer_id.clone()),
            Err(e) => return fail(&shared, format!("identity error: {e}")),
        }
    };

    loop {
        let (socket, wg_port) = match tunnel::lan_bind().await {
            Ok(pair) => pair,
            Err(e) => return fail(&shared, format!("LAN bind failed: {e}")),
        };

        let beacon = rwk_core::engine::lan_discovery::Beacon {
            callsign: callsign.clone(),
            keyer_id: keyer_id.clone(),
            public_key: identity.public_bytes(),
            wg_port,
        };
        let advertiser = match rwk_core::engine::lan_discovery::LanAdvertiser::new(beacon) {
            Ok(a) => a,
            Err(e) => return fail(&shared, format!("LAN advertise failed: {e}")),
        };
        let beacon_task = tokio::spawn(advertiser.run());
        shared.emit(ClientEvent::Advertising { active: true });

        let connected = match tunnel::lan_await_initiator(socket, &identity).await {
            Ok(c) => c,
            Err(e) => {
                beacon_task.abort();
                shared.log("warn", format!("LAN listen cycle ended: {e}; re-advertising."));
                continue;
            }
        };
        beacon_task.abort();
        shared.emit(ClientEvent::Advertising { active: false });
        bring_up_client(&shared, connected).await;
    }
}

/// Pushes `rules` to the Station over the control channel (if paired) and restarts the
/// Client's own forward tasks to match. Safe to call when not paired (does nothing).
async fn push_rules(shared: &Arc<Shared>, rules: &[ForwardRuleConfig]) {
    // Push to the Station.
    if let Some(link) = shared.control.lock().await.as_ref() {
        if let Err(e) = link.send(&ControlMessage::ForwardRules { rules: rules.to_vec() }).await {
            shared.log("warn", format!("Could not push forward rules: {e}"));
        }
    }
    // Restart the Client's own forwards (binds local ports for enabled rules).
    if let Some(session) = shared.session.lock().await.as_mut() {
        let started = session.apply_forwards(rules).await;
        shared.log("info", format!("{started} forward rule(s) active locally."));
    }
}

/// Spawns a task that receives FlexRadio discovery announcements on the control channel and
/// re-broadcasts them on the Client LAN. Runs until the control channel closes.
fn spawn_discovery_receiver(shared: Arc<Shared>) {
    tokio::spawn(async move {
        loop {
            // Take the recv side under the lock for one message, then release so sends can
            // interleave. The control link is single-reader (this task), single-writer.
            let message = {
                let mut guard = shared.control.lock().await;
                match guard.as_mut() {
                    Some(link) => link.recv().await,
                    None => return, // disconnected
                }
            };
            match message {
                Ok(Some(ControlMessage::DiscoveryAnnounce { payload })) => {
                    handle_discovery_announce(&shared, &payload);
                }
                Ok(Some(_)) => {} // the Client ignores other message types
                Ok(None) | Err(_) => return, // closed or error
            }
            // Small yield so a concurrent send can grab the lock.
            tokio::time::sleep(std::time::Duration::from_millis(1)).await;
        }
    });
}

/// Rewrites a captured Flex discovery packet to the local forwarded endpoint and re-broadcasts
/// it on the Client LAN. Also learns the radio's real IP and fills it into the Flex rules'
/// `station_target_address` so the Station dials the right device.
fn handle_discovery_announce(shared: &Arc<Shared>, payload_b64: &str) {
    let Ok(packet) = base64_decode(payload_b64) else {
        return;
    };
    // Learn the radio's real address from the packet and set it on the Flex rules.
    if let Ok(radio) = flex_vita::parse(&packet) {
        let mut changed = false;
        {
            let mut config = shared.config.lock().unwrap();
            for rule in config.forwards.iter_mut().filter(|r| r.id.starts_with("flex-")) {
                let addr = radio.address.to_string();
                if rule.station_target_address != addr {
                    rule.station_target_address = addr;
                    changed = true;
                }
            }
        }
        if changed {
            shared.persist_config();
            shared.emit_forward_rules();
        }
    }
    // Re-broadcast rewritten to the local Flex command port (4992) on the Client LAN.
    if let Some(emitter) = shared.flex_emitter.lock().unwrap().as_ref() {
        let _ = emitter.rebroadcast(&packet, std::net::Ipv4Addr::LOCALHOST, 4992);
    }
}

/// Reports a pairing failure through the event sink.
fn fail(shared: &Arc<Shared>, message: String) {
    shared.log("warn", message);
    shared.emit(ClientEvent::Connection { state: "disconnected".into(), path: "none".into() });
}

impl Drop for ClientController {
    fn drop(&mut self) {
        // Stop the send worker so its thread exits, then tear the link down.
        self.shared.send.shutdown();
        self.disconnect();
    }
}

// ─── Send worker: drains the type-ahead / macro buffer with real CW timing ─────

/// Spawns the single send-worker thread. It waits for buffered characters, keys them through
/// every output (`drive_edge`) with correct element/character/word spacing, and picks up
/// characters typed or pasted during a send on the fly. An abort (ESC) drops the queue and
/// the in-progress character immediately. Spawned once for the controller's life.
fn spawn_send_worker(shared: Arc<Shared>, rt: tokio::runtime::Handle) {
    std::thread::Builder::new()
        .name("rwk-sender".into())
        .spawn(move || send_worker(&shared, &rt))
        .expect("spawn send worker");
}

/// The send worker keys the buffer as a **continuous element stream**, one character at a
/// time, managing the inter-character and word gaps itself. Characters typed *while sending*
/// chain on seamlessly with correct spacing, because the running `position` stays ahead of
/// real time whenever the buffer is non-empty, so the next character is scheduled a proper
/// inter-character gap after the previous one — never jammed up against it.
///
/// Each edge is fanned through [`drive_edge`] (sidetone + local key line + UI indicator +
/// tunnel), exactly the path the paddle poller uses, so a typed macro and a paddle key
/// identically — including paired keying over the tunnel.
fn send_worker(shared: &Arc<Shared>, rt: &tokio::runtime::Handle) {
    // Element timing is sub-element-critical: without a raised priority and a 1 ms timer the
    // OS scheduler can overshoot a ~17 ms inter-character gap by a whole quantum and smear the
    // spacing. Match the paddle loop's scheduling protection.
    let _priority = ThreadPriorityGuard::raise_time_critical();
    let _timer = TimerResolutionGuard::raise(1);
    let clock = MonotonicClock;
    let hz = clock.frequency();

    // Running schedule cursor and whether a character has been keyed since the last gap, so
    // the next character knows to insert an inter-character gap.
    let mut position = clock.now();
    let mut prev_emitted = false;
    let mut sending = false;

    // The tunnel edge sender, re-anchored to `now` with the current epoch each time a fresh
    // sending burst begins (so a re-pairing between bursts is honored).
    let mut sender = EdgeSender::new(*shared.epoch.lock().unwrap(), hz, clock.now());

    let aborting = || shared.send.state.lock().unwrap_or_else(|e| e.into_inner()).abort;

    loop {
        // Pop the next character, blocking until one is available.
        let next: Option<char> = {
            let mut st = shared.send.state.lock().unwrap_or_else(|e| e.into_inner());
            loop {
                if st.shutdown {
                    return;
                }
                if st.abort {
                    st.abort = false;
                    st.queue.clear();
                    // Reset the stream so the next character starts fresh.
                    drop(st);
                    position = clock.now();
                    prev_emitted = false;
                    if sending {
                        let tone = shared.sidetone.lock().unwrap().clone();
                        drive_edge(shared, rt, tone.as_ref(), &mut sender, false, clock.now());
                        shared.emit(ClientEvent::Sending { active: false });
                        sending = false;
                    }
                    st = shared.send.state.lock().unwrap_or_else(|e| e.into_inner());
                    continue;
                }
                if let Some(ch) = st.queue.pop_front() {
                    break Some(ch);
                }
                // Nothing queued: drop the SEND indicator and wait for more input.
                if sending {
                    drop(st);
                    shared.emit(ClientEvent::Sending { active: false });
                    sending = false;
                    st = shared.send.state.lock().unwrap_or_else(|e| e.into_inner());
                }
                st = shared.send.cv.wait(st).unwrap_or_else(|e| e.into_inner());
            }
        };
        let Some(ch) = next else { continue };

        let (wpm, weight) = {
            let s = shared.settings.lock().unwrap();
            (s.wpm, s.weight)
        };
        let timing = match KeyerElementTiming::from_speed(wpm, weight, hz) {
            Ok(t) => t,
            Err(e) => {
                shared.log("warn", format!("CW timing error: {e}"));
                continue;
            }
        };

        // Re-anchor the schedule cursor to "now" whenever it has fallen behind real time
        // (after any idle, and under scheduler slop). We do NOT clear `prev_emitted` here: a
        // character typed a moment after the previous one must still get its inter-character
        // gap, exactly as a pasted string does, or separate keystrokes would merge.
        // `prev_emitted` is only reset on a real word space, an abort, or startup.
        let now = clock.now();
        if position < now {
            position = now;
        }

        if !sending {
            // Fresh burst: re-anchor the tunnel sender to now with the current epoch.
            sender = EdgeSender::new(*shared.epoch.lock().unwrap(), hz, now);
            shared.emit(ClientEvent::Sending { active: true });
            sending = true;
        }

        // Word separator: advance by a word gap (only between words) and reset.
        if MorseTable::is_word_separator(ch) {
            if prev_emitted {
                position += timing.word_gap_ticks();
            }
            prev_emitted = false;
            continue;
        }

        let Some(pattern) = MorseTable::pattern(ch) else {
            continue; // unkeyable character: skip
        };

        if prev_emitted {
            position += timing.inter_character_gap_ticks();
        }

        let tone = shared.sidetone.lock().unwrap().clone();

        // Key each element of the character with intra-element gaps.
        let mut first_element = true;
        for symbol in pattern.chars() {
            if !first_element {
                position += timing.gap_ticks;
            }
            first_element = false;

            let element = if symbol == '-' { KeyerElement::Dah } else { KeyerElement::Dit };
            let duration = timing.ticks_for(element);

            // Key down at `position`, up at `position + duration`.
            HybridWaiter::wait_until_ticks(&clock, position, &aborting);
            if aborting() {
                break;
            }
            drive_edge(shared, rt, tone.as_ref(), &mut sender, true, position);

            HybridWaiter::wait_until_ticks(&clock, position + duration, &aborting);
            drive_edge(shared, rt, tone.as_ref(), &mut sender, false, position + duration);
            position += duration;

            if aborting() {
                break;
            }
        }
        prev_emitted = true;
    }
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
    // 1. Local sidetone (via its own audio thread) — immediate, never waits on the network.
    if let Some(tone) = tone {
        tone.key(key_down);
    }
    // 2. Local key line, if a port is open.
    if let Some(out) = shared.keying.lock().unwrap().as_mut() {
        let _ = out.apply(key_down, false);
    }
    // 3. UI indicator.
    shared.emit(ClientEvent::KeyState { down: key_down });
    // 4. Tunnel transport when paired — fire-and-forget. Spawning the send (rather than
    //    blocking on it) keeps CW timing decoupled from the tunnel: a stalled or backed-up
    //    link can never latch the key down between a key-down edge and its key-up. The frame
    //    carries its own timestamp, so the Station reconstructs spacing despite send jitter.
    let transport = shared.transport.lock().unwrap().clone();
    if let Some(transport) = transport {
        let event = EdgeEvent { timestamp_ticks, key_down, source: EdgeSource::Paddle };
        let frame = sender.frame_for(event);
        rt.spawn(async move {
            let _ = transport.send_frame(&frame).await;
        });
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

/// Whether `selected` names a port that is not in `present` (and is `Some`).
fn port_missing(selected: &Option<String>, present: &[String]) -> bool {
    matches!(selected, Some(name) if !present.iter().any(|p| p == name))
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
