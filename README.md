# 🎙️ RWK — Remote WinKeyer

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows" alt="Windows x64" />
  <img src="https://img.shields.io/badge/protocol-K1EL%20WinKeyer-orange" alt="WinKeyer Protocol" />
  <img src="https://img.shields.io/badge/transport-UDP%20%7C%20Cloud%20Relay-green" alt="UDP | Cloud Relay" />
  <img src="https://img.shields.io/badge/license-free%20to%20use-blue" alt="Free" />
  <img src="https://img.shields.io/badge/status-beta-yellow" alt="Beta" />
</p>

<p align="center"><b>Pretty trivial remote CW with a paddle.</b></p>

---

## 📡 What Is This?

This Remote WinKeyer project was designed to overcome the challenge of operating CW remotely. If you already use simple remote desktop and audio for remote operation (as described at [remote.radio](https://remote.radio)), RWK lets you **use a paddle** at your local QTH to key your remote station — with proper timing and zero sidetone latency.

### The Design

| Component | Role |
|-----------|------|
| **RWKServer** | Runs at the remote station. Emulates the full K1EL WinKeyer protocol in software. Accepts commands from a local logger (N1MM via serial) and/or a remote client (via UDP or Cloud Relay). Keys the radio by toggling DTR/RTS on a physical serial port. |
| **RWKClient** | Runs at your local QTH. Connects to your physical WinKeyer hardware. Forwards all paddle keying and commands to the remote RWKServer over UDP or Cloud Relay. |

### Transport Options

RWK supports two transport modes:

| Transport | Best For | Setup Complexity |
|-----------|----------|------------------|
| **UDP** | LAN or VPN (Tailscale) connections with stable IP addresses | Medium — requires Tailscale or port forwarding |
| **Cloud Relay** | Zero-config internet connectivity, works through any NAT/firewall | **Easy** — just share a pairing token |

### Three Connections on the Server

```
┌─────────────────────────────────────────────────────────────┐
│                        RWK Server                           │
│                                                             │
│   Serial IN ──────→  Virtual WinKeyer  ──────→ Serial OUT   │
│   (N1MM local)         Engine              (DTR/RTS key)    │
│                          ↑                                  │
│   UDP IN ────────────────┤                                  │
│   (RWKClient UDP)        │                                  │
│                          │                                  │
│   Cloud Relay ───────────┘                                  │
│   (RWKClient Relay)                                         │
└─────────────────────────────────────────────────────────────┘
```

- **Keying Port (output):** Toggles DTR or RTS to key your transmitter
- **Local WinKey Control Port (input):** Serial connection for N1MM or other logging software at the station
- **Remote Input:** UDP listener OR Cloud Relay — accepts WinKeyer protocol bytes from the remote RWKClient

### How the Client Works

```
┌──────────────────────────────────┐    UDP or Relay     ┌──────────────┐
│         RWK Client               │ ──────────────────→ │  RWK Server  │
│                                  │                     │              │
│  Physical WinKeyer ←→ Serial     │                     │  → Radio TX  │
│  Paddle keying     → Transport   │                     └──────────────┘
│  Speed pot changes → Transport   │
│  Keyboard typing   → Transport   │
└──────────────────────────────────┘
```

- Your **local WinKeyer** handles the paddle input and generates sidetone — zero latency for the operator
- Speed pot changes are forwarded to the server (with debouncing to filter noise)
- All WinKeyer protocol bytes are transparently relayed
- The **Send Text** tab lets you type characters directly from the keyboard

---

## 🏗️ Architecture

### Virtual WinKeyer Engine (RWKServer)

The server implements the K1EL WinKeyer2/3 protocol from scratch in C#. It's not a simple pass-through — it's a **full protocol state machine** that:

1. **Parses all WinKeyer commands** — Admin Open/Close, Speed, Weighting, PTT Lead/Tail, Pin Config, WK2 Mode, Load Defaults, Clear Buffer, and more
2. **Handles multi-byte command framing** — correctly consumes follow-on bytes for each command type
3. **Echoes characters** back to the host (required by N1MM to track transmission state)
4. **Reports status bytes** with the 0xC0 prefix format expected by host software
5. **Starts in host mode** by default so it works immediately after restart without requiring Admin Open

### Sub-Millisecond CW Timing

Accurate Morse timing is critical. A dit at 40 WPM is only 30ms — any jitter is audible. The server achieves consistent timing through:

| Technique | Purpose |
|-----------|---------|
| **Precomputed Edge Schedules** | Converts text to an array of absolute timestamps before keying begins — no computation during transmission |
| **PARIS Timing Standard** | dit = 1200/WPM ms; dah = 3×dit; inter-char = 3×dit; word gap = 7×dit |
| **Dedicated High-Priority Thread** | Keying runs on `ThreadPriority.Highest` to minimize scheduling interference |
| **GCLatencyMode.SustainedLowLatency** | Suppresses garbage collection pauses during keying |
| **Hybrid Wait Strategy** | `Thread.Sleep(1)` for coarse approach, then `SpinWait` for final sub-ms precision |
| **timeBeginPeriod(1)** | Sets Windows timer resolution to 1ms during active keying |
| **EscapeCommFunction** | Toggles DTR/RTS via a single IOCTL — no SerialPort property setter overhead |
| **Cached SafeFileHandle** | Port opened once with `CreateFile`; no open/close overhead per edge |
| **Absolute Deadline Scheduling** | Never uses relative sleeps between edges — USB latency cancels rather than accumulates |

### Inter-Message Spacing

When characters arrive as separate UDP packets (typed slowly), the timing engine automatically inserts the correct **3-dit inter-character gap** between consecutively-queued messages. This prevents letters from running together even when each character arrives in its own packet.

### UDP Packet Handling (RWKClient)

To avoid characters being split across many tiny UDP packets over a long internet path:

| Strategy | Detail |
|----------|--------|
| **Keystroke Batching** | Characters typed within 150ms of each other are grouped into a single UDP datagram |
| **Server-Side Buffering** | The server's 50ms flush timer accumulates characters before committing them to the timing engine |
| **Abort Support** | ESC key immediately sends Clear Buffer (0x0A), which aborts the current transmission within 1ms |

This two-tier buffering (client batches → server buffers) means "W1TU" typed quickly arrives and keys as a single coherent word, not four separate letters.

---

## ☁️ Cloud Relay — Zero-Config Connectivity

### The Easiest Way to Connect

Cloud Relay is the simplest way to connect RWKClient and RWKServer across the internet. It requires **no VPN, no port forwarding, and no firewall configuration**. Both endpoints connect outbound to a relay server hosted on Cloudflare's global edge network.

### How It Works

```
┌──────────────┐         wss://          ┌─────────────────┐         wss://          ┌──────────────┐
│  RWKClient   │ ──────────────────────→ │ Cloudflare Edge │ ←────────────────────── │  RWKServer   │
│  (Home)      │    WebSocket + TLS      │  (wrs.w1ve.com) │    WebSocket + TLS      │  (Station)   │
└──────────────┘                         └─────────────────┘                         └──────────────┘
```

1. Server generates a **64-character pairing token** and connects to the relay
2. You copy the token to the client (via email, text message, etc.)
3. Client connects using the same token — the relay pairs them together
4. All WinKeyer data flows through the encrypted WebSocket tunnel

### Setup — Cloud Relay (Recommended)

**At the remote station (RWKServer):**
1. Select the Keying Port (COM port connected to your radio keying circuit)
2. Choose DTR or RTS
3. Set **Transport** to **Cloud Relay**
4. Click **Generate Token** — a 64-character hex token appears
5. Click **Copy** to copy the token to clipboard
6. Send this token to yourself (email, text, etc.)
7. Click **Start** — status shows "Relay: Paired" when connected

**At your local QTH (RWKClient):**
1. Select your WinKeyer's COM port
2. Set **Transport** to **Cloud Relay**
3. Paste the **Pairing Token** from the server
4. Click **Start** — status shows "✓ Paired" when connected
5. Key with your paddle — or switch to the Send Text tab and type

### Cloud Relay Features

| Feature | Detail |
|---------|--------|
| **Zero Config** | No VPN, no port forwarding, no firewall rules needed |
| **Automatic Reconnect** | If connection drops, both sides reconnect automatically |
| **Heartbeat Keep-Alive** | 5-second heartbeats prevent NAT timeouts |
| **End-to-End Encryption** | TLS 1.3 WebSocket connection |
| **Global Edge Network** | Cloudflare routes to nearest data center |
| **Session Pairing** | Unique token ensures only your client connects |

### Security Notes

- The pairing token is a cryptographically random 256-bit value
- Tokens are single-use — generate a new one each session if desired
- The relay only passes data between paired endpoints — no storage or logging
- All traffic is encrypted via TLS 1.3

---

## 🌐 Networking — UDP with Tailscale

> **Note:** This section is only needed if you're using **UDP transport** instead of Cloud Relay. If you're using Cloud Relay (recommended), skip to [Getting Started](#-getting-started).

### Why You Need This (for UDP Mode)

When your station PC and your operating PC are on different internet connections (different houses, different ISPs), they can't normally talk directly to each other via UDP. Home routers, cable modems, and firewalls all block incoming connections. This is a fundamental problem with the internet — it's not specific to RWK.

**Tailscale** solves this completely. It's a free program that creates a private encrypted tunnel between your computers, giving each one a simple `100.x.x.x` address that works no matter where they are — behind NAT, on cellular, on hotel WiFi, anything. It just works.

### Step 1: Create a Tailscale Account (Once)

1. Go to [https://tailscale.com/](https://tailscale.com/)
2. Click **Get Started** — it's free for personal use (up to 100 devices)
3. Sign in with your Google, Microsoft, or GitHub account
4. That's it — no credit card, no trial period

### Step 2: Install Tailscale on Your Station PC (Remote)

1. Go to [https://tailscale.com/download/windows](https://tailscale.com/download/windows)
2. Download and run the installer
3. When it finishes, a Tailscale icon appears in your system tray (bottom-right near the clock)
4. Click the icon and sign in with the same account you created in Step 1
5. After signing in, the icon turns blue — you're connected
6. **Note the IP address** — hover over the tray icon or right-click → "My IP" — it will be something like `100.64.x.x`

### Step 3: Install Tailscale on Your Operating PC (Local/Home)

1. Same process — download from [https://tailscale.com/download/windows](https://tailscale.com/download/windows)
2. Install, sign in with the **same account**
3. Both machines are now on your private Tailscale network

### Step 4: Test the Connection

On your local PC, open a Command Prompt and type:
```
ping 100.64.x.x
```
(use the IP from your station PC in Step 2)

You should see replies. If so, everything is working.

### Step 5: Use the Tailscale IP in RWKClient

In the RWKClient app, enter the station PC's Tailscale IP (e.g., `100.64.0.2`) as the **WKR Server IP**. That's all the configuration needed.

### That's It!

Tailscale handles everything else automatically:
- ✅ Works through any NAT or firewall
- ✅ Works on different ISPs (cable, fiber, cellular, Starlink)
- ✅ Encrypted end-to-end (WireGuard)
- ✅ Starts automatically with Windows
- ✅ Reconnects automatically if internet drops
- ✅ Adds only 1-3ms of latency (negligible for CW)
- ✅ Free for personal use

### Troubleshooting

| Problem | Fix |
|---------|-----|
| Can't ping the other machine | Make sure both are signed in to Tailscale (icon should be blue/connected) |
| Tailscale connected but RWK doesn't work | Check that port 7388 isn't blocked by Windows Firewall — add an exception for WKRServer.exe |
| High latency (>100ms) | Tailscale is relaying through a server instead of going direct. This is rare but can happen. Try restarting Tailscale on both machines. |
| Forgot the IP | Right-click the Tailscale tray icon → "My IP addresses" or visit [https://login.tailscale.com/admin/machines](https://login.tailscale.com/admin/machines) |
---

## 🚀 Getting Started

### Prerequisites

- Windows x64 (both machines)
- A serial port or USB-to-serial adapter at the remote station (for keying)
- A K1EL WinKeyer at your local QTH (optional — keyboard-only mode works without one)
- For **Cloud Relay**: Internet connection on both machines (no other setup needed)
- For **UDP**: [Tailscale](https://tailscale.com/) or any other way to route UDP between the machines

### Installation

No installer needed. Download the EXEs from the [Releases](https://github.com/w1ve/rwk/releases) page or build from source:

- **Remote station:** Run `WKRServer.exe`
- **Local QTH:** Run `WKRClient.exe`

### Quick Setup — Cloud Relay (Easiest)

**At the remote station (RWKServer):**
1. Select the Keying Port (COM port connected to your radio keying circuit)
2. Choose DTR or RTS
3. Set **Transport** to **Cloud Relay**
4. Click **Generate Token** — copy the 64-character token
5. Send the token to yourself (email, text, etc.)
6. Click **Start**

**At your local QTH (RWKClient):**
1. Select your WinKeyer's COM port
2. Set **Transport** to **Cloud Relay**
3. Paste the **Pairing Token**
4. Click **Start**
5. Status shows "✓ Paired" — you're connected!

### Quick Setup — UDP Mode

**At the remote station (RWKServer):**
1. Select the Keying Port (COM port connected to your radio keying circuit)
2. Choose DTR or RTS
3. Optionally select a Local WinKey Control Port for N1MM
4. Set **Transport** to **UDP**
5. Set the UDP listen port (default 7388)
6. Click **Start**

**At your local QTH (RWKClient):**
1. Select your WinKeyer's COM port
2. Set **Transport** to **UDP**
3. Enter the RWKServer's IP address and port
4. Click **Start**
5. Key with your paddle — or switch to the Send Text tab and type

---

## 🔨 Building from Source

```bash
# Clone
git clone https://github.com/w1ve/rwk.git
cd rwk

# Build
dotnet build WinKeyerEmulator.sln -c Release

# Run tests
dotnet test WinKeyerEmulator.sln

# Publish single-file EXEs
dotnet publish src/WinKeyerEmulator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/WKRClient -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output:
```
src/WinKeyerEmulator.App/bin/Release/net9.0-windows/win-x64/publish/WKRServer.exe
src/WKRClient/bin/Release/net9.0-windows/win-x64/publish/WKRClient.exe
```

---

## 📋 Project Structure

```
rwk/
├── src/
│   ├── WinKeyerEmulator.Core/     # Protocol engine, timing, abstractions (no UI)
│   │   ├── CloudRelay/            # WebSocket relay transport
│   │   │   ├── CloudRelayTransport.cs  # WebSocket client with reconnect/heartbeat
│   │   │   ├── WireProtocol.cs         # Binary frame serialization
│   │   │   └── TokenGenerator.cs       # Pairing token generation
│   │   ├── Protocol/              # WinKeyer protocol state machine
│   │   ├── Timing/                # High-precision Morse timing engine
│   │   └── IO/                    # Keying output abstractions
│   ├── WinKeyerEmulator.App/      # WKRServer — WinForms app
│   └── WKRClient/                 # WKRClient — WinForms app
├── tests/
│   ├── WinKeyerEmulator.Core.Tests/        # Unit + property-based tests
│   └── WinKeyerEmulator.Integration.Tests/ # UDP protocol tests
├── binaries/                      # Pre-built executables
└── WinKeyerEmulator.sln
```

---

## ⚠️ Known Limitations

- **Windows x64 only** — uses WinForms and Win32 P/Invoke
- **Beta** — not all WinKeyer commands have full behavioral implementation (they are correctly parsed and consumed, but some like Weighting and Farnsworth are acknowledged without affecting timing)
- **UDP is fire-and-forget** — a dropped packet means a missed character (acceptable trade-off for latency)
- **Cloud Relay adds ~20-50ms latency** — traffic routes through Cloudflare; UDP via Tailscale is faster but requires more setup
- **`timeBeginPeriod(1)`** affects system-wide timer resolution while running
- **Speed pot range** — WinKeyer speed pot is mapped to 5-50 WPM; changes are debounced to filter ADC noise

---

## 📜 License

Copyright © 2026 by Gerry Hull, W1VE

This code is freely available to use and modify as you wish.

---

## 🧪 Help Wanted — Testing with Different WinKeyer Versions

This project has been developed and tested primarily with a **WinKeyer 3 (WK3) version 31** at the client side. The K1EL WinKeyer family spans multiple hardware generations and firmware versions, each with subtle protocol differences.

### We Need Your Help!

If you have any of the following hardware, we'd love your feedback:

| Hardware | Firmware | Status |
|----------|----------|--------|
| WinKeyer 1 (WK1) | Any | **Untested** — please report! |
| WinKeyer 2 (WK2) | Any | **Untested** — please report! |
| WinKeyer 3 (WK3) | v23-v30 | **Untested** — please report! |
| WinKeyer 3 (WK3) | v31 | ✅ Tested — working |
| WinKeyer USB | Any | **Untested** — please report! |
| WinKeyer Lite | Any | **Untested** — please report! |
| WKUSB-SMT | Any | **Untested** — please report! |
| K1EL Keyer Kits | Any | **Untested** — please report! |

### How to Help

1. **Try it out** — Download the binaries and test with your WinKeyer
2. **Note your hardware** — WinKeyer model, firmware version (shown in RWKClient log on connect)
3. **Report what works and what doesn't:**
   - Does paddle keying work?
   - Does the speed pot control local speed?
   - Are speed changes forwarded to the server?
   - Any unexpected behavior?

### Known Compatibility Notes

- **Paddle echo mode** (`0x0D 0x40`) — Required for forwarding paddle characters, but may interact differently with speed pot commands on some firmware versions
- **Speed pot status bytes** — Format is `0x80 | pot_position`; debouncing filters ADC noise but behavior may vary
- **WK2 vs WK3 mode** — The client currently uses WK3-style initialization

### Report Issues

Please open a [GitHub Issue](https://github.com/w1ve/rwk/issues) with:
- Your WinKeyer model and firmware version
- What worked / what didn't work
- Any log output showing the problem

Your testing helps make RWK work for everyone. Thanks! 🙏

---

<p align="center"><i>73 de W1VE</i></p>
