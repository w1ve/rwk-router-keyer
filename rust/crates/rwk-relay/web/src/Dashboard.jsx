import React, { useEffect, useRef, useState } from "react";
import {
  Area,
  AreaChart,
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { fetchMetrics, logout } from "./api.js";
import { bitrate, bytes, count, duration } from "./format.js";

const POLL_MS = 2000;

export default function Dashboard({ onLoggedOut }) {
  const [snap, setSnap] = useState(null);
  const [stale, setStale] = useState(false);
  const timer = useRef(null);

  useEffect(() => {
    let alive = true;
    async function tick() {
      try {
        const data = await fetchMetrics();
        if (!alive) return;
        if (data === null) {
          onLoggedOut(); // session expired
          return;
        }
        setSnap(data);
        setStale(false);
      } catch {
        if (alive) setStale(true); // server unreachable; keep last values, flag stale
      }
    }
    tick();
    timer.current = setInterval(tick, POLL_MS);
    return () => {
      alive = false;
      clearInterval(timer.current);
    };
  }, [onLoggedOut]);

  async function doLogout() {
    await logout();
    onLoggedOut();
  }

  if (!snap) {
    return (
      <>
        <TopBar stale={stale} onLogout={doLogout} />
        <div className="content">
          <div className="panel">Connecting to relay…</div>
        </div>
      </>
    );
  }

  // Chart data: map epoch seconds to a short clock label.
  const chart = snap.history.map((p) => ({
    label: new Date(p.t * 1000).toLocaleTimeString([], { hour12: false }),
    kbps: Math.round((p.bytes_per_sec * 8) / 1000),
    active: p.active,
  }));

  return (
    <>
      <TopBar stale={stale} onLogout={doLogout} uptime={snap.uptime_secs} />
      <div className="content">
        <div className="grid">
          <Stat k="Active Nodes" v={count(snap.active)} cls="accent" spark />
          <Stat k="Throughput" v={bitrate(snap.bytes_per_sec)} cls="green" spark />
          <Stat k="Pairs Served" v={count(snap.total_pairs_served)} />
          <Stat k="Total Relayed" v={bytes(snap.bytes_relayed)} />
          <Stat k="Connections" v={count(snap.total_connections)} />
          <Stat k="Uptime" v={duration(snap.uptime_secs)} />
        </div>

        <div className="row2">
          <Panel title="Relay throughput (kbps)">
            <ResponsiveContainer width="100%" height={220}>
              <AreaChart data={chart} margin={{ top: 6, right: 8, left: -16, bottom: 0 }}>
                <defs>
                  <linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="#39d4c8" stopOpacity={0.5} />
                    <stop offset="100%" stopColor="#39d4c8" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid stroke="#1f2a36" vertical={false} />
                <XAxis dataKey="label" tick={{ fill: "#7d8ca0", fontSize: 10 }} minTickGap={48} />
                <YAxis tick={{ fill: "#7d8ca0", fontSize: 10 }} width={40} />
                <Tooltip contentStyle={tooltip} labelStyle={{ color: "#7d8ca0" }} />
                <Area type="monotone" dataKey="kbps" stroke="#39d4c8" strokeWidth={2} fill="url(#g)" isAnimationActive={false} />
              </AreaChart>
            </ResponsiveContainer>
          </Panel>

          <Panel title="Active nodes">
            <ResponsiveContainer width="100%" height={220}>
              <LineChart data={chart} margin={{ top: 6, right: 8, left: -16, bottom: 0 }}>
                <CartesianGrid stroke="#1f2a36" vertical={false} />
                <XAxis dataKey="label" tick={{ fill: "#7d8ca0", fontSize: 10 }} minTickGap={48} />
                <YAxis allowDecimals={false} tick={{ fill: "#7d8ca0", fontSize: 10 }} width={40} />
                <Tooltip contentStyle={tooltip} labelStyle={{ color: "#7d8ca0" }} />
                <Line type="stepAfter" dataKey="active" stroke="#7CFC9B" strokeWidth={2} dot={false} isAnimationActive={false} />
              </LineChart>
            </ResponsiveContainer>
          </Panel>
        </div>

        <Panel title="Health">
          <div className="grid" style={{ marginBottom: 4 }}>
            <Stat k="Frames Relayed" v={count(snap.frames_relayed)} />
            <Stat k="Dropped (no route)" v={count(snap.frames_dropped_no_route)} />
            <Stat k="Dropped (rate cap)" v={count(snap.frames_dropped_rate)} />
            <Stat k="Rejected (full)" v={count(snap.rejected_full)} />
            <Stat k="Rejected (bad reg)" v={count(snap.rejected_bad_register)} />
          </div>
        </Panel>

        <div className="foot">
          RWK Relay · <span className="mono">metrics refresh {POLL_MS / 1000}s</span>
        </div>
      </div>
    </>
  );
}

const tooltip = {
  background: "#121820",
  border: "1px solid #1f2a36",
  borderRadius: 8,
  color: "#d7e2ec",
  fontSize: 12,
};

function TopBar({ stale, onLogout, uptime }) {
  return (
    <div className="topbar">
      <div className="left">
        <span className="dot" style={{ width: 10, height: 10, borderRadius: "50%", background: stale ? "#ff6b7a" : "#7CFC9B", boxShadow: `0 0 12px ${stale ? "#ff6b7a" : "#7CFC9B"}` }} />
        <h1>RWK Relay</h1>
        <span className="pill">{stale ? "reconnecting" : "live"}</span>
      </div>
      <button className="ghost" onClick={onLogout}>Sign out</button>
    </div>
  );
}

function Stat({ k, v, cls, spark }) {
  return (
    <div className="card">
      <div className="k">{k}</div>
      <div className={`v ${cls || ""}`}>{v}</div>
      {spark && <div className="spark" />}
    </div>
  );
}

function Panel({ title, children }) {
  return (
    <div className="panel">
      <h2>{title}</h2>
      {children}
    </div>
  );
}
