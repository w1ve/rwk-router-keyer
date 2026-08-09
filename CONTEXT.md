# RWK Project Context

## Project Overview

RWK (Remote WinKeyer) is a system for operating CW remotely using a paddle. It consists of two Windows applications:

- **WKRServer** — Runs at the remote station. Emulates the K1EL WinKeyer protocol in software. Accepts commands via serial port (for local N1MM), UDP (for remote client), or Cloud Relay (for zero-config internet connectivity). Keys the radio by toggling DTR/RTS on a physical serial port.
- **WKRClient** — Runs at the operator's local QTH. Connects to a physical WinKeyer, forwards paddle input and speed changes to the server over UDP or Cloud Relay. Also supports keyboard text entry.

## Repository

- GitHub: https://github.com/w1ve/rwk
- Branches: 
  - `main` — UDP-only version
  - `cloudflare-relay` — Full version with Cloud Relay support
- Copyright: (C) 2026 by Gerry Hull, W1VE

## Solution Structure

```
e:\AI\RWK\
├── WinKeyerEmulator.sln
├── src/
│   ├── WinKeyerEmulator.Core/          # Protocol engine, timing, interfaces (net9.0)
│   │   └── CloudRelay/                 # WebSocket relay transport
│   │       ├── CloudRelayTransport.cs  # WebSocket client with reconnect/heartbeat
│   │       ├── WireProtocol.cs         # Binary frame serialization (CRC32)
│   │       └── TokenGenerator.cs       # 256-bit pairing token generation
│   ├── WinKeyerEmulator.App/           # WKRServer WinForms app (net9.0-windows, win-x64)
│   └── WKRClient/                      # WKRClient WinForms app (net9.0-windows, win-x64)
├── tests/
│   ├── WinKeyerEmulator.Core.Tests/    # Unit + FsCheck property tests
│   └── WinKeyerEmulator.Integration.Tests/  # UDP protocol integration tests
├── binaries/                           # Pre-built EXEs
├── .kiro/specs/winkeyer-emulator/      # Local only (gitignored)
└── wkrserver.ico                       # Application icon
```

## Key Files

### Server (WKRServer.exe)
- `src/WinKeyerEmulator.App/Controllers/AppController.cs` — Lifecycle orchestrator, supports UDP and Cloud Relay
- `src/WinKeyerEmulator.App/Controllers/AppConfig.cs` — Configuration including TransportMode enum
- `src/WinKeyerEmulator.App/MainForm.cs` / `MainForm.Designer.cs` — UI with transport selection
- `src/WinKeyerEmulator.App/IO/SerialKeyingOutput.cs` — Native DTR/RTS toggling via EscapeCommFunction
- `src/WinKeyerEmulator.App/IO/SerialCommandSource.cs` — Serial command port reader
- `src/WinKeyerEmulator.App/IO/UdpCommandSource.cs` — UDP listener
- `src/WinKeyerEmulator.App/Settings/AppSettings.cs` — Persisted to %AppData%/WKRServer/

### Core Protocol Engine
- `src/WinKeyerEmulator.Core/Protocol/WinKeyerProtocol.cs` — Full WinKeyer state machine
- `src/WinKeyerEmulator.Core/Protocol/CommandDefinitions.cs` — All WK command byte constants
- `src/WinKeyerEmulator.Core/Protocol/ProtocolState.cs` — HostMode, WPM, buffer state
- `src/WinKeyerEmulator.Core/KeyerCore.cs` — Orchestrates protocol + timing, char echo, thread-safe
- `src/WinKeyerEmulator.Core/Timing/TimingEngine.cs` — High-priority keying thread, edge scheduling
- `src/WinKeyerEmulator.Core/Timing/EdgeScheduleBuilder.cs` — Precomputes absolute tick arrays
- `src/WinKeyerEmulator.Core/Timing/HybridWaiter.cs` — Sleep+spin wait with abort support

### Cloud Relay Transport
- `src/WinKeyerEmulator.Core/CloudRelay/CloudRelayTransport.cs` — WebSocket client with reconnect, heartbeat
- `src/WinKeyerEmulator.Core/CloudRelay/WireProtocol.cs` — Binary frame format with CRC32
- `src/WinKeyerEmulator.Core/CloudRelay/TokenGenerator.cs` — Cryptographic pairing token generation

