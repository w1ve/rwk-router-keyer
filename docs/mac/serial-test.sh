#!/usr/bin/env bash
#
# serial-test.sh — exercise the WinKeyer logger-decode path on a headless Mac using a virtual
# serial pair (no USB hardware).
#
# It creates two linked PTYs with socat. One end is the "logger" we drive; the other end is
# the port you point a Keyer's LOGGER input at. We write a WinKeyer2 host sequence to our end:
#
#     00 02        Admin / Open Host Mode   (the keyer replies with version byte 23 / 0x17)
#     02 19        Set Speed = 25 WPM
#     43 51 20     the text "CQ "          (keyed as CW through the arbiter's local source)
#
# What this proves on the VPS:
#   • serialport can enumerate + open a port on macOS (the IOKit path);
#   • the WinKeyer host decoder runs end to end and replies with the version byte;
#   • buffered text is keyed as CW.
# What it does NOT prove: a real DTR/RTS edge on a physical pin (a PTY has no modem lines) —
# that needs a hardware adapter on a physical Mac. See README §4.
#
# Usage:
#   chmod +x serial-test.sh && ./serial-test.sh
# Then, in the Keyer UI, select the printed "KEYER PORT" as the Logger (WinKeyer) input and
# watch the SEND indicator + sidetone. Press Ctrl-C here to tear the pair down.

set -euo pipefail

command -v socat >/dev/null 2>&1 || { echo "socat not found — run provision.sh first."; exit 1; }

LINKDIR="$(mktemp -d)"
OUR_END="$LINKDIR/logger"     # the end this script writes to
KEYER_END="$LINKDIR/keyer"    # the end you select in the Keyer UI

cleanup() {
  echo
  echo "Tearing down the virtual serial pair."
  [ -n "${SOCAT_PID:-}" ] && kill "$SOCAT_PID" 2>/dev/null || true
  rm -rf "$LINKDIR"
}
trap cleanup EXIT INT TERM

echo "Creating a virtual serial pair with socat…"
socat -d -d "pty,raw,echo=0,link=$OUR_END" "pty,raw,echo=0,link=$KEYER_END" &
SOCAT_PID=$!

# Wait for socat to create both symlinks.
for _ in $(seq 1 20); do
  [ -e "$OUR_END" ] && [ -e "$KEYER_END" ] && break
  sleep 0.25
done
if [ ! -e "$KEYER_END" ]; then
  echo "socat did not create the PTY links in time." >&2
  exit 1
fi

cat <<EOF

  Virtual serial pair ready.
    KEYER PORT (select this as the Logger input in the W1VE Keyer):
      $KEYER_END
    (this script drives the other end:)
      $OUR_END

  Select the KEYER PORT above as the Logger (WinKeyer) input, then press Enter here to send
  the test sequence (Admin Open, Set Speed 25, "CQ ").
EOF
read -r _

echo "Sending WinKeyer2 host sequence…"
# Admin Open, Set Speed=25 (0x19), then "CQ " as printable ASCII.
printf '\x00\x02\x02\x19CQ ' > "$OUR_END"

echo
echo "Sent. The Keyer should decode host-open (version 23 reply), set 25 WPM, and key 'CQ '."
echo "Watch the SEND indicator and listen for the sidetone. Ctrl-C to finish."

# Keep the pair alive and show anything the keyer writes back (e.g. the version byte).
echo
echo "Bytes returned by the keyer (hex):"
xxd < "$OUR_END" 2>/dev/null || cat "$OUR_END"
