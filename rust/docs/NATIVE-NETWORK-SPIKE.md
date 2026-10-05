# Spike: replacing the Tailscale sidecar natively

## The problem

The current shipping app supervises `rwk-tailscale-sidecar.exe`, a Go program that
embeds Tailscale's `tsnet` server (`tailscale.com v1.102.2`). That single binary
provides, at once:

| Capability | Where it lives in the sidecar |
|---|---|
| WireGuard encryption + peer NAT traversal | `tsnet` / wireguard-go / gVisor netstack |
| DERP relay fallback when direct fails | `tsnet` control plane |
| Userspace IP stack (no TUN, no admin) | gVisor (`tsnet` default) |
| Tailnet join + interactive auth | `node.go`, `GET /v1/status` `authUrl` |
| Peer status, path type, RTT, DERP region | `GET /v1/status` |
| Edge UDP relay | `edge.go` |
| TCP/UDP port forwarding | `forward.go` |

Requirement: **one EXE, no sidecar**. That means the Rust process must provide all of
the above itself, including WireGuard and the coordination protocol.

## Options assessed

1. **Bundle the Go sidecar** — rejected: it is exactly what "no sidecar" removes.
2. **Require a system Tailscale install** — rejected: reintroduces an external runtime
   and a manual login the user does not want; also breaks the single-EXE distribution.
3. **Native Rust transport (chosen for the spike)** — build the tunnel in-process.

## Recommended native architecture

```
  Application (keying, forwards)
        │  plain UDP/TCP datagrams
        ▼
  smoltcp  ── userspace IP stack ── no TUN, no admin  (parity with tsnet/gVisor)
        │
        ▼
  boringtun ── userspace WireGuard ── Noise handshake, transport keys, keepalives
        │
        ▼
  Coordination ── peer discovery, DERP map, key exchange  ◀── the hard part
```

* **`boringtun`** (Cloudflare, MPL-2.0) is a production userspace WireGuard
  implementation. It is the direct analogue of wireguard-go inside `tsnet` and covers
  the encryption, handshake and roaming/keepalive half of the problem.
* **`smoltcp`** (0BSD) is a `no_std`-capable userspace TCP/IP stack. It preserves the
  sidecar's most valuable property — no TUN adapter and no administrator privilege —
  which Requirement 5.1 treated as a hard constraint.
* **Coordination** is the genuine risk. Tailscale's control plane (node keys, peer
  discovery, DERP map distribution, ACLs) has no maintained Rust client. Options, in
  increasing order of effort:
  1. **Static peer configuration** — for RWK's actual topology (one Client, one
     Station, addresses typed in by the operator and already stored in
     `config.json` as `Tailscale.StationAddress`), a pre-shared WireGuard peer list
     removes the need for a control plane entirely. This is the pragmatic MVP: the
     Station address is already operator-supplied.
  2. **Embedded DERP client** — implement the DERP relay protocol for NAT-traversal
     fallback. Significant, but bounded and well-specified.
  3. **Full control-plane client** — only needed for managed, multi-node tailnets.

For the RWK use case (a single Client talking to a single Station, address already
known), option 1 is sufficient for the Windows MVP; option 2 is the follow-on.

## What is already done

`rwk-core::engine::network` implements the parts that do **not** depend on the tunnel,
against plain UDP, so they will ride over whichever transport lands:

* `EdgeTransport` — the sidecar's `edgeRelay`: one socket, outbound frames to a
  configured peer, inbound frames accepted only from that peer, with the same
  datagram counters (`tx/rx`, `dropNoPeer`, `dropForeign`).
* `UdpForwarder` — the sidecar's `out-udp` / `in-udp` relays, used for Flex SmartSDR
  command (`4992/udp`) and VITA-49 (`4991/udp`).
* `ForwardRule` — the persisted rule shape from `config.json`, including the
  loopback-only bind default.

Both are covered by loopback integration tests, so when the WireGuard layer arrives it
is substituted beneath an already-verified keying path.

## Recommendation

For the **Windows MVP**: implement `boringtun` + static pre-shared peers + `smoltcp`,
sourced from the operator-supplied station address. Defer DERP. This satisfies
"one EXE, no sidecar" for the single-peer topology the app actually uses, and keeps
the door open to a full control-plane client later.

For **Linux x64 / ARM (RasPi)**: the same stack is portable, but prefer the kernel
WireGuard interface (`wg`) where available for power efficiency on the Pi, falling
back to `boringtun` userspace when the kernel module is absent.
