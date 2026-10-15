#!/usr/bin/env bash
#
# build-all.sh — build the web bundles and all five release binaries of the W1VE keyer suite
# natively on macOS (aarch64-apple-darwin).
#
# Run after provision.sh, from anywhere; it locates the repos relative to the parent dir.
#
# Config (override via env):
#   SUITE_PARENT   Parent dir holding the three repos. Default: auto-detected from this
#                  script's location (…/RWK-Rust/docs/mac → parent of RWK-Rust).
#
# Usage:
#   chmod +x build-all.sh && ./build-all.sh

set -euo pipefail

# Auto-detect the parent: this script lives at <parent>/RWK-Rust/docs/mac/build-all.sh.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_PARENT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SUITE_PARENT="${SUITE_PARENT:-$DEFAULT_PARENT}"

RWK="$SUITE_PARENT/RWK-Rust/rust"
REMOTE="$SUITE_PARENT/W1VE-Remote-Keyer"
KEYER="$SUITE_PARENT/W1VE-Keyer"

say() { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }

# shellcheck disable=SC1090
source "$HOME/.cargo/env" 2>/dev/null || true

# ─── 1. Web bundles (each app serves its own bundled web/dist) ──────────────────
build_web() {
  local dir="$1" name="$2"
  say "Building web bundle: $name"
  ( cd "$dir/web" && npm install && npm run build )
}
build_web "$RWK/crates/rwk-app"       "Keyer-Router"
build_web "$REMOTE/crates/wrk-app"    "Remote Keyer"
build_web "$KEYER/crates/wk-app"      "W1VE Keyer"

# ─── 2. Release binaries (custom-protocol so the webview loads web/dist) ─────────
say "Building Keyer-Router (w1ve-rkr, w1ve-rkr-station)…"
( cd "$RWK" && cargo build --release --features custom-protocol -p rwk-app )

say "Building Remote Keyer (w1ve-remote-keyer, w1ve-remote-keyer-station)…"
( cd "$REMOTE" && cargo build --release --features custom-protocol -p wrk-app )

say "Building W1VE Keyer (w1ve-keyer)…"
( cd "$KEYER" && cargo build --release --features custom-protocol -p wk-app )

# ─── 3. Report ──────────────────────────────────────────────────────────────────
say "Build complete. Binaries:"
report() {
  local path="$1"
  if [ -f "$path" ]; then
    printf '    \033[1;32m✓\033[0m %s\n' "$path"
  else
    printf '    \033[1;31m✗ missing:\033[0m %s\n' "$path"
  fi
}
report "$RWK/target/release/w1ve-rkr"
report "$RWK/target/release/w1ve-rkr-station"
report "$REMOTE/target/release/w1ve-remote-keyer"
report "$REMOTE/target/release/w1ve-remote-keyer-station"
report "$KEYER/target/release/w1ve-keyer"

cat <<'EOF'

Note: these are unsigned binaries. To produce a distributable, signed, notarized .app:
  - build the .app bundle (tauri build, or the bundle target), then
  - ./sign-notarize.sh "path/to/<App>.app"
EOF
