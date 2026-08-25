# ADR 0002: IPv6 Dual-Stack Support and Boundaries

## Status

Accepted

## Context

RWK operates across three network boundaries where IP addresses matter:

1. **Client-side listener bind** (BindAddress on ForwardRule) — local addresses accepting connections
2. **Tailnet relay leg** — the Go sidecar (tsnet) forwarding TCP/UDP between peers
3. **Station-side target dial** (StationTargetAddress) — the Station LAN address dialed out to

Tailscale assigns both IPv4 (100.x.x.x CGNAT) and IPv6 (fd7a:115c:a1e0::/48 ULA) addresses to every node. Some operators have IPv6-only LAN segments or prefer to bind listeners on IPv6 addresses.

## Decision

### What changed (IPv6-capable)

- **Go sidecar (`forward.go`)**: Inbound UDP listeners now bind on BOTH the node's IPv4 and IPv6 tailnet addresses (two listeners, same port, merged behind one forwardEntry). Outbound session sockets bind to the tailnet address matching the peer's address family. TCP was already family-agnostic and needed no change.

- **.NET shared library**: `BindAddressResolver`, `PortForwardManager`, `TcpForwarder` were already fully family-agnostic (confirmed by audit and new tests). `ForwardRule` gained IPv6 constants (`AnyAddressV6 = "::"`, `LoopbackAddressV6 = "::1"`) and an `AddressExposure` classifier that correctly categorizes IPv6 ULA, link-local, and global unicast.

- **UI**: The bind address warning now differentiates between LAN exposure (private/link-local) and global exposure (IPv6 without NAT), with appropriately stronger warning copy for the latter.

### What was deliberately left IPv4-only

- **Sidecar ↔ .NET IPC**: The loopback UDP sockets between the Go sidecar process and the .NET process (`forwardOutboundUdp` local bind, `getOrCreateSession` inbound branch, `UdpForwarder.CreateSession`) remain IPv4 (127.0.0.1). This is process-local IPC, never crosses a network, and matching the .NET side's `IPAddress.Any` sockets is intentional.

- **Station SessionManager**: The control port TcpListener binds `IPAddress.Any` (IPv4). It only receives connections from the Go sidecar's `forwardInbound` over loopback — never directly from the network.

- **FlexRadio Discovery Relay**: Left entirely IPv4. UDP broadcast has no IPv6 equivalent (multicast is a different feature), and ham radio LAN gear is universally IPv4. The `FlexVitaDiscoveryCodec` and `ClientDiscoveryEmitter` are unchanged.

### tsnet dual-stack behavior (verified empirically)

On the pinned `tailscale.com v1.102.2`:
- `tsnet.Server.ListenPacket("udp", ":port")` is **rejected** — tsnet requires an explicit tailnet IP address
- `tsnet.Server.ListenPacket("udp", "100.x.x.x:port")` works (IPv4)
- `tsnet.Server.ListenPacket("udp", "[fd7a:...]:port")` works (IPv6)
- There is **no single dual-stack listener** — two separate binds are required

This means the Go sidecar must query its own tailnet addresses and bind explicitly on each.

## Consequences

- Operators with IPv6-only LAN segments can now use RWK for port forwarding
- The Tailscale mesh correctly relays between IPv4 and IPv6 peers
- Mixed-family configurations work (IPv4 bind relaying to IPv6 target, and vice versa)
- The exposure warning accurately reflects the greater risk of global-unicast IPv6 binds
- Future tsnet versions may support bare `:port` syntax — the current approach remains correct regardless

## References

- Integration test: `src/RWK.TailscaleSidecar/forward_integration_test.go` (build tag `integration`)
- Unit tests: `forward_test.go` (12 tests), `AddressExposureTests.cs` (44 tests)
