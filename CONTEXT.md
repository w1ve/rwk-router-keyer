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

## Next Steps

1. **Test FlexRadio relay** with a physical Flex 6000-series radio
2. **Write remaining PBT tests** (optional but valuable for correctness confidence)
3. **Live network tests** (33.x) when separate machines + auth key available
4. **Consider** adding the discovered radio list to the Client UI (currently just logs)
5. **Consider** Station-side allow/deny override per pushed rule
