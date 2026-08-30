# RWK v2.0 — Session Context

## Project Overview

RWK v2.0 is a Client/Station CW (Morse code) remoting and port forwarding system. A ham radio operator runs RWKClient.exe at their operating position with a paddle/keyer, and RWKStation.exe at the remote radio site. The two communicate over a Tailscale mesh network via a Go-based tsnet sidecar (`rwk-tailscale-sidecar.exe`). The system includes fail-safe protection, bidirectional TCP/UDP port forwarding, and FlexRadio VITA-49 discovery brokering.

**Repository**: https://github.com/w1ve/rwk-router-keyer
**Release**: https://github.com/w1ve/rwk-router-keyer/releases/tag/v1.0.0
**Published Release**: v1.0.4 (on GitHub)
**Current Working Version**: 1.0.5 — branch `v1.0.5-ipv6`, NOT yet merged/pushed to main or GitHub
**Spec location**: `.kiro/specs/rwk-v2/` (requirements.md, design.md, tasks.md)

> **RESUME NOTE (v1.0.5):** All v1.0.5 work is committed on branch `v1.0.5-ipv6`. The
> installer at `artifacts\release\RWK-Setup.exe` is built from this branch for local
> testing with a friend. v1.0.4 remains the published GitHub release. Do NOT push to
> GitHub or merge to main until explicitly told. See the "v1.0.5 Changes" section below
> for everything done this cycle.

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
- **Inno Setup 6** at `C:\Users\gerry\AppData\Local\Programs\Inno Setup 6\ISCC.exe` — per-user install, build with `/O<dir> /FRWK-Setup` flags
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

- `e:\AI\RWK\src\RWK.Client\MainForm.Designer.cs` — Client UI layout (3-tab: Keyer | Ham Router | Log)
- `e:\AI\RWK\src\RWK.Client\MainForm.cs` — Client code-behind (all event handlers)
- `e:\AI\RWK\src\RWK.Client\Controllers\ClientController.cs` — Client orchestration (pairing, forwarding, discovery, ARM, loopback test)
- `e:\AI\RWK\src\RWK.Client\Auth\TailscaleAuthWizard.cs` — 5-step Tailscale Auth Wizard UI
- `e:\AI\RWK\src\RWK.Shared\Auth\AuthWizardState.cs` — Auth Wizard state machine (testable without WinForms)
- `e:\AI\RWK\src\RWK.Client\IO\KeyboardPaddleInput.cs` — Global key hook keyboard paddle (7 presets)
- `e:\AI\RWK\src\RWK.Client\Wizard\VspeGenerator.cs` — VSPE XML generation for serial bridge
- `e:\AI\RWK\src\RWK.Client\Wizard\SerialPresets.cs` — 9 radio-type serial bridge presets
- `e:\AI\RWK\src\RWK.Client\LogService.cs` — Thread-safe visual log
- `e:\AI\RWK\src\RWK.Client\IO\HardwareWinKeyerHost.cs` — Hardware WinKeyer driver
- `e:\AI\RWK\src\RWK.Client\Discovery\ClientDiscoveryEmitter.cs` — FlexRadio rewrite + broadcast
- `e:\AI\RWK\src\RWK.Shared\Net\PortForwardManager.cs` — Port forward lifecycle, validation, tunnel delegates
- `e:\AI\RWK\src\RWK.Shared\Net\TsnetSidecarHost.cs` — Sidecar IPC (TCP + UDP outbound/inbound forwards)
- `e:\AI\RWK\src\RWK.Shared\Config\ForwardRule.cs` — Rule model with Direction + StationTargetAddress
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

### v1.0.4 Changes — FlexRadio Discovery Auto-Forwarding (August 2026)

Major improvement to the FlexRadio discovery relay: the Client's "Enable discovery re-emission" checkbox now automatically creates and manages all required port forward rules. No wizard step needed for Flex radios.

