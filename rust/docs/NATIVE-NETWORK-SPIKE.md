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
| 2. Userspace netstack (`netstack::NetStack`, `smoltcp`) | ✅ done — turns inner IP packets into UDP/TCP sockets |
| 3. Relay fallback (`relay`, `tunnel::wire`) | ✅ done — a key-addressed packet relay; the tunnel rides UDP or the relay |
| 4. Key and address plumbing (`config`, `tunnel::connect`, CLI) | ✅ done — identity + peer + relay persisted; auto direct-then-relay; `keygen`/`pubkey`/`relay`/`connect` |

All four increments of the native-transport plan are now in place: the WireGuard tunnel, the
smoltcp netstack with its forwarders, the relay fallback, and the config/CLI that bring them
up.

### Integration — `rwk connect` carries real traffic

The four increments are now joined so a config actually moves bytes. `engine::session::Session`
takes a [`Connected`](crate::engine::tunnel::Connected) tunnel and a [`Config`], spawns the
[`NetDriver`] that drives the stack over the tunnel, and starts a forward task per enabled
rule by role:

* **Client**: each rule binds a `TunnelTcpForward` / `TunnelUdpForward` on
  `127.0.0.1:client_port`, carrying local traffic over the tunnel to the Station's inner
  address and `station_port`.
* **Station**: each rule starts a `StationTcpResponder` / `StationUdpResponder` listening on
  the inner address and `station_port`, dialing the real LAN device at
  `station_target_address`.

`rwk connect` builds the session, so the link carries traffic instead of just staying up; a
malformed rule is skipped rather than failing the whole session.

**Routing fix found along the way.** The netstack interface used a `/32` address with no
route, which left the peer's inner address off-link: smoltcp could not select a source
address or next hop for it, so a forwarded SYN was undeliverable. It only *appeared* to work
in a single-process test because the tight in-process retransmit loop papered over the lost
first packet; across two real processes over the relay the connection never established. The
fix widens the interface prefix to `/24` (v4) / `/64` (v6) so the two fixed inner addresses
are on-link (`NetStack::new`, netstack.rs). Verified end to end **across two separate `rwk
connect` processes**: `app → Client forward → relay tunnel → Station responder → LAN echo →
back`, plus the `engine::session` module tests (TCP and UDP forward rules carrying a flow over
a real tunnel) and the malformed-rule-skip test.

What remains is not transport but integration — wiring this stack under the existing keying
path (the edge transport over the same `NetStackHandle`) and the Tauri UI (below).

Note that the UDP and TCP forwarders, and `EdgeTransport`, are written against **plain
sockets** today. They keep working over a LAN or a hardware VPN unchanged.

### Increment 2 — what landed

`engine::netstack` is the `smoltcp` layer. A [`TunnelDevice`] is a `smoltcp::phy::Device` on
`Medium::Ip` whose "wire" is two packet queues: an inbound queue fed from the tunnel's
decrypted `poll` output, and an outbound queue drained into the tunnel's `send_inner`. A
[`NetStack`] owns the smoltcp `Interface`, its `SocketSet` and the device, and exposes UDP and
TCP sockets bound to the tunnel's inner address. The owning task's loop is: inject each inner
packet read off the tunnel, `poll`, then drain-and-`send_inner` whatever smoltcp framed.
smoltcp stays an internal detail — `NetStack` exposes std-typed `udp_send`/`udp_recv` and
`tcp_send`/`tcp_recv` helpers so callers never touch `IpEndpoint`.

Verified (`netstack` module tests + the `rwk selftest` `netstack` check): a UDP datagram and a
bidirectional TCP stream each cross between two stacks over the device seam; a TCP half-close
propagates without cutting the reply direction; and — the point of the increment — a UDP
datagram survives the **full encrypted path** socket→smoltcp→WireGuard encrypt→wire→decrypt→
smoltcp→socket between two live `WireGuardTunnel`s.

**Concurrency seam and forwarders (`netstack_driver`, `tunnel_forward`).** `NetStack` is
single-owner and `!Sync`, but many forwards want a socket on one tunnel at once. A `NetDriver`
task owns the `NetStack` *and* the `WireGuardTunnel` and runs the only poll loop; every other
task holds a cloneable `NetStackHandle` and opens `UdpConn`/`TcpConn` sockets over channels.
On top of that, `TunnelTcpForward` / `TunnelUdpForward` are the tunnel-riding port forwards:
the local application still connects to a real `127.0.0.1:port` host socket, but the far leg
is a tunnel socket dialed on the Station's inner address. Verified over a real tunnel: UDP and
TCP each cross two independent drivers; a host-OS TCP connection is bridged end to end and
echoed back (the `rwk selftest` `forward-bridge` check); the TCP close is forwarded only after
the handshake, so a connecting stream is never torn down early.

