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
* `TcpForwarder` — the sidecar's `out` / `in` relays, the client↔station control channel,
  with `TCP_NODELAY` on both legs and half-close propagation per direction.
* `ForwardRule` — the persisted rule shape from `config.json`, including the
  loopback-only bind default.
* `tunnel::WireGuardTunnel` — the **WireGuard half of the transport**. boringtun driven over
  a UDP socket: static-keyed peers, handshake, keepalives, a roaming endpoint, and its own
  `PathHealth` so a dead tunnel raises F9. It hands up *inner IP packets* only; the netstack
  that turns those into sockets is the piece still missing (below).

All of these are covered by loopback integration tests — including a real WireGuard
handshake and a byte-exact inner-packet round trip — so the remaining transport work is
substituted beneath an already-verified keying path.

## Progress

| Increment | State |
|---|---|
| 1. WireGuard transport (`tunnel::WireGuardTunnel`) | ✅ done — real handshake, encrypted inner packets, F9 source |
| 2. Userspace netstack (`smoltcp`) | ⬜ next — turns inner IP packets into UDP/TCP sockets |
| 3. DERP relay client | ⬜ after the netstack — NAT-traversal fallback |
| 4. Key and address plumbing | ⬜ generate an identity, persist a peer in `config.json` |

Note that the UDP and TCP forwarders, and `EdgeTransport`, are written against **plain
sockets** today. They keep working over a LAN or a hardware VPN unchanged; only step 2 makes
them ride the tunnel.

## Recommendation

The chosen direction is **full `tsnet` parity**: our own WireGuard mesh *plus* a DERP relay
client, so the app keeps `tsnet`'s zero-install, no-admin, NAT-traversing behaviour without
Tailscale. Steps 1–4 above are that plan in order; the ordering matters because each step is
useful on its own and none requires revisiting an earlier one.

Being explicit about what this costs, since it is not just an architecture swap: a
boringtun node **cannot join an existing tailnet** — Tailscale's coordination server issues
the keys its peers accept, and there is no "bring your own WireGuard key" path. Adopting this
plan means leaving Tailscale, and losing tailnet interop (reaching the station from a phone's
Tailscale app, or sharing the node with a friend's tailnet). It also means the DERP client
must be written before NAT traversal works at all, since step 3 is the whole of it.

For **Linux x64 / ARM (RasPi)**: the same stack is portable, but prefer the kernel
WireGuard interface (`wg`) where available for power efficiency on the Pi, falling
back to `boringtun` userspace when the kernel module is absent.
