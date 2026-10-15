#!/usr/bin/env bash
#
# sign-notarize.sh — codesign (hardened runtime), notarize, and staple a built .app bundle.
#
# This is the manual path for a bundle produced by `tauri build` or the bundle target. Tauri
# can also sign during the build; use whichever fits. Notarization uses `notarytool` (the
# current tool; the old `altool` path is deprecated).
#
# Required env vars:
#   APPLE_SIGNING_IDENTITY   e.g. "Developer ID Application: Your Name (TEAMID)"
#   APPLE_ID                 your Apple ID email
#   APPLE_TEAM_ID            your 10-char team id
#   APPLE_APP_PASSWORD       an app-specific password from appleid.apple.com
# Optional:
#   ENTITLEMENTS             path to the entitlements plist (default: alongside this script)
#
# Usage:
#   export APPLE_SIGNING_IDENTITY="Developer ID Application: … (TEAMID)"
#   export APPLE_ID="you@example.com" APPLE_TEAM_ID="TEAMID" APPLE_APP_PASSWORD="xxxx-xxxx-xxxx-xxxx"
#   ./sign-notarize.sh "path/to/W1VE Keyer.app"

set -euo pipefail

APP="${1:-}"
if [ -z "$APP" ] || [ ! -d "$APP" ]; then
  echo "Usage: $0 \"path/to/<App>.app\"   (the .app bundle must exist)" >&2
  exit 1
fi

: "${APPLE_SIGNING_IDENTITY:?set APPLE_SIGNING_IDENTITY}"
: "${APPLE_ID:?set APPLE_ID}"
: "${APPLE_TEAM_ID:?set APPLE_TEAM_ID}"
: "${APPLE_APP_PASSWORD:?set APPLE_APP_PASSWORD}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENTITLEMENTS="${ENTITLEMENTS:-$SCRIPT_DIR/entitlements.plist}"
[ -f "$ENTITLEMENTS" ] || { echo "entitlements not found: $ENTITLEMENTS" >&2; exit 1; }

say() { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }

# ─── 1. Codesign, deep, with the hardened runtime + entitlements ────────────────
say "Codesigning $APP"
codesign --force --deep --timestamp --options runtime \
  --entitlements "$ENTITLEMENTS" \
  --sign "$APPLE_SIGNING_IDENTITY" \
  "$APP"

say "Verifying the signature"
codesign --verify --deep --strict --verbose=2 "$APP"

# ─── 2. Zip the bundle for notarization submission ──────────────────────────────
ZIP="${APP%.app}.zip"
say "Packaging $ZIP for notarization"
rm -f "$ZIP"
/usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"

# ─── 3. Submit and wait ─────────────────────────────────────────────────────────
say "Submitting to Apple notary service (this can take a few minutes)…"
xcrun notarytool submit "$ZIP" \
  --apple-id "$APPLE_ID" \
  --team-id "$APPLE_TEAM_ID" \
  --password "$APPLE_APP_PASSWORD" \
  --wait

# ─── 4. Staple the ticket onto the .app ─────────────────────────────────────────
say "Stapling the notarization ticket"
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"

say "Done. $APP is signed, notarized, and stapled."
echo "    Distribute the .app (or wrap it in a .dmg and staple that too)."
rm -f "$ZIP"
