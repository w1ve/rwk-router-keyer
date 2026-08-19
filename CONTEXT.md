# RWK v2.0 — Session Context

## Project Overview

RWK v2.0 is a Client/Station CW (Morse code) remoting system. A ham radio operator runs RWKClient.exe at their operating position with a paddle/keyer, and RWKStation.exe at the remote radio site. The two communicate over a Tailscale mesh network via a Go-based tsnet sidecar (`rwk-tailscale-sidecar.exe`). The system includes fail-safe protection, port forwarding for CAT/audio/RRC, and FlexRadio VITA-49 discovery brokering.

**Spec location**: `.kiro/specs/rwk-v2/` (requirements.md, design.md, tasks.md)

## Completed Work

### Phase 1: Shared Core Library (RWK.Shared) — DONE
- Edge protocol codec (RwkPaddleFrame, EdgeEntry, EdgeSequenceTracker)
- Config models with DPAPI encryption for secrets
- Shared interfaces/enums (IKeyingOutput, IPttOutput, ITailscaleNode, IPortForwardManager, etc.)
- Discovery broker contracts (IDiscoveryPayloadCodec, DiscoveryAnnounce, IDiscoveryListener/Emitter)
- ForwardRule with BindAddress + RuleType, unknown rule-type deserializes to Generic
- 232+ tests passing

### Phase 2: Client Application (RWK.Client) — DONE (except PBT tests)
- PaddleInputPoller (THREAD_PRIORITY_HIGHEST, 1ms polling, QPC timestamps, debounce)
- SoftWinKeyerCore (refactored from v1 SoftKeyer, injectable clock, all 5 keyer modes)
- WinKeyerProtocolHost (wraps v1 WinKeyerProtocol via temporary project reference)
- **HardwareWinKeyerHost** (drives a physical K1EL WinKeyer2/3 chip over serial: Admin Open/Close, SetSpeed, SendText, reads status/echoes)
- **WinKeyer mode selection**: Logger App (emulator for N1MM/DXLog) vs Hardware WinKey (talks to real chip)
- **WinKeyer Loopback Test**: injects WK2 protocol bytes (Admin Open, speed changes, buffered text) through the state machine; multi-speed test at 25/30/45/20 WPM; suppresses TX during test; respects ARM state
- LocalSidetoneEngine (WASAPI shared-mode, 20ms buffer, raised-cosine envelope)
- **Client-side Station ARM/DISARM toggle**: checkbox suppresses edge frame sending when unchecked

### Phase 3: Station Application (RWK.Station) — DONE (except PBT tests)
- StationKeyingOutput (serial port key/PTT with polarity inversion)
- EdgeReplayer (TIME_CRITICAL thread, jitter buffer, anchor logic, adaptive EWMA)
- FailSafeMonitor (F1-F10 with correct latch policy)

### Phase 4: Tailscale Integration — DONE (except PBT tests + task 14.9)
- Go sidecar built and verified (tsnet.Server.ListenPacket for true UDP datagrams)
- TsnetSidecarHost (process supervision, handshake parsing, status polling, stdin lifetime)
- TailscaleNode facade (SendEdgeAsync, ConnectControlAsync, StateChanged)
- SidecarPath resolver (AppContext.BaseDirectory, never Assembly.Location)
- Interactive login flow (POST /v1/start triggers auth URL, browser open + paste key)
- ADR 0001 documents the tsnet embedding decision

