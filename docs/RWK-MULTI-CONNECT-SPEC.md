# RWK Multi-Connect N1MM Relay — Design Specification

**Version:** 1.0-draft  
**Date:** 2026-08-22  
**Author:** W1VE  
**Branch:** `v1.0.3` (continuation)

---

## 1. Purpose

RWK currently supports one Client paired with one Station at a time. For N1MM+
multi-op contest networking, multiple operators at different locations need their
N1MM instances to discover each other and exchange data — even though they're on
different LANs connected through RWK.

This specification adds **multi-connect support** with two connection types:

1. **Keyer session** (exclusive, max 1) — full keying, port forwarding, control
2. **N1MM relay connection** (unlimited) — N1MM broadcast relay only

An operator who arrives second gets "KEYER BUSY" but can still participate in
N1MM networking via an observer connection.

---

## 2. Connection Types

### 2.1 Keyer Session (existing, unchanged)

- First client to pair successfully gets the keyer session
- Full protocol: edge transport, port forward push, control channel, discovery relay
- Pairing key required and validated via HMAC-SHA256
- Response: `OK PAIRED`

### 2.2 N1MM Relay Connection (new)

- Any number of clients can connect for N1MM relay
- Lightweight protocol: only N1MM packet exchange over a control stream
- No pairing key required (the client is already authenticated via Tailscale peer identity)
- No edge transport, no port forward push, no keying
- Response: `N1MM OK`

### 2.3 Keyer Busy (new response)

When a second client attempts to pair while a keyer session is already active:
- Station responds: `KEYER BUSY`
- Client plays "KEYER BUSY" in CW via local sidetone (not over the air)
- Client displays a red "KEYER BUSY" indicator below the Pair button
- Client automatically falls back to attempting an N1MM relay connection
- The Pair button remains available — operator can retry if the first client disconnects

---

## 3. Protocol Changes

### 3.1 Connection Handshake (modified)

Current handshake:
```
Client → Station: PAIR <hmac>
Station → Client: OK PAIRED | BUSY
```

New handshake:
```
Client → Station: PAIR <hmac>
Station → Client: OK PAIRED | KEYER BUSY

Client → Station: N1MM RELAY
Station → Client: N1MM OK | N1MM DENIED
```

The `N1MM RELAY` request is a separate connection attempt. The client opens a
second TCP connection to the Station's control port and sends `N1MM RELAY` instead
of `PAIR`. No HMAC is included — Tailscale peer identity provides authentication.

`N1MM DENIED` is reserved for future use (if the Station operator wants to
restrict N1MM relay access).

### 3.2 N1MM Relay Protocol

Once connected with `N1MM OK`, the stream carries bidirectional length-prefixed
JSON messages (same framing as the keyer control channel):

```
4-byte big-endian length + UTF-8 JSON body
```

Message types:

**Station → Client:**
```json
{ "type": "n1mm_discovery_announce", "payload": "<base64>" }
```
N1MM packets captured on the Station's LAN or received from other clients.

**Client → Station:**
```json
{ "type": "n1mm_client_announce", "payload": "<base64>" }
```
N1MM packets captured from the client's local N1MM instance.

Same message types as the keyer session uses — just on a separate, lighter connection.

### 3.3 Fan-Out Logic (Station)

When the Station receives an N1MM packet (from local LAN capture or from any client):

1. Re-emit on the Station's localhost (so Station-local N1MM instances see it)
2. Forward to ALL connected N1MM relay streams EXCEPT the sender
3. Forward to the keyer session's control stream (if active and N1MM-enabled)

When the Station captures a local LAN N1MM broadcast:
1. Forward to ALL connected N1MM relay streams
2. Forward to the keyer session's control stream (if active)

This ensures every N1MM instance in the network (local + all remote clients)
sees every other instance's broadcasts.

---

## 4. Station Architecture Changes

### 4.1 SessionManager Modifications

