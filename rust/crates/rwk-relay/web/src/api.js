// Thin wrapper over the relay's JSON API. The session is an HttpOnly cookie set by the
// server on login, so these calls just need `credentials: "include"` — the token is never
// visible to this code, which is the point.

async function post(path, body) {
  const res = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "include",
    body: body ? JSON.stringify(body) : undefined,
  });
  return res;
}

export async function login(user, password) {
  const res = await post("/api/login", { user, password });
  return res.ok;
}

export async function logout() {
  await post("/api/logout");
}

// Fetches the metrics snapshot. Returns the JSON on success, `null` on 401 (session gone), and
// throws on a network error so the caller can distinguish "logged out" from "server down".
export async function fetchMetrics() {
  const res = await fetch("/api/metrics", { credentials: "include" });
  if (res.status === 401) return null;
  if (!res.ok) throw new Error(`metrics ${res.status}`);
  return res.json();
}