### Phase 5: Port Forwarding — DONE (fully functional)
- PortForwardManager with TCP relay (half-close propagation) and UDP relay (NAT-style sessions)
- **TCP tunnel wired**: `TunnelDial` delegate connects through sidecar outbound forward to Station
- **UDP tunnel wired**: `UdpTunnelBind` delegate creates outbound-udp forward on sidecar; datagrams relay over tailnet
- **StationTargetAddress** on ForwardRule: Station-side inbound forwards dial the specified LAN address (not just localhost)
- BindAddressResolver (no silent substitution, Error on unavailable/invalid)
- NetworkChange re-evaluation of rule bindings
- ForwardRuleType equivalence (Generic/Cat/Audio/RemoteRig all use same relay)
- SidecarFailureHandler (asymmetric: Client degrades for practice, Station refuses to arm)
- **Dynamic rule management**: AddForwardRule/RemoveForwardRule/SetForwardRuleEnabled at runtime, with re-push to Station
- **Control-channel rule push**: length-prefixed JSON messages over TCP session stream; Station reads in a loop for the session lifetime
- **Port validation**: duplicate port+protocol+bind detection, reserved ports 7373/41373 rejected, port range 1-65535 enforced
- Go sidecar supports `out-udp` and `in-udp` forward kinds (ListenPacket-based, NAT-style session tracking, 60s idle timeout)
- **Two-address model**: BindAddress = Client listen address, StationTargetAddress = Station-side dial target
- Supported scenarios:
  - LAN device (Client) ↔ UDP ↔ tailnet ↔ UDP ↔ LAN device (Station) — bidirectional
  - Localhost app (Client) → TCP/UDP → tailnet → target on Station LAN — bidirectional

### Phase 6: User Interface — DONE
- **Client MainForm**: fully wired to ClientController
  - **TabControl wrapper**: Tab 1 "WinKeyer / Forwarding" (main UI), Tab 2 "Log" (visual log)
  - "Remote WinKeyer" GroupBox (top): Paddle indicators, Keyer controls, Sidetone panel, Input Ports
  - "Network Control" GroupBox (bottom): Station address + Connect + Station Armed toggle, Port Forwards (inline DataGridView with +Add/-Remove and Station Target column), FlexRadio Discovery (greyed-out placeholder)
  - Sidetone and Input Ports use inner TableLayoutPanels (no clipping)
  - Port forwards use inner TableLayoutPanel: grid in row 0 (Fill), buttons+warnings in row 1 (80px fixed)
  - Bind exposure warning (10.14) and RemoteRig unverified warning (10.18)
  - Interactive Tailscale login panel (Open Browser + Paste Auth Key, auto-dismiss on Connected)
  - **Login panel fix**: no longer shows when auth token is present; `_loginDismissed` set unconditionally in DismissLoginPanel so panel cannot reappear after auth success
  - Device monitoring: 2s COM port timer + NAudio IMMNotificationClient for audio changes
  - No refresh buttons — all auto-detected
  - COM ports sorted numerically
  - Status strip: link indicator, path label, RTT, buffer, key state
  - **Visual Log tab**: level selector (None/Descriptive/Debug), Consolas 8.5pt read-only TextBox, 5000 line cap with bulk trim
  - **LogService**: ConcurrentQueue + dedicated BelowNormal drain thread (100ms batch), zero-allocation filtering, never blocks keyer timing
  - Window: Normal state, CenterScreen, min 700x400, 940x600 default
- **Station MainForm**: fully wired to StationController
  - SAFE/ARMED banner (red/green) with Re-Arm button
  - Keying output configuration (COM port, Key line RTS/DTR, PTT line RTS/DTR/None, inversion)
  - KEY/PTT LED indicators (50ms polling, 200ms sticky visibility)
  - Session info (client name, duration timer)
  - Disconnect button
  - Tailscale status (link indicator, path type, RTT)
  - Station's own Tailscale IP display with Copy button
  - Interactive Tailscale login panel (same pattern as Client)
  - Device monitoring: 2s COM port timer, sorted numerically
  - FlexRadio discovery capture control (greyed-out placeholder)
  - Keying config persisted to StationConfig on change

### Phase 7: Controllers & Wiring — DONE
- **ClientController** (paddle→keyer→sidetone→edge frames→Tailscale, heartbeat 250ms)
  - ConnectWinKeyerPort / ConnectPaddlePort (dynamic port connection from UI)
  - ConnectToStationAsync (session establishment with Station)
  - SetSpeed/Weight/Mode/PaddleReverse/ToneFrequency/ToneVolume (live updates)
  - SendTestMessage (sidetone test + network TX test)
  - SubmitAuthKeyAsync (interactive Tailscale login)
  - SidecarFailureHandler integration (degrades for practice)
  - Port forwarding start/stop on session lifecycle
  - Reconnect scheduling on disconnect
