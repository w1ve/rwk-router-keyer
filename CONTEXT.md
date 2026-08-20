# RWK v2.0 — Session Context

## Project Overview

RWK v2.0 is a Client/Station CW (Morse code) remoting and port forwarding system. A ham radio operator runs RWKClient.exe at their operating position with a paddle/keyer, and RWKStation.exe at the remote radio site. The two communicate over a Tailscale mesh network via a Go-based tsnet sidecar (`rwk-tailscale-sidecar.exe`). The system includes fail-safe protection, bidirectional TCP/UDP port forwarding, and FlexRadio VITA-49 discovery brokering.

**Repository**: https://github.com/w1ve/rwk-router-keyer
**Release**: https://github.com/w1ve/rwk-router-keyer/releases/tag/v1.0.0
**Spec location**: `.kiro/specs/rwk-v2/` (requirements.md, design.md, tasks.md)

## Three Independent Features

1. **Remote WinKeyer** — timing-accurate CW remoting (±2ms) with fail-safe protection. Requires pairing.
2. **Port Forwarding** — bidirectional TCP/UDP tunneling to Station LAN devices. Does not require pairing.
3. **FlexRadio Discovery Relay** — VITA-49 broadcast intercept and rewrite. No SmartLink needed.

## Completed Work

### Shared Core Library (RWK.Shared)
- Edge protocol codec (RwkPaddleFrame, EdgeEntry, EdgeSequenceTracker)
- Config models with DPAPI encryption for secrets
- ForwardRule with BindAddress + StationTargetAddress + RuleType
- FlexVitaDiscoveryCodec (VITA-49 preamble + ASCII key=value parsing, endpoint rewrite)
- DiscoveredRadio, DiscoveryAnnounce, IDiscoveryListener/Emitter contracts
- IPortForwardManager with TunnelDial and UdpTunnelBind delegates
- Port validation (duplicates, reserved ports 7373/41373)
- 232+ tests passing

### Client Application (RWK.Client)
- PaddleInputPoller (THREAD_PRIORITY_HIGHEST, 1ms polling, QPC timestamps, debounce)
- SoftWinKeyerCore (all 5 keyer modes, dedicated timing thread)
- WinKeyerProtocolHost (Logger App mode — emulates WK2 for N1MM/DXLog)
- HardwareWinKeyerHost (Hardware WinKey mode — drives K1EL WK2/3 chip)
- WinKeyer mode selection (Logger App / Hardware WinKey radio buttons)
- WinKeyer Loopback Test (multi-speed WK2 protocol byte injection, sidetone only)
- LocalSidetoneEngine (WASAPI shared-mode, 20ms buffer, raised-cosine envelope)
- ClientController orchestration (paddle→keyer→sidetone→edge→Tailscale, heartbeat 250ms)
- Client-side Station ARM/DISARM toggle (suppresses edge sending)
- Port forwarding: TCP tunnel via CreateOutboundForwardAsync, UDP tunnel via CreateOutboundUdpForwardAsync
- Dynamic rule management (AddForwardRule/RemoveForwardRule/SetForwardRuleEnabled with live push)
- Enable Selected / Disable Selected / Enable All / Disable All buttons
- Row locked when rule is active (Listening/Active)
- ClientDiscoveryEmitter (receives announcements, rewrites IP/port, broadcasts on Client LAN)
- LogService (ConcurrentQueue, BelowNormal drain thread, 100ms batch, 5000 line cap)
- TabControl UI (WinKeyer/Forwarding tab + Log tab with None/Descriptive/Debug levels)
- Interactive Tailscale login panel (Open Browser + Paste Auth Key, auto-dismiss)
- Station address saved immediately on Pair click
- Single-instance enforcement (mutex)

### Station Application (RWK.Station)
- StationKeyingOutput (serial port key/PTT with polarity inversion)
- EdgeReplayer (TIME_CRITICAL thread, jitter buffer, anchor logic, adaptive EWMA)
- FailSafeMonitor (F1-F10 with correct latch policy)
- Heartbeat processing even without keying output (prevents false F2 on connect)
- StationController (9-step startup, session→replayer, fail-safe, pairing key generation)
- Pairing key (8-char, auto-generated on first run, DPAPI-encrypted)
- Unpair button (closes TCP session, Client detects EOF → "AS UNPAIRED" sidetone)
- Control channel message loop (reads length-prefixed JSON messages for session lifetime)
- Forward rules grid (shows pushed rules with ✓/✗ enabled state, Client/Station ports)
- StationDiscoveryListener (UDP 4992 with SO_REUSEADDR, VITA-49 codec, forwards to Client)
- KEY/PTT LED indicators (22pt, 50ms polling, 200ms sticky)
- Interactive Tailscale login panel (shows proactively on NeedsAuth)
- Single-instance enforcement (mutex)