Not yet done: the reverse (Station → Client) forward direction, and pointing `EdgeTransport`
at a `NetStackHandle` so keying datagrams ride the tunnel too (the same `open_udp` seam).

### Increment 3 — the relay fallback

This is the "both sides behind aggressive NAT" case: when no direct WireGuard path can be
opened, the two ends meet at a relay. It is **not** Tailscale's DERP — RWK has already left
the tailnet (a boringtun node cannot join one), so pointing at Tailscale's DERP servers is
neither possible nor wanted. Instead `engine::relay` is a minimal relay sized to RWK's
one-Client-one-Station topology, modelled on DERP's *shape* only:

* `relay::frame` — a pure wire codec: a one-byte type + 4-byte big-endian length + payload,
  with curve25519 public keys as addresses (`Register`, `Send`, `Recv`, `KeepAlive`).
* `relay::server` — `RelayServer`: accepts connections, learns each node's key from its
  `Register` frame into a routing table, and forwards a `Send` to the destination key's
  connection as a `Recv` tagged with the sender's key. It holds **no key that can decrypt
  anything** — the payloads are WireGuard transport messages — so it is a pure forwarder an
  operator can run on any reachable host (the same binary: a future `rwk relay`).
* `relay::client` — `RelayClient`: connects, registers this node's key, and offers a
  send-to-peer / recv-from-peer datagram channel.

The tunnel rides it through a transport seam, `tunnel::wire::Wire`: `Direct { socket, peer }`
is the fast UDP path, `Relay(RelayClient)` the fallback. The crypto above is identical for
both — `WireGuardTunnel::bind` builds the direct variant, `bind_relay` the relayed one — so
NAT traversal costs the tunnel nothing but one enum. A relayed received datagram carries no
source socket address (the relay addresses by key); boringtun uses that only to learn a
roaming endpoint, which a relayed path has none of, so it is soundly `None`.

Verified: frame round-trips (byte-exact, oversize/short/unknown rejected); two clients
exchange a datagram both ways through a `RelayServer`; and — the headline — a full WireGuard
handshake *and* an encrypted inner packet are carried end to end through the relay by public
key with zero decapsulation errors (`tunnel` module test + the `rwk selftest` `relay` check).
The direct-path tunnel tests are unchanged and still pass, so the seam did not disturb the
fast path.

### Increment 4 — key and address plumbing

The glue that makes increments 1–3 usable, so the engine runs from a config file and a few
commands instead of hand-written code:

* `config` — a serde `Config` (role, identity, peer, optional relay, forward rules) that
  loads from and saves to JSON, the same shape the .NET app keeps. Keys are base64; the
  private and pre-shared keys are serialised but **redacted in every `Debug`**, so a secret
  cannot reach a log. The two inner addresses are a fixed mirrored pair (`10.6.0.1` client,
  `10.6.0.2` station) — no subnet to configure.
* `tunnel::connect` — the auto path-selector: try the peer's direct endpoint, fall back to the
  relay, error if neither is reachable. Role-aware: the Client initiates
  (`ensure_session`), the Station waits and answers (`await_session`), so two ends started
  independently don't race. Returns the established tunnel and the `Path` (Direct/Relay) that
  won, which maps to the UI's `PathType`.
* CLI — `rwk keygen <config> --role <r>` (generate an identity, print the public key, refuse
  to overwrite), `rwk pubkey <config>` (print it again), `rwk relay <addr>` (run the server),
  `rwk connect <config>` (bring the link up and hold it). Public keys are exchanged out of
  band, exactly as the Station address already is.

Verified end to end from the CLI, not just in unit tests: two `keygen`'d configs cross-wired
with each other's `pubkey` and a shared relay, a `relay` server, and two `connect` processes
both reporting "link established over Relay path" — a real WireGuard handshake completed
through the relay with no direct path. Plus the `config` module tests (JSON round-trip,
secret redaction, mirrored addresses, identity stability), the `connect` tests (relay
fallback with a real handshake), and the `rwk selftest` `config` check.

Not yet wired (integration, not transport): driving the keying path and the forwarders over a
`connect`ed tunnel from the CLI (today `connect` only holds the link up), relay keepalives on
an idle link, and the Tauri UI.

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
