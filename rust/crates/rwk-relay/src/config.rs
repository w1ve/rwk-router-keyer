//! Server configuration, read from the environment.
//!
//! Everything the standalone relay needs to run is an environment variable, so the Docker
//! image is configured entirely through `docker-compose.yml` / `-e` flags with no config file
//! to mount. Sensible defaults mean the only variables an operator *must* set are the admin
//! password and the JWT secret; the rest have VPS-appropriate defaults.

use rwk_core::engine::relay::RelayLimits;

/// Resolved server configuration.
#[derive(Debug, Clone)]
pub struct Config {
    /// Address the relay (the TCP rendezvous/relay protocol) listens on.
    pub relay_addr: String,
    /// Address the HTTP dashboard + API listens on. Behind Cloudflare this is an internal
    /// port the edge forwards to; it serves plain HTTP (Cloudflare terminates TLS).
    pub http_addr: String,
    /// Dashboard admin username.
    pub admin_user: String,
    /// Dashboard admin password (compared in constant time after hashing).
    pub admin_password: String,
    /// Secret used to sign dashboard session tokens. Must be stable across restarts or live
    /// sessions are invalidated on every restart.
    pub jwt_secret: String,
    /// Session lifetime in seconds.
    pub session_secs: u64,
    /// Relay protection limits.
    pub limits: RelayLimits,
}

impl Config {
    /// Builds the configuration from the environment, applying defaults.
    ///
    /// # Errors
    ///
    /// Returns an error describing the first required-but-missing or malformed variable, so a
    /// misconfigured container fails fast with a clear message instead of starting insecure.
    pub fn from_env() -> Result<Self, String> {
        let relay_addr = env_or("RWK_RELAY_ADDR", "0.0.0.0:9000");
        let http_addr = env_or("RWK_HTTP_ADDR", "0.0.0.0:8080");
        let admin_user = env_or("RWK_ADMIN_USER", "admin");

        // The password and JWT secret have no safe default: refuse to start without them so a
        // relay is never exposed with a guessable login or a predictable token signer.
        let admin_password = require("RWK_ADMIN_PASSWORD")?;
        if admin_password.len() < 8 {
            return Err("RWK_ADMIN_PASSWORD must be at least 8 characters".into());
        }
        let jwt_secret = require("RWK_JWT_SECRET")?;
        if jwt_secret.len() < 16 {
            return Err("RWK_JWT_SECRET must be at least 16 characters".into());
        }

        let session_secs = env_or("RWK_SESSION_SECS", "86400")
            .parse()
            .map_err(|_| "RWK_SESSION_SECS must be a positive integer".to_string())?;

        let max_registrations = env_or("RWK_MAX_REGISTRATIONS", "2000")
            .parse()
            .map_err(|_| "RWK_MAX_REGISTRATIONS must be a positive integer".to_string())?;
        let per_conn_bytes_per_sec = env_or("RWK_PER_CONN_BYTES_PER_SEC", "500000")
            .parse()
            .map_err(|_| "RWK_PER_CONN_BYTES_PER_SEC must be an integer (0 disables)".to_string())?;

        Ok(Self {
            relay_addr,
            http_addr,
            admin_user,
            admin_password,
            jwt_secret,
            session_secs,
            limits: RelayLimits { max_registrations, per_conn_bytes_per_sec },
        })
    }
}

/// Reads an environment variable, or returns `default` if unset or empty.
fn env_or(key: &str, default: &str) -> String {
    match std::env::var(key) {
        Ok(v) if !v.is_empty() => v,
        _ => default.to_string(),
    }
}

/// Reads a required environment variable, erroring if unset or empty.
fn require(key: &str) -> Result<String, String> {
    match std::env::var(key) {
        Ok(v) if !v.is_empty() => Ok(v),
        _ => Err(format!("{key} is required but not set")),
    }
}
