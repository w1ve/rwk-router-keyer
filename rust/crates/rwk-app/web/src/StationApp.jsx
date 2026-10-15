import { useEffect, useRef, useState } from "react";
import { onRwkEvent, stationApi } from "./station-api.js";
import StationTab from "./StationTab.jsx";

const DEFAULT_SETTINGS = {
  keyingPort: null,
  keyLine: "dtr",
  pttLine: "rts",
  loggerPort: null,
  loggerEnabled: false,
  relay: null,
  pairingCode: null,
  transport: "wan",
};

// The Station application shell: a Station tab and a Log tab, plus a status strip showing
// the link, the KEY state and the PTT state. It mirrors the Client's App.jsx structure and
// shares the same `rwk://event` stream, but renders StationEvents (which add Ptt and
// FailSafe on top of the Client's set).
export default function StationApp() {
  const [tab, setTab] = useState("station");
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [connection, setConnection] = useState({ state: "disconnected", path: "none" });
  const [keyDown, setKeyDown] = useState(false);
  const [pttOn, setPttOn] = useState(false);
  const [loggerSending, setLoggerSending] = useState(false);
  const [lastFailSafe, setLastFailSafe] = useState(null);
  const [lanPeers, setLanPeers] = useState([]);
  const [ports, setPorts] = useState([]);
  const [errors, setErrors] = useState({}); // control -> short red message
  const [log, setLog] = useState([]);
  const unlisten = useRef(null);

  useEffect(() => {
    onRwkEvent((ev) => {
      switch (ev.kind) {
        case "settings":
          setSettings({
            keyingPort: ev.keying_port,
            keyLine: ev.key_line,
            pttLine: ev.ptt_line,
            loggerPort: ev.logger_port,
            loggerEnabled: ev.logger_enabled,
            relay: ev.relay,
            pairingCode: ev.pairing_code,
            transport: ev.transport ?? "wan",
          });
          break;
        case "ports":
          setPorts(ev.available || []);
          break;
        case "error":
          setErrors((e) => ({ ...e, [ev.control]: ev.message || undefined }));
          break;
        case "lanPeers":
          setLanPeers(ev.peers || []);
          break;
        case "connection":
          setConnection({ state: ev.state, path: ev.path });
          // A fresh pairing clears any stale fail-safe banner.
          if (ev.state === "connecting" || ev.state === "disconnected") {
            setLastFailSafe(null);
          }
          break;
        case "keyState":
          setKeyDown(ev.down);
          break;
        case "ptt":
          setPttOn(ev.asserted);
          break;
        case "loggerSending":
          setLoggerSending(ev.active);
          break;
        case "failSafe":
          setLastFailSafe({ code: ev.code, message: ev.message });
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
      // Beat the startup race: ask for a fresh settings + ports snapshot now that we have
      // subscribed.
      stationApi.requestState().catch(() => {});
    });
    return () => {
      if (unlisten.current) unlisten.current();
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
        ? "Waiting for client…"
        : "Disconnected";

  return (
    <div className="app">
      <div className="tabbar">
        <div
          className={`tab ${tab === "station" ? "active" : ""}`}
          onClick={() => setTab("station")}
        >
          Station
        </div>
        <div className={`tab ${tab === "log" ? "active" : ""}`} onClick={() => setTab("log")}>
          Log
        </div>
      </div>

      <div className="tabbody">
        {tab === "station" && (
          <StationTab
            settings={settings}
            connection={connection}
            keyDown={keyDown}
            pttOn={pttOn}
            loggerSending={loggerSending}
            lastFailSafe={lastFailSafe}
            lanPeers={lanPeers}
            ports={ports}
            errors={errors}
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
        <span style={{ color: keyDown ? "var(--key-on)" : "var(--text-dim)" }}>
          Key: {keyDown ? "Down" : "Up"}
        </span>
        <span style={{ color: pttOn ? "var(--ptt-on)" : "var(--text-dim)" }}>
          PTT: {pttOn ? "On" : "Off"}
        </span>
      </div>
    </div>
  );
}
