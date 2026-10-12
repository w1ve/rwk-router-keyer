import React, { useState } from "react";
import { login } from "./api.js";

export default function Login({ onAuthed }) {
  const [user, setUser] = useState("admin");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e) {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      const ok = await login(user, password);
      if (ok) {
        onAuthed();
      } else {
        setError("Invalid username or password.");
      }
    } catch {
      setError("Could not reach the relay.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-wrap">
      <form className="login-card" onSubmit={submit}>
        <div className="brand">
          <span className="dot" />
          <div>
            <h1>RWK Relay</h1>
          </div>
        </div>
        <div className="sub">Rendezvous &amp; Relay Server</div>

        <label htmlFor="user">Username</label>
        <input
          id="user"
          value={user}
          autoComplete="username"
          onChange={(e) => setUser(e.target.value)}
        />

        <label htmlFor="pw">Password</label>
        <input
          id="pw"
          type="password"
          value={password}
          autoComplete="current-password"
          onChange={(e) => setPassword(e.target.value)}
        />

        <button className="primary" disabled={busy || !password}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
        <div className="error">{error}</div>
      </form>
    </div>
  );
}