**Features:**
1. **Auto port forward creation** — Checking the Client's "Enable discovery re-emission" box auto-creates TCP 4992 (SmartSDR Command) and UDP 4991 (VITA-49 Stream) forward rules with `RuleType = FlexDiscovery`. Unchecking removes them.
2. **Auto StationTargetAddress** — When the first `discovery_announce` arrives from Station, the radio's actual IP is extracted from the VITA-49 payload and set as `StationTargetAddress` on both rules automatically.
3. **Station auto-enables discovery capture** — When Station receives `[Flex]`-prefixed rules from Client, it automatically starts the `StationDiscoveryListener` (UDP 4992). No separate checkbox needed on Station.
4. **Session lifecycle** — Rules are removed on unpair, re-created on next pair if checkbox is still checked. Checkbox state persists across restarts.
5. **Flex Forwarding indicator** — Station shows "Flex Forwarding ✓" (red checkmark) next to Unpair button when Flex rules are active.
6. **Removed FlexRadio from Wizard** — The `flex.smartsdr` entry removed from `radios.json` since it's now automatic.
7. **Client button renamed** — "Pair Keyer with Station" → "Pair with Station"
8. **Checkbox disabled until paired** — Prevents confusion; enables after successful pairing.

**Bug Fixes (discovery relay):**
- Fixed concurrent TCP control stream writes corrupting length-prefixed framing (added `_suppressRulePush` flag for batch operations)
- Fixed empty rule push not reaching Station (`PushForwardRulesToStationAsync` had early-return on zero rules)
- Fixed `FlexVitaDiscoveryCodec` to accept both class ID formats: original (`0x001C2D53:0x4CFFFF00`) and SmartUnlink/newer firmware (`0x00001C2D:0x534CFFFF`)
- Client discovery emitter now broadcasts on subnet broadcast addresses (not just 255.255.255.255)

**Infrastructure:**
- **Windows Firewall rules** — Both apps call `FirewallHelper.EnsureAppAllowed()` at startup. Installer (now `PrivilegesRequired=admin`) creates inbound allow rules for RWKClient.exe, RWKStation.exe, and rwk-tailscale-sidecar.exe via `netsh advfirewall`. Rules cleaned up on uninstall.
- **Inno Setup path**: `C:\Users\gerry\AppData\Local\Programs\Inno Setup 6\ISCC.exe`
- **Installer requires elevation** for firewall rules (was per-user/lowest, now admin with dialog override)
- **publish.ps1** updated to copy `Wizard/radios.json` to staging, splash.png in expected files list

**New Tool:**
- `tools/FakeFlex/` — Console VITA-49 discovery emulator. Broadcasts packets matching SmartUnlink's proven format (header `0x38500000`, class ID `0x00001C2D:0x534CFFFF`). Used to test the full discovery relay chain without a physical FlexRadio. Single-file self-contained exe at `artifacts/release/staging/tools/FakeFlex.exe`.

Files changed:
- `src/RWK.Client/Controllers/ClientController.cs` — SetDiscoveryEmitEnabled auto-creates/removes rules, _suppressRulePush, ForwardRulesChanged event, PushForwardRulesToStationAsync logging
- `src/RWK.Client/MainForm.cs` — checkbox disabled until paired, _suppressFlexCheckEvent, OnForwardRulesChanged handler
- `src/RWK.Client/MainForm.Designer.cs` — "Pair with Station" button text, checkbox starts disabled
- `src/RWK.Client/Discovery/ClientDiscoveryEmitter.cs` — ephemeral source port for broadcast socket
- `src/RWK.Client/Wizard/radios.json` — removed flex.smartsdr entry
- `src/RWK.Shared/Discovery/FlexVitaDiscoveryCodec.cs` — dual class ID acceptance
- `src/RWK.Shared/Net/FirewallHelper.cs` — NEW: netsh advfirewall rule management
- `src/RWK.Station/MainForm.cs` — auto start/stop discovery capture from Flex rules
- `src/RWK.Station/MainForm.Designer.cs` — removed hidden _flexDiscoveryGroup, added Flex Forwarding indicator
- `src/RWK.Station/Controllers/StationController.cs` — discovery capture/announce logging, firewall call
- `build/installer/rwk-setup.iss` — admin privileges, firewall rule creation/cleanup
- `build/release/publish.ps1` — Wizard folder copy, splash.png in expected files

## v1.0.3 Completed Work (Ham Router Architecture)

### UI Restructure
- 3-tab Client UI: **Keyer | Ham Router | Log** — port forwarding grid gets full form height
- Keyer tab: dit/dah LEDs integrated into keyer group (no separate Paddle group)
- 4 CW macro buttons (F1–F4) with Edit dialog and persistence
- Type-ahead CW input box
- Keyboard paddle with global key hook (7 presets), PageUp/PageDn speed ±2 WPM
- Sidetone layout fixed: absolute positioning, value labels centered above sliders
- Network section renamed "Pair with Station" with validation (Pair button greyed until valid IP + key set, red ✓ indicator)
- Form centered on active screen

