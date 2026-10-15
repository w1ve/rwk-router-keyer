import { Fragment, useEffect, useState } from "react";
import { api } from "./api.js";

const MODES = [
  ["iambicB", "Iambic B"],
  ["iambicA", "Iambic A"],
  ["ultimatic", "Ultimatic"],
  ["bug", "Bug"],
  ["straight", "Straight"],
];

const DEFAULT_MACROS = [
  { name: "CQ", text: "CQ DE MYCALL" },
  { name: "599", text: "599" },
  { name: "TU", text: "TU" },
  { name: "73", text: "73" },
  { name: "MYCALL", text: "MYCALL" },
  { name: "QRL?", text: "QRL?" },
  { name: "?", text: "?" },
  { name: "QRX", text: "QRX" },
];

function loadMacros() {
  try {
    const raw = localStorage.getItem("rwk.macros");
    if (raw) return JSON.parse(raw);
  } catch {
    /* fall through to defaults */
  }
  return DEFAULT_MACROS;
}

// Is this platform event-driven for device changes? On Windows the backend pushes updated
// device/port lists on WM_DEVICECHANGE, so no manual Refresh is needed. Elsewhere we show a
// Refresh button. navigator.userAgent carries the OS in the webview.
const IS_WINDOWS = /windows/i.test(navigator.userAgent);

