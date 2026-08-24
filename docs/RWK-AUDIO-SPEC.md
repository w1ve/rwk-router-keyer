# RWK v1.0.4 — Bidirectional Opus Audio Link Specification

**Version:** 1.0-draft  
**Date:** 2026-08-22  
**Author:** W1VE  
**Branch:** `v1.0.4-audio`  
**Companion to:** RWK Router/Keyer (https://github.com/w1ve/rwk-router-keyer)

---

## 1. Purpose

RWK v1.0.3 provides CW keying, port forwarding, and discovery relaying. What it
does not provide is audio — the operator hears nothing from the radio unless they
separately configure an audio streaming solution (RS-BA1 audio, RemoteRig VoIP, or
a manual tunnel for an audio-over-IP codec).

v1.0.4 adds a **built-in bidirectional Opus audio link** between Client and Station.
The operator hears the radio's receiver audio through their local speakers, and can
transmit SSB/AM/digital voice by speaking into their local microphone — all over the
existing Tailscale mesh, with no additional software or configuration.

This eliminates the last dependency on vendor-specific remote audio solutions and
makes RWK a **complete remote station system**: CW keying + voice audio + port
forwarding + discovery relay, in one package.

---

## 2. Design Principles

### 2.1 Audio is parallel to CW, not a replacement

The CW keyer continues to operate independently. Audio is a separate bidirectional
stream. Both can be active simultaneously (e.g., operator sends CW while monitoring
RX audio for the reply). The two streams share the same Tailscale path but use
different UDP ports.

### 2.2 Opus is the only codec

No codec negotiation, no fallback. Opus is the right answer for low-latency,
variable-bitrate, patent-free voice/music audio over lossy networks. It handles
both voice (SILK mode) and music/tones (CELT mode) adaptively. WSJT-X tones,
CW sidetone from the radio, SSB voice, and digital voice all encode well at
24-48 kbps mono.

### 2.3 Lowest achievable latency

Target: **under 80ms glass-to-glass** on a direct Tailscale path. This requires:
- 20ms Opus frame size (960 samples at 48kHz)
- Minimal encode/decode buffering
- Small playout jitter buffer (40-80ms, adaptive)
- No resampling if possible (request 48kHz from WASAPI)

### 2.4 The Station is the audio bridge to the radio

The Station captures audio FROM the radio (receiver audio) and plays audio TO the
radio (transmit audio). The Client captures audio FROM the operator's microphone
and plays audio TO the operator's speakers. Two independent audio paths, each
using a separate sound device at each end.

---

## 3. Architecture

```
CLIENT                                          STATION
──────                                          ───────

┌─────────────────────┐                         ┌─────────────────────┐
│ RX Audio Path       │                         │ RX Audio Path       │
│                     │                         │                     │
│ Speakers/Headphones │                         │ Radio Audio Out     │
│       ▲             │                         │       │             │
│       │             │                         │       ▼             │
│  WASAPI Render      │                         │  WASAPI Capture     │
│       ▲             │                         │       │             │
│       │             │                         │       ▼             │
│  Opus Decode        │    UDP (port 7375)      │  Opus Encode        │
│       ▲             │◄────────────────────────│       │             │
│       │             │                         │       │             │
│  Jitter Buffer      │                         │  Frame Packetizer   │
└─────────────────────┘                         └─────────────────────┘

┌─────────────────────┐                         ┌─────────────────────┐
│ TX Audio Path       │                         │ TX Audio Path       │
│                     │                         │                     │
│ Microphone          │                         │ Radio Audio In      │
│       │             │                         │       ▲             │
│       ▼             │                         │       │             │
│  WASAPI Capture     │                         │  WASAPI Render      │
│       │             │                         │       ▲             │
│       ▼             │                         │       │             │
│  Opus Encode        │    UDP (port 7376)      │  Opus Decode        │
│       │             │────────────────────────►│       ▲             │
│       │             │                         │       │             │
│  Frame Packetizer   │                         │  Jitter Buffer      │
└─────────────────────┘                         └─────────────────────┘

┌─────────────────────┐                         ┌─────────────────────┐
│ PTT Control         │                         │ PTT Control         │
│                     │                         │                     │
│ PTT button / VOX    │    Control channel      │ Assert radio PTT    │
│       │             │────────────────────────►│ (CAT cmd or line)   │
└─────────────────────┘                         └─────────────────────┘
```

### 3.1 UDP Ports

| Port | Direction | Content |
|------|-----------|---------|
| 7375 | Station → Client | RX audio (radio receiver → operator speakers) |
| 7376 | Client → Station | TX audio (operator mic → radio transmitter) |

These are **sidecar-managed UDP forwards** — same infrastructure as the edge port
and user-defined UDP forwards. No new sidecar API needed; just two additional
`out-udp` / `in-udp` forward registrations at session start.

### 3.2 Packet Format

Each UDP datagram contains exactly one Opus frame:

```
Offset  Size    Field
0       2       Sequence number (uint16, big-endian, wraps)
2       2       Timestamp (uint16, big-endian, frame count since stream start)
4       1       Flags (bit 0 = PTT active, bits 1-7 reserved)
5       N       Opus encoded frame (variable length, typically 60-150 bytes)
```

Total overhead per frame: 5 bytes header + Opus payload + UDP/IP overhead.
At 20ms frames: 50 packets/second per direction.

Sequence numbers enable loss detection. Timestamps enable jitter buffer
ordering. The PTT flag in the TX→Station path allows the Station to assert
radio PTT without a separate control message (lower latency than a JSON
control channel round-trip).

### 3.3 Opus Configuration

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Sample rate | 48000 Hz | Opus native rate, avoids resampling |
| Channels | 1 (mono) | Ham radio audio is always mono |
| Frame size | 20 ms (960 samples) | Lowest latency Opus supports well |
| Bitrate | 32000 bps (default) | Good quality for voice + CW tones |
| Application | VOIP | Optimizes for low latency over quality |
| Complexity | 5 | Good balance of quality vs CPU |
| Packet loss % | 10 (expected) | Tells encoder to add FEC |
| DTX | Disabled | Don't pause on silence — operator wants to hear band noise |
| Inband FEC | Enabled | Helps on lossy paths (DERP, satellite) |

Bitrate is user-configurable from the UI: 16/24/32/48/64 kbps.
Lower = less bandwidth, higher = better quality for music/digital modes.

---

## 4. Audio Capture and Playback

### 4.1 Windows (WASAPI)

Both Client and Station use WASAPI in **shared mode** (not exclusive) to
coexist with other applications using the same device.

- **Capture:** `IAudioCaptureClient` with event-driven buffering
- **Render:** `IAudioRenderClient` with event-driven buffering
- **Buffer size:** Request 20ms period (960 samples at 48kHz)
- **Format:** IEEE Float 32-bit, 48kHz, mono. If the device doesn't support
  mono 48kHz, use the device's preferred format and resample.

The existing `LocalSidetoneEngine` already uses WASAPI shared mode for
sidetone output. The audio link reuses the same approach but for both
capture and render, and with Opus in the middle.

### 4.2 Linux/Pi (future)

For the Linux Station (v1.0.4-linux parallel work):
- ALSA or PulseAudio/PipeWire capture and render
- Same Opus configuration
- `System.Device.Audio` or `PortAudio` via P/Invoke

### 4.3 Device Selection

The UI exposes two device dropdowns per direction:

**Station:**
- "Radio RX Audio" — capture device (radio speaker out → sound card line-in)
- "Radio TX Audio" — render device (sound card line-out → radio mic-in)

**Client:**
- "Speakers" — render device for RX audio playback
- "Microphone" — capture device for TX audio

Devices are enumerated via `MMDeviceEnumerator` (same as existing sidetone
device selection). Hot-plug detection via `IMMNotificationClient`.

---

## 5. Jitter Buffer

Each direction has an independent playout jitter buffer at the receiving end.
The existing `JitterBuffer` / `EdgeJitterProfile` infrastructure for CW edges
provides the conceptual model, but audio needs a different implementation
because:

1. Audio frames arrive at a fixed 20ms cadence (vs. edges which are sporadic)
2. Audio can tolerate packet loss (Opus PLC handles it) but not gaps
3. The buffer needs to output silence for missing frames rather than blocking

### 5.1 Adaptive buffer

- **Target depth:** 2-4 frames (40-80ms) on a direct path
- **DERP path:** 5-10 frames (100-200ms)
- **Adjustment:** Same EWMA approach as the CW jitter buffer — measure
  inter-arrival jitter, adjust depth smoothly
- **Underrun:** If buffer empties, Opus PLC (Packet Loss Concealment)
  generates extrapolated audio for up to 60ms, then fades to silence
- **Overflow:** If buffer exceeds max depth, discard oldest frames

### 5.2 Statistics (exposed to UI)

- Buffer depth (ms)
- Packet loss %
- Jitter (ms)
- Late packets discarded
- Codec bitrate (actual)

---

## 6. PTT Control

### 6.1 Explicit PTT

A PTT button in the Client UI (keyboard shortcut: Space bar, configurable).
Press = TX, release = RX. The PTT state is carried in the TX audio packet
header (flag bit 0) so the Station asserts radio PTT on the very first audio
frame — zero additional latency vs. a separate control message.

The Station applies PTT via:
1. **CAT command** — `TX;` / `RX;` for Kenwood/Yaesu, CI-V for Icom
2. **Serial line** — existing keying output RTS/DTR (if not used for CW)
3. **Dedicated PTT port** — separate serial port for PTT only

### 6.2 VOX (optional)

Client-side VOX: detect audio energy above a configurable threshold →
set the PTT flag automatically. Includes:
- Threshold (dB)
- Hold time (ms) — how long PTT stays asserted after voice stops
- Anti-trip — mute RX audio during TX to prevent feedback loop

VOX is off by default. Most operators prefer explicit PTT.

### 6.3 Full-duplex option

For RTTY, FT8, and other digital modes where TX and RX audio flow
simultaneously, a "Full Duplex" checkbox disables the RX mute during TX.

---

## 7. Client UI

### 7.1 Audio Panel (new tab or section in Keyer tab)

```
┌─ Audio ──────────────────────────────────────────────────────┐
│                                                              │
│  RX (from radio):                                            │
│    Device: [laptop speakers (Realtek)     ▼]                 │
│    Volume: ═══════════════════●═══  [72%]                    │
│    Level:  ▓▓▓▓▓▓▓▓▓░░░░░░░░░░░  -12 dB                   │
│                                                              │
│  TX (to radio):                                              │
│    Device: [USB Microphone ▼]                                │
│    Volume: ═══════════●═══════════  [50%]                    │
│    Level:  ▓▓▓▓░░░░░░░░░░░░░░░░░  -24 dB                   │
│                                                              │
│  ┌──────────┐                                                │
│  │   PTT    │  [ ] VOX (threshold: -30 dB, hold: 500ms)     │
│  │ (Space)  │                                                │
│  └──────────┘                                                │
│                                                              │
│  Codec: Opus 32 kbps | Buffer: 60ms | Loss: 0.1%            │
│  [ ] Enable Audio    Bitrate: [32 kbps ▼]                    │
│  [ ] Full Duplex                                             │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

### 7.2 Level Meters

Real-time level meters for both RX and TX, updated every 20ms (each frame).
Computed as peak dBFS over the frame. Rendered as a horizontal bar with:
- Green: -60 to -12 dBFS
- Yellow: -12 to -3 dBFS
- Red: -3 to 0 dBFS (clipping)

### 7.3 PTT Keyboard Shortcut

Default: Space bar (global hotkey when Client window is focused).
Configurable to any key. Uses the same low-level keyboard hook as the
keyboard paddle — but only when the Audio panel is active and the paddle
keyboard mode is not using the same key.

---

## 8. Station UI (Windows)

Minimal — the Station is mostly unattended:

```
┌─ Audio ──────────────────────────────────────────────────────┐
│  Radio RX Audio In:  [Line In (Realtek)  ▼]                  │
│  Radio TX Audio Out: [Line Out (Realtek) ▼]                  │
│  Status: Streaming (32 kbps, 0.2% loss)                      │
│  PTT: ○ (idle)  via: [CAT ▼] / [Serial Port ▼]              │
└──────────────────────────────────────────────────────────────┘
```

### 8.1 Station Web UI (Linux/Pi)

Same controls exposed via the web dashboard:
- Device selection (ALSA device names)
- Status + PTT indicator
- Level meters via SignalR

---

## 9. Transport Integration

### 9.1 Session Establishment

When a Client pairs with the Station and a session is established, the audio
forwards are registered automatically (if audio is enabled on both sides):

1. Station registers **inbound** UDP forward on port 7376 (to receive TX audio)
2. Client registers **outbound** UDP forward to Station port 7375 (to receive RX audio)

This uses the existing sidecar forward infrastructure — no new API.

### 9.2 Audio Enable Handshake

The Client sends a control channel message:
```json
{ "type": "audio_enable", "rxEnabled": true, "txEnabled": true, "bitrate": 32000 }
```

The Station responds:
```json
{ "type": "audio_ready", "rxPort": 7375, "txPort": 7376 }
```

Audio streams start flowing after this handshake. If either side has audio
disabled, the streams are not started and no CPU is used for encode/decode.

### 9.3 Bandwidth Estimates

| Bitrate | Per direction | Both directions | Notes |
|---------|---------------|-----------------|-------|
| 16 kbps | ~2 KB/s | ~4 KB/s | Minimum usable (voice only) |
| 24 kbps | ~3 KB/s | ~6 KB/s | Good for CW monitoring |
| 32 kbps | ~4 KB/s | ~8 KB/s | Default, excellent for voice |
| 48 kbps | ~6 KB/s | ~12 KB/s | High quality, music/digital |
| 64 kbps | ~8 KB/s | ~16 KB/s | Maximum, transparent quality |

Even at 64 kbps both directions, the total is 16 KB/s — trivial for any
internet connection, including Starlink or cellular.

---

## 10. Codec Library

### 10.1 Concentus (pure C# Opus)

**Recommendation: Use Concentus** (MIT-licensed, pure managed C#, no native
dependencies, works on all platforms including ARM64 Pi).

NuGet: `Concentus` (v2.x)

```csharp
var encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
encoder.Bitrate = 32000;
encoder.Complexity = 5;
encoder.UseInbandFEC = true;
encoder.PacketLossPercentage = 10;

byte[] encoded = new byte[4000];
int len = encoder.Encode(pcmFrame, 960, encoded, encoded.Length);

var decoder = new OpusDecoder(48000, 1);
short[] decoded = new short[960];
int samples = decoder.Decode(encoded, 0, len, decoded, 960, false);
```

CPU usage: ~1-2% on a modern x64 CPU, ~3-5% on a Pi 4 at complexity 5.
Negligible compared to the audio capture/render overhead.

### 10.2 Alternative: libopus native wrapper

If Concentus performance is insufficient on Pi (unlikely), fall back to
`OpusDotNet` which wraps the native `libopus.so` / `opus.dll`. This requires
shipping native binaries per platform but gives ~30% better performance.

---

## 11. Latency Budget

| Stage | Latency | Notes |
|-------|---------|-------|
| WASAPI capture buffer | 10-20ms | Event-driven, depends on device |
| Opus encode | <1ms | Frame is already complete |
| Packetize + send | <1ms | Small UDP packet |
| Network (direct) | 1-5ms | Tailscale direct path |
| Network (DERP) | 20-50ms | Relay through nearest DERP server |
| Jitter buffer | 40-80ms | Adaptive, 2-4 frames |
| Opus decode | <1ms | |
| WASAPI render buffer | 10-20ms | Event-driven |
| **Total (direct)** | **62-127ms** | |
| **Total (DERP)** | **82-172ms** | |

For comparison: a phone call over LTE is 100-200ms. RS-BA1 over the internet
is 150-400ms. This is competitive with or better than all existing solutions.

---

## 12. Error Handling

### 12.1 Packet loss

Opus has built-in PLC (Packet Loss Concealment). When a frame is missing:
1. Check next frame for inband FEC data → decode lost frame from FEC
2. If no FEC, extrapolate from previous frame (Opus PLC, good for ~60ms)
3. If sustained loss (>5 frames), fade to silence gracefully

### 12.2 Device disconnection

If a capture or render device is unplugged:
- Log the event
- Show a warning in the UI
- Stop the affected stream gracefully (no crash)
- Auto-recover when the device reappears (same hot-plug logic as port enumeration)

### 12.3 Network path change

When the Tailscale path changes (direct ↔ DERP), the jitter buffer
automatically adjusts depth. A brief glitch (20-40ms) is acceptable during
path transitions.

---

## 13. Implementation Plan

### Phase 1: RX audio only (Station → Client)

1. Station: WASAPI capture from radio audio device
2. Station: Opus encode (Concentus) → UDP frames
3. Transport: Register audio UDP forward at session start
4. Client: Receive → jitter buffer → Opus decode → WASAPI render
5. Client UI: device dropdown, volume, level meter, enable checkbox

This is the most valuable feature alone — the operator can hear the radio.

### Phase 2: TX audio (Client → Station)

1. Client: WASAPI capture from microphone
2. Client: Opus encode → UDP frames with PTT flag
3. Station: Receive → jitter buffer → Opus decode → WASAPI render to radio input
4. Station: PTT assertion from packet flag
5. Client UI: PTT button, mic device dropdown, TX level meter

### Phase 3: Polish

1. VOX
2. Full duplex mode
3. Adaptive bitrate (reduce bitrate when loss exceeds threshold)
4. RX mute during TX (anti-feedback)
5. Audio routing for digital modes (virtual audio cable guidance)
6. Statistics display (buffer depth, loss %, jitter)

---

## 14. Project Structure

```
src/RWK.Shared/Audio/
  IAudioCaptureSource.cs        # Interface for platform audio capture
  IAudioRenderSink.cs           # Interface for platform audio render
  OpusAudioEncoder.cs           # Wraps Concentus encoder
  OpusAudioDecoder.cs           # Wraps Concentus decoder
  AudioJitterBuffer.cs          # Playout buffer with PLC
  AudioPacket.cs                # Serialize/deserialize the 5-byte header + payload
  AudioStreamConfig.cs          # Bitrate, frame size, etc.

src/RWK.Client/Audio/
  WasapiCaptureSource.cs        # Client microphone capture
  WasapiRenderSink.cs           # Client speaker playback (extends existing sidetone infra)
  ClientAudioController.cs      # Orchestrates TX capture + RX playback
  AudioPanel.cs                 # WinForms UI (or section in MainForm)

src/RWK.Station/Audio/
  StationAudioController.cs     # Orchestrates RX capture + TX playback
  AudioPttHandler.cs            # Translates PTT flag → radio PTT assertion

src/RWK.Station.Linux/Audio/
  AlsaCaptureSource.cs          # Linux ALSA capture
  AlsaRenderSink.cs             # Linux ALSA render
```

### 14.1 NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Concentus | 2.x | Pure C# Opus encoder/decoder |
| NAudio | 2.x | WASAPI capture/render helpers (already in project) |

No new native dependencies. Concentus is pure managed code.

---

## 15. Configuration

### 15.1 ClientConfig additions

```csharp
public AudioConfig Audio { get; init; } = new();

public record AudioConfig
{
    public bool RxEnabled { get; init; } = true;
    public bool TxEnabled { get; init; } = true;
    public int BitrateBps { get; init; } = 32000;
    public string? RxDeviceId { get; init; }     // Speaker device
    public string? TxDeviceId { get; init; }     // Microphone device
    public int RxVolume { get; init; } = 100;    // 0-100%
    public int TxVolume { get; init; } = 100;    // 0-100%
    public bool VoxEnabled { get; init; }
    public int VoxThresholdDb { get; init; } = -30;
    public int VoxHoldMs { get; init; } = 500;
    public bool FullDuplex { get; init; }
    public Keys PttKey { get; init; } = Keys.Space;
}
```

### 15.2 StationConfig additions

```csharp
public StationAudioConfig Audio { get; init; } = new();

public record StationAudioConfig
{
    public bool Enabled { get; init; } = true;
    public string? RxCaptureDeviceId { get; init; }  // Radio audio out → capture
    public string? TxRenderDeviceId { get; init; }   // Render → radio audio in
    public string PttMethod { get; init; } = "cat";  // "cat", "serial", "none"
    public string? PttSerialPort { get; init; }
    public int RxGainDb { get; init; } = 0;
    public int TxGainDb { get; init; } = 0;
}
```

---

## 16. Security Considerations

- Audio streams are encrypted by Tailscale (WireGuard). No additional
  encryption layer is needed.
- The PTT flag in the audio packet is authenticated by virtue of the
  Tailscale peer identity — only the paired Client can assert PTT.
- VOX threshold should default to off to prevent accidental transmission.

---

## 17. Testing Plan

| Test | Method |
|------|--------|
| Opus encode/decode round-trip | Unit test: encode PCM → decode → compare |
| Jitter buffer ordering | Unit test: insert out-of-order, verify playout |
| Jitter buffer PLC | Unit test: simulate loss, verify PLC invoked |
| Packet serialization | Unit test: serialize/deserialize AudioPacket |
| End-to-end latency | Integration test: loopback with timestamp measurement |
| WASAPI capture/render | Manual test with real audio devices |
| PTT flag propagation | Integration test: send frame with PTT=1, verify Station keys |
| Bitrate adaptation | Unit test: simulate loss, verify bitrate reduction |
| Device hot-plug | Manual test: unplug/replug while streaming |

---

## 18. Roadmap

**v1.0.4** — Core audio link:
- Bidirectional Opus audio (RX + TX)
- Explicit PTT (button + keyboard shortcut)
- Adaptive jitter buffer
- Device selection UI
- Level meters
- Bitrate selection

**v1.0.5** — Audio polish:
- VOX with anti-trip
- Full duplex mode
- Adaptive bitrate (loss-driven)
- Virtual audio cable integration guide
- Audio routing for WSJT-X / fldigi
- Stereo option (for binaural CW, diversity receive)

**v1.0.6** — Advanced:
- Audio recording (save QSOs to WAV)
- Equalizer / band-pass filter (reduce noise)
- Noise gate (squelch)
- Compressor (leveling for SSB)
- Spectral display (waterfall in the Client UI)

---

## 19. Open Questions

1. **Should audio replace the existing port-forward approach for Icom/Yaesu audio?**
   No — the built-in audio link is complementary. RS-BA1 audio over UDP 50003 carries
   the radio's native compressed stream at the radio's chosen bitrate. RWK's Opus link
   carries raw audio from a sound card. Operators with radios that have built-in LAN
   audio should continue using port forwarding for that. The Opus link is for radios
   without LAN audio (RS-232 only radios, older rigs) or for operators who want lower
   latency than the vendor provides.

2. **Can the Opus link and CW keyer share the same UDP port?**
   Technically yes (multiplex by packet type), but separate ports are cleaner: no
   multiplexer overhead, independent flow control, and the sidecar's existing per-port
   statistics work out of the box.

3. **What about echo cancellation?**
   Not needed for ham radio. The operator wears headphones (no acoustic coupling), or
   the RX audio is muted during TX. Echo cancellation is a VoIP telephony problem, not
   a half-duplex radio problem.

4. **Pi CPU budget?**
   Concentus at complexity 5, 48kHz mono, 20ms frames: measured ~3% of one core on a
   Pi 4. Combined with the CW replayer and sidecar, total CPU should stay under 15%.
   If it's too high, reduce complexity to 3 (quality barely affected).

---

*73 de W1VE*
