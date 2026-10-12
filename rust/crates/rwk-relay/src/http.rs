//! The dashboard HTTP server: login, metrics API, and the static SPA.
//!
//! One axum router serves everything:
//!
//! * `POST /api/login` — exchanges admin credentials for a session cookie.
//! * `POST /api/logout` — clears the cookie.
//! * `GET  /api/metrics` — the live [`Snapshot`], gated on a valid session.
//! * `GET  /api/health` — an unauthenticated liveness probe for the container.
//! * everything else — the built React bundle, with a fallback to `index.html` so the SPA's
//!   client-side routes resolve.
//!
//! The session token is carried in an `HttpOnly` cookie, so the SPA never has to store it and
//! it is not reachable from page scripts. Behind Cloudflare the whole thing is HTTPS at the
//! edge; the server itself speaks plain HTTP on its internal port.

use std::path::PathBuf;
use std::sync::Arc;

use axum::{
    extract::State,
    http::{header, HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{get, post},
    Json, Router,
};
use serde::Deserialize;
use serde_json::json;
use tower_http::services::{ServeDir, ServeFile};

use crate::auth::Auth;
use crate::metrics::MetricsHistory;

/// Shared state for the handlers.
#[derive(Clone)]
pub struct AppState {
    pub auth: Auth,
    pub metrics: MetricsHistory,
    pub session_secs: u64,
}

/// Name of the session cookie.
const COOKIE: &str = "rwk_session";

/// Builds the dashboard router, serving the SPA from `web_root`.
pub fn router(state: Arc<AppState>, web_root: PathBuf) -> Router {
    // Static files, falling back to index.html so the SPA owns its routes.
    let index = web_root.join("index.html");
    let spa = ServeDir::new(web_root).not_found_service(ServeFile::new(index));

    Router::new()
        .route("/api/health", get(health))
        .route("/api/login", post(login))
        .route("/api/logout", post(logout))
        .route("/api/metrics", get(metrics))
        .fallback_service(spa)
        .with_state(state)
}

/// Liveness probe: always 200, no auth, so the container/orchestrator can health-check.
async fn health() -> impl IntoResponse {
    Json(json!({ "status": "ok" }))
}

/// A login request body.
#[derive(Deserialize)]
struct LoginReq {
    user: String,
    password: String,
}

/// Exchanges credentials for a session cookie.
async fn login(State(state): State<Arc<AppState>>, Json(req): Json<LoginReq>) -> Response {
    match state.auth.login(&req.user, &req.password) {
        Some(token) => {
            // HttpOnly + SameSite=Strict; Secure is added by Cloudflare's edge. Max-Age ties
            // the cookie lifetime to the token's.
            let cookie = format!(
                "{COOKIE}={token}; HttpOnly; SameSite=Strict; Path=/; Max-Age={}",
                state.session_secs
            );
            let mut headers = HeaderMap::new();
            if let Ok(value) = cookie.parse() {
                headers.insert(header::SET_COOKIE, value);
            }
            (StatusCode::OK, headers, Json(json!({ "ok": true }))).into_response()
        }
        None => (StatusCode::UNAUTHORIZED, Json(json!({ "error": "invalid credentials" }))).into_response(),
    }
}

/// Clears the session cookie.
async fn logout() -> Response {
    let cookie = format!("{COOKIE}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0");
    let mut headers = HeaderMap::new();
    if let Ok(value) = cookie.parse() {
        headers.insert(header::SET_COOKIE, value);
    }
    (StatusCode::OK, headers, Json(json!({ "ok": true }))).into_response()
}

/// Returns the live metrics snapshot, gated on a valid session cookie.
async fn metrics(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if !authorized(&state, &headers) {
        return (StatusCode::UNAUTHORIZED, Json(json!({ "error": "unauthorized" }))).into_response();
    }
    Json(state.metrics.snapshot()).into_response()
}

/// Whether the request carries a valid session cookie.
fn authorized(state: &AppState, headers: &HeaderMap) -> bool {
    let Some(cookies) = headers.get(header::COOKIE).and_then(|v| v.to_str().ok()) else {
        return false;
    };
    // Find the session cookie among any others on the header.
    for pair in cookies.split(';') {
        let pair = pair.trim();
        if let Some(token) = pair.strip_prefix(&format!("{COOKIE}=")) {
            return state.auth.verify(token);
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::metrics::MetricsHistory;
    use rwk_core::engine::relay::RelayMetrics;

    fn state() -> Arc<AppState> {
        let auth = Auth::new("admin".into(), "supersecret", "0123456789abcdef0123".into(), 3600);
        let metrics = MetricsHistory::new(Arc::new(RelayMetrics::default()));
        Arc::new(AppState { auth, metrics, session_secs: 3600 })
    }

    #[test]
    fn authorized_accepts_a_valid_session_cookie_and_rejects_others() {
        let st = state();
        let token = st.auth.login("admin", "supersecret").unwrap();

        let mut ok = HeaderMap::new();
        ok.insert(header::COOKIE, format!("other=1; {COOKIE}={token}").parse().unwrap());
        assert!(authorized(&st, &ok), "a valid session cookie among others is accepted");

        let mut no_cookie = HeaderMap::new();
        no_cookie.insert(header::COOKIE, "other=1".parse().unwrap());
        assert!(!authorized(&st, &no_cookie), "a request without the session cookie is rejected");

        let mut bad = HeaderMap::new();
        bad.insert(header::COOKIE, format!("{COOKIE}=garbage").parse().unwrap());
        assert!(!authorized(&st, &bad), "a bad token is rejected");

        assert!(!authorized(&st, &HeaderMap::new()), "no Cookie header at all is rejected");
    }
}