### ForwardDirection & Reverse Forwards
- `ForwardDirection` enum: `ClientToStation`, `StationToClient` with `Direction` field on `ForwardRule`
- Reverse port forwards: Station→Client direction supported
- Direction column (→/←) in the port forwarding grid
- Forward rule dedup on Station (prevents duplicate sidecar registrations, detects conflicts)
- Port conflict check includes direction (same port, different directions = no conflict)
- `StationToClient` rules do NOT bind locally — they're pushed to Station for outbound forwarding
- Reverse rules skip `StartRuleListener` (no local socket bind)

### Keyer Bug Fixes
- Bug mode fixed: dah is manual keying (held = keyed), dits are automatic
- Straight mode fixed: dit contact works as straight key
- Keyer mode combo wiring fixed (was never connected to UI event)

### Tailscale Auth Wizard
- 5-step wizard: Welcome → Browser OAuth → Verify → Authorization Required → Success (+ key expiry warning)
- Auth Wizard state machine in `RWK.Shared.Auth` (testable without WinForms), UI per-app
- `SidecarAuthProvider` heuristic: NeedsAuth + no AuthUrl = Connecting (bridges timing gap)
- Wizard poll interval: 1.5s for snappy auth detection
- PLEASE WAIT overlay on startup until connected or wizard shown
- Delete Tailscale Auth: stop sidecar, delete state, restart, show wizard immediately (no app restart)
- Go sidecar: clear authURL on any state != NeedsLogin/NeedsMachineAuth

### Port Forward Wizard & Serial Bridge
- Serial bridge sub-flow with radio-type presets (9 types)
- VSPE XML generation, com2tcp command generation, enhanced readme
- Serial bridge VSPE generation: matching TCP port in rule + client .vspe + station .vspe + com2tcp.cmd
- Wizard catalog: **31 entries** (Icom, Kenwood, Yaesu, Flex, Elecraft, RemoteRig, 4O3A, Green Heron, SPE, SteppIR, Alpha, ACOM, generic forward/reverse TCP/UDP)

### System Tray & Multi-Client
- System tray icon: minimize to tray, click to restore (both Client and Station)
- KEYER BUSY: if second client tries to pair, plays "KEYER BUSY" in CW sidetone, shows red indicator

### Removed (Attempted but Deferred)
- **N1MM+ discovery relay**: Attempted symmetric relay (Client captures local N1MM broadcasts via raw socket, forwards to Station, Station re-emits with IP=127.0.0.1 and vice versa). Removed because:
  - N1MM holds port 2237 with `SO_EXCLUSIVEADDRUSE` (raw socket required elevation)
  - The sidecar can't have both inbound and outbound forwards on the same UDP port simultaneously
  - The architecture required too many workarounds
  - **Recommendation**: N1MM multi-op networking should use system Tailscale installed on both PCs — N1MM already supports Tailscale natively when installed
- `N1mmDiscoveryCodec` kept in `RWK.Shared/Discovery` for potential future use

### Key Technical Decisions (v1.0.3)
- `ForwardDirection.StationToClient` rules do NOT bind locally — pushed to Station for outbound forwarding
- Port conflict check includes direction (same port, different directions = no conflict)
- Reverse rules skip `StartRuleListener` (no local socket bind)
- Auth Wizard state machine is in `RWK.Shared.Auth` (testable without WinForms), UI is per-app
- `SidecarAuthProvider` heuristic: NeedsAuth + no AuthUrl = Connecting (bridges timing gap)
- Wizard poll interval: 1.5s for snappy auth detection
- Serial bridge VSPE generation: matching TCP port in rule + client .vspe + station .vspe + com2tcp.cmd

## Next Steps

1. **v1.0.4 — Opus Audio**: Low-latency audio streaming for radio monitoring/operating
2. **Linux/Pi Station**: Port RWK.Station to run on Raspberry Pi / Linux (headless, no WinForms)
3. **Test FlexRadio relay** with a physical Flex 6000-series radio
4. **Write remaining PBT tests** (optional but valuable for correctness confidence)
5. **Consider** adding the discovered radio list to the Client UI (currently just logs)
6. **Consider** Station-side allow/deny override per pushed rule

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

