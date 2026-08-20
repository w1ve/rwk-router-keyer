# RWK Router/Keyer v1.0.1

**Any Rig, Any Internet, Anytime**

CW remoting and port forwarding over Tailscale mesh networking for amateur radio operators.

RWK lets you operate your remote station's radio from anywhere with an internet connection. A paddle at your operating position keys the radio at the remote site with timing-accurate CW (within 2ms). TCP/UDP port forwarding tunnels CAT control, audio, and other protocols through the same encrypted Tailscale link. No public IP address, no dynamic DNS, no router port forwarding required.

**Repository:** https://github.com/w1ve/rwk-router-keyer  
**Author:** Gerry Hull, W1VE  
**License:** MIT

---

## What's New in v1.0.1

### New Features

- **Port Forward Wizard** — A guided 5-step wizard inside the Client that configures port forwarding rules for your specific radio and control software. Select your radio from the catalog, answer a few questions, and the Wizard creates all necessary rules, saves a portable profile, and generates a plain-text setup guide. Supported radios and interfaces:
  - Icom RS-BA1 v2 (direct to radio LAN port, or via server PC)
  - Icom native LAN protocol (wfview, Win4Icom)
  - Kenwood KNS direct (TS-890S with ARCP-890)
  - Kenwood ARHP conventional remote (TS-890S, TS-590S/SG, TS-990S)
  - Yaesu SCU-LAN10 (FTDX101, FTDX10, FT-710)
  - FlexRadio SmartSDR (6000/8000 series with discovery relay)
  - Elecraft K4/K4D remote (single TCP port)
  - RemoteRig RRC-1258 MkII
  - Generic RS-232 serial bridge (VSPE/com0com)
  - Generic TCP/UDP service
  - Ancillary services: Hamlib rigctld, rotctld, PstRotator, RDP, VNC

- **Import Profiles** — Load a previously saved `.rwkprofile.json` to restore or share configurations between PCs.

- **Station Logger WinKeyer Input** — The Station app now accepts WK2 protocol CW macros from logging software (N1MM+, DXLog) running on the Station PC via Remote Desktop. When the logger sends CW, it takes priority over remote paddle keying.

- **Hardware WinKeyer Support (WK2/WK3)** — The Client can drive a physical K1EL WinKeyer chip. Paddle CW is decoded by the chip and re-generated with proper timing for the remote Station. Supports WK3 chips (version 31+) with single-byte Admin Open response.

- **Improved Jitter Buffer for Satellite Links** — Direct path maximum delay raised from 150ms to 300ms for Starlink and other high-latency direct paths. The adaptive mode ramps the buffer automatically based on measured jitter.

### Bug Fixes

- Fixed: Client Tailscale login panel never appeared on fresh installs (state file guard, startup dismissal, state bouncing between NeedsAuth and Disconnected)
- Fixed: "Delete Tailscale Authorization" failed with file lock (sidecar now stopped before directory delete)
- Fixed: Hardware WinKey mode didn't send Admin Open when switching modes (mode change now reconnects the port)
- Fixed: WK3 chip (version >= 30) only sends one byte in Admin Open response (no longer waits for second byte)
- Fixed: 500ms delay after port open for WK chip initialization; Admin Close sent first to reset stale host mode
- Fixed: Paddle echo enabled (mode register 0x40) so WK3 echoes decoded paddle characters for remote transmission
- Fixed: Sidetone muted in Hardware WinKey mode (chip sidetone is the valid audio source)
- Fixed: COM port dropdowns now have "(None)" option; paddle and WinKeyer ports enforced unique
- Fixed: WinKeyer mode (Logger App / Hardware WinKey) persisted across restarts
- Fixed: Station Logger Input settings persisted across restarts (shutdown no longer wipes config)
- Fixed: Station Logger echo timing matched to Client (immediate echo on character receive for N1MM flow control)
- Fixed: DPI scaling issues with sidetone slider labels (Dock.Bottom) and mode controls
- Fixed: Installer now silently uninstalls previous version before installing

---

## Software Overview

RWK consists of three executables that work together:

| File | Run where | Purpose |
|------|-----------|---------|
| `RWKClient.exe` | Operator's PC | Paddle sensing, WinKeyer emulation, keyer engine, sidetone, port forwarding, Wizard |
| `RWKStation.exe` | Remote radio site | Edge replayer, serial keying output, fail-safe system, logger input |
| `rwk-tailscale-sidecar.exe` | Both locations | Embedded Tailscale networking (must stay in same directory) |

The sidecar provides a userspace Tailscale node — no system-wide Tailscale installation is needed. Each PC gets its own identity on your private Tailscale network (tailnet).

---

## The Client

![RWK Client](client.png)

The Client runs at your operating position. Its main features:

**Remote WinKeyer (top section):**
- **Paddle** — Dit/Dah indicators show paddle contact closure. Connect your paddle to a serial port (RTS/DTR lines).
- **Keyer** — Speed (WPM), weight, and mode (Iambic A/B, Ultimatic, Bug, Straight). The built-in software keyer generates timing-accurate CW edges.
- **Sidetone** — Local audio feedback via WASAPI. Select your sound device, adjust frequency and volume. In Hardware WinKey mode, local sidetone is muted (use the WinKeyer's own sidetone).
- **Input Ports** — Paddle port and WinKeyer port selection. Choose "Logger App" to emulate a WinKeyer for N1MM+/DXLog, or "Hardware WinKey" to drive a physical K1EL WK2/WK3 chip.

**Network Control (bottom section):**
- **Station Address** — The Tailscale IP of your remote Station (100.x.x.x).
- **Pair / Station Armed** — Pair with the Station using the shared pairing key. The "Station Armed" checkbox suppresses edge sending when unchecked.
- **Port Forwards** — TCP/UDP forwarding rules with enable/disable, bind address, and station target. The **Wizard** and **Import** buttons below the rule list open the Port Forward Wizard or load a saved profile.

**Status Bar:** Shows connection state (Direct/DERP path), round-trip time, buffer depth, and key state.

---

## The Station

![RWK Station](station.png)

The Station runs at the remote radio site. Its main features:

- **ARMED/SAFE Banner** — Green = armed and keying. Red = SAFE latch triggered (all lines forced inactive).
- **Re-Arm** — Clears the SAFE latch after a fail-safe event.
- **KEY/PTT LEDs** — Real-time indicators of the keying output state (50ms polling, 200ms sticky).
- **Logger Input** — Enable checkbox + COM port for WK2 protocol from logging software on the Station PC.
- **Keying Output** — COM port selection, Key Line (RTS/DTR), PTT Line (RTS/DTR/None), polarity inversion.
- **Session** — Shows the paired Client's Tailscale IP, session duration, and Unpair button.
- **Forward Rules** — Displays rules pushed from the Client with enabled/disabled state.

---

## The Port Forward Wizard

![Port Forward Wizard](wizard.png)

The Wizard is the fastest way to configure port forwarding for your radio. Access it from the **Wizard** button in the Port Forwards panel or from the File menu.

### How It Works

1. **Select Radio** — Choose your radio or interface from the searchable catalog. Generic options appear at the bottom for unlisted devices.
2. **Control Path** — Confirms the selected entry and shows confidence level.
3. **Station Target** — Enter the IP address of the radio/device on the Station's LAN. If the device runs on the Station PC itself, select "On the Station PC" and it uses 127.0.0.1.
4. **Extras** — Optionally add ancillary services (rigctld, rotctld, RDP, VNC, PstRotator).
5. **Review & Apply** — See all rules, conflict detection results, and choose whether to enable immediately.

On Apply, the Wizard:
- Writes rules directly into the Port Forwards grid (merge by name — re-running updates in place)
- Saves a `.rwkprofile.json` to `%LOCALAPPDATA%\RWK Router Keyer\profiles\`
- Generates a plain-text setup guide and opens it in Notepad with step-by-step instructions

### The radios.json Catalog

The Wizard's knowledge lives in `Wizard\radios.json`, a data file shipped alongside `RWKClient.exe`. Each entry contains:

- **id** — Unique identifier (e.g. `icom.rsba1.radio-lan`)
- **vendor, displayName, models** — For search and display
- **forwards** — Port definitions with protocol, port number, role, and `portIdentity` (required/floating/unknown)
- **prompts** — Per-input explanatory text with `why`, `howToFind`, and `ifWrong` fields
- **clientNotes, stationNotes, radioNotes** — Checklist items for the generated setup guide
- **confidence** — `verified` (vendor docs), `community` (field reports), or `unverified` (best guess)

**Contributing:** If your radio or interface isn't in the catalog, you can add it! The catalog is plain JSON — submit a pull request to the repository with your entry. The most valuable contribution is the exact menu path to find settings on your specific radio model (`howToFind` field).

---

## Technical: Paddle Input vs Hardware WinKeyer

### Paddle Input (recommended for remote-only operation)

When you connect a paddle to the Client's Paddle port, RWK's built-in software keyer handles all iambic timing locally with 1ms resolution. Edges are generated with QPC timestamps and sent to the remote Station where they're replayed at the correct absolute time via the jitter buffer.

**Advantages:**
- Local sidetone plays in real time (zero delay) through the selected sound device
- Sidetone can share the same sound card as your radio's receive audio — mix CW feedback with what you're hearing
- Full speed range (5-60 WPM) with proper weighting
- No additional hardware needed beyond a paddle and a serial port

### Hardware WinKeyer (for operators with a K1EL WK2/WK3)

When you select "Hardware WinKey" mode, RWK drives a physical K1EL WinKeyer chip. The chip decodes paddle contacts using its own iambic logic, then echoes the decoded ASCII characters back to RWK. RWK re-generates the CW with proper timing and sends it to the remote Station.

**Trade-offs:**
- There is a **one-character decode delay** — the chip must finish decoding the character before RWK can send it. This means the remote Station hears each character one element-time after you finish keying it.
- Because of this delay, **local software sidetone is muted** (it would sound wrong). You must use the WinKeyer's own sidetone output, which plays in real time as you key.
- The WinKeyer sidetone is a separate audio output — it **cannot be mixed with receive audio** on the same sound card unless you use an external audio mixer or virtual audio cable.
- The red indicator "Muted (HW WK sidetone)" appears in the Sidetone section when this mode is active.

**When to use Hardware WinKey mode:**
- You prefer the feel of K1EL's iambic logic over the software keyer
- You have a WinKeyer with a built-in sidetone speaker/output and don't need mixed audio
- You want the WinKeyer's speed pot or other hardware features

---

## Installation

### Requirements

- Windows 10 or 11, 64-bit
- Internet connectivity (Starlink, cable, cellular — any connection works)
- A Tailscale account (free for personal use at https://tailscale.com)
- Serial port(s) for paddle input and/or radio keying (USB-to-serial adapters work)

### Running the Installer

1. Download `RWK-Setup.exe` from the GitHub release.
2. Run it — no administrator rights are required. It installs to `%LOCALAPPDATA%\RWK Router Keyer\`.
3. If a previous version is installed, it's automatically uninstalled first.
4. Choose components: Client only, Station only, or both.
5. Optional: create desktop shortcuts.

The installer places the three executables plus `Wizard\radios.json` in the install directory.

---

## Tailscale Authentication (The Hardest Part)

Both the Client and Station need to join your Tailscale network (tailnet). This is a one-time setup per machine.

### Option A: Interactive Browser Login (recommended)

1. Launch RWKClient.exe or RWKStation.exe.
2. A login panel appears with two buttons: **Open Browser** and **Paste Auth Key**.
3. Click **Open Browser**. Your default browser opens to the Tailscale login page.
4. Sign in with your Tailscale account (Google, Microsoft, GitHub, or email).
5. Authorize the device. The browser shows "Success" and you can close it.
6. Return to RWK — the login panel dismisses automatically within a few seconds.
7. The status bar shows "Connected" with a Tailscale IP (100.x.x.x).

The identity is persisted in `%APPDATA%\RWK\tailscale\` — subsequent launches connect automatically without re-authentication.

### Option B: Auth Key (for headless/remote machines)

If the machine has no browser (headless Station at a remote site), or if browser login doesn't work (e.g. Starlink with slow initial DNS):

1. On any machine with a browser, go to https://login.tailscale.com/admin/settings/keys
2. Click **Generate auth key**. Check "Reusable" if desired. Copy the key (starts with `tskey-auth-...`).
3. In RWK, click **Paste Auth Key Instead**.
4. Paste the key and click Submit.
5. The sidecar authenticates directly via HTTPS — no browser needed.

### Troubleshooting Auth

- **"Waiting for browser login..." stays forever:** Use the Paste Auth Key method instead. This bypasses the browser OAuth flow entirely.
- **Panel doesn't appear:** The sidecar may already have a valid persisted identity. Check the status bar — if it shows "Connected", auth is already done.
- **To reset auth:** File menu → Delete Tailscale Authorization. This stops the sidecar, deletes the state directory, and clears the saved key. Restart to re-authenticate.

---

## Pairing the Client and Station

Once both machines are on the same tailnet:

1. **Note the Station's Tailscale IP** — shown in the Station's Session panel as "Station IP: 100.x.x.x". Click the copy button to copy it.
2. **On the Client**, enter this IP in the "Station Address" field.
3. **Get the pairing key** — On the Station, File menu → Show Pairing Key. An 8-character code is displayed.
4. **On the Client**, click "Set Key" and enter the pairing code.
5. **Click "Pair"** — The Client authenticates to the Station using HMAC-SHA256 with the shared pairing secret. On success, the status shows "Paired" and the Station shows the Client's address.

The pairing key is generated on the Station's first run and stored DPAPI-encrypted. Both sides must use the same key. Once paired, the connection re-establishes automatically on subsequent launches.

---

## Interface Wiring

Configure the Station's keying output so that the **safe state (line dropped / port closed) equals key-up and PTT off**.

- If your radio keys on RTS-high: set Key Line = RTS, Key Invert = No.
- If your radio keys on RTS-low (active-low): set Key Line = RTS, Key Invert = Yes.
- PTT Line can be set to DTR, RTS (if not used for key), or None.

**Why:** The fail-safe system forces all lines to their default/dropped state on any error. If polarity is wrong, a crash could leave your transmitter keyed.

---

## Port Forwarding

Port forwarding rules tunnel TCP/UDP traffic through the Tailscale connection. Rules bind a local port on the Client and relay traffic to a target address on the Station's LAN.

By default, rules bind to `127.0.0.1` (only the Client PC can reach them). Changing to `0.0.0.0` exposes the port to your entire LAN — use only when a hardware device (like a RemoteRig RRC) needs to connect.

Use the **Wizard** for guided setup, or add rules manually with **+ Add**.

---

## Acknowledgments

Thanks to **Jim Talens, N3JT**, for his invaluable feedback, testing across multiple configurations, and patience with the many iterative builds during development.

---

## Feedback

Questions, bug reports, feature requests, or catalog contributions are welcome:

**Email:** gerry@w1ve.com  
**GitHub Issues:** https://github.com/w1ve/rwk-router-keyer/issues

73 de W1VE