### Client (WKRClient.exe)
- `src/WKRClient/MainForm.cs` — Opens WinKeyer serial, forwards to server via UDP or Cloud Relay
- `src/WKRClient/ClientSettings.cs` — Persisted to %AppData%/WKRClient/

## Current State & Known Issues

### Working
- N1MM connects, shows version, sends CW successfully
- Remote keyboard typing works (client → UDP/Relay → server → DTR/RTS)
- Speed changes forwarded with debouncing to filter noise
- ESC aborts transmission immediately
- Stop/Start works without crash (OperationCanceledException fixed)
- Inter-character gaps between separate UDP messages (3-dit spacing)
- Keystroke batching on client (75ms) prevents characters running together
- Settings persisted to AppData
- COM ports sorted numerically
- Form centered on screen
- Version auto-increments on build
- **Cloud Relay transport** — zero-config WebSocket connectivity via Cloudflare
- Automatic reconnect with exponential backoff
- Heartbeat keep-alive (5 seconds)
- Thread-safe KeyerCore with protocol lock

### Known Limitations
- Beta — not all WinKeyer commands have full behavioral effect (parsed and consumed correctly)
- UDP is fire-and-forget — dropped packets = missed characters
- Cloud Relay adds ~20-50ms latency vs direct UDP
- timeBeginPeriod(1) affects system-wide timer resolution while running
- Windows x64 only
- Paddle echo mode (0x0D 0x40) conflicts with speed pot command in some WinKeyer firmware versions

## Build Commands

```bash
# Build all
dotnet build WinKeyerEmulator.sln -c Release

# Run tests
dotnet test WinKeyerEmulator.sln

# Publish server
dotnet publish src/WinKeyerEmulator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Publish client
dotnet publish src/WKRClient -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Output Paths
- Server: `src/WinKeyerEmulator.App/bin/Release/net9.0-windows/win-x64/publish/WKRServer.exe`
- Client: `src/WKRClient/bin/Release/net9.0-windows/win-x64/publish/WKRClient.exe`

## Protocol Notes

### N1MM Init Sequence (what the server must handle)
```
Admin Open (00 02) → version + status response (17 C0)
WK2 Mode (0E 05)
Speed Pot Setup (05 xx xx xx) — 3 data bytes
Sidetone (01 xx)
Pin Config (09 xx)
PTT Lead/Tail (04 xx xx) — 2 data bytes!
First Extension (10 xx) — admin sub-cmd 0x10, consumes 1 data byte
Key Compensation (00 11 xx) — admin sub-cmd 0x11, consumes 1 data byte
Weighting (03 xx)
GetSpeedPot (07) — no response (we have no pot)
Speed (02 xx)
```

### Key Protocol Fixes Applied
1. PTT Lead/Tail consumes 2 bytes (not 1)
2. Admin sub-commands >= 0x10 consume 1 data byte
3. GetSpeedPotCmd returns no response (caused init loop)
4. Status bytes have 0xC0 prefix (bits 7:6 set)
5. Character echo sent asynchronously via ResponseAvailable event
6. ProtocolState defaults to HostMode=true (for restart mid-session)
7. Text not returned as immediate response (buffered, flushed after 50ms)

## Architecture Decisions
- **Transport options**: UDP for lowest latency (via Tailscale), Cloud Relay for zero-config ease
- UDP chosen over TCP for minimal latency (CW timing-critical)
- No handshake needed — server starts in host mode
- Two-tier buffering: client batches keystrokes (75ms) → server flush timer (25ms)
- Inter-message gap enforced in TimingEngine (3-dit between consecutive schedules)
- Tailscale recommended for UDP NAT traversal (free, simple)
- Cloud Relay uses Cloudflare Workers for global edge deployment
- Thread-safe KeyerCore protects protocol state from concurrent serial/UDP/relay access
- EscapeCommFunction return values checked, KeyUp guaranteed in all failure paths
- Abort clears both current keying AND queued schedules
