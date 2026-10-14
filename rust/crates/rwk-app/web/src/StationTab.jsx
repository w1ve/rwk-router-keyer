import { useEffect, useState } from "react";
import { stationApi as api } from "./station-api.js";

// The Station tab: KEY/PTT indicators, the keying output configuration (port + control
// lines), and the Pair-with-Client panel. On WAN the Station is the responder — it waits for
// the operating position to connect with the shared relay + code. On LAN the roles reverse:
// the Station browses the keyers advertising themselves on the local network and the operator
// picks one from the list, so no relay or pairing code is needed.
export default function StationTab({
  settings,
  connection,
  keyDown,
  pttOn,
  loggerSending,
  lastFailSafe,
  lanPeers = [],
}) {
  const [ports, setPorts] = useState([]);
  const [keyingPort, setKeyingPort] = useState(settings.keyingPort || "");
  const [keyLine, setKeyLine] = useState(settings.keyLine || "dtr");
  const [pttLine, setPttLine] = useState(settings.pttLine || "rts");
  const [loggerPort, setLoggerPort] = useState(settings.loggerPort || "");
  const [loggerEnabled, setLoggerEnabled] = useState(settings.loggerEnabled || false);

  const [relay, setRelay] = useState(settings.relay || "107.172.138.103:9000");
  const [code, setCode] = useState(settings.pairingCode || "");
  const [transport, setTransport] = useState(settings.transport || "wan");
  const [pickedKey, setPickedKey] = useState("");

  // Reflect backend-echoed settings into local state.
  useEffect(() => {
    setKeyingPort(settings.keyingPort || "");
    setKeyLine(settings.keyLine || "dtr");
    setPttLine(settings.pttLine || "rts");
    setLoggerPort(settings.loggerPort || "");
    setLoggerEnabled(settings.loggerEnabled || false);
    if (settings.relay) setRelay(settings.relay);
    if (settings.pairingCode) setCode(settings.pairingCode);
    if (settings.transport) setTransport(settings.transport);
  }, [settings]);

  useEffect(() => {
    api.serialPorts().then(setPorts).catch(() => {});
  }, []);

  // Keep the picked keyer valid: default to the first peer, and drop the selection if the
  // advertised keyer we had chosen has aged out of the list.
  useEffect(() => {
    if (lanPeers.length === 0) {
      if (pickedKey) setPickedKey("");
      return;
    }
    if (!lanPeers.some((p) => p.key === pickedKey)) {
      setPickedKey(lanPeers[0].key);
    }
  }, [lanPeers]);

  const paired = connection.state === "connected";
  const connecting = connection.state === "connecting";
  const canPair = !!keyingPort && !!code.trim() && !!relay.trim();
  const picked = lanPeers.find((p) => p.key === pickedKey) || null;
  const canConnectLan = !!keyingPort && !!picked;

  function selectTransport(next) {
    setTransport(next);
    api.setTransport(next);
    if (next === "lan") api.lanBrowse();
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <div className="panel-row" style={{ gridTemplateColumns: "1fr 1fr" }}>
        {/* ─── Status: KEY / PTT indicators ──────────────── */}
        <div className="panel">
          <h3>Keying Status</h3>
          <div className="row" style={{ gap: 24, justifyContent: "center", padding: "8px 0" }}>
            <Indicator label="KEY" on={keyDown} onColor="var(--key-on)" />
            <Indicator label="PTT" on={pttOn} onColor="var(--ptt-on)" />
            <Indicator label="LOGGER" on={loggerSending} onColor="var(--accent)" />
          </div>
          {lastFailSafe ? (
            <div
              style={{
                marginTop: 8,
                padding: "8px 10px",
                borderRadius: 6,
                background: "#3a2323",
                border: "1px solid var(--bad)",
                color: "var(--bad)",
                fontSize: 13,
              }}
            >
              Fail-safe F{lastFailSafe.code}: {lastFailSafe.message}
            </div>
          ) : (
            <p style={{ color: "var(--text-dim)", fontSize: 12, textAlign: "center", margin: 0 }}>
              {paired ? "Replaying keyed edges onto the radio." : "Not paired."}
            </p>
          )}
        </div>

        {/* ─── Keying output configuration ───────────────── */}
        <div className="panel">
          <h3>Keying Output</h3>
          <div className="field">
            <label>Serial port (rig key line)</label>
            <select
              value={keyingPort}
              disabled={paired || connecting}
              onChange={(e) => {
                setKeyingPort(e.target.value);
                api.selectKeyingPort(e.target.value || null);
              }}
            >
              <option value="">(None)</option>
              {ports.map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </div>
          <div className="row" style={{ gap: 10 }}>
            <div className="field" style={{ flex: 1, marginBottom: 0 }}>
              <label>Key line</label>
              <select
                value={keyLine}
                disabled={paired || connecting}
                onChange={(e) => {
                  setKeyLine(e.target.value);
                  api.setKeyLine(e.target.value);
                }}
              >
                <option value="dtr">DTR</option>
                <option value="rts">RTS</option>
              </select>
            </div>
            <div className="field" style={{ flex: 1, marginBottom: 0 }}>
              <label>PTT line</label>
              <select
                value={pttLine}
                disabled={paired || connecting}
                onChange={(e) => {
                  setPttLine(e.target.value);
                  api.setPttLine(e.target.value);
                }}
              >
                <option value="rts">RTS</option>
                <option value="dtr">DTR</option>
                <option value="none">None</option>
              </select>
            </div>
          </div>
          <p style={{ color: "var(--text-dim)", fontSize: 12, marginBottom: 0, marginTop: 10 }}>
            The port opens when pairing starts and is held for the session.
          </p>
        </div>
      </div>

      {/* ─── Logger (WinKeyer) input ────────────────────── */}
      <div className="panel">
        <h3>Logger Input (WinKeyer)</h3>
        <div className="row" style={{ gap: 14, flexWrap: "wrap", alignItems: "flex-end" }}>
          <label className="row" style={{ gap: 6 }}>
            <input
              type="checkbox"
              checked={loggerEnabled}
              disabled={paired || connecting}
              onChange={(e) => {
                setLoggerEnabled(e.target.checked);
                api.setLoggerEnabled(e.target.checked);
              }}
            />
            Enable logger input
          </label>
          <div className="field" style={{ marginBottom: 0, minWidth: 180 }}>
            <label>Logger COM port</label>
            <select
              value={loggerPort}
              disabled={paired || connecting || !loggerEnabled}
              onChange={(e) => {
                setLoggerPort(e.target.value);
                api.setLoggerPort(e.target.value || null);
              }}
            >
              <option value="">(None)</option>
              {ports
                .filter((p) => p !== keyingPort)
                .map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
            </select>
          </div>
        </div>
        <p style={{ color: "var(--text-dim)", fontSize: 12, marginBottom: 0, marginTop: 10 }}>
          A logging program running at the radio site can send CW macros over this port
          (WinKeyer2 protocol). Local CW takes priority over remote paddle keying. The keying
          output port is excluded from the list.
        </p>
      </div>

      {/* ─── Pair with Client ───────────────────────────── */}
      <div className="panel">
        <h3>Pair with Client</h3>
        <div className="row" style={{ gap: 10, flexWrap: "wrap", alignItems: "flex-end" }}>
          <div>
            <label style={{ display: "block", fontSize: 12, color: "var(--text-dim)", marginBottom: 4 }}>
              Transport
            </label>
            <div className="segmented" style={paired || connecting ? { opacity: 0.5, pointerEvents: "none" } : null}>
              <button
                className={transport === "wan" ? "active" : ""}
                onClick={() => selectTransport("wan")}
              >
                WAN (Relay)
              </button>
              <button
                className={transport === "lan" ? "active" : ""}
                onClick={() => selectTransport("lan")}
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
                  style={{ width: 200 }}
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
                  style={{ width: 160 }}
                />
              </div>
              <div style={{ alignSelf: "flex-end" }}>
                {paired || connecting ? (
                  <button onClick={() => api.disconnect()} style={{ background: "#5a2d2d" }}>
                    {connecting ? "Cancel" : "Unpair"}
                  </button>
                ) : (
                  <button
                    onClick={() => api.listen(relay, code)}
                    disabled={!canPair}
                    style={{ background: "var(--accent-dim)", borderColor: "var(--accent)" }}
                  >
                    Wait for Client
                  </button>
                )}
              </div>
            </>
          ) : (
            <>
              <div className="field" style={{ marginBottom: 0, minWidth: 220 }}>
                <label>Available keyers</label>
                <select
                  value={pickedKey}
                  disabled={paired || connecting || lanPeers.length === 0}
                  onChange={(e) => setPickedKey(e.target.value)}
                >
                  {lanPeers.length === 0 ? (
                    <option value="">(searching…)</option>
                  ) : (
                    lanPeers.map((p) => (
                      <option key={p.key} value={p.key}>
                        {p.name}
                      </option>
                    ))
                  )}
                </select>
              </div>
              <div style={{ alignSelf: "flex-end", display: "flex", gap: 8 }}>
                <button onClick={() => api.lanBrowse()} disabled={paired || connecting}>
                  Refresh
                </button>
                {paired || connecting ? (
                  <button onClick={() => api.disconnect()} style={{ background: "#5a2d2d" }}>
                    {connecting ? "Cancel" : "Unpair"}
                  </button>
                ) : (
                  <button
                    onClick={() => picked && api.connectLan(picked.key, picked.endpoint)}
                    disabled={!canConnectLan}
                    style={{ background: "var(--accent-dim)", borderColor: "var(--accent)" }}
                  >
                    Connect
                  </button>
                )}
              </div>
            </>
          )}
        </div>
        <p style={{ color: "var(--text-dim)", fontSize: 12, marginBottom: 0 }}>
          {transport === "wan"
            ? "Enter the same relay and pairing code as the operating position, then wait for it to connect. Select a keying output port first."
            : "Pick the advertised keyer from the list and connect — no relay, no internet. Select a keying output port first."}
        </p>
      </div>
    </div>
  );
}

// A labelled round LED that glows `onColor` when active and sits dim otherwise.
function Indicator({ label, on, onColor }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 6 }}>
      <div
        style={{
          width: 44,
          height: 44,
          borderRadius: "50%",
          background: on ? onColor : "var(--bg-panel-2)",
          border: `2px solid ${on ? onColor : "var(--border)"}`,
          boxShadow: on ? `0 0 14px ${onColor}` : "none",
          transition: "background 40ms, box-shadow 40ms",
        }}
      />
      <span style={{ fontSize: 13, fontWeight: 700, color: on ? onColor : "var(--text-dim)" }}>
        {label}
      </span>
    </div>
  );
}
