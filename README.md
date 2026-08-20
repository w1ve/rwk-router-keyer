# RWK Router/Keyer

<p align="center">
  <img src="splash.png" alt="RWK Router/Keyer" width="600">
</p>

**Any Rig, Any Internet, Anytime.**

Free, open-source CW remoting and port forwarding for amateur radio — hand-generated Morse code sent across any internet connection without timing distortion.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)]()
[![Go](https://img.shields.io/badge/Go-1.22+-00ADD8.svg)]()

---

## Why This Project Exists

Remote amateur radio operation has a fundamental problem: **hand-generated CW cannot tolerate network latency and jitter.** When an operator sends Morse code with a paddle, each dit and dah is precisely timed — a 25 WPM dit is exactly 48 milliseconds. If those timing events cross a network with variable delay, the code arrives distorted. Characters merge, spacing is destroyed, and the result is unreadable.

Commercial solutions exist (most notably in high-end radios like the FlexRadio 6000 series), but they require expensive hardware, are locked to a single vendor's ecosystem, and often demand either a public IP address or a complex VPN configuration that's beyond most operators.

**RWK solves both problems:**

1. **Timing-accurate CW remoting** — The keyer runs at the operator's position. Edge transitions (key-down, key-up) are timestamped with microsecond-resolution QPC clocks, packed into UDP datagrams, and replayed at the remote station with an adaptive jitter buffer that absorbs network variation while preserving the original timing relationships. The technique is inspired by commercial implementations but was designed and built from scratch using property-based testing to prove correctness at every speed from 5 to 60 WPM.

2. **Zero-configuration private networking** — RWK uses [Tailscale](https://tailscale.com) to create a private WireGuard mesh between the operator and the remote station. No port forwarding, no dynamic DNS, no public IP addresses required. Works over DSL, cable, satellite, 4G LTE, or any combination — on either end.

---

## Three Independent Features

RWK provides **three features that work independently** over the same Tailscale network:

### 1. Remote WinKeyer (CW Remoting)
Sends hand-generated Morse code from a paddle or logger at your operating position to a radio at a remote station. **Requires pairing** (Station Key authentication) to establish the keying session. This is the timing-critical path with fail-safe protection.

### 2. Port Forwarding (TCP/UDP Tunneling)
Tunnels arbitrary TCP and UDP traffic between your operating position and the remote station's LAN. Used for CAT control, audio streaming, RemoteRig connections, etc. **Does not require pairing** — port forwards are configured on the Client and pushed to the Station when paired, but the actual TCP/UDP relay uses the Tailscale mesh directly.

### 3. FlexRadio Discovery Relay (No SmartLink Required)
Automatically discovers FlexRadio 6000/8000 series radios on the Station's LAN and makes them appear as if they're on your local network — **without SmartLink, without a public IP, and without Flex's cloud infrastructure.** SmartSDR on your Client PC sees the radio in its discovery list and connects directly through the RWK tunnel.

You can use any feature alone or in combination:
- **CW only:** Pair the Client and Station for remote keying, no port forwards needed.
- **Port forwards only:** Configure forwards on the Client for CAT/audio/RRC access without ever pairing for CW.
- **FlexRadio only:** Enable discovery relay + port forwards for SmartSDR access without CW.
- **All together:** Full remote operation with CW, CAT, audio, and FlexRadio discovery.

---

## What You Can Forward (TCP/UDP)

RWK's port forwarding tunnels any TCP or UDP traffic between your operating position and your remote station:

- **FlexRadio 6000/8000 series** — SmartSDR command/data/audio streams (with discovery relay — no SmartLink needed!)
- **RemoteHams** audio/control connections
- **Icom** IP-based radios (IC-705, IC-7610 remote head, IC-R8600)
- **Kenwood** KENWOOD-ARCP connections
- **Elecraft** K3/K4 serial CAT control
- **Microham microKeyer** / **RRC** (RemoteRig) control and audio
- **FlexRadio** SmartSDR DAX/CAT streams
- **Any application** that communicates over TCP or UDP ports

All of this works regardless of your ISP type. Both ends can be behind NAT, on CGNAT, on cellular — it doesn't matter. Tailscale handles the connectivity.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           OPERATOR POSITION (Client)                             │
│                                                                                 │
│  ┌──────────┐  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐   │
│  │  Paddle  │  │   Logger     │  │  Hardware    │  │  Local Applications   │   │
│  │  (CTS/   │  │  (N1MM,      │  │  WinKeyer    │  │  (CAT, Audio, RRC)    │   │
│  │  DSR/DCD)│  │  DXLog, etc) │  │  (K1EL)      │  │                       │   │
│  └────┬─────┘  └──────┬───────┘  └──────┬───────┘  └───────────┬───────────┘   │
│       │ Serial         │ Serial          │ Serial               │ TCP/UDP       │
│       ▼                ▼                 ▼                      ▼               │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │                        RWK Client Application                           │    │
│  │  ┌────────────┐ ┌───────────────┐ ┌───────────┐ ┌──────────────────┐   │    │
│  │  │ Paddle     │ │ WinKeyer      │ │ Soft      │ │ Port Forward     │   │    │
│  │  │ Input      │ │ Protocol Host │ │ Keyer     │ │ Manager          │   │    │
│  │  │ Poller     │ │ (Logger/HW)   │ │ Core      │ │ (TCP + UDP)      │   │    │
│  │  └─────┬──────┘ └───────┬───────┘ └─────┬─────┘ └────────┬─────────┘   │    │
│  │        │                 │               │                │             │    │
│  │        └────────────┬────┘      Sidetone │                │             │    │
│  │                     ▼            ▼       ▼                ▼             │    │
│  │              ┌─────────────┐  ┌──────┐  ┌──────────────────────┐        │    │
│  │              │ Edge Frame  │  │Audio │  │  Tailscale Sidecar   │        │    │
│  │              │ Builder     │  │Output│  │  (Go tsnet process)  │        │    │
│  │              └──────┬──────┘  └──────┘  └──────────┬───────────┘        │    │
│  └─────────────────────┼──────────────────────────────┼────────────────────┘    │
│                        │ UDP Datagrams                 │ TCP/UDP tunnels         │
└────────────────────────┼──────────────────────────────┼─────────────────────────┘
                         │          WireGuard Mesh       │
                         ▼        (Tailscale Network)    ▼
┌────────────────────────┼──────────────────────────────┼─────────────────────────┐
│                        │                              │                         │
│  ┌─────────────────────┼──────────────────────────────┼────────────────────┐    │
│  │              ┌──────┴──────┐           ┌───────────┴───────────┐        │    │
│  │              │  Tailscale  │           │  Tailscale Sidecar    │        │    │
│  │              │  Sidecar    │           │  (Go tsnet process)   │        │    │
│  │              └──────┬──────┘           └───────────┬───────────┘        │    │
│  │                     ▼                              ▼                    │    │
│  │  ┌──────────────────────────┐    ┌──────────────────────────────────┐   │    │
│  │  │     Edge Replayer        │    │     Port Forward Manager         │   │    │
│  │  │  (TIME_CRITICAL thread,  │    │  (inbound TCP/UDP → LAN devices) │   │    │
│  │  │   jitter buffer, anchor) │    │                                  │   │    │
│  │  └────────────┬─────────────┘    └──────────────────┬───────────────┘   │    │
│  │               │                                     │                   │    │
│  │               ▼                                     ▼                   │    │
│  │  ┌────────────────────────┐           ┌──────────────────────────┐      │    │
│  │  │   Keying Output        │           │  Station LAN Devices     │      │    │
│  │  │   (Serial DTR/RTS)     │           │  (Radio, RRC, etc.)      │      │    │
│  │  └────────────┬───────────┘           └──────────────────────────┘      │    │
│  │               │                                                         │    │
│  │               │                        RWK Station Application          │    │
│  └───────────────┼─────────────────────────────────────────────────────────┘    │
│                  ▼                                                               │
│            ┌──────────┐                   REMOTE STATION                        │
│            │  Radio   │                                                         │
│            │  Key     │                                                         │
│            │  Jack    │                                                         │
│            └──────────┘                                                         │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## Core Technology: Timing-Accurate CW Remoting

### The Problem

At 25 WPM, a dit lasts 48ms. At 35 WPM, it's 34ms. Internet connections typically have 20-100ms of jitter. If you simply key a remote transmitter in real-time over the network, the code is destroyed.

### The Solution

RWK separates the **timing decision** from the **physical keying:**

1. **At the Client:** The paddle input is polled at 1ms intervals on a dedicated high-priority thread. Contact transitions are timestamped with Windows QPC (QueryPerformanceCounter) — sub-microsecond resolution. The soft keyer engine (running on its own `THREAD_PRIORITY_HIGHEST` thread) generates precisely-timed edge events: key-down at time T₁, key-up at time T₂, etc.

2. **Over the Network:** Edge events are packed into compact UDP datagrams (RWK-PADDLE frames) carrying sequence numbers and relative timestamps. True UDP datagrams travel over the WireGuard mesh — datagram boundaries are preserved end-to-end, which is critical for the jitter buffer.

3. **At the Station:** A `THREAD_PRIORITY_TIME_CRITICAL` replay thread receives the datagrams, buffers them in an adaptive jitter buffer, and fires the keying output at the correct relative times. An anchor system resets after idle periods (>2 seconds) so accumulated drift never builds up.

The result: **timing accuracy within ±2ms at 35 WPM over sustained 5-minute sessions**, verified by automated integration tests with real serial port loopback.

### Fail-Safe Protection

The Station implements 10 independent fail-safe conditions (F1-F10) that guarantee the key is never stuck down:

- **F1:** No heartbeat for 750ms while key is down → force key up
- **F2:** No heartbeat for 3 seconds while idle → close session, latch SAFE
- **F3:** Continuous key-down for 10 seconds → force key up (protects against TUNE)
- **F6:** Serial port error → latch SAFE
- **F9:** Tailscale path lost → force key up

A latched SAFE condition requires deliberate operator action (Re-Arm button or remote ARM) to resume keying.

---

## FlexRadio Discovery Relay — No SmartLink Required

### The Problem with SmartLink

FlexRadio's SmartLink service requires a public IP address, port forwarding on your router, or reliance on Flex's cloud infrastructure. For many operators — especially those with CGNAT, satellite, or cellular internet — SmartLink simply doesn't work. Others don't want their radio traffic routed through a third-party cloud service.

### RWK's Solution: VITA-49 Discovery Relay

RWK intercepts the FlexRadio discovery broadcasts at the Station and replays them on the Client's local network with the endpoint rewritten to point through the RWK tunnel. SmartSDR on the Client sees the radio in its discovery list and connects as if it were local.

**How it works:**

```
Station LAN:
  FlexRadio 6xxx → broadcasts VITA-49 discovery on UDP 4992
                 → Station's Discovery Listener captures it
                 → Forwards raw payload over control channel to Client

Client LAN:
  Client receives discovery_announce
  → Rewrites ip= and port= fields to Client's local forward rule endpoint
  → Broadcasts rewritten VITA-49 packet on Client LAN (UDP 4992)
  → SmartSDR discovers the radio and connects via the forwarded ports
```

**Technical details:**
- Discovery packets use VITA-49 encapsulation (FlexRadio's format since SmartSDR v1.1.3)
- 28-byte VITA-49 preamble: header, stream ID 0x800, Flex OUI class ID (0x001C2D53:4CFFFF00), timestamps
- ASCII payload: space-separated key=value pairs (model, serial, ip, port, status, etc.)
- The codec verifies stream ID and class ID before parsing — non-Flex traffic on port 4992 is ignored
- Rewrite preserves all fields except `ip=` and `port=`, including unknown/future fields
- Packet length word is recomputed after rewrite so the result remains valid VITA-49
- The Station binds with SO_REUSEADDR so SmartSDR at the Station continues to work

**What you need:**
1. A TCP port forward for the FlexRadio command port (default 4992)
2. UDP port forwards for the streaming data ports (as needed)
3. Discovery capture enabled on the Station
4. Discovery re-emission enabled on the Client

---

## Private VPN: Tailscale Networking

### Why Tailscale

RWK uses [Tailscale](https://tailscale.com) because it solves the networking problem completely:

- **No port forwarding** — works behind any NAT, CGNAT, or firewall
- **No public IP needed** — both ends can be on residential connections
- **WireGuard encryption** — all traffic is encrypted end-to-end
- **Direct connections** — peers connect directly when possible (typical latency: 1-5ms on same ISP)
- **DERP fallback** — when direct connection isn't possible, traffic relays through Tailscale's servers (adds 20-50ms but still works)
- **Always free** for personal use (up to 100 devices on a free plan — more than enough)

### The Go Sidecar

RWK embeds a Tailscale node using the `tsnet` library in a Go-based sidecar process (`rwk-tailscale-sidecar.exe`). This runs in **userspace** — no system Tailscale install, no TUN adapter, no administrator privileges. The sidecar:

- Joins the tailnet using an OAuth key (one-time browser login)
- Provides true UDP datagram transport for edge data
- Handles TCP and UDP port forwarding over the mesh
- Reports path type (Direct vs DERP), RTT, and connection health
- Exits automatically if the parent process dies (stdin EOF detection)

### Direct Mode vs DERP

When both the Client and Station are on the same ISP or can reach each other directly, Tailscale establishes a **direct WireGuard tunnel** with latency typically under 5ms. This is the ideal case for CW.

When direct connection isn't possible (double-NAT, symmetric NAT, restrictive firewalls), traffic is relayed through Tailscale's DERP (Designated Encrypted Relay for Packets) servers. This adds 20-50ms of latency but the jitter buffer compensates automatically — the adaptive algorithm widens its buffer window when it detects DERP-class jitter.

The status bar shows the current path type and RTT so you always know your connection quality.

---

## Station Pairing Key (CW Remote Keying Only)

Each Station generates a unique 8-character pairing key on first run. This key is the shared secret used for HMAC-SHA256 challenge/response authentication when a Client **pairs** for CW remote keying.

> **Note:** Pairing is only required for CW remote keying. Port forwarding works over the Tailscale mesh without pairing.

**Setup flow:**
1. Station operator: **File menu → Show Pairing Key** → copies the key (e.g., `K7XP3NWD`)
2. Gives the key to the Client operator (phone, email, etc.)
3. Client operator: clicks **Set Key** → pastes the key
4. Client clicks **Pair** → HMAC handshake authenticates the keying session

This allows one Client to pair with different Stations (home, contest site, portable) by entering the appropriate pairing key. Each Station has its own unique key. The Station can **Unpair** the Client at any time, which sends "AS UNPAIRED" in sidetone to the operator.

---

## Client Inputs

### Paddle Input (Serial Port)

Connect a CW paddle directly to a serial port (or USB-to-serial adapter). The pin mapping follows the standard used by many amateur radio interfaces:

```
DB-9 Serial Connector          Paddle
─────────────────────          ──────
Pin 8  (CTS) ◄──────────────── Dit contact
Pin 6  (DSR) ◄──────────────── Dah contact
Pin 1  (DCD) ◄──────────────── Straight key (optional)
Pin 4  (DTR) ──────────────────► +5V (paddle voltage source)
Pin 5  (GND) ◄──────────────── Common / Ground
```

The poller asserts DTR as the voltage source for the paddle contacts. When a contact closes, the corresponding modem status pin goes active. Software debounce (default 5ms, configurable) prevents false triggers.

### Logger App Input (WK2 Protocol)

RWK emulates a K1EL WinKeyer2 on a serial port at 1200 baud, 8-N-2. Any logging software that supports WinKeyer can send CW through RWK:

- **N1MM+** — select RWK's port as the WinKeyer port
- **DXLog** — same configuration
- **WriteLog, Win-Test, Logger32** — any logger with WK2 support

The protocol handling is complete: Admin Open/Close, buffered text, speed changes, character echo, status reporting — all per the WK2 specification.

### Hardware WinKeyer Input

If you have a physical K1EL WinKeyer2 or WinKeyer3, RWK can drive it as a host. Select "Hardware WinKey" mode in the Input Ports panel, and RWK sends commands to the chip (speed, text) while reading status and character echoes back.

### Using Virtual Serial Ports (VSPE or com0com)

If your logging software and RWK are on the same PC, you need a virtual serial port pair (back-to-back ports). Tools like **VSPE** or **com0com** create paired virtual ports (e.g., COM10 ↔ COM11):

1. Install [com0com](https://sourceforge.net/projects/com0com/) or [VSPE](https://www.eterlogic.com/Products.VSPE.html)
2. Create a port pair (e.g., COM10 ↔ COM11)
3. In your logger, set COM10 as the WinKeyer port
4. In RWK Client, select COM11 as the WinKeyer port
5. Select "Logger App" mode

Your logger thinks it's talking to a real WinKeyer. RWK receives the commands and generates edges.

---

## Keying Output (Station Side)

The Station keys the radio via a serial port control line (DTR or RTS). Many radios accept direct DTR/RTS keying on their serial/USB port:

- **Elecraft K3/K4** — DTR keying via the serial CAT port
- **Icom** — CI-V CW keying or external key jack
- **Yaesu** — DTR on the CAT port (some models)

For radios that only accept a key jack closure, a simple transistor switch on the DTR/RTS line provides the interface:

```
Serial DTR/RTS ──── 1kΩ ──── Base
                              │
                           2N2222
                              │
Radio Key Jack ────────── Collector
Radio Key GND  ────────── Emitter
```

**Polarity note:** Configure the polarity so that a **dropped control line = key up.** This ensures the fail-safe (which de-asserts all lines on error/exit) produces key-up rather than a stuck transmitter.

---

## Installation & Configuration

### Prerequisites

- Windows 10 or 11, 64-bit
- A free [Tailscale](https://tailscale.com) account

### Step 1: Create a Tailscale Account

> **Important:** Create a **new Google account** (or any supported OAuth provider) specifically for your RWK network. Do not use an account that already has a Tailscale network — you want a fresh, dedicated tailnet so you can always access the Tailscale admin page without conflicts.

1. Go to [https://login.tailscale.com](https://login.tailscale.com)
2. Sign up with your new account
3. The personal Tailscale plan is **always free** (up to 100 devices)

### Step 2: Install RWK

Run `RWK-Setup.exe`. Choose which components to install:

- **Client** — install at your operating position
- **Station** — install at the remote radio site
- **Both** — if you're setting up on one machine for testing

The installer places everything in `%LOCALAPPDATA%\RWK Router Keyer\` — no administrator rights required. All three files (Client, Station, sidecar) must be in the same directory.

### Step 3: First Run — Station

1. Launch **RWK Station**
2. On first run, it will open a browser window for Tailscale login
3. Log in with your dedicated Tailscale account
4. The Station joins the tailnet and shows its Tailscale IP (e.g., `100.64.x.x`)
5. Note the IP address (or copy it with the Copy button)
6. Go to **File menu → Show Pairing Key** — note the 8-character key (needed only for CW pairing)

### Step 4: First Run — Client

1. Launch **RWK Client**
2. Log in to Tailscale with the **same account** used for the Station
3. Once connected, enter the Station's Tailscale IP in "Station Address"
4. Click **Set Key** and enter the Station's pairing key
5. Click **Pair** — you should see "Paired" and "Session active"

### Step 5: Configure Keying Output (Station)

1. In the Station app, select the COM port connected to your radio
2. Choose the key line (DTR or RTS) — match your radio's wiring
3. Set polarity inversion if needed (remember: dropped line = key up)
4. The Station should show "ARMED" in green

### Step 6: Configure Client Input

1. Select your paddle's COM port in the "Paddle" dropdown
2. OR select your WinKeyer/Logger COM port in the "WinKeyer" dropdown
3. Choose "Logger App" or "Hardware WinKey" mode as appropriate
4. Test with the **WinKeyer Loopback Test** button (plays sidetone without keying the transmitter)

### Step 7: Port Forwarding (Optional — does not require pairing)

To tunnel other traffic (CAT control, audio, RRC):

1. In the Client's Port Forwards grid, click **+ Add**
2. Edit the rule: set Name, Protocol (TCP or UDP), Client port, Station port
3. Set the **Bind Address** (127.0.0.1 for local apps, 0.0.0.0 for LAN devices)
4. Set the **Station Target** address (the IP of the device on the Station's LAN, or 127.0.0.1 for apps running on the Station itself)
5. Select the rule and click **Enable Sel** to start forwarding
6. Status column shows "Listening" when ready, "Active" when traffic flows

Port forwards are persisted and automatically re-created on restart. Use **Disable Sel** or **Disable All** to stop forwarding without removing the rule.

### Step 8: FlexRadio Setup (Optional — for Flex 6000/8000 series)

To remote a FlexRadio without SmartLink:

1. **Create port forwards** on the Client for the FlexRadio ports:
   - TCP port 4992 → Station Target = radio's IP on Station LAN (e.g., 192.168.1.50), Station Port 4992
   - Additional UDP ports for VITA-49 streaming data (typically 4993+, as needed by your setup)

2. **Enable discovery capture on Station:**
   - Check "Enable discovery capture" in the FlexRadio Discovery section

3. **Enable discovery re-emission on Client:**
   - Check "Enable discovery re-emission" in the FlexRadio Discovery section

4. **Enable the port forward rules** using the Enable buttons

5. **Open SmartSDR** on the Client PC — the radio should appear in the discovery list as if it were on your local network. Connect normally.

> **Note:** The discovery relay rewrites the radio's IP and command port to point at your local forward rule. SmartSDR connects through the tunnel transparently. No SmartLink account, no public IP, no Flex cloud infrastructure required.

---

## Tailscale Administration

- **File menu → Go to Tailscale Admin Page** opens [login.tailscale.com/admin/machines](https://login.tailscale.com/admin/machines)
- **File menu → Delete Tailscale Authorization** removes the stored credentials (forces re-login on next start)
- **File menu → Show Pairing Key** (Station only) displays the key for CW pairing
- The **Station Armed** checkbox on the Client controls whether CW edges are sent to the transmitter
- The **Unpair** button on the Station disconnects the current CW session

---

## Building from Source

```bash
# .NET apps
dotnet build RWK.sln -c Release

# Go sidecar
cd src/RWK.TailscaleSidecar
go build -o rwk-tailscale-sidecar.exe .

# Installer (requires Inno Setup 6)
iscc build/installer/rwk-setup.iss
```

---

## License

MIT License — Copyright (c) 2026 Gerry Hull, W1VE

Free and open-source. Use it, modify it, share it.

---

## Acknowledgments

- [Tailscale](https://tailscale.com) — for making private networking trivially easy
- [K1EL Electronics](https://www.k1el.com) — for the WinKeyer protocol that loggers universally support
- The amateur radio community — for decades of innovation in CW operating

---

*73 de W1VE*