// The Keyer tab: Keyer | Sidetone | Inputs panels across the top, and the Pair-with-Station
// panel below — mirroring the C# client's layout, but with the Rust pairing-code model.
//
// `ports`, `audioDevices` and `errors` are pushed from the backend (App.jsx forwards the
// Ports / AudioDevices / Error events), so there is no manual polling: a plug/unplug updates
// the lists live, and per-control failures render in red under the offending control.
export default function KeyerTab({
  settings,
  connection,
  advertising,
  ports = [],
  audioDevices = [],
  errors = {},
}) {
  // Keyer
  const [wpm, setWpm] = useState(settings.wpm);
  const [weight, setWeight] = useState(settings.weight);
  const [mode, setMode] = useState(settings.mode);
  const [paddleReverse, setPaddleReverse] = useState(settings.paddleReverse);

  // Sidetone
  const [toneHz, setToneHz] = useState(settings.toneHz);
  const [toneVol, setToneVol] = useState(settings.toneVolume);

  // Inputs — reflected from the backend-echoed settings (no local source of truth).
  const keyingPort = settings.keyingPort || "";
  const paddlePort = settings.paddlePort || "";
  const winkeyerPort = settings.winkeyerPort || "";

  // Macros + type-ahead
  const [macros, setMacros] = useState(loadMacros);
  const [editing, setEditing] = useState(false);
  const [typeAhead, setTypeAhead] = useState("");

  // Pairing
  const [relay, setRelay] = useState("107.172.138.103:9000");
  const [code, setCode] = useState("");
  const [transport, setTransport] = useState(settings.transport || "wan");
  const [callsign, setCallsign] = useState(settings.callsign || "");
  const [keyerId, setKeyerId] = useState(settings.keyerId || "");

  // Reflect backend-echoed settings (e.g. first load) into local state.
  useEffect(() => {
    setWpm(settings.wpm);
    setWeight(settings.weight);
    setMode(settings.mode);
    setPaddleReverse(settings.paddleReverse);
    setToneHz(settings.toneHz);
    setToneVol(settings.toneVolume);
    if (settings.transport) setTransport(settings.transport);
    setCallsign(settings.callsign || "");
    setKeyerId(settings.keyerId || "");
  }, [settings]);

  const paired = connection.state === "connected";
  const connecting = connection.state === "connecting";

  // Sidetone device selection model: settings.audioDevice is null (follow system default),
  // "" (off), or a device name. When null, show the actual default device by name so the user
  // sees what they are hearing through. "(None)" = off = "".
  const defaultDevice = audioDevices.find((d) => d.isDefault)?.name || "";
  const audioValue = settings.audioDevice == null ? defaultDevice : settings.audioDevice;

  function saveMacros(next) {
    setMacros(next);
    localStorage.setItem("rwk.macros", JSON.stringify(next));
  }

  // Type-ahead: any text added to the box — typed OR pasted — is appended to the send buffer
  // and keyed with proper CW spacing. We diff against what we have already sent so a paste of
  // a whole sentence enqueues the whole sentence (not just one key). ESC aborts + clears.
  function onTypeAheadChange(e) {
    const next = e.target.value;
    if (next.length > typeAhead.length && next.startsWith(typeAhead)) {
      const added = next.slice(typeAhead.length);
      api.sendCw(added.toUpperCase());
    } else if (next.length > typeAhead.length) {
      // Non-append edit (rare): send the added remainder conservatively.
      api.sendCw(next.toUpperCase());
    }
    setTypeAhead(next);
  }

  function onTypeAheadKey(e) {
    if (e.key === "Escape") {
      e.preventDefault();
      api.abortSend();
      setTypeAhead("");
    }
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <div className="panel-row">
        {/* ─── Keyer ─────────────────────────────────────── */}
        <div className="panel">
          <h3>Keyer</h3>
          <div className="row spread" style={{ alignItems: "flex-end", marginBottom: 10 }}>
            <div className="wpm">
              {wpm} <small>WPM</small>
            </div>
          </div>
          <input
            type="range"
            min={5}
            max={60}
            value={wpm}
            onChange={(e) => {
              const v = Number(e.target.value);
              setWpm(v);
              api.setSpeed(v);
            }}
          />
          <div className="field" style={{ marginTop: 10 }}>
            <label>Weight: {weight}%</label>
            <input
              type="range"
              min={25}
              max={75}
              value={weight}
              onChange={(e) => {
                const v = Number(e.target.value);
                setWeight(v);
                api.setWeight(v);
              }}
            />
          </div>
          <div className="field">
            <label>Mode</label>
            <select
              value={mode}
              onChange={(e) => {
                setMode(e.target.value);
                api.setMode(e.target.value);
              }}
            >
              {MODES.map(([val, label]) => (
                <option key={val} value={val}>
                  {label}
                </option>
              ))}
            </select>
          </div>
          <label className="row" style={{ gap: 6 }}>
            <input
              type="checkbox"
              checked={paddleReverse}
              onChange={(e) => {
                setPaddleReverse(e.target.checked);
                api.setPaddleReverse(e.target.checked);
              }}
            />
            Paddle reverse
          </label>

          {/* Macros */}
          <div style={{ marginTop: 12 }}>
            <div className="row spread" style={{ marginBottom: 6 }}>
              <label style={{ color: "var(--text-dim)", fontSize: 12 }}>CW Macros</label>
              <button onClick={() => setEditing(true)} style={{ padding: "2px 10px" }}>
                Edit
              </button>
            </div>
            <div className="macros">
              {macros.map((m, i) => (
                <button key={i} onClick={() => m.text && api.sendCw(m.text)} title={m.text}>
                  {m.name || `F${i + 1}`}
                </button>
              ))}
            </div>
          </div>

          {/* Type-ahead + Test TX */}
          <div className="row" style={{ marginTop: 10, gap: 6 }}>
            <input
              type="text"
              placeholder="Type or paste CW (sent as entered) — ESC to stop…"
              value={typeAhead}
              onChange={onTypeAheadChange}
              onKeyDown={onTypeAheadKey}
              style={{ flex: 1 }}
            />
            <button onClick={() => api.testTx()}>Test TX</button>
          </div>
        </div>

        {/* ─── Sidetone ──────────────────────────────────── */}
        <div className="panel">
          <h3>Sidetone</h3>
          <div className="field">
            <label className="row spread">
              <span>Device</span>
              {!IS_WINDOWS && (
                <button className="mini" onClick={() => api.refreshAudioDevices()}>
                  Refresh
                </button>
              )}
            </label>
            <select
              value={audioValue}
              onChange={(e) => {
                const v = e.target.value;
                // "" -> off; a name equal to the default device -> null (follow default);
                // any other name -> that device.
                const device = v === "" ? "" : v === defaultDevice ? null : v;
                api.selectAudioDevice(device);
              }}
            >
              <option value="">(None — sidetone off)</option>
              {audioDevices.map((d) => (
                <option key={d.name} value={d.name}>
                  {d.isDefault ? `${d.name} (default)` : d.name}
                </option>
              ))}
              {/* keep a stale named selection visible until the next refresh */}
              {settings.audioDevice &&
                !audioDevices.some((d) => d.name === settings.audioDevice) && (
                  <option value={settings.audioDevice}>{settings.audioDevice}</option>
                )}
            </select>
            <ErrorLine msg={errors.audio} />
          </div>
          <div className="field">
            <label>Frequency: {toneHz} Hz</label>
            <input
              type="range"
              min={400}
              max={1000}
              step={10}
              value={toneHz}
              onChange={(e) => {
                const v = Number(e.target.value);
                setToneHz(v);
                api.setToneFrequency(v);
              }}
            />
          </div>
          <div className="field">
            <label>Volume: {toneVol}%</label>
            <input
              type="range"
              min={0}
              max={100}
              value={toneVol}
              onChange={(e) => {
                const v = Number(e.target.value);
                setToneVol(v);
                api.setToneVolume(v);
              }}
            />
          </div>
        </div>

        {/* ─── Inputs ────────────────────────────────────── */}
        <div className="panel">
          <div className="row spread" style={{ marginBottom: 8 }}>
            <h3 style={{ margin: 0 }}>Inputs</h3>
            {!IS_WINDOWS && (
              <button className="mini" onClick={() => api.serialPorts()}>
                Refresh
              </button>
            )}
          </div>
          <PortSelect
            label="Keying out (rig key line)"
            value={keyingPort}
            ports={ports}
            error={errors.keying}
            onChange={(p) => api.selectKeyingPort(p || null)}
          />
          <PortSelect
            label="Paddle input (CTS=dit, DSR=dah, DCD=straight)"
            value={paddlePort}
            ports={ports}
            error={errors.paddle}
            onChange={(p) => api.selectPaddlePort(p || null)}
          />
          <PortSelect
            label="WinKeyer (not yet active)"
            value={winkeyerPort}
            ports={ports}
            error={errors.winkeyer}
            onChange={(p) => api.selectWinkeyerPort(p || null)}
          />
          <PttButton />
        </div>
      </div>

      {/* ─── Pair with Station ──────────────────────────── */}
      <div className="panel">
        <h3>Pair with Station</h3>
        <div className="row" style={{ gap: 10, flexWrap: "wrap", alignItems: "flex-end" }}>
          <div>
            <label style={{ display: "block", fontSize: 12, color: "var(--text-dim)", marginBottom: 4 }}>
              Transport
            </label>
            <div className="segmented" style={paired || connecting ? { opacity: 0.5, pointerEvents: "none" } : null}>
              <button
                className={transport === "wan" ? "active" : ""}
                onClick={() => {
                  setTransport("wan");
                  api.setTransport("wan");
                }}
              >
                WAN (Relay)
              </button>
              <button
                className={transport === "lan" ? "active" : ""}
                onClick={() => {
                  setTransport("lan");
                  api.setTransport("lan");
                }}
              >
                LAN
              </button>
            </div>
          </div>

          {transport === "wan" ? (
            <>
              <div className="field" style={{ marginBottom: 0 }}>
                <label>Relay (host:port)</label>
                <input
                  type="text"
                  value={relay}
                  onChange={(e) => setRelay(e.target.value)}
                  disabled={paired || connecting}
                  style={{ width: 180 }}
                />
              </div>
              <div className="field" style={{ marginBottom: 0 }}>
                <label>Pairing code</label>
                <input
                  type="text"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder="shared code"
                  disabled={paired || connecting}
                  style={{ width: 140 }}
                />
              </div>
              <div style={{ alignSelf: "flex-end" }}>
                {paired ? (
                  <button onClick={() => api.disconnect()} style={{ background: "#5a2d2d" }}>
                    Unpair
                  </button>
                ) : (
                  <button
                    onClick={() => api.connect(relay, code)}
                    disabled={connecting || !code.trim() || !relay.trim()}
                    style={{ background: "var(--accent-dim)", borderColor: "var(--accent)" }}
                  >
                    {connecting ? "Pairing…" : "Pair with Station"}
                  </button>
                )}
              </div>
            </>
          ) : (
            <>
              <div className="field" style={{ marginBottom: 0 }}>
                <label>Station Callsign</label>
                <input
                  type="text"
                  value={callsign}
                  placeholder="Station Callsign"
                  disabled={paired || connecting}
                  onChange={(e) => {
                    setCallsign(e.target.value);
                    api.setCallsign(e.target.value);
                  }}
                  style={{ width: 130 }}
                />
              </div>
              <div className="field" style={{ marginBottom: 0 }}>
                <label>Keyer ID</label>
                <input
                  type="text"
                  value={keyerId}
                  placeholder="Keyer ID (20m, etc.)"
                  disabled={paired || connecting}
                  onChange={(e) => {
                    setKeyerId(e.target.value);
                    api.setKeyerId(e.target.value);
                  }}
                  style={{ width: 150 }}
                />
              </div>
              <div style={{ alignSelf: "flex-end" }}>
                {paired || connecting ? (
                  <button onClick={() => api.lanAdvertise(false)} style={{ background: "#5a2d2d" }}>
                    {connecting ? "Stop" : "Unpair"}
                  </button>
                ) : (
                  <button
                    onClick={() => api.lanAdvertise(true)}
                    disabled={!callsign.trim()}
                    style={{ background: "var(--accent-dim)", borderColor: "var(--accent)" }}
                  >
                    Advertise
                  </button>
                )}
              </div>
            </>
          )}
        </div>
        <p style={{ color: "var(--text-dim)", fontSize: 12, marginBottom: 0 }}>
          {transport === "wan"
            ? "Both ends enter the same relay and pairing code — no key exchange, no network setup."
            : advertising
              ? "Advertising on the LAN — pick this keyer on the Station. No relay, no internet."
              : "Enter your callsign (and optional ID), then Advertise so the Station can find and pick this keyer."}
        </p>
      </div>

      {editing && (
        <MacroEditor
          macros={macros}
          onSave={(next) => {
            saveMacros(next);
            setEditing(false);
          }}
          onCancel={() => setEditing(false)}
        />
      )}
    </div>
  );
}