- **StationController** (9-step startup, session→replayer, fail-safe monitor, refuses arm on failure)
  - ConnectKeyingPort with KeyingOutputConfig (dynamic port connection)
  - SaveConfig (persists keying config changes)
  - ClearSafeLatch / DisconnectSession
  - SubmitAuthKeyAsync (interactive Tailscale login)
  - SidecarFailureHandler integration (refuses to arm)
  - IsKeyDown / IsPttOn properties for UI indicator polling
  - Jitter buffer processing

### Phase 8: Integration Tests — DONE
- 28 integration tests (loopback timing ±2ms, fail-safe battery, N1MM+ conformance, network loss)

### Phase 9: Release Packaging — DONE
- `build/release/publish.ps1` produces flat zip
- `build/release/README.md` covers all Req 16.15-16.20
- Output: `artifacts/release/RWK-v2.0.0-win-x64.zip`

## Remaining Tasks (Non-Blocked)

### Property-Based Tests (all marked `[ ]*` = optional but desired)
- 5.3: Paddle input mapping/debounce PBTs
- 6.2, 6.3: Iambic keyer mode PBTs + edge generation/timing PBTs
- 7.2: WK2 protocol compliance PBTs
- 8.2: Sidetone independence PBT
- 10.3: Keying output behavior PBTs
- 11.2: Jitter buffer/scheduling PBTs
- 12.7: Fail-safe conditions PBTs
- 14.4-14.6, 14.8: Sidecar path + handshake PBTs
- 14.10-14.11: Client degradation + Station fail-safe PBTs
- 15.2: Authentication PBTs
- 17.3, 17.5, 17.7, 17.9, 17.11, 17.13: Port forwarding PBTs

### Implementation Tasks Still Needed
- **14.9**: Asymmetric sidecar-failure behaviour — the `SidecarFailureHandler` class exists with full policy logic and is integrated into both controllers, but the task is not formally marked complete in tasks.md (may need status correction)
- **Task 12 parent**: Fail-safe system parent task — subtasks 12.1-12.6 are implemented in FailSafeMonitor + EdgeReplayer but marked `[~]` (need status correction)
- **Task 14 parent**: Tailscale sidecar parent task — subtasks done except 14.9 formally

### Tasks Needing Status Correction in tasks.md
- **7.1** (WinKeyerProtocolHost): Implemented, marked `[-]` because it wraps v1 code
- **11.1** (EdgeReplayer): Implemented, marked `[-]`
- **14.2** (TailscaleNode wrapper): Implemented, marked `[-]`
- **12.1-12.6** (Fail-safe system): All implemented in FailSafeMonitor, marked `[~]`
- **17.2, 17.4, 17.6, 17.8, 17.10, 17.12** (Port forwarding): All implemented, marked `[~]`
- **19.1-19.7** (Client UI): All implemented, marked `[~]`
- **20.1-20.6** (Station UI): All implemented, marked `[~]`
- **22.1-22.2** (Client wiring): Implemented, marked `[~]`
- **23.1-23.2** (Station wiring): Implemented, marked `[~]`
- **25.1-25.4** (Integration tests): Written, marked `[ ]*`
- **14.9** (Asymmetric sidecar failure): SidecarFailureHandler implemented and integrated

### Blocked Tasks (FlexRadio — needs Wireshark capture from physical hardware)
- All of Phase 5b: tasks 27.x, 28.x, 29.x, 30
- Integration test 25.5 (discovery brokering e2e)
- Wiring tasks 22.3, 23.3 (discovery emitter/listener into controllers)
- UI panels built but greyed out with "coming soon" tooltip

