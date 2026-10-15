import { useEffect, useRef, useState } from "react";
import { api, onRwkEvent } from "./api.js";
import KeyerTab from "./KeyerTab.jsx";
import RouterTab from "./RouterTab.jsx";

const DEFAULT_SETTINGS = {
  wpm: 20,
  weight: 50,
  mode: "iambicB",
  paddleReverse: false,
  toneHz: 600,
  toneVolume: 70,
  audioDevice: null,
  keyingPort: null,
  paddlePort: null,
  winkeyerPort: null,
  transport: "wan",
  callsign: "",
  keyerId: "",
};

export default function App() {
  const [tab, setTab] = useState("keyer");
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [connection, setConnection] = useState({ state: "disconnected", path: "none" });
  const [keyDown, setKeyDown] = useState(false);
  const [pttOn, setPttOn] = useState(false);
  const [sending, setSending] = useState(false);
  const [advertising, setAdvertising] = useState(false);
  const [ports, setPorts] = useState([]);
  const [audioDevices, setAudioDevices] = useState([]);
  const [errors, setErrors] = useState({}); // control -> short red message
  const [forwardRules, setForwardRules] = useState([]);
  const [flexEnabled, setFlexEnabled] = useState(false);
  const [log, setLog] = useState([]);
  const unlisten = useRef(null);

  useEffect(() => {
    onRwkEvent((ev) => {
      switch (ev.kind) {
        case "settings":
          setSettings({
            wpm: ev.wpm,
            weight: ev.weight,
            mode: ev.mode,
            paddleReverse: ev.paddle_reverse,
            toneHz: ev.tone_hz,
            toneVolume: ev.tone_volume,
            audioDevice: ev.audio_device ?? null,
            keyingPort: ev.keying_port ?? null,
            paddlePort: ev.paddle_port ?? null,
            winkeyerPort: ev.winkeyer_port ?? null,
            transport: ev.transport ?? "wan",
            callsign: ev.callsign ?? "",
            keyerId: ev.keyer_id ?? "",
          });
          break;
        case "ports":
          setPorts(ev.available || []);
          break;
        case "audioDevices":
          // Each entry: { name, isDefault }.
          setAudioDevices(ev.available || []);
          break;
        case "error":
          setErrors((e) => ({ ...e, [ev.control]: ev.message || undefined }));
          break;
        case "connection":
          setConnection({ state: ev.state, path: ev.path });
          break;
        case "keyState":
          setKeyDown(ev.down);
          break;
        case "pttState":
          setPttOn(ev.on);
          break;
        case "sending":
          setSending(ev.active);
          break;
        case "advertising":
          setAdvertising(ev.active);
          break;
        case "forwardRules":
          setForwardRules(ev.rules || []);
          break;
        case "flexDiscovery":
          setFlexEnabled(ev.enabled);
          break;
        case "log":
          setLog((prev) => [
            ...prev.slice(-500),
            { level: ev.level, message: ev.message, t: new Date() },
          ]);
          break;
        default:
          break;
      }
    }).then((fn) => {
      unlisten.current = fn;
      // Beat the startup race: the backend's initial emit happens before this webview
      // subscribed, so ask for a fresh snapshot now.
      api.requestState().catch(() => {});
    });

    // Global ESC: abort sending from anywhere in the window (e.g. while a macro plays and the
    // type-ahead box is not focused).
    const onKey = (e) => {
      if (e.key === "Escape") api.abortSend().catch(() => {});
    };
    window.addEventListener("keydown", onKey);

    return () => {
      if (unlisten.current) unlisten.current();
      window.removeEventListener("keydown", onKey);
    };
  }, []);

  const linkColor =
    connection.state === "connected"
      ? "var(--good)"
      : connection.state === "connecting"
        ? "var(--warn)"
        : "var(--text-dim)";

  const pathText =
    connection.state === "connected"
      ? connection.path === "relay"
        ? "Relay"
        : connection.path === "direct"
          ? "Direct"
          : "Connected"
      : connection.state === "connecting"
        ? "Pairing…"
        : "Disconnected";

  return (
    <div className="app">
      <div className="tabbar">
        <div className={`tab ${tab === "keyer" ? "active" : ""}`} onClick={() => setTab("keyer")}>
          Keyer
        </div>
        <div className={`tab ${tab === "router" ? "active" : ""}`} onClick={() => setTab("router")}>
          Ham Router
        </div>
        <div className={`tab ${tab === "log" ? "active" : ""}`} onClick={() => setTab("log")}>
          Log
        </div>
      </div>

      <div className="tabbody">
        {tab === "keyer" && (
          <KeyerTab
            settings={settings}
            connection={connection}
            advertising={advertising}
            ports={ports}
            audioDevices={audioDevices}
            errors={errors}
          />
        )}
        {tab === "router" && (
          <RouterTab
            rules={forwardRules}
            flexEnabled={flexEnabled}
            connected={connection.state === "connected"}
          />
        )}
        {tab === "log" && (
          <div className="panel" style={{ height: "100%" }}>
            <h3>Log</h3>
            <div
              style={{
                fontFamily: "Consolas, monospace",
                fontSize: 12,
                whiteSpace: "pre-wrap",
                color: "var(--text-dim)",
                maxHeight: "100%",
                overflow: "auto",
              }}
            >
              {log.length === 0
                ? "(no messages yet)"
                : log.map((l, i) => (
                    <div key={i} style={{ color: l.level === "warn" ? "var(--warn)" : undefined }}>
                      {l.t.toLocaleTimeString()} [{l.level}] {l.message}
                    </div>
                  ))}
            </div>
          </div>
        )}
      </div>

      <div className="statusbar">
        <span className="led" style={{ color: linkColor }}>
          ●
        </span>
        <span className="spring">{pathText}</span>
        <span>RTT: --</span>
        <span style={{ color: keyDown ? "var(--key-on)" : "var(--text-dim)" }}>
          Key: {keyDown ? "Down" : "Up"}
        </span>
        <span style={{ color: pttOn ? "var(--ptt-on)" : "var(--text-dim)" }}>
          PTT: {pttOn ? "On" : "Off"}
        </span>
        <span style={{ color: sending ? "var(--accent)" : "var(--text-dim)" }}>
          {sending ? "Sending" : "Idle"}
        </span>
      </div>
    </div>
  );
}
