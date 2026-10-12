import React, { useEffect, useState } from "react";
import Login from "./Login.jsx";
import Dashboard from "./Dashboard.jsx";
import { fetchMetrics } from "./api.js";

// Auth state is derived from the server, not stored here: on load we probe the metrics
// endpoint. A 200 means the session cookie is still valid (show the dashboard); a 401 means
// we need to log in. This keeps the SPA stateless about the token — the server owns it.
export default function App() {
  const [state, setState] = useState("checking"); // checking | login | authed

  useEffect(() => {
    let alive = true;
    fetchMetrics()
      .then((data) => alive && setState(data === null ? "login" : "authed"))
      .catch(() => alive && setState("login"));
    return () => {
      alive = false;
    };
  }, []);

  if (state === "checking") {
    return <div className="login-wrap"><div className="login-card">Loading…</div></div>;
  }
  if (state === "authed") {
    return <Dashboard onLoggedOut={() => setState("login")} />;
  }
  return <Login onAuthed={() => setState("authed")} />;
}