function ErrorLine({ msg }) {
  if (!msg) return null;
  return (
    <div style={{ color: "var(--bad, #e06c6c)", fontSize: 12, marginTop: 4 }}>{msg}</div>
  );
}

function PortSelect({ label, value, ports, error, onChange }) {
  // Keep the current value visible even if it is not in the live list (busy/hidden port).
  const list = value && !ports.includes(value) ? [...ports, value] : ports;
  return (
    <div className="field">
      <label>{label}</label>
      <select value={value || ""} onChange={(e) => onChange(e.target.value)}>
        <option value="">(None)</option>
        {list.map((p) => (
          <option key={p} value={p}>
            {p}
          </option>
        ))}
      </select>
      <ErrorLine msg={error} />
    </div>
  );
}

// Momentary PTT: assert on press, release on release/leave. (PTT control messages are a
// later increment; for now the button reflects intent and will drive the control channel
// once the Station side lands.)
function PttButton() {
  const [down, setDown] = useState(false);
  return (
    <button
      className={`pttbtn ${down ? "active" : ""}`}
      style={{ marginTop: 8 }}
      onMouseDown={() => setDown(true)}
      onMouseUp={() => setDown(false)}
      onMouseLeave={() => setDown(false)}
      onMouseOut={() => setDown(false)}
    >
      PTT
    </button>
  );
}