### Tailscale Integration
- Go sidecar built and verified (tsnet.Server for userspace networking)
- True UDP datagrams for edge data (not TCP, not WebSocket)
- TCP port forwarding (outbound/inbound via sidecar API)
- UDP port forwarding (out-udp/in-udp via sidecar ListenPacket, NAT-style sessions)
- TsnetSidecarHost (process supervision, handshake, status polling, stdin lifetime)
- TailscaleNode facade (SendEdgeAsync, ConnectControlAsync, StateChanged)
- SidecarPath resolver (AppContext.BaseDirectory, never Assembly.Location)
- SidecarFailureHandler (asymmetric: Client degrades, Station refuses arm)
- Interactive login flow + Delete Authorization menu item

### Port Forwarding (Fully Functional)
- PortForwardManager with TCP relay (half-close) and UDP relay (NAT sessions, 60s idle timeout)
- TunnelDial delegate wired after session establishment (TCP via outbound forward)
- UdpTunnelBind delegate wired (UDP via outbound-udp forward on sidecar)
- StationTargetAddress on ForwardRule (Station dials specified LAN address, not just localhost)
- Dynamic add/remove/enable/disable while connected (re-push to Station)
- Control channel rule push (length-prefixed JSON, full replace on each push)
- Port validation (duplicates, reserved ports, port range check)
- ForwardRuleStatusChanged event → grid Status column + log
- Go sidecar: out-udp and in-udp forward kinds with bidirectional UDP relay

### FlexRadio Discovery Relay
- FlexVitaDiscoveryCodec: parses 28-byte VITA-49 preamble (stream ID 0x800, Flex OUI class ID)
- ASCII key=value payload extraction (serial, model, ip, port, status, etc.)
- Endpoint rewrite: replaces ip= and port= fields, recomputes packet length, verification re-parse
- StationDiscoveryListener: UDP 4992 with SO_REUSEADDR, codec validation, DiscoveryCaptured event
- ClientDiscoveryEmitter: rewrites payload, broadcasts on Client LAN (255.255.255.255:4992)
- Control channel bidirectional: Station sends discovery_announce (base64), Client processes via WatchControlStreamAsync
- Both UI checkboxes enabled and wired

### Product Features
- rwk.ico as application icon (both apps + form icon)
- Auto-increment version 1.0.0.X (DayOfYear+Hour build number via Directory.Build.props)
- Title bars: "RWK Router/Keyer [Client|Station] Version X.X.X.Y — Any Rig, Any Internet, Anytime"
- File menu: About RWK / Show Pairing Key (Station) / Delete Tailscale Authorization / Go to Tailscale Admin Page / Exit
- About dialog: splash.png, version, copyright, MIT license, GitHub URL
- MIT LICENSE file in repo root
- MIT SPDX comment header on all 240+ source files (.cs and .go)
- Inno Setup installer (per-user, no admin rights, %LOCALAPPDATA%\RWK Router Keyer)
- GitHub release v1.0.0 with RWK-Setup.exe asset

### Integration Tests
- 28 integration tests (loopback timing ±2ms, fail-safe battery, N1MM+ conformance, network loss)

### Deployment
- Successfully deployed Client + Station on Windows 11 via Starlink in Malawi (remote site)
- Tailscale interactive auth (browser + paste-key fallback) verified working on both apps

## Key Technical Decisions

