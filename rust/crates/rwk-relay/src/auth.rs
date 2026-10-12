//! Dashboard authentication: a single admin credential and signed session tokens.
//!
//! The dashboard guards live metrics, so it needs a login — but it is a single-operator tool,
//! not a multi-tenant service, so the model is deliberately minimal: one admin username and
//! password from the environment, and a signed JWT as the session token after login. There is
//! no user database and no registration.
//!
//! The password is compared by hashing both sides and comparing the digests in constant time,
//! so the comparison leaks neither the password nor its length by timing. (A relay dashboard
//! is low-value enough that a slow KDF like Argon2 would be over-engineering; the credential
//! lives in the operator's environment, not a breachable database.)

use std::time::{SystemTime, UNIX_EPOCH};

use jsonwebtoken::{decode, encode, DecodingKey, EncodingKey, Header, Validation};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

/// The claims carried by a session token.
#[derive(Debug, Serialize, Deserialize)]
struct Claims {
    /// Subject: the admin username.
    sub: String,
    /// Expiry, Unix epoch seconds.
    exp: u64,
}

/// Verifies admin credentials and issues/validates session tokens.
#[derive(Clone)]
pub struct Auth {
    user: String,
    password_hash: [u8; 32],
    secret: Vec<u8>,
    session_secs: u64,
}

impl Auth {
    /// Builds the authenticator from the configured credential and signing secret.
    #[must_use]
    pub fn new(user: String, password: &str, secret: String, session_secs: u64) -> Self {
        Self {
            user,
            password_hash: Sha256::digest(password.as_bytes()).into(),
            secret: secret.into_bytes(),
            session_secs,
        }
    }

    /// Checks a login attempt. Returns a signed session token on success.
    ///
    /// The username and password are both verified; the comparison is constant-time over the
    /// hashes so a wrong guess cannot be distinguished by timing.
    #[must_use]
    pub fn login(&self, user: &str, password: &str) -> Option<String> {
        let user_ok = constant_time_eq(user.as_bytes(), self.user.as_bytes());
        let attempt: [u8; 32] = Sha256::digest(password.as_bytes()).into();
        let pass_ok = constant_time_eq(&attempt, &self.password_hash);
        // Evaluate both before branching so a wrong username and a wrong password take the
        // same path.
        if user_ok && pass_ok {
            self.issue_token()
        } else {
            None
        }
    }

    /// Issues a signed token for the admin user.
    fn issue_token(&self) -> Option<String> {
        let exp = now_secs().saturating_add(self.session_secs);
        let claims = Claims { sub: self.user.clone(), exp };
        encode(&Header::default(), &claims, &EncodingKey::from_secret(&self.secret)).ok()
    }

    /// Returns whether `token` is a currently-valid session token.
    #[must_use]
    pub fn verify(&self, token: &str) -> bool {
        let mut validation = Validation::default();
        validation.validate_exp = true;
        decode::<Claims>(token, &DecodingKey::from_secret(&self.secret), &validation).is_ok()
    }
}

/// Compares two byte slices in constant time with respect to their contents.
///
/// Unequal lengths return `false` immediately (length is not secret here — the stored hash is
/// always 32 bytes); for equal lengths every byte is examined so the time does not depend on
/// where the first difference is.
fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

/// Current Unix epoch seconds.
fn now_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn auth() -> Auth {
        Auth::new("admin".into(), "supersecret", "0123456789abcdef0123".into(), 3600)
    }

    #[test]
    fn correct_credentials_issue_a_verifiable_token() {
        let a = auth();
        let token = a.login("admin", "supersecret").expect("login should succeed");
        assert!(a.verify(&token), "the issued token must verify");
    }

    #[test]
    fn a_wrong_password_is_rejected() {
        assert!(auth().login("admin", "wrong").is_none());
    }

    #[test]
    fn a_wrong_username_is_rejected() {
        assert!(auth().login("root", "supersecret").is_none());
    }

    #[test]
    fn a_garbage_token_does_not_verify() {
        assert!(!auth().verify("not.a.token"));
    }

    #[test]
    fn a_token_signed_by_a_different_secret_does_not_verify() {
        let issuer = Auth::new("admin".into(), "pw-at-least-8", "secret-one-aaaaaaaa".into(), 3600);
        let token = issuer.login("admin", "pw-at-least-8").unwrap();
        let other = Auth::new("admin".into(), "pw-at-least-8", "secret-two-bbbbbbbb".into(), 3600);
        assert!(!other.verify(&token), "a token must not verify under a different secret");
    }

    #[test]
    fn constant_time_eq_matches_only_identical_slices() {
        assert!(constant_time_eq(b"abc", b"abc"));
        assert!(!constant_time_eq(b"abc", b"abd"));
        assert!(!constant_time_eq(b"abc", b"ab"));
    }
}
