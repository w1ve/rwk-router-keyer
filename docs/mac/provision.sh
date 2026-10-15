#!/usr/bin/env bash
#
# provision.sh — set up a fresh macOS (Apple silicon) box to build the W1VE keyer suite.
#
# Installs: Xcode command-line tools, Homebrew, Rust (aarch64-apple-darwin), Node, socat.
# Clones:   the three suite repos side by side into one parent directory, which the relative
#           rwk-core path dependency requires.
#
# Idempotent: re-running skips anything already present.
#
# Config (override via env before running):
#   SUITE_PARENT    Parent dir the repos are cloned into. Default: the current directory.
#   SUITE_GIT_BASE  Git host base for the repos, e.g. "git@github.com:w1ve" or
#                   "https://github.com/w1ve". Default: https://github.com/w1ve
#
# Usage:
#   chmod +x provision.sh && ./provision.sh

set -euo pipefail

SUITE_PARENT="${SUITE_PARENT:-$(pwd)}"
SUITE_GIT_BASE="${SUITE_GIT_BASE:-https://github.com/w1ve}"

# Repo directory name  ->  git repo name
REPOS=(
  "RWK-Rust:rwk-router-keyer"
  "W1VE-Remote-Keyer:w1ve-remote-keyer"
  "W1VE-Keyer:w1ve-keyer"
)

say()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

# ─── 1. Xcode command-line tools (clang, git, make) ────────────────────────────
if xcode-select -p >/dev/null 2>&1; then
  say "Xcode command-line tools already installed."
else
  say "Installing Xcode command-line tools (a GUI dialog may appear)…"
  xcode-select --install || true
  # Wait until the tools are actually present.
  until xcode-select -p >/dev/null 2>&1; do
    echo "   waiting for the Xcode CLT install to finish…"
    sleep 15
  done
fi

# ─── 2. Homebrew ───────────────────────────────────────────────────────────────
if have brew; then
  say "Homebrew already installed."
else
  say "Installing Homebrew…"
  NONINTERACTIVE=1 /bin/bash -c \
    "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
fi
# Apple silicon installs brew in /opt/homebrew; make sure it is on PATH for this shell.
if [ -x /opt/homebrew/bin/brew ]; then
  eval "$(/opt/homebrew/bin/brew shellenv)"
fi

# ─── 3. Node + socat (via Homebrew) ─────────────────────────────────────────────
for pkg in node socat; do
  if brew list --versions "$pkg" >/dev/null 2>&1; then
    say "$pkg already installed."
  else
    say "Installing $pkg…"
    brew install "$pkg"
  fi
done

# ─── 4. Rust (rustup) + the aarch64 target ──────────────────────────────────────
if have rustc && have cargo; then
  say "Rust already installed ($(rustc --version))."
else
  say "Installing Rust via rustup…"
  curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
fi
# shellcheck disable=SC1090
source "$HOME/.cargo/env" 2>/dev/null || true
# On an Apple-silicon Mac the host target is already aarch64-apple-darwin, but add it
# explicitly so a cross build from an Intel box would also work.
rustup target add aarch64-apple-darwin >/dev/null 2>&1 || true

# ─── 5. Clone the three repos side by side ──────────────────────────────────────
mkdir -p "$SUITE_PARENT"
cd "$SUITE_PARENT"
for entry in "${REPOS[@]}"; do
  dir="${entry%%:*}"
  repo="${entry##*:}"
  if [ -d "$dir/.git" ]; then
    say "$dir already cloned; pulling latest."
    git -C "$dir" pull --ff-only || echo "   (skipped pull — local changes present)"
  else
    say "Cloning $repo into $dir…"
    git clone "$SUITE_GIT_BASE/$repo.git" "$dir"
  fi
done

# ─── 6. Verify the sibling layout the path dependency needs ──────────────────────
ENGINE="$SUITE_PARENT/RWK-Rust/rust/crates/rwk-core"
if [ -f "$ENGINE/Cargo.toml" ]; then
  say "Engine present at $ENGINE — sibling layout is correct."
else
  echo "ERROR: engine not found at $ENGINE" >&2
  echo "The Remote Keyer and Keyer depend on it via a relative path; the repos must sit" >&2
  echo "side by side under $SUITE_PARENT. Check the clone step above." >&2
  exit 1
fi

say "Provisioning complete."
echo "    Toolchains:  $(rustc --version 2>/dev/null), node $(node --version 2>/dev/null)"
echo "    Repos under: $SUITE_PARENT"
echo "    Next:        ./build-all.sh"