- **.NET 9.0**, Windows x64, WinForms
- **Go 1.26.5** toolchain at E:\go for sidecar
- **Single-file publish** (self-contained, no .NET runtime needed on target)
- **Tailscale via tsnet** (userspace, no system Tailscale install required)
- **True UDP datagrams** for edge data and port forwarding
- **DPAPI** for secret encryption (auth keys, pairing secrets)
- **No dark theme** — use Windows system colors for proper high-contrast support
- **No self-extracting archive** — Inno Setup installer to avoid AV flags
- **Sidecar is sibling file** — never embedded, never extracted at runtime
- **Assembly.Location forbidden** for path resolution (empty in single-file bundles)
- **Pairing key** — 8-char alphanumeric, generated on Station first run, HMAC-SHA256 auth
- **Control channel** — bidirectional TCP stream with length-prefixed JSON messages
- **FlexRadio** — VITA-49 discovery intercept/rewrite, no SmartLink dependency
- **Jitter buffer** — Direct band 30-300ms (default 60ms), DERP band 100-500ms (default 200ms), adaptive EWMA formula with late-edge auto-bump

## User Preferences & Corrections

- No dark theme — Windows system colors only
- No refresh buttons — auto-detect via OS events/timers
- COM ports sorted numerically (COM1, COM2, ..., COM10)
- "Input Ports" (not "Ports")
- Top section = "Remote WinKeyer" group, bottom = "Network Control" group
- Pair/Unpair terminology (not Connect/Disconnect)
- Port forward: Enable/Disable buttons (not checkbox in grid)
- Forward rules default to OFF when added
- Row locked (read-only) when rule is active
- Station shows ✓/✗ for enabled state in forwarded rules grid
- FlexRadio discovery: enable on both Station and Client separately
- Single-instance enforcement with MessageBox on duplicate launch
- Log only state transitions (not every poll)
- Speed persisted on every change

## Files to Read on Resume

- `e:\AI\RWK\src\RWK.Client\MainForm.Designer.cs` — Client UI layout (TabControl, forward grid, sidetone, discovery)
- `e:\AI\RWK\src\RWK.Client\MainForm.cs` — Client code-behind (all event handlers)
- `e:\AI\RWK\src\RWK.Client\Controllers\ClientController.cs` — Client orchestration (pairing, forwarding, discovery, ARM, loopback test)
- `e:\AI\RWK\src\RWK.Client\LogService.cs` — Thread-safe visual log
- `e:\AI\RWK\src\RWK.Client\IO\HardwareWinKeyerHost.cs` — Hardware WinKeyer driver
- `e:\AI\RWK\src\RWK.Client\Discovery\ClientDiscoveryEmitter.cs` — FlexRadio rewrite + broadcast
- `e:\AI\RWK\src\RWK.Shared\Net\PortForwardManager.cs` — Port forward lifecycle, validation, tunnel delegates
- `e:\AI\RWK\src\RWK.Shared\Net\TsnetSidecarHost.cs` — Sidecar IPC (TCP + UDP outbound/inbound forwards)
- `e:\AI\RWK\src\RWK.Shared\Config\ForwardRule.cs` — Rule model with StationTargetAddress
- `e:\AI\RWK\src\RWK.Shared\Discovery\FlexVitaDiscoveryCodec.cs` — VITA-49 parser + endpoint rewriter
- `e:\AI\RWK\src\RWK.TailscaleSidecar\forward.go` — Go sidecar TCP + UDP forwarding (out/in/out-udp/in-udp)
- `e:\AI\RWK\src\RWK.Station\MainForm.cs` — Station code-behind (session, keying config, discovery)
- `e:\AI\RWK\src\RWK.Station\Controllers\StationController.cs` — Station orchestration (pairing key, control messages, discovery listener)
- `e:\AI\RWK\src\RWK.Station\Discovery\StationDiscoveryListener.cs` — UDP 4992 capture

### Bug Fixes — Tailscale Interactive Auth (August 2026)

Fixed critical bug where the Client app's Tailscale login panel never appeared on fresh installs (first reported on Windows 11 in Malawi with Starlink). Root causes:

1. **State bouncing** — Go sidecar never cleared `authURL` after successful auth. The C# status polling saw `authUrl` disappear only on `Connected` state, but the sidecar kept reporting the stale URL. Fixed in `node.go`: `n.authURL = ""` when `BackendState == "Running"`.

2. **NeedsAuth override only on first transition** — `TsnetSidecarHost.ApplyStatusUpdate()` only overrode state to `NeedsAuth` when `authUrl` transitioned from empty→non-empty. On subsequent polls the raw state mapped to `Disconnected`. Fixed: override applies on EVERY poll where `authUrl` is present.