```
┌─────────────────────────────────────────────────────┐
│  SessionManager                                     │
│                                                     │
│  _keyerSession: ActiveSession? (max 1, exclusive)   │
│  _n1mmRelayStreams: List<Stream> (0..N)             │
│                                                     │
│  AcceptLoop:                                        │
│    Read first message from new connection           │
│    If "PAIR <hmac>" → try keyer session             │
│      If no existing keyer → accept → "OK PAIRED"   │
│      If keyer exists → reject → "KEYER BUSY"       │
│    If "N1MM RELAY" → add to relay list → "N1MM OK" │
└─────────────────────────────────────────────────────┘
```

### 4.2 N1MM Relay Manager (new component)

```csharp
public sealed class N1mmRelayManager
{
    private readonly List<Stream> _relayStreams = new();
    private readonly object _lock = new();

    // Add a new relay connection
    public void AddRelay(Stream stream);

    // Remove a disconnected relay
    public void RemoveRelay(Stream stream);

    // Broadcast an N1MM packet to all relays except the sender
    public Task BroadcastAsync(byte[] payload, Stream? excludeSender);

    // Read loop for a single relay connection (runs per-connection)
    public Task ReadRelayAsync(Stream stream, CancellationToken ct);
}
```

### 4.3 Integration with Existing N1MM Capture

The Station's `StationN1mmDiscoveryListener` captures local LAN broadcasts.
Currently it forwards only to the keyer session. With the relay manager:

```
StationN1mmDiscoveryListener.DiscoveryCaptured →
  1. N1mmRelayManager.BroadcastAsync(payload, excludeSender: null)  // all clients
  2. Send to keyer session control stream (if active)
```

When a relay client sends `n1mm_client_announce`:
```
RelayManager receives payload from Client A →
  1. StationN1mmDiscoveryEmitter.OnClientN1mmPacket(payload)        // local LAN
  2. N1mmRelayManager.BroadcastAsync(payload, excludeSender: A)     // other clients
  3. Send to keyer session control stream (if active and != A)
```

---

## 5. Client Behavior Changes

### 5.1 Pair Attempt Flow

```
1. Client clicks "Pair Keyer with Station"
2. Connect TCP to Station control port
3. Send "PAIR <hmac>"
4. Read response:
   - "OK PAIRED" → normal keyer session (existing behavior)
   - "KEYER BUSY" →
     a. Play "KEYER BUSY" in CW via local sidetone
     b. Show red "KEYER BUSY" box below Pair button
     c. Close this connection
     d. If N1MM relay is enabled, auto-connect as relay (see 5.2)
     e. Pair button stays enabled for retry
```

### 5.2 N1MM Relay Fallback

When keyer is busy but N1MM relay is enabled:
```
1. Open new TCP connection to Station control port
2. Send "N1MM RELAY"
3. Read response:
   - "N1MM OK" → start N1MM relay loop (send/receive packets)
   - "N1MM DENIED" → log warning, N1MM won't work
4. Status bar shows: "N1MM Relay (keyer busy)"
```

### 5.3 UI Changes

**Pair button area (Keyer tab):**
```
[Pair Keyer with Station]
┌──────────────────────────────┐
│  ■ KEYER BUSY                │  ← Red background, white text
│  N1MM relay active           │  ← Smaller italic text below
└──────────────────────────────┘
```

The red box disappears if:
- The keyer becomes available (other client disconnects)
- The user successfully re-pairs
- The user cancels/disconnects

### 5.4 Automatic N1MM Relay Without Keyer

An operator who ONLY wants N1MM networking (no keying) can:
1. Enable the N1MM checkbox
2. Click Pair — gets KEYER BUSY (or succeeds)
3. Either way, N1MM relay is active

OR: In future, a "Connect N1MM Only" button could skip the keyer attempt entirely.
For v1.0.3, the auto-fallback is sufficient.

---

## 6. Security

- **Keyer session:** Requires pairing key (HMAC-SHA256). Exclusive access to keying
  and port forwarding. Only one operator can key the radio.
- **N1MM relay:** No pairing key required. Authenticated implicitly by Tailscale
  peer identity — only machines on the operator's tailnet can reach the Station.
  N1MM data is not security-sensitive (contest scores, not radio control).