function MacroEditor({ macros, onSave, onCancel }) {
  const [draft, setDraft] = useState(macros.map((m) => ({ ...m })));
  const update = (i, field, v) =>
    setDraft((d) => d.map((m, j) => (i === j ? { ...m, [field]: v } : m)));

  return (
    <div
      style={{
        position: "fixed",
        inset: 0,
        background: "rgba(0,0,0,0.5)",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
      }}
    >
      <div className="panel" style={{ width: 440 }}>
        <h3>Edit CW Macros</h3>
        <div className="macro-edit-grid">
          {draft.map((m, i) => (
            <Fragment key={i}>
              <span style={{ color: "var(--text-dim)" }}>F{i + 1}</span>
              <input
                type="text"
                value={m.name}
                onChange={(e) => update(i, "name", e.target.value)}
              />
              <input
                type="text"
                value={m.text}
                onChange={(e) => update(i, "text", e.target.value)}
              />
            </Fragment>
          ))}
        </div>
        <div className="row" style={{ justifyContent: "flex-end", gap: 8, marginTop: 12 }}>
          <button onClick={onCancel}>Cancel</button>
          <button
            onClick={() => onSave(draft)}
            style={{ background: "var(--accent-dim)", borderColor: "var(--accent)" }}
          >
            Save
          </button>
        </div>
      </div>
    </div>
  );
}