3. **Startup dismissal** — `UpdateStatusForState(Connecting)` called `DismissLoginPanel()` during form init, setting `_loginDismissed = true` before `AuthUrlAvailable` ever fired. Fixed: `Connecting` state no longer calls `DismissLoginPanel()` (only `Connected` does).

4. **HasPersistedTailscaleState guard** — `OnControllerAuthUrlAvailable` checked if `tailscaled.state` existed on disk and skipped showing the panel. This was wrong when the state file is stale/invalid. Guard removed.

5. **Delete Authorization file lock** — `Directory.Delete()` failed because the sidecar process held `tailscaled.log1.txt` open. Fixed: both Client and Station now stop the sidecar process before deleting the state directory.

6. **Panel layout** — Increased login panel from 420×180 to 460×200 for proper rendering at Windows 11 DPI scaling.

Files changed:
- `src/RWK.Shared/Net/TsnetSidecarHost.cs` — authUrl state override logic
- `src/RWK.Client/MainForm.cs` — login panel show/dismiss/layout
- `src/RWK.Client/Controllers/ClientController.cs` — added `StopSidecarAsync()`
- `src/RWK.Station/MainForm.cs` — same dismiss/delete fixes
- `src/RWK.TailscaleSidecar/node.go` — clear authURL on Running

### Bug Fixes — DPI Layout & Starlink Jitter (August 2026)

Fixed UI layout issues on Windows 11 with DPI scaling, and choppy CW on high-latency Starlink path (290ms RTT Direct).

1. **Sidetone Frequency/Volume labels not visible** — Value labels (e.g. "600 Hz", "70%") used `Dock = Top` inside panels where the slider also docked top. At higher DPI the labels got pushed below visible area. Fixed: changed to `Dock = Bottom`.

2. **TestTX button cutoff** — Mode combo and TestTX button in the Keyer group extended past the group border at scaled DPI. Reduced combo width (120→100) and repositioned button (X=200→168) to fit within 30% column allocation.

3. **Choppy code at 290ms RTT** — The Direct path jitter buffer maximum was 150ms. Starlink has high jitter (30-80ms), and the adaptive formula (`base + 2×jitter_ewma`) was clamped at 150ms, causing late edges and choppy keying. Raised `DirectMaxDelay` from 150ms to 300ms. The adaptive mode now has room to ramp the buffer for satellite links.

Files changed:
- `src/RWK.Client/MainForm.Designer.cs` — slider value label Dock, mode combo/button positions
- `src/RWK.Station/Replay/JitterBuffer.cs` — DirectMaxDelay 150→300ms
- `tests/RWK.Station.Tests/Replay/JitterBufferTests.cs` — updated clamp expectation
- `tests/RWK.Station.Tests/Replay/JitterBufferAdaptiveTests.cs` — updated max band assertions

## Next Steps

1. **Test FlexRadio relay** with a physical Flex 6000-series radio
2. **Write remaining PBT tests** (optional but valuable for correctness confidence)
3. ~~**Live network tests** (33.x) when separate machines + auth key available~~ ✓ Verified on Starlink from Malawi
4. **Consider** adding the discovered radio list to the Client UI (currently just logs)
5. **Consider** Station-side allow/deny override per pushed rule

## Planned Work — Station Logger WinKeyer Input

### Motivation
Hams running their logging program over Remote Desktop on the Station PC want to send CW macros from the logger locally, while using RWK for remote paddle keying. The Station needs a secondary COM input that accepts WinKeyer protocol from the logger and drives the same keying output.

### Design
- **UI location:** New frame/group to the right of the KEY/PTT LED indicators in the Station MainForm, containing:
  - Checkbox: "Enable Logger Input"
  - ComboBox: COM port dropdown (auto-updated, same enumeration pattern as existing COM port combos)
  - The COM port list MUST exclude whichever port is selected for the Keying Output (avoids conflict)