### Live Network Tests (Phase 10)
- Tests 33.1-33.6 written but require `RWK_TEST_AUTHKEY` environment variable
- Need Go sidecar binary built + real Tailscale pre-auth key configured
- Run with: `dotnet test --filter Category=LiveNetwork`

## Key Technical Decisions

- **.NET 9.0**, Windows x64, WinForms
- **Go 1.26.5** toolchain at E:\go for sidecar
- **Single-file publish** (self-contained, no .NET runtime needed on target)
- **Tailscale via tsnet** (userspace, no system Tailscale install required)
- **True UDP datagrams** for edge data (not TCP, not WebSocket)
- **DPAPI** for secret encryption (auth keys, pairing secrets)
- **No dark theme** — use Windows system colors for proper high-contrast support
- **No self-extracting archive** — plain zip to avoid AV heuristic flags
- **Sidecar is sibling file** — never embedded, never extracted at runtime
- **Assembly.Location forbidden** for path resolution (empty in single-file bundles)

## User Preferences & Corrections

- No dark theme — Windows system colors only
- No refresh buttons — auto-detect via OS events/timers
- COM ports sorted numerically (COM1, COM2, ..., COM10)
- "Input Ports" (not "Ports")
- Top section = "Remote WinKeyer" group, bottom = "Network Control" group
- FlexRadio implementation blocked until Wireshark capture — greyed-out UI only
- Interactive Tailscale login preferred over paste-auth-key
- Distribution: plain zip, two .NET single-file exes + Go sidecar + README
- Sidecar ships alongside (same directory), never embedded
- Forms must not span all monitors (3-monitor system)
- Port forwards specified via inline grid editing with +Add/-Remove buttons

## Files to Read on Resume

- `e:\AI\RWK\src\RWK.Client\MainForm.Designer.cs` — Client UI layout (Input Ports has WinKeyer mode radios + loopback test + Station Target column)
- `e:\AI\RWK\src\RWK.Client\MainForm.cs` — Client code-behind (fully wired to ClientController)
- `e:\AI\RWK\src\RWK.Client\Controllers\ClientController.cs` — Client orchestration (TunnelDial/UdpTunnelBind, ARM toggle, loopback test, dynamic rule push, LogService integration)
- `e:\AI\RWK\src\RWK.Client\LogService.cs` — Thread-safe visual log (queue-based, level-filtered, 5000 line cap)
- `e:\AI\RWK\src\RWK.Client\IO\HardwareWinKeyerHost.cs` — Hardware WinKeyer driver (K1EL WK2/3)
- `e:\AI\RWK\src\RWK.Shared\Net\PortForwardManager.cs` — Port forward lifecycle, validation, tunnel delegates
- `e:\AI\RWK\src\RWK.Shared\Net\TsnetSidecarHost.cs` — Sidecar IPC (TCP + UDP outbound/inbound forwards)
- `e:\AI\RWK\src\RWK.Shared\Config\ForwardRule.cs` — Rule model with StationTargetAddress
- `e:\AI\RWK\src\RWK.TailscaleSidecar\forward.go` — Go sidecar TCP + UDP forwarding (out/in/out-udp/in-udp)
- `e:\AI\RWK\src\RWK.Station\MainForm.cs` — Station code-behind (fully wired to StationController)
- `e:\AI\RWK\src\RWK.Station\Controllers\StationController.cs` — Station orchestration (control message loop, inbound forward registration)
- `e:\AI\RWK\.kiro\specs\rwk-v2\tasks.md` — full task list with status
- `e:\AI\RWK\.kiro\specs\rwk-v2\design.md` — architectural design

## Next Steps (Priority Order)

1. **Commit the v2 work** — all src/RWK.* directories are currently untracked
2. **Publish and test** the port forwarding with real hardware (UDP scenario with RRC/FlexRadio devices)
3. **Write remaining PBT tests** (optional but valuable for correctness confidence)
4. **Correct task statuses** in tasks.md — many implemented tasks still show `[~]` or `[-]`
5. **Live network tests** (33.x) when auth key is available