## Historical — v1.0.3 Plan (Ham Router Architecture) — COMPLETED

> This section preserved for reference. See "v1.0.3 Completed Work" above for final state.

### Branch: `v1.0.3-ham-router` (merged to main)

### Major Changes

1. **Client UI Restructure — TabControl**
   - Tab 1: "Keyer" — all keyer stuff (paddle, WinKeyer, sidetone, speed, mode) + Network Connection/Pairing UI (Station Address, Pair, Station Armed, Set Key). Required for keyer — can't do anything until Tailnet is connected.
   - Tab 2: "Ham Router" — Port forwarding UI with much larger grid, Wizard button, Import button, direction column. All port forwarding and routing configuration lives here.
   - Remove the existing "WinKeyer / Forwarding" + "Log" tab split. New split: Keyer | Ham Router | Log.

2. **Station-Side Inbound Forwards (eliminate Client-side listeners)**
   - Instead of Client binding localhost ports, the Station sidecar listens on its Tailscale IP
   - Client apps connect directly to `<Station Tailscale IP>:<port>`
   - Wizard generates rules as before, but they're registered as inbound forwards on the Station sidecar
   - No port conflicts with local services (Tailscale IP binding is exclusive to RWK)
   - Wizard output changes: "connect to 100.x.x.x:50001" instead of "connect to 127.0.0.1:50001"

3. **Reverse Port Forwards (Station → Client direction)**
   - ForwardRule model gets a `Direction` field: `ClientToStation` (default) or `StationToClient`
   - Station-to-Client: Station sidecar registers outbound forward → Client sidecar registers inbound
   - Use cases: N1MM broadcasts from Station logger to Client, license servers, cluster connections
   - Wizard and grid show direction indicator

4. **N1MM+ Network Discovery Relay**
   - Similar to FlexRadio VITA-49 discovery relay
   - Station captures N1MM discovery packets on port 12070 (format: `COMPUTER%LAN_IP%PORT%VERSION%CALLSIGN%%`)
   - Rewrites LAN_IP field to Station's Tailscale IP
   - Forwards to Client over control channel
   - Client re-emits on localhost:12070 so remote N1MM instance discovers the Station's N1MM
   - Enables N1MM multi-op networking over RWK without system Tailscale

5. **Port Grid Enhancements**
   - Larger grid (Tab 2 gives full form height)
   - Direction column (→ or ←)
   - Status column with probe results (reachable/unreachable/unknown)
   - Target validation: optional Station-side probe after rules are pushed

### Key Design Decisions
- Binding on Tailscale IP (`100.x.x.x`) eliminates manufacturer port conflicts with local services
- No TUN, no wintun, no admin elevation required
- Wizard and Import still work — just targeting Station inbound forwards instead of Client listeners
- Profile format compatible (existing profiles load, direction defaults to ClientToStation)

### Files Likely to Change
- `src/RWK.Client/MainForm.Designer.cs` — major restructure to TabControl (Keyer | Ham Router | Log)
- `src/RWK.Client/MainForm.cs` — reorganize event handlers into tab-specific regions
- `src/RWK.Client/Wizard/` — update output to reference Station Tailscale IP instead of localhost
- `src/RWK.Shared/Config/ForwardRule.cs` — add Direction field
- `src/RWK.Shared/Config/ClientConfig.cs` — any new config fields
- `src/RWK.Station/Controllers/StationController.cs` — register inbound forwards from pushed rules
- `src/RWK.TailscaleSidecar/forward.go` — verify inbound forward on specific Tailscale IP (not 0.0.0.0)
- New: `src/RWK.Station/Discovery/N1mmDiscoveryListener.cs` — capture port 12070 packets
- New: `src/RWK.Client/Discovery/N1mmDiscoveryEmitter.cs` — re-emit rewritten packets
- `src/RWK.Shared/Discovery/` — N1MM packet codec (parse/rewrite the %-delimited format)
- `Directory.Build.props` — version 1.0.3

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

---

## v1.0.5 Changes — IPv6 Support + Test-Driven Fixes (August 2026)

