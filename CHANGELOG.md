# RWK Changelog — cloudflare-relay Branch

All changes and improvements since the original `main` branch (UDP-only release).

---

## Summary

The `cloudflare-relay` branch adds a complete **zero-configuration Cloud Relay transport** via Cloudflare Workers, along with significant **performance**, **reliability**, and **protocol correctness** improvements throughout the codebase.

---

## Major Features

### 1. Cloud Relay Transport (NEW)

A complete WebSocket-based transport that connects client and server through Cloudflare's global edge network. **Zero VPN, zero port forwarding, zero firewall configuration required.**

| Files Added | Description |
|-------------|-------------|
| `CloudRelay/CloudRelayTransport.cs` | WebSocket client with heartbeat, reconnect, status events |
| `CloudRelay/WireProtocol.cs` | Binary frame serialization with CRC32 validation |
| `CloudRelay/TokenGenerator.cs` | Cryptographic 256-bit pairing token generation |

**Key Features:**
- 64-character hex pairing token for session matching
- Automatic reconnect with exponential backoff + jitter
- 5-second heartbeat keep-alive to prevent NAT timeout
- TLS 1.3 end-to-end encryption
- Status events (Connecting → Connected → Paired)
- Transport selection UI in both client and server

---

## Performance Improvements

### 2. Non-Blocking Send Pump

**Problem:** Original code called `WebSocket.SendAsync().Wait(5s)` blocking the calling thread. On the client, this blocked the serial read thread. On the server, it blocked the relay receive thread. Additionally, `ClientWebSocket.SendAsync` is not thread-safe — concurrent calls from the serial-read thread and keyboard-flush timer would crash.

**Solution:** Single-writer send pump via `Channel<byte[]>`. Callers enqueue frames and return immediately; exactly one dedicated task handles all socket sends.

```
SendResponse() → _sendQueue.Writer.TryWrite() → SendPump() → _ws.SendAsync()
           ↑ never blocks                            ↑ one task, no concurrency
```

### 3. TCP_NODELAY (Nagle Disabled)

**Problem:** Nagle's algorithm batches small packets, adding 10-40ms latency for tiny CW frames (18-byte header + few payload bytes).

**Solution:** Custom `ConnectCallback` creates the socket with `NoDelay = true` before the WebSocket handshake.

```csharp
var handler = new SocketsHttpHandler {
    ConnectCallback = async (ctx, ct) => {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(ctx.DnsEndPoint, ct);
        return new NetworkStream(s, ownsSocket: true);
    }
};
```

**Impact:** This is likely the biggest single latency win on the relay path — reduces typical latency from ~30-50ms to ~5-20ms.

### 4. Reduced Buffering Delays

| Stage | Before | After | Notes |
|-------|--------|-------|-------|
| Client keyboard flush | 75ms | 50ms | Batches rapid keystrokes |
| Server text flush | 25-50ms | 5ms | TimingEngine enforces gaps anyway |
| Paddle path | 0ms | 0ms | Bypasses client batching entirely |

---

## Reliability Improvements

### 5. Exponential Backoff with Jitter

**Problem:** Original code used a fixed 3-second reconnect delay. Documentation claimed exponential backoff but it wasn't implemented.

**Solution:** True exponential backoff: `min(30s, 500ms × 2^attempt)` with 0-25% jitter to prevent thundering herd on the relay.

### 6. Infinite Retries for Station Side

**Problem:** `MaxReconnectAttempts = 10` meant a 30-second internet hiccup permanently killed an unattended station.

**Solution:** Default `MaxReconnectAttempts = 0` (infinite) for the station side. A flaky Starlink connection doesn't require manual intervention.

### 7. Dead Peer Detection

**Problem:** Half-open TCP connections (peer power loss, NAT mapping expiry) looked "Open" until the next send failed.

**Solution:** Track `_lastRxTimestamp` on every received frame. Force reconnect if no data for 3× heartbeat interval (15s default).

### 8. WebSocket Message Reassembly

**Problem:** Original code parsed whatever a single `ReceiveAsync` returned, ignoring `EndOfMessage`. Fragmented frames would fail CRC.

**Solution:** Accumulate into a buffer until `EndOfMessage` is true before parsing.

### 9. Sequence Gap Detection

**Problem:** Frames lost during reconnect (or any network hiccup) were silently dropped with no operator awareness.

**Solution:** Track received sequence numbers. Log "dropped N frames" when a gap is detected. Expose `DroppedFrameCount` property.

### 10. Client Shutdown Flush

**Problem:** Keystrokes buffered in `_keyBuffer` were lost if `_running` was set false before flush.

**Solution:** Flush pending keystrokes BEFORE setting `_running = false` during shutdown.

---

## Protocol Correctness Fixes

### 11. CRITICAL: WinKeyer Mode Register Fix

**Problem:** Code sent `0x0D 0x40` to enable paddle echo. **WRONG!** Command `0x0D` is Farnsworth spacing, not mode register. The "paddle echo" was working only because residual state from a prior N1MM session had echo already enabled.

**Solution:** Corrected to `0x0E mode` where `0x0E` is the actual mode register command.

