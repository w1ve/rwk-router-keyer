# RWK Linux/Raspberry Pi Station — Design Specification

**Version:** 1.0-draft  
**Date:** 2026-08-22  
**Author:** W1VE  
**Companion to:** RWK Router/Keyer (https://github.com/w1ve/rwk-router-keyer)

---

## 1. Purpose

The Windows Station works well for operators who have a Windows PC at the remote
site. But many remote stations — especially solar-powered, low-draw, always-on
installations — run on a Raspberry Pi or a small Linux SBC. The hardware cost is
$35-75, power draw is 3-5W, and the form factor fits in a weatherproof enclosure
beside the radio.

This specification defines **RWK Station for Linux**, a headless .NET console
application with an embedded web UI, targeting:

- **Raspberry Pi 4/5** (64-bit Raspberry Pi OS, arm64)
- **Generic Linux x64** (Ubuntu, Debian, any systemd-based distro)

It provides the same Station functionality as the Windows version minus the Logger
WinKeyer Input (no logger runs on a headless Pi), with two keying output options:

1. **Serial port** (Linux `/dev/ttyUSB0`, `/dev/ttyS0`) — same RTS/DTR keying as Windows
2. **GPIO pin** (Raspberry Pi only) — direct transistor-switched keying via `/dev/gpiochip0`

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  RWK Linux Station (single self-contained binary)               │
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐ │
│  │ StationCore  │  │ Web UI       │  │ Tailscale Sidecar     │ │
│  │ (from Shared)│  │ (Kestrel +   │  │ (Go binary, same as   │ │
│  │              │  │  React SPA)  │  │  Windows)             │ │
│  │ - EdgeReplay │  │              │  │                       │ │
│  │ - FailSafe   │  │ - Dashboard  │  │ - tsnet userspace     │ │
│  │ - PortFwd    │  │ - Config     │  │ - UDP edge datagrams  │ │
│  │ - Discovery  │  │ - Auth Wizard│  │ - TCP/UDP forwards    │ │
│  │ - N1MM relay │  │ - Logs       │  │                       │ │
│  └──────┬───────┘  └──────┬───────┘  └───────────┬───────────┘ │
│         │                  │                      │             │
│  ┌──────┴──────────────────┴──────────────────────┴───────────┐ │
│  │                    Keying Output                            │ │
│  │   Serial: /dev/ttyUSBx (RTS/DTR)                           │ │
│  │   GPIO:   /dev/gpiochip0 pin N (Pi only, via libgpiod)     │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

### 2.1 What is shared with Windows

| Component | Shared (RWK.Shared) | Platform-specific |
|-----------|--------------------|--------------------|
| Edge protocol codec | Yes | — |
| EdgeReplayer + JitterBuffer | Yes | — |
| FailSafeMonitor | Yes | — |
| PortForwardManager | Yes | — |
| FlexRadio discovery | Yes | — |
| N1MM discovery | Yes | — |
| ForwardRule model | Yes | — |
| Config models | Yes | — |
| TsnetSidecarHost | Yes | — |
| Keying output (serial) | Shared interface | Linux serial impl |
| Keying output (GPIO) | Shared interface | Pi-specific impl |
| Web UI | — | New (Kestrel + React) |
| Logger WinKeyer Input | — | **Not supported** |
| WinForms UI | — | **Not applicable** |

### 2.2 Deployment model

Single self-contained binary + sidecar binary:

```
/opt/rwk-station/
  rwk-station           # .NET self-contained (arm64 or x64)
  rwk-tailscale-sidecar # Go binary (arm64 or x64)
  wwwroot/              # React SPA static assets (embedded or alongside)
  config.json           # Station config (auto-created on first run)
```

Installed via:
- `.deb` package (apt install)
- `.tar.gz` archive (extract and run)
- Docker container (optional, for advanced users)

---

## 3. Keying Output

### 3.1 Serial Port (Linux + Pi)

Same logic as Windows: assert RTS or DTR for key-down, deassert for key-up.
PTT on the alternate line. Uses .NET `System.IO.Ports.SerialPort` which works
on Linux with the `System.IO.Ports` NuGet package.

Configuration:
```json
{
  "keyingOutput": {
    "type": "serial",
    "port": "/dev/ttyUSB0",
    "keyLine": "RTS",
    "pttLine": "DTR",
    "keyInvert": false,
    "pttInvert": false
  }
}
```

Port enumeration: scan `/dev/ttyUSB*`, `/dev/ttyACM*`, `/dev/ttyS*`, and
`/dev/ttyAMA*`. Present all in the web UI dropdown.

### 3.2 GPIO Pin (Raspberry Pi only)

For direct keying via a transistor switch (2N2222, 2N7000, or optocoupler)
connected between a GPIO pin and the radio's key jack.

Uses the `System.Device.Gpio` NuGet package which interfaces with
`/dev/gpiochip0` via the Linux chardev GPIO interface (libgpiod).
No root required if the user is in the `gpio` group.

Configuration:
```json
{
  "keyingOutput": {
    "type": "gpio",
    "keyPin": 17,
    "pttPin": 27,
    "keyInvert": false,
    "pttInvert": false,
    "chip": "/dev/gpiochip0"
  }
}
```

**Hardware guide (included in web UI help):**

```
GPIO 17 ──── 1kΩ ──┬── Base (2N2222)
                    │
                    └── Collector ──── KEY jack tip
                        Emitter ────── KEY jack sleeve (ground)

   (Same circuit for PTT on GPIO 27)
```

For isolation (galvanic separation from radio), recommend an optocoupler
(4N35 or PC817) instead of a direct transistor. The web UI help section
should include both circuits.

### 3.3 Timing requirements

Same as Windows Station: the keying output must respond within 100
microseconds of the replayer's fire event. On Linux, the process should
run with `nice -n -20` or `SCHED_FIFO` priority. The GPIO chardev
interface has measured latency under 10 microseconds on a Pi 4, which
is well within spec.

The systemd service file should set:
```ini
[Service]
Nice=-20
CPUSchedulingPolicy=fifo
CPUSchedulingPriority=80
```

---

## 4. Web UI

### 4.1 Technology stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| HTTP server | ASP.NET Core Kestrel (minimal API) | Ships with the runtime, no external dependency |
| Frontend | React 18 + Bootstrap 5 | Modern, responsive, widely known |
| Build | Vite | Fast dev server, production builds to wwwroot/ |
| Real-time | SignalR (WebSocket) | Live updates: key state, status, logs |
| API | REST + SignalR | Config CRUD via REST, live events via SignalR |

The React SPA is built at compile time and served as static files from
`wwwroot/`. No Node.js required at runtime on the Pi.

### 4.2 Pages

**Dashboard (home)**
```
┌─────────────────────────────────────────────────────────────┐
│  RWK Station — Linux                            [Connected] │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─ Status ──────────────────────────────────────────────┐  │
│  │  Tailscale: Connected (100.64.1.5)                    │  │
│  │  Path: Direct | RTT: 45ms | Buffer: 68ms             │  │
│  │  Client: W1VE-Client (100.64.1.3) — Session active   │  │
│  │  Keying: /dev/ttyUSB0 (RTS) | PTT: DTR               │  │
│  │  KEY: ○  PTT: ○  (live indicators via SignalR)        │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌─ Fail-Safe ───────────────────────────────────────────┐  │
│  │  Status: SAFE (not latched)                           │  │
│  │  F1 ○  F2 ○  F3 ○  F6 ○  F9 ○  F10 ○               │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌─ Port Forwards ──────────────────────────────────────-┐  │
│  │  Name         Proto  Tailnet Port  Target       Status│  │
│  │  RSBA1-Ctrl   UDP    50001         192.168.1.40  ✓    │  │
│  │  RSBA1-Audio  UDP    50003         192.168.1.40  ✓    │  │
│  │  rigctld      TCP    4532          127.0.0.1     ✓    │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌─ Discovery ───────────────────────────────────────────┐  │
│  │  FlexRadio: Enabled (1 radio discovered)              │  │
│  │  N1MM+: Enabled (relay active)                        │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

**Configuration**
- Keying output: type selector (Serial / GPIO), port/pin config, polarity
- Jitter buffer: mode (adaptive/fixed), delay range
- PTT timing: lead, tail
- Discovery: FlexRadio enable, N1MM enable
- Network: Tailscale hostname, auth status

**Tailscale Auth Wizard**
- Same 5-step flow as the Windows wizard, rendered as a web form
- Step 1: Welcome + explain
- Step 2: "Click to open auth URL in a new tab" (or show QR code for mobile)
- Step 3: Verify (polls status via SignalR)
- Step 4: Authorization required (admin link, pre-auth key paste)
- Step 5: Success + key expiry warning
- QR code generation for the auth URL (useful when accessing the Pi's web
  UI from a phone on the same LAN)

**Logs**
- Live log stream via SignalR
- Level filter (Descriptive / Debug)
- Download log file button

**Forward Rules (read-only)**
- Shows rules pushed from the Client
- Status column (active/idle/error)
- No editing — rules are managed from the Client

### 4.3 API endpoints

```
GET  /api/status          — Full status snapshot (JSON)
GET  /api/config          — Current config
PUT  /api/config          — Update config (validates, restarts affected components)
POST /api/config/keying/test — Pulse key line for 500ms (test without a Client)
GET  /api/forwards        — Current forward rules + status
GET  /api/logs?lines=100  — Recent log lines
GET  /api/auth/status     — Tailscale auth state
POST /api/auth/key        — Submit pre-auth key
```

SignalR hub at `/hubs/station`:
```
Events (server → client):
  StatusUpdate(statusJson)
  KeyStateChanged(keyDown, pttOn)
  FailSafeTriggered(condition)
  LogEntry(level, message, timestamp)
  ForwardRuleStatusChanged(ruleName, status)
```

### 4.4 Security

The web UI binds to `0.0.0.0:8080` by default (configurable). Since the
Station is on a private LAN (not exposed to the internet — Tailscale handles
the WAN side), authentication is optional but recommended:

- **Default:** No authentication (LAN-only access assumed)
- **Optional:** Basic auth with a PIN/password set via config.json or
  first-run wizard
- **HTTPS:** Self-signed cert generated on first run (avoids mixed-content
  issues if the operator bookmarks it)

The web UI should show a warning banner if accessed from a non-LAN IP
(indicates the port is somehow exposed to the internet).

---

## 5. Systemd Integration

### 5.1 Service file

```ini
[Unit]
Description=RWK Router/Keyer Station
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/opt/rwk-station/rwk-station
WorkingDirectory=/opt/rwk-station
Restart=always
RestartSec=5
Nice=-20
CPUSchedulingPolicy=fifo
CPUSchedulingPriority=80
User=rwk
Group=rwk
SupplementaryGroups=dialout gpio

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/rwk-station

[Install]
WantedBy=multi-user.target
```

### 5.2 First-run experience

On first launch (no config.json exists):
1. Creates default config.json
2. Starts the web UI on port 8080
3. Prints to stdout: `RWK Station started. Open http://<hostname>:8080 to configure.`
4. The web UI shows the Tailscale Auth Wizard as the first page
5. After auth completes, redirects to the Dashboard

### 5.3 Updates

- Check GitHub releases API on startup (once per day)
- Show "Update available" banner in web UI
- Manual update: download new binary, `systemctl restart rwk-station`
- Future: self-update via the web UI (download + replace + restart)

---

## 6. Build and Cross-Compilation

### 6.1 .NET publish

```bash
# For Raspberry Pi (arm64)
dotnet publish src/RWK.Station.Linux -c Release \
  -r linux-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true

# For generic Linux (x64)
dotnet publish src/RWK.Station.Linux -c Release \
  -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true
```

Trimming is important for Pi — reduces the binary from ~80MB to ~30-40MB
by removing unused framework code.

### 6.2 Go sidecar cross-compilation

```bash
# For Raspberry Pi (arm64)
GOOS=linux GOARCH=arm64 go build -o rwk-tailscale-sidecar -ldflags "-s -w" .

# For generic Linux (x64)
GOOS=linux GOARCH=amd64 go build -o rwk-tailscale-sidecar -ldflags "-s -w" .
```

### 6.3 React frontend build

```bash
cd src/RWK.Station.Linux/webapp
npm ci
npm run build    # outputs to ../wwwroot/
```

The build output is committed to the repo (or built in CI) so the .NET
publish includes it without requiring Node.js in the build pipeline.

### 6.4 Project structure

```
src/RWK.Station.Linux/
  RWK.Station.Linux.csproj
  Program.cs                    # Entry point, Kestrel setup, DI
  StationService.cs             # Hosted service: sidecar, controller lifecycle
  Api/
    StatusController.cs
    ConfigController.cs
    AuthController.cs
    ForwardsController.cs
    LogsController.cs
  Hubs/
    StationHub.cs               # SignalR hub
  Keying/
    LinuxSerialKeyingOutput.cs  # Serial port (RTS/DTR) on Linux
    GpioKeyingOutput.cs         # Raspberry Pi GPIO via System.Device.Gpio
    KeyingOutputFactory.cs      # Creates the right impl from config
  Config/
    LinuxStationConfig.cs       # Extends StationConfig with web UI + GPIO fields
  Auth/
    WebAuthWizardState.cs       # Reuses AuthWizardStateMachine, exposes via API
  webapp/                       # React source
    src/
      App.tsx
      pages/
        Dashboard.tsx
        Config.tsx
        AuthWizard.tsx
        Logs.tsx
      components/
        StatusCard.tsx
        KeyIndicator.tsx
        FailSafePanel.tsx
        ForwardRulesTable.tsx
    vite.config.ts
    package.json
  wwwroot/                      # Built React output (served by Kestrel)
```

---

## 7. Differences from Windows Station

| Feature | Windows Station | Linux/Pi Station |
|---------|----------------|------------------|
| UI | WinForms desktop app | Web UI (browser-based) |
| Keying output | Serial (COM port) | Serial (/dev/tty*) or GPIO pin |
| Logger WinKeyer Input | Supported | **Not supported** |
| FlexRadio discovery | Supported | Supported |
| N1MM relay | Supported | Supported |
| Port forwarding | Supported | Supported |
| Fail-safe | Supported | Supported |
| Tailscale auth | Desktop wizard (modal dialog) | Web wizard (in-browser) |
| Pairing key | DPAPI encrypted | File permissions (chmod 600) |
| Install | Inno Setup .exe | .deb / .tar.gz / Docker |
| Auto-start | Windows startup | systemd service |
| Process priority | THREAD_PRIORITY_HIGHEST | SCHED_FIFO (nice -20) |

---

## 8. GPIO Hardware Reference

### 8.1 Recommended circuit — NPN transistor

```
                    +──── KEY jack tip (to radio)
                    │
              ┌─────┘
              │ C
GPIO pin ── 1kΩ ──── B   2N2222A
              │ E
              └─────┐
                    │
                    +──── KEY jack sleeve (ground)
                         (also connect Pi GND here)
```

Component values:
- Base resistor: 1kΩ (limits base current to ~3mA at 3.3V GPIO)
- Transistor: 2N2222A, BC547, or any general-purpose NPN
- Maximum key voltage: 30V (2N2222A collector-emitter rating)

### 8.2 Recommended circuit — optocoupler (isolated)

```
GPIO pin ── 470Ω ──┐       ┌──── KEY jack tip
                   │ Anode │ Collector
                   4N35     4N35
                   │ Cathode│ Emitter
Pi GND ────────────┘       └──── KEY jack sleeve
```

The optocoupler provides galvanic isolation — no electrical connection
between the Pi and the radio. Recommended for radios with keying voltages
above 12V or where ground loops are a concern.

### 8.3 Pin assignments (defaults)

| Function | Default GPIO | Physical pin | Notes |
|----------|-------------|-------------|-------|
| KEY | GPIO 17 | Pin 11 | BCM numbering |
| PTT | GPIO 27 | Pin 13 | BCM numbering |

These are configurable via the web UI. Any GPIO pin can be used except
pins reserved for I2C/SPI/UART if those interfaces are active.

### 8.4 Testing without a radio

The web UI provides a "Test Key" button that pulses the key line for 500ms.
Use a multimeter or LED + resistor across the output to verify the circuit
before connecting to a radio.

---

## 9. Configuration File

`/opt/rwk-station/config.json`:

```json
{
  "keyingOutput": {
    "type": "gpio",
    "keyPin": 17,
    "pttPin": 27,
    "keyInvert": false,
    "pttInvert": false
  },
  "jitterBuffer": {
    "mode": "adaptive",
    "directMinDelay": 30,
    "directMaxDelay": 300,
    "derpMinDelay": 100,
    "derpMaxDelay": 500
  },
  "pttTiming": {
    "leadMs": 50,
    "tailMs": 500
  },
  "tailscale": {
    "hostname": "rwk-station",
    "authKey": null
  },
  "discovery": {
    "flexEnabled": true,
    "n1mmEnabled": true
  },
  "webUi": {
    "port": 8080,
    "bindAddress": "0.0.0.0",
    "requireAuth": false,
    "pin": null
  }
}
```

Secrets (auth key, pairing key) are stored with `chmod 600` file
permissions rather than DPAPI (which is Windows-only). The config file
itself should be readable only by the `rwk` user.

---

## 10. Packaging

### 10.1 Debian package (.deb)

```
rwk-station_1.0.3_arm64.deb
rwk-station_1.0.3_amd64.deb
```

Contents:
- `/opt/rwk-station/rwk-station` (main binary)
- `/opt/rwk-station/rwk-tailscale-sidecar` (Go sidecar)
- `/opt/rwk-station/wwwroot/` (React build)
- `/etc/systemd/system/rwk-station.service`
- `/opt/rwk-station/config.json.example`

Post-install script:
- Creates `rwk` user and group
- Adds `rwk` to `dialout` and `gpio` groups
- Enables and starts the service
- Prints the URL to access the web UI

### 10.2 Tar archive

```
rwk-station-1.0.3-linux-arm64.tar.gz
rwk-station-1.0.3-linux-x64.tar.gz
```

Extract, run `./install.sh`, which does the same as the deb post-install.

### 10.3 Docker (future)

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-noble-arm64v8
COPY rwk-station /app/rwk-station
COPY rwk-tailscale-sidecar /app/rwk-tailscale-sidecar
COPY wwwroot /app/wwwroot
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["./rwk-station"]
```

Note: Docker adds complexity for GPIO access (`--device /dev/gpiochip0`)
and serial ports (`--device /dev/ttyUSB0`). Recommend native install for
most operators.

---

## 11. Roadmap

**v1.0** — Core functionality:
- Headless Station with serial keying output
- GPIO keying (Pi)
- Web dashboard (status, config, logs)
- Tailscale auth wizard (web version)
- Port forwarding (pushed from Client)
- FlexRadio + N1MM discovery relay
- Fail-safe monitor
- systemd service
- .deb and .tar.gz packages for arm64 + x64

**v1.1** — Quality of life:
- Auto-update via web UI
- QR code for Tailscale auth (scan from phone)
- Keying scope (web-based oscilloscope showing key/PTT timing)
- Config backup/restore via web UI
- HTTPS with auto-generated self-signed cert

**v1.2** — Advanced:
- Docker support
- Prometheus metrics endpoint (`/metrics`)
- MQTT integration for home automation (key state, fail-safe events)
- Multiple keying outputs (key via GPIO, PTT via serial)
- Hardware watchdog (Pi hardware watchdog timer for fail-safe F10)

---

## 12. Open Questions

1. **Should the Linux Station support being a "Client" too?** A Pi could
   theoretically run both roles (e.g., a portable Pi keyer that connects
   to a remote Station). Out of scope for v1.0 but architecturally possible
   since all Client logic is in RWK.Shared.

2. **Audio pass-through?** Some operators want to run WSJT-X or fldigi on
   the Pi. This requires audio routing (PulseAudio/PipeWire) which is
   orthogonal to RWK's keying/forwarding mission. Defer to v2.0.

3. **PWM sidetone on Pi?** The Pi has hardware PWM on GPIO 18. Could
   generate a local sidetone for operators with headphones plugged into
   the Pi. Low priority — the Client already has sidetone.

4. **Hat/shield product?** A custom PCB with optocouplers, LED indicators,
   and screw terminals for KEY/PTT/GND would make installation trivial.
   Community interest should drive this.

---

*73 de W1VE*
