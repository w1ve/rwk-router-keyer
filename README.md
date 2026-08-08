# 🎙️ RWK — Remote WinKeyer

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows" alt="Windows x64" />
  <img src="https://img.shields.io/badge/protocol-K1EL%20WinKeyer-orange" alt="WinKeyer Protocol" />
  <img src="https://img.shields.io/badge/transport-UDP-green" alt="UDP" />
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
| **RWKServer** | Runs at the remote station. Emulates the full K1EL WinKeyer protocol in software. Accepts commands from a local logger (N1MM via serial) and/or a remote client (via UDP). Keys the radio by toggling DTR/RTS on a physical serial port. |
| **RWKClient** | Runs at your local QTH. Connects to your physical WinKeyer hardware. Forwards all paddle keying and commands to the remote RWKServer over UDP. |

### Three Connections on the Server

```
┌─────────────────────────────────────────────────────────────┐
│                        RWK Server                            │
│                                                             │
│   Serial IN ──────→  Virtual WinKeyer  ──────→ Serial OUT   │
│   (N1MM local)         Engine              (DTR/RTS key)    │
│                          ↑                                  │
│   UDP IN ────────────────┘                                  │
│   (RWKClient remote)                                        │
└─────────────────────────────────────────────────────────────┘
```

- **Keying Port (output):** Toggles DTR or RTS to key your transmitter
- **Local WinKey Control Port (input):** Serial connection for N1MM or other logging software at the station
- **UDP Listener (input):** Accepts WinKeyer protocol bytes from the remote RWKClient

### How the Client Works

```
┌──────────────────────────────────┐         UDP          ┌──────────────┐
│         RWK Client               │ ──────────────────→  │  RWK Server  │
│                                  │                      │              │
│  Physical WinKeyer ←→ Serial     │                      │  → Radio TX  │
│  Keyboard typing   → UDP        │                      └──────────────┘
│  Speed changes     → UDP        │
└──────────────────────────────────┘
```

- Your **local WinKeyer** handles the paddle input and generates sidetone — zero latency for the operator
- Speed changes on the local paddle knob are forwarded to the server
- All WinKeyer protocol bytes are transparently relayed over UDP
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

## 🌐 Networking with Tailscale

The challenge with remote UDP is peer-to-peer connectivity through NATs and firewalls. The simplest (and free) solution is **[Tailscale](https://tailscale.com/)**:

1. Install Tailscale on both PCs (takes 2 minutes)
2. Both machines get a `100.x.x.x` address
3. Enter the server's Tailscale IP in RWKClient — done

No port forwarding, no firewall rules, no VPN configuration. Tailscale's WireGuard-based mesh adds minimal latency (~1-3ms overhead).

---

## 🚀 Getting Started

### Prerequisites

- Windows x64 (both machines)
- A serial port or USB-to-serial adapter at the remote station (for keying)
- A K1EL WinKeyer at your local QTH (optional — keyboard-only mode works without one)
- [Tailscale](https://tailscale.com/) or any other way to route UDP between the machines

### Installation

No installer needed. Download the EXEs and run them:

- **Remote station:** Run `WKRServer.exe`
- **Local QTH:** Run `WKRClient.exe`

### Quick Setup

**At the remote station (RWKServer):**
1. Select the Keying Port (COM port connected to your radio keying circuit)
2. Choose DTR or RTS
3. Optionally select a Local WinKey Control Port for N1MM
4. Set the UDP listen port (default 7388)
5. Click **Start**

**At your local QTH (RWKClient):**
1. Select your WinKeyer's COM port
2. Enter the RWKServer's IP address and port
3. Click **Start**
4. Key with your paddle — or switch to the Send Text tab and type

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
│   ├── WinKeyerEmulator.App/      # WKRServer — WinForms app
│   └── WKRClient/                 # WKRClient — WinForms app
├── tests/
│   ├── WinKeyerEmulator.Core.Tests/        # Unit + property-based tests
│   └── WinKeyerEmulator.Integration.Tests/ # UDP protocol tests
└── WinKeyerEmulator.sln
```

---

## ⚠️ Known Limitations

- **Windows x64 only** — uses WinForms and Win32 P/Invoke
- **Beta** — not all WinKeyer commands have full behavioral implementation (they are correctly parsed and consumed, but some like Weighting and Farnsworth are acknowledged without affecting timing)
- **UDP is fire-and-forget** — a dropped packet means a missed character (acceptable trade-off for latency)
- **`timeBeginPeriod(1)`** affects system-wide timer resolution while running

---

## 📜 License

Copyright © 2026 by Gerry Hull, W1VE

This code is freely available to use and modify as you wish.

---

<p align="center"><i>73 de W1VE</i></p>