```csharp
// BEFORE (WRONG - sets Farnsworth to 64 WPM, does nothing for paddle echo)
_winKeyerPort.Write(new byte[] { 0x0D, 0x40 }, 0, 2);

// AFTER (CORRECT - sets mode register with paddle echo bit)
byte mode = _settings.BuildModeRegister(); // bit 6 = paddle echo
_winKeyerPort.Write(new byte[] { 0x0E, mode }, 0, 2);
```

### 12. WinKeyer Generation Detection

**Problem:** No awareness of WK1/WK2/WK3 differences; mode register layout may vary between generations.

**Solution:** Version byte detection with warnings:
- Version ≥30 = WK3
- Version ≥20 = WK2
- Version <20 = WK1 (warning: mode register untested)

### 13. Robust Version Detection

**Problem:** Fixed 500ms `Thread.Sleep` on UI thread waiting for version response.

**Solution:** Retry loop (10 attempts × 50ms) with validation that response byte is plausible (10-50 range).

### 14. Paddle Settings UI

**Added client UI controls for WinKeyer mode register settings:**
- **Key Mode:** Iambic B (default), Iambic A, Ultimatic, Bug
- **Paddle Swap:** Reverse dit/dah paddles
- **Autospace:** Automatic inter-word spacing

Mode byte built via `ClientSettings.BuildModeRegister()`:
```
Bit 7: Disable paddle watchdog (0)
Bit 6: Paddle echoback (1 = always on)
Bits 5-4: Key mode
Bit 3: Paddle swap
Bit 2: Serial echoback (0)
Bit 1: Autospace
Bit 0: CT spacing (0)
```

---

## Thread Safety & Correctness

### 15. Thread-Safe KeyerCore

**Problem:** Concurrent access from serial, UDP, and relay threads could corrupt protocol state.

**Solution:** `_protocolLock` object serializes all access to `WinKeyerProtocol`.

### 16. SerialKeyingOutput Hardening

**Problem:** `EscapeCommFunction()` return value not checked; KeyUp not guaranteed on failure paths.

**Solution:**
- Check return value, throw `KeyingException` on failure
- `EnsureKeyUp()` guarantees key-up in all failure paths via `finally` blocks

### 17. AbortCurrent Improvements

**Problem:** Abort only stopped current keying, not queued schedules; `_lastEdgeTimestamp` remained stale.

**Solution:** 
- Drain the schedule queue on abort
- Reset `_lastEdgeTimestamp` via `Interlocked.Exchange(ref _lastEdgeTimestamp, 0)`

### 18. Volatile _lastWpm

**Problem:** `_lastWpm` written by enqueue thread, read by keying thread without synchronization.

**Solution:** Marked `volatile` to make cross-thread intent explicit.

### 19. UDP Bind Failure

**Problem:** `UdpCommandSource.Start()` silently returned if bind failed.

**Solution:** Now throws exception so caller knows startup failed.

---

## UI Improvements

### 20. Efficient UILogger

**Problem:** Log concatenation was O(n) per append due to string rebuilding.

**Solution:** Use `TextBox.AppendText()` which is O(1) for each append.

---

## Files Modified

| File | Changes |
|------|---------|
| `CloudRelay/CloudRelayTransport.cs` | NEW — Complete WebSocket transport |
| `CloudRelay/WireProtocol.cs` | NEW — Binary frame serialization |
| `CloudRelay/TokenGenerator.cs` | NEW — Pairing token generation |
| `WKRClient/MainForm.cs` | Relay support, corrected WinKeyer init, paddle settings |
| `WKRClient/MainForm.Designer.cs` | Paddle settings UI controls |
| `WKRClient/ClientSettings.cs` | KeyMode enum, BuildModeRegister() |
| `WinKeyerEmulator.App/MainForm.cs` | Relay support, transport selection |
| `WinKeyerEmulator.App/Controllers/AppController.cs` | Relay integration |
| `WinKeyerEmulator.Core/KeyerCore.cs` | Thread-safe with _protocolLock |
| `WinKeyerEmulator.Core/Timing/TimingEngine.cs` | volatile _lastWpm, reset _lastEdgeTimestamp |
| `WinKeyerEmulator.App/IO/SerialKeyingOutput.cs` | EscapeCommFunction checking, EnsureKeyUp |
| `WinKeyerEmulator.App/IO/UdpCommandSource.cs` | Throw on bind failure |

---

## Documentation Updated

- **README.md** — Cloud Relay setup, technical details, WinKeyer testing help section
- **CONTEXT.md** — Architecture decisions, relay design notes

---

## Upgrade Notes

1. **No configuration migration needed** — Settings files are additive
2. **Transport defaults to UDP** — Existing users see no change
3. **Cloud Relay requires pairing token** — Generate on server, paste on client
4. **Paddle settings default to Iambic B** — Same as before

---

## Testing

All 122 unit tests pass. Tested with:
- WinKeyer 3 v31
- N1MM+ 1.0.10591
- Cloud Relay via wrs.w1ve.com
- UDP via Tailscale VPN

---

*73 de W1VE*
