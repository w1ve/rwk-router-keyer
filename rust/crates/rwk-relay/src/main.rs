//! `rwk-relay` — the standalone RWK relay/rendezvous server with a metrics dashboard.
//!
//! This is the box an operator runs once on a public host (a cheap VPS, or a home machine
//! with one forwarded port) so their Client and Station — behind any NAT — can find each
//! other and, when a direct path cannot be punched, relay through it. It is the same
//! [`RelayServer`](rwk_core::engine::relay::RelayServer) the RWK app embeds, wrapped with:
//!
//! * operator limits (registration cap + per-connection rate cap) from the environment;
//! * a time-series metrics layer; and
//! * an HTTP dashboard (login + live charts) served from the same process.
//!
//! TLS is terminated at the edge (Cloudflare), so the server speaks plain HTTP on an internal
//! port. Everything is configured through environment variables — see [`config::Config`].

mod auth;
mod config;
mod http;
mod metrics;

use std::path::PathBuf;
use std::sync::Arc;

use rwk_core::engine::relay::RelayServer;

use crate::auth::Auth;
use crate::config::Config;
use crate::http::AppState;
use crate::metrics::{MetricsHistory, SAMPLE_SECS};

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "rwk_relay=info,tower_http=warn".into()),
        )
        .init();

    if let Err(e) = run().await {
        tracing::error!("fatal: {e}");
        std::process::exit(1);
    }
}

async fn run() -> Result<(), String> {
    let config = Config::from_env()?;
    tracing::info!(
        relay = %config.relay_addr,
        http = %config.http_addr,
        max_registrations = config.limits.max_registrations,
        per_conn_bytes_per_sec = config.limits.per_conn_bytes_per_sec,
        "starting rwk-relay"
    );

    // The relay (TCP rendezvous/relay protocol) with the operator's limits.
    let relay_addr = config
        .relay_addr
        .parse()
        .map_err(|e| format!("invalid RWK_RELAY_ADDR: {e}"))?;
    let relay = RelayServer::bind_with(relay_addr, config.limits)
        .await
        .map_err(|e| format!("binding relay: {e}"))?;
    let core_metrics = relay.metrics();
    tracing::info!("relay listening on {}", relay.local_addr().map_err(|e| e.to_string())?);

    // The time-series metrics layer and its sampler task.
    let history = MetricsHistory::new(core_metrics);
    {
        let history = history.clone();
        tokio::spawn(async move {
            let mut tick = tokio::time::interval(std::time::Duration::from_secs(SAMPLE_SECS));
            loop {
                tick.tick().await;
                history.sample();
            }
        });
    }

    // The HTTP dashboard.
    let auth = Auth::new(
        config.admin_user.clone(),
        &config.admin_password,
        config.jwt_secret.clone(),
        config.session_secs,
    );
    let state = Arc::new(AppState { auth, metrics: history, session_secs: config.session_secs });
    let web_root = web_root();
    tracing::info!("serving dashboard from {}", web_root.display());
    let app = http::router(state, web_root);

    let http_addr: std::net::SocketAddr = config
        .http_addr
        .parse()
        .map_err(|e| format!("invalid RWK_HTTP_ADDR: {e}"))?;
    let listener = tokio::net::TcpListener::bind(http_addr)
        .await
        .map_err(|e| format!("binding HTTP: {e}"))?;
    tracing::info!("dashboard listening on {http_addr}");

    // Run the relay and the HTTP server together; if either returns, the process exits.
    tokio::select! {
        r = relay.run() => r.map_err(|e| format!("relay stopped: {e}"))?,
        r = axum::serve(listener, app) => r.map_err(|e| format!("http stopped: {e}"))?,
    }
    Ok(())
}

/// Where the built React bundle lives.
///
/// `RWK_WEB_ROOT` overrides it (the Docker image sets it); otherwise it defaults to `web/dist`
/// relative to the working directory, which is where a local `vite build` lands.
fn web_root() -> PathBuf {
    match std::env::var("RWK_WEB_ROOT") {
        Ok(v) if !v.is_empty() => PathBuf::from(v),
        _ => PathBuf::from("web/dist"),
    }
}