- **Future:** A Station config option `AllowN1mmRelayWithoutKey` (default true)
  could be set to false if the operator wants to restrict N1MM access.

---

## 7. Backward Compatibility

- Stations running v1.0.3 without this change will respond `BUSY` to the second
  client (existing behavior). The new `KEYER BUSY` response is distinguishable
  by the Client — old `BUSY` and new `KEYER BUSY` both trigger the busy path.
- A new Client connecting to an old Station: sends `N1MM RELAY`, old Station
  doesn't understand it and closes the connection. Client logs a warning.
- An old Client connecting to a new Station: sends `PAIR`, gets either
  `OK PAIRED` or `KEYER BUSY`. Old client doesn't know about N1MM relay fallback
  but otherwise works normally.

---

## 8. Implementation Plan

### Phase 1: Station multi-accept

1. Modify `SessionManager.AcceptLoop` to read the first message before deciding
   connection type
2. Add `N1mmRelayManager` class for tracking relay streams + fan-out
3. Handle `N1MM RELAY` connections separately from `PAIR` connections
4. Wire N1MM capture fan-out through the relay manager

### Phase 2: Client fallback

1. Handle `KEYER BUSY` response: sidetone, red indicator, auto-fallback
2. Implement `N1MM RELAY` connection attempt
3. Run the N1MM relay read/write loop on the relay connection
4. Status bar update for relay-only mode

### Phase 3: Testing

1. Two Clients + one Station: verify both N1MMs see each other
2. Three Clients: verify all three N1MMs form a network
3. Keyer exclusivity: verify only one client can key
4. Disconnect/reconnect: verify keyer becomes available after disconnect
5. Mixed versions: verify backward compat

---

## 9. Message Flow Example (3 N1MMs)

```
Station LAN: N1MM-A (192.168.1.10)
Client 1:    N1MM-B (192.168.88.50) — keyer session
Client 2:    N1MM-C (10.0.0.5)     — N1MM relay only

1. N1MM-A broadcasts on Station LAN port 2237
2. StationN1mmDiscoveryListener captures it
3. Station rewrites IP → 127.0.0.1 and sends to:
   - Client 1 (keyer control stream): n1mm_discovery_announce
   - Client 2 (relay stream): n1mm_discovery_announce
4. Client 1 emits on localhost:2237 → N1MM-B sees N1MM-A at 127.0.0.1
5. Client 2 emits on localhost:2237 → N1MM-C sees N1MM-A at 127.0.0.1

6. N1MM-B broadcasts on Client 1's LAN port 2237
7. ClientN1mmDiscoveryListener captures it
8. Client 1 sends n1mm_client_announce to Station
9. Station receives, rewrites IP → 127.0.0.1, then:
   - Re-emits on Station localhost → N1MM-A sees N1MM-B at 127.0.0.1
   - Forwards to Client 2 relay stream
10. Client 2 emits on localhost:2237 → N1MM-C sees N1MM-B at 127.0.0.1

Result: N1MM-A, N1MM-B, N1MM-C all see each other at 127.0.0.1
        and communicate via port forwards on 2237/2238.
```

---

## 10. Open Questions

1. **Should relay connections count against a limit?** Probably yes — cap at 10
   relay connections to prevent resource exhaustion. N1MM multi-op rarely exceeds
   6-8 stations.

2. **Should the relay connection also carry port 2238 (data exchange)?** Yes —
   the relay carries ALL N1MM traffic (discovery on 2237 and data on 2238).
   The fan-out applies to both.

3. **What about N1MM XML broadcast data (ports 12060-12062)?** These are separate
   from inter-station networking. They could be handled by the existing port
   forward rules (one per data type). The relay only handles the discovery/
   inter-station protocol (2237/2238).

4. **Should the Station UI show connected relay clients?** Yes — a small counter
   or list in the forward rules area: "N1MM Relay: 2 clients connected".

---

*73 de W1VE*
