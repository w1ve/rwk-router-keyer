# RWK v2.0 — Remote WinKeyer

CW remoting over Tailscale mesh networking for amateur radio.

## What's in the Archive

| File | Run where | Purpose |
|------|-----------|---------|
| `RWKClient.exe` | Operator's PC | Paddle sensing, WinKeyer emulation, keyer engine, sidetone, port forwarding |
| `RWKStation.exe` | Remote radio site | Edge replayer, serial keying output, fail-safe system |
| `rwk-tailscale-sidecar.exe` | Both locations | Embedded Tailscale networking (must stay in the same directory as the main .exe) |
| `README.md` | — | This file |

## Important: Sidecar Placement

The `rwk-tailscale-sidecar.exe` file **must remain in the same directory** as
`RWKClient.exe` and `RWKStation.exe`. Both applications locate the sidecar
relative to their own executable path. Moving it to a subdirectory or a
different location will prevent Tailscale connectivity.

## Pairing Quickstart

### 1. Create a Tailscale network (tailnet)

Go to <https://login.tailscale.com> and create a tailnet if you don't already
have one.

### 2. Set up the Station

1. Extract the archive at the remote radio site.
2. Run `RWKStation.exe`.
3. On first launch, with no auth key configured, a login prompt appears:
   - Click **Open Browser** to sign in interactively with your Tailscale account.
   - Or click **Paste Auth Key Instead** to enter a reusable pre-auth key
     (generate one from the Tailscale admin console: Settings → Keys →
     Generate auth key → check Reusable).
4. Once authenticated, the sidecar's state directory persists the identity.
   Subsequent launches join the tailnet automatically — no re-auth needed.
5. The Station displays a **pairing code**. Note it — you'll give it to the
   operator.

### 3. Set up the Client

1. Extract the archive at the operator's location.
2. Run `RWKClient.exe`.
3. On first launch, a login prompt appears:
   - Click **Open Browser** to sign in interactively (recommended for personal
     use — no key to manage).
   - Or click **Paste Auth Key Instead** to enter the same auth key used at the
     Station (required for headless/unattended operation).
4. Enter the **pairing code** from the Station when prompted.
5. The Client joins the tailnet and authenticates to the Station via
   HMAC challenge/response using the shared pairing secret.
6. Once connected, the status bar shows the path type (Direct or DERP),
   round-trip time, and session state.

> **Note on auth keys vs interactive login:** Interactive browser login is the
> simplest path for a personal tailnet — no key to generate, copy, or expire.
> Auth keys are still available for headless stations (unattended remote sites)
> or automated deployments. If you paste a key, it's stored DPAPI-encrypted in
> the app's config for headless re-use.

### 4. Verify connectivity

- The Client status bar should show **Connected** with a path type.
- The Station UI should show **ARMED** (green banner).
- Key a few dits — you should hear local sidetone immediately, and the
  Station keying output should follow after the jitter buffer delay.

## Interface Wiring Guidance

Configure the Station's key and PTT output lines so that the **safe state
(line dropped / port closed) corresponds to key-up and PTT de-asserted**.

Concretely:

- If your radio keys on RTS-high, set **Key Line = RTS, Key Invert = No**.
  A dropped serial port leaves RTS low → key up → transmitter silent.
- If your radio keys on RTS-low (active-low interface), set
  **Key Line = RTS, Key Invert = Yes** so that the inverted output still
  yields key-up when the port is closed.

**Why this matters:** The fail-safe system (F6, F7, F8) forces all output
lines to their default/dropped state on serial port error, unhandled
exception, or application exit. If you wire polarity such that a dropped
line means key-down, a crash will hold your transmitter keyed until you
physically intervene.

## Port Forwarding & Non-Loopback Bind Warning

Port forwarding rules let you tunnel CAT control, audio, and other
protocols through the same Tailscale connection. By default, each rule's
Client-side listener binds to `127.0.0.1` (loopback) — only the Client PC
itself can reach it.

### ⚠️ Non-Loopback Bind Exposure Warning

If you change a rule's bind address to a LAN interface address or to
`0.0.0.0`, the forwarded port becomes reachable by **every host on your
local network**. This exposes an unauthenticated tunnel path into the
Station's network.

Use a non-loopback bind only when a device on your LAN (such as a
RemoteRig RRC Client box or a logger on a second PC) genuinely needs to
connect through the tunnel. Understand that any device on that network
segment can reach the forwarded port.

## Unverified Areas

The following areas have not been validated against physical hardware in
this release:

1. **RemoteRig RRC compatibility** — Generic TCP/UDP forward rules carry
   RRC traffic without payload inspection. This works only if the RRC
   protocol embeds no IP address or port number inside its payload and the
   RRC Client box initiates every flow. Compatibility has not been
   confirmed against physical RRC hardware. Rules of type "RemoteRig" are
   labeled as unverified in the UI.

2. **FlexRadio Discovery payload field layout** — The discovery broker
   rewrites the radio address and command port fields inside the FlexRadio
   6000-series discovery broadcast. The exact field layout (names,
   ordering, encoding, byte offsets, and broadcast port) is provisional
   and must be verified against a datagram captured from a real radio.
   The test suite includes a placeholder for that fixture.

## System Requirements

- Windows 10/11 x64
- No .NET runtime installation required (self-contained executables)
- Internet connectivity for Tailscale mesh networking
- Serial port(s) for paddle input and radio keying

## License & Contact

Copyright © 2026 by Gerry Hull, W1VE
