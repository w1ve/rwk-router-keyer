import { useEffect, useState } from "react";
import { onRwkEvent } from "./api.js";
import KeyerTab from "./KeyerTab.jsx";
import RouterTab from "./RouterTab.jsx";

const DEFAULT_SETTINGS = {
  wpm: 20,
  weight: 50,
  mode: "iambicB",
  paddleReverse: false,
  toneHz: 600,
  toneVolume: 70,
  transport: "wan",
  callsign: "",
  keyerId: "",
};

export default function App() {
  const [tab, setTab] = useState("keyer");
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [connection, setConnection] = useState({ state: "disconnected", path: "none" });
  const [keyDown, setKeyDown] = useState(false);
  const [advertising, setAdvertising] = useState(false);
  const [forwardRules, setForwardRules] = useState([]);
  const [flexEnabled, setFlexEnabled] = useState(false);
  const [log, setLog] = useState([]);

  useEffect(() => {
    const unlisten = onRwkEvent((ev) => {
      switch (ev.kind) {
        case "settings":
          setSettings({
            wpm: ev.wpm,
            weight: ev.weight,
            mode: ev.mode,
            paddleReverse: ev.paddle_reverse,
            toneHz: ev.tone_hz,
            toneVolume: ev.tone_volume,
            transport: ev.transport ?? "wan",
            callsign: ev.callsign ?? "",
            keyerId: ev.keyer_id ?? "",
          });
          break;
        case "connection":
          setConnection({ state: ev.state, path: ev.path });
          break;
        case "keyState":
          setKeyDown(ev.down);
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
    });
    return () => {
      unlisten.then((f) => f());
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
          <KeyerTab settings={settings} connection={connection} advertising={advertising} />
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
      </div>
    </div>
  );
}