**Branch:** `v1.0.5-ipv6` (version 1.0.5 in `Directory.Build.props` + `build/installer/rwk-setup.iss`).
**Status:** All committed on the branch. Installer built at `artifacts\release\RWK-Setup.exe` for
local test. NOT merged to main, NOT pushed to GitHub. v1.0.4 is still the published release.

### 1. IPv6 Support
- **Go sidecar dual-listener UDP fix** (`src/RWK.TailscaleSidecar/forward.go`) — the sidecar now
  listens on both IPv4 and IPv6 for UDP forwarding (dual listeners) so edge/data works over v6 tailnets.
- **.NET `AddressExposure`** — address handling reworked to accept v4, v6, or both.
- **`IpAddressValidator`** — validates v4 dotted-quad and v6 (incl. `::` compression) input.
- **ADR 0002** — architecture decision record for the IPv6 approach (see `docs/adr/`).
- Tests: Go 26 pass; .NET 111+28 pass. Integration test is gated behind build tag `integration`
  and needs a tsnet auth key at `C:\Users\gerry\.rwk-tsnet-authkey`.
- **Decision:** Removed the custom `IpAddressTextBox` control. Pairing now uses a dropdown + paste
  (Import) workflow, not manual IP entry, so a structured octet/slider control was unnecessary.
  Kept `DataGridViewIpAddressColumn` + `IpAddressValidator` for the port-forward grid.

### 2. Station Import / Pair UX Overhaul
- Replaced the manual "Station Tailscale IP + IP box + Set Key" area with **"Station:" + a dropdown**
  of imported station names (default `(None)`), plus an **"Import..."** button.
- **Import dialog:** user pastes the Station Info string (copied from the Station), enters a name
  (20 char max). Imported stations are persisted and added to the dropdown.
- **Station side:** menu item renamed **"Show Pairing Key" → "Copy Station Info to Clipboard"**.
  Exported format is `TailscaleIP|Key`.
- **New models/stores:** `StationEntry` (Name|TailscaleIP|Key), `StationListStore` (persisted table).
- **"Pair with Station"** button is disabled until a valid station is selected in the dropdown, then
  turns **red** until actually paired.
- **Auto-unpair** when the station dropdown is changed while paired to a different station.

### 3. WinKeyer Hang Fix (N1MM logger input hang)
- **Symptom:** N1MM keying into the Station's logger input hung after a few transmissions with no
  recovery ("no keyer output configured" / stuck WK protocol).
- **Root cause:** concurrent serial writes to the WinKeyer port from two threads (ReaderLoop and
  KeyerLoop) corrupted the protocol.
- **Fix:** serialized all port writes through `WriteToPort` guarded by `_writeLock`, added an
  idle-timeout safety net, and a TextBuffer drain. (Rejected reworking the status protocol.)

### 4. Logger Host Start-Before-Armed Fix
- Persist the intent to start the logger host and retry on arm, so the logger input works even when
  configured before the station is armed. File: `src/RWK.Station/IO/StationLoggerHost.cs`.

### 5. Sound Card Picker Fix
- The client audio device combo was never wired. Added `OnAudioDeviceComboChanged` +
  `SetSidetoneDevice` so selecting a sound card actually changes the sidetone output device.

### 6. Toast Notifications (lower-right, title "RWK-Client")
- Balloon notifications via `_trayIcon` on: pair / unpair ("Paired/Unpaired with Station XXXX"),
  tailnet connect / disconnect, minimize ("Client Minimized. Click the icon in the system tray to
  restore"), and trapped system error ("System Error occurred. Please restart RWK-Client").
- Notifications fire whether the window is full-size or minimized.

### 7. Fixed (Non-Sizable) Window
- `FormBorderStyle.FixedSingle`, `MaximizeBox = false` — window keeps minimize + close only.

### 8. Inputs Panel Rework
- DCD=PTT (Footswitch) checkbox moved directly below the Paddle dropdown, left-aligned with it.
- Removed the "WinKeyer Loopback Test" button and its handler. (Left `RunWinKeyerLoopbackTestAsync`
  in ClientController as dead code to avoid destabilizing the `_loopbackTestActive` echo-path guard.)
- "Logger App" / "Hardware WinKey" radio buttons vertically stacked and left-aligned, with an italic
  help line below that changes per selection:
  - Logger App: "N1MM, DXLog, Wintest, etc"
  - Hardware WinKey: "Warning: one-character delay in sending.  Local sidetone muted."
- Removed the hidden speaker-mute button that was to the right of "Hardware WinKey".
- **Decision:** panels stay ENABLED when unpaired so the keyer can be tested locally (keying goes to
  sidetone only when unpaired). Rejected greying them out.

### 9. Digital Loggers Web Power Switch (DLI) — Wizard entry
- Added a DLI Web Power Switch entry to the Wizard's optional port-forward services (catalog v4,
  31 entries in `src/RWK.Client/Wizard/radios.json`).