- **Protocol:** WinKeyer2 emulation — same protocol host logic as the Client's `WinKeyerProtocolHost` (Logger App mode). Receives text from the logger, generates CW edges internally.
- **CW generation:** Use `SoftWinKeyerCore` (same keyer engine as Client) to convert characters to key/unkey timing. Speed comes from the logger via WK2 speed command (not from the Client's speed setting).
- **Output:** Keys the same `StationKeyingOutput` (serial port KEY/PTT) that the remote paddle edges use. When logger is sending, remote paddle edges are temporarily suppressed (logger has priority, or interlock).
- **Priority/interlock:** Logger CW takes precedence over remote paddle. While logger is actively sending (buffer not empty), incoming edge frames from the Client are queued or discarded. When logger finishes, remote keying resumes.
- **Lifecycle:** Enabled/disabled at runtime via the checkbox. Opening/closing the COM port dynamically. If the keying output port changes, refresh the excluded-port filter.

### Files likely to change
- `src/RWK.Station/MainForm.cs` + `MainForm.Designer.cs` — new UI group
- `src/RWK.Station/Controllers/StationController.cs` — orchestrate logger WK host alongside edge replayer
- New file: `src/RWK.Station/IO/StationWinKeyerHost.cs` — WK2 protocol listener on serial port (can reuse/adapt `WinKeyerProtocolHost` from Client)
- New file: `src/RWK.Station/IO/StationSoftKeyer.cs` — local CW generation (or reuse `SoftWinKeyerCore` from Client)
- `src/RWK.Shared/Config/StationConfig.cs` — add logger port config fields

## Planned Work — Port Forward Wizard (Client)

### Motivation
Operators don't know which ports to forward for their radio/software combination. The Wizard asks 3-5 questions and produces live port forward rules, a saved JSON profile, and a plain-text setup guide.

### Spec
Full specification in `RWK-Wizard-SPEC.md` (root folder). Key design points:

### Architecture
- **In-process** inside RWK Client, not a separate tool. Can validate against live rule set and socket state.
- **Entry point:** "Wizard" button to the right of "Enable All" in the Port Forwards panel. Also accessible from File menu.
- **5-step flow:** Radio → Control Path → Endpoint Location → Extras → Review & Apply
- **Catalog-driven:** `radios.json` shipped alongside the app, versioned independently, community-contributable.

### Three Outputs
1. **Live rules** — written directly into the Port Forwards grid (merge by name, idempotent re-runs)
2. **JSON profile** — `[radioname].rwkprofile.json` in `%LOCALAPPDATA%\RWK Router Keyer\profiles\`
3. **Plain-text setup guide** — `[radioname]-readme.txt`, opened immediately in Notepad. Hard-wrapped at 76 cols, CRLF, ASCII, no Markdown.

### Key Concepts
- **portIdentity** — `required` (client port must equal station port), `floating` (can remap), `unknown` (treat as required)
- **Explanatory copy** — every prompt carries `why`, `howToFind`, `ifWrong` fields from the catalog
- **confidence** — `verified` (vendor docs), `community` (field reports), `unverified` (best guess, shows banner)
- **Conflict detection** — checks existing rules, trial socket bind, and optional Station reachability probe
- **Undo** — snapshot rules before Apply, offer "Undo wizard changes" until next manual grid edit

### Seed Catalog (radios.json)
- Icom RS-BA1 v2 (UDP 50001-50003, radio-lan and server-pc variants)
- Icom native LAN / wfview (same ports)
- Kenwood KNS direct / TS-890S (TCP 60000 + UDP 60001)
- Kenwood ARHP conventional (TCP 50000 + UDP 33550)
- Yaesu SCU-LAN10 (UDP 50000-50003)
- FlexRadio SmartSDR (TCP 4992 + UDP 4991, requires discovery relay)
- Elecraft K4 remote (TCP 9205 only)
- RemoteRig RRC-1258 MkII (UDP 13000-13002 + optional TCP 80, needs bindAddress 0.0.0.0)
- Ancillary services: rigctld, rotctld, PstRotator, N1MM+ broadcasts, RDP, VNC, HTTP
- Generic RS-232 serial bridge (TCP tunnel + VSPE/com0com helper files)

### Files likely to create/change
- New folder: `src/RWK.Client/Wizard/` — WizardForm, WizardSteps, CatalogLoader, ProfileManager, ConflictDetector, ReadmeGenerator
- New file: `src/RWK.Client/Wizard/radios.json` — seed catalog
- `src/RWK.Client/MainForm.cs` + `MainForm.Designer.cs` — Wizard button, File menu entry
- `src/RWK.Shared/Config/ForwardRule.cs` — may need metadata fields (role, portIdentity, notes) for round-trip with profiles
- `src/RWK.Shared/Config/ClientConfig.cs` — profile storage path