- Notes remind the user to change the DLI setting so access is not limited to the local LAN.
- **AutoPing tip:** documented that the user can plug their router/modem into a DLI outlet and program
  the switch to ping Google/Cloudflare DNS; on repeated ping failure the DLI power-cycles the devices,
  potentially saving a trip to the remote site.

### 10. Install Path + Elevation
- Install path changed to `{autopf}\W1VE Software\RWK Router Keyer` (Program Files) with
  `UsePreviousAppDir=no` (needed because upgrades were reusing the old path).
- Elevation retained (required for `netsh advfirewall` firewall rules).

### 11. Wizard Port-Conflict Feedback
- `MergeWizardRules` now surfaces port-conflict errors to the user, plus a reminder that ports can be
  edited after the wizard (many hardware boxes like the RRC may not use the defaults).

### 12. Keyer Group Layout Rework (final UI polish — commit 4015cf4)
- WPM readout shrunk 28pt → 22pt so it no longer clips the weight row.
- Speed slider right-aligned to the group box (anchored L+R), moved down, widened.
- Weight row: "Weight:" label left-justified with the speed slider's left edge (X=105), value beside
  it, weight slider right-aligned (anchored right).
- Mode label + dropdown left-justified below the weight row.
- Paddle Rev / Keyboard Paddle, Paddle Keys, macros, type-ahead, Test TX shifted down into freed space.
- Keyer row given 72% of form height (was 65%); ClientSize 940×540 → 940×580.
- Sliders use anchoring for clean scaling on non-hi-DPI monitors (fixes 1920×1200 clipping).

### Key Files (v1.0.5)
- `src/RWK.Client/MainForm.cs`, `src/RWK.Client/MainForm.Designer.cs`
- `src/RWK.Client/Wizard/radios.json`
- `src/RWK.Station/IO/StationLoggerHost.cs`
- `src/RWK.TailscaleSidecar/forward.go`

### Build / Release Commands (v1.0.5)
- Go: `E:\go\bin\go.exe`; tests: `cd src/RWK.TailscaleSidecar; go test .` (26 pass). Integration test
  needs `C:\Users\gerry\.rwk-tsnet-authkey` and tag `integration`. If the Go build cache corrupts,
  delete `C:\Users\gerry\AppData\Local\go-build`.
- Installer (AV locks the output — build to a tmp name then rename):
  ```
  dotnet publish src/RWK.Client/RWK.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/release/publish-client
  dotnet publish src/RWK.Station/RWK.Station.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/release/publish-station
  Copy-Item artifacts\release\publish-client\RWKClient.exe artifacts\release\staging\RWKClient.exe -Force
  Copy-Item artifacts\release\publish-station\RWKStation.exe artifacts\release\staging\RWKStation.exe -Force
  Copy-Item artifacts\release\publish-client\Wizard\radios.json artifacts\release\staging\Wizard\radios.json -Force
  Start-Sleep 3; & "C:\Users\gerry\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "build\installer\rwk-setup.iss" /O"artifacts\release" /FRWK-Setup-tmp
  Start-Sleep 3; Remove-Item artifacts\release\RWK-Setup.exe -Force; Move-Item artifacts\release\RWK-Setup-tmp.exe artifacts\release\RWK-Setup.exe -Force
  ```
- `gh` CLI: `C:\tools\gh\bin\gh.exe` (authenticated as w1ve). Release repo: `w1ve/rwk-router-keyer`
  (remote `rwk-router-keyer`); also remote `origin` = `w1ve/rwk.git`.

### Next Steps (v1.0.5)
1. Local test with friend using `artifacts\release\RWK-Setup.exe`.
2. If good: bump/confirm version, merge `v1.0.5-ipv6` → main, push, and cut a v1.0.5 GitHub release.
3. (Deferred) Opus audio streaming; Linux/Pi Station port.
