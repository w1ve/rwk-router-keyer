# RWK.TailscaleSidecar

A userspace Tailscale node, embedded with [`tsnet`](https://pkg.go.dev/tailscale.com/tsnet) and
supervised as a child process by RWK.Client and RWK.Station. It backs the
`RWK.Shared.Net.ITailscaleNode` interface (design Component 5, requirements
5.1–5.8).

> **Build status: builds and tests clean; not yet exercised on a live tailnet.**
> Go 1.26.5 windows/amd64 is installed, `go mod tidy` has resolved the full
> `tailscale.com v1.102.2` dependency graph, and `go build`, `go vet` and
> `go test ./...` all pass. Everything that needs a real tailnet — joining,
> path/RTT/DERP reporting, end-to-end datagram delivery — is still unverified.
> See [Verification status](#verification-status) for the exact boundary.

## Why a Go sidecar

See [`docs/adr/0001-tailscale-embedding.md`](../../docs/adr/0001-tailscale-embedding.md).
The short version: `tsnet` is the only embedding route that gives a **real UDP
datagram path** over the mesh without a TUN adapter or administrator rights,
and edge datagram fidelity is what keeps the Station's jitter buffer on the
fast Direct profile.

## Userspace guarantee (5.1)

`tsnet` runs a gVisor userspace TCP/IP stack. `tsnet.Server.Tun` is deliberately
left `nil` in `node.go` and must stay that way: setting it would introduce a TUN
device and, on Windows, an elevation requirement. The status document reports
`"userspace": true` so the C# side can assert this rather than assume it.

## Edge datagrams are real UDP (5.6)

`tsnet.Server.ListenPacket("udp4", ip:port)` (added in tailscale v1.68.0) returns
a `net.PacketConn` served by the userspace stack. Datagram boundaries are
preserved end to end, so **no TCP substitution takes place**. The status document
declares this explicitly:

```json
"edge": { "transport": "udp", "jitterProfile": "PathAdaptive", ... }
```

`jitterProfile` is the contract that matters operationally:

| Value             | Meaning for the Station                                                    |
| ----------------- | -------------------------------------------------------------------------- |
| `PathAdaptive`    | Jitter delay may follow the observed path (Direct 30–150 ms, DERP 100–500 ms) |
| `DerpClassOnly`   | Jitter delay must use the conservative DERP-class profile at all times      |

If a future change ever loses true datagram delivery, `transport` becomes `tcp`
and `jitterProfile` becomes `DerpClassOnly`. The Station should read this field
rather than hardcoding a profile.

## IPC

Localhost TCP, not a named pipe — it keeps this program portable and lets the
.NET side use an ordinary `HttpClient`. Two channels:

1. **HTTP control/status API** on `-api-addr` (default `127.0.0.1:0`, i.e. a free
   port chosen by the OS).
2. **Loopback UDP socket** on `-edge-local-addr` for edge datagrams, so edges
   never pass through HTTP.

No port is hardcoded. On startup the process writes exactly one JSON line to
**stdout** and everything else to stderr:

```json
{"protocol":1,"pid":4242,"apiAddress":"127.0.0.1:52341","token":"…","edgeLocalAddress":"127.0.0.1:52342","edgeTransport":"udp"}
```

The supervisor reads that line, then uses `apiAddress` and `token` for all
requests.

### Authentication

Every request must carry `X-RWK-Token: <token>`. The API is loopback-only, but
any local process can reach a loopback port, and an unauthenticated endpoint here
would let any process on the machine join or leave the tailnet and open tunnels
into the Station's network. Pass `-token` to supply your own, or let the process
generate one and read it from the handshake line.

**Known residual exposure:** the loopback *UDP* edge socket has no token. Any
local process can send it a datagram, which would be relayed to the peer as an
edge frame. This matches the design's threat model — edge frames are already
unauthenticated over the wire and the Station gates sessions with the HMAC
pairing secret — but it is worth stating plainly rather than leaving implied.

### Endpoints

| Method   | Path                    | Purpose                                                       |
| -------- | ----------------------- | ------------------------------------------------------------- |
| `GET`    | `/v1/health`            | Liveness plus protocol/transport declaration                  |
| `GET`    | `/v1/status`            | Full status document — poll this every 2 s                    |
| `POST`   | `/v1/start`             | `{"authKey":"tskey-auth-…"}` join the tailnet (5.2)           |
| `POST`   | `/v1/stop`              | Leave the tailnet, release resources                          |
| `POST`   | `/v1/peer`              | `{"address":"100.x.y.z","edgePort":41000}` set the peer       |
| `POST`   | `/v1/edge/callback`     | `{"address":"127.0.0.1:51500"}` where inbound edges are sent  |
| `GET`    | `/v1/forwards`          | List TCP forwards                                             |
| `POST`   | `/v1/forwards`          | Create a TCP forward                                          |
| `DELETE` | `/v1/forwards/{id}`     | Remove a TCP forward                                          |

`POST /v1/start` returns `202 Accepted` immediately and the caller polls
`/v1/status` until `state` is `Connected` or `Fault`; failures surface in
`lastError`. The auth key travels in a request body rather than on the command
line so it never appears in the process list.

### Status document

```json
{
  "protocol": 1,
  "state": "Connected",
  "backendState": "Running",
  "userspace": true,
  "hostname": "rwk-client-vk3abc",
  "selfAddress": "100.101.102.103",
  "selfDnsName": "rwk-client-vk3abc.tailnet-1234.ts.net",
  "peerSpec": "rwk-station-vk3abc",
  "peerAddress": "100.64.0.9",
  "peerOnline": true,
  "path": "Direct",
  "roundTripMs": 23.4,
  "derpRegion": "",
  "probeAgeMs": 1200,
  "probeFailures": 0,
  "edge": { "transport": "udp", "jitterProfile": "PathAdaptive", "tailnetPort": 41000, "…": "counters" },
  "forwards": [],
  "socks5": { "address": "127.0.0.1:5xxxx", "username": "tsnet", "password": "…" },
  "localApi": { "address": "127.0.0.1:5xxxx", "password": "…" }
}
```

Mapping to `ITailscaleNode`:

| Interface member  | Source                                                     |
| ----------------- | ---------------------------------------------------------- |
| `State`           | `state`                                                    |
| `PeerAddress`     | `peerAddress`                                              |
| `CurrentPath`     | `path` (5.3)                                               |
| `RoundTripTime`   | `roundTripMs`, `-1` when unmeasured (5.4)                  |
| `DerpRegion`      | `derpRegion`, empty unless `path` is `Derp` (5.5)          |
| `StateChanged`    | Raise on transitions observed while polling (5.8)          |
| `SendEdgeAsync`   | UDP send to `edgeLocalAddress` (5.6)                       |
| `EdgeReceived`    | UDP receive on the callback socket (5.6)                   |
| `ConnectControlAsync` | An outbound forward, or the SOCKS5 proxy (5.7)          |

`path`, `roundTripMs` and `derpRegion` come from a disco ping issued every
`-poll-interval` (default 2 s), corroborated by the peer's netmap entry: a
non-empty `CurAddr` means Direct, otherwise `Relay` names the DERP region. This
matches upstream Tailscale's own interpretation of those fields.

### TCP forwards and the control channel (5.7)

The tailnet stack lives in this process, so the .NET side cannot listen on or
dial the tailnet directly. Two forward kinds bridge that:

- `"kind":"out"` — listens on loopback, dials the peer over the tailnet. The
  Client's `ConnectControlAsync` connects an ordinary `TcpClient` to the returned
  `listenAddress`.
- `"kind":"in"` — listens on a tailnet port, dials a loopback port. The Station's
  `SessionManager` keeps its own loopback listener and receives control
  connections through this.

Both propagate half-close (a FIN in one direction closes only that direction) and
set `TCP_NODELAY`, since the control channel is small and latency sensitive.

`ListenService`-style SOCKS5 is also available via `socks5` in the status
document for callers that prefer it, but a `"kind":"out"` forward is simpler from
.NET because it needs no SOCKS5 handshake.

## Lifecycle and clean shutdown

A stranded sidecar still holding a tailnet identity is a real operational
problem, so there are three independent exits:

1. **stdin EOF** — the supervisor must launch with stdin redirected and keep the
   write handle open. When the parent dies the pipe closes and this process
   exits. Disable with `-exit-on-stdin-close=false`.
2. **Watchdog** — if no authenticated request arrives within `-watchdog`
   (default 15 s), exit. The C# side's 2 s status polling doubles as the
   liveness signal. `-watchdog 0` disables it.
3. **SIGINT / SIGTERM**.

All three run the same ordered teardown: stop the IPC listener, drop tailnet
forwards, close the edge socket, close the tsnet server (which leaves the
tailnet), then close the loopback socket.

Consider `-ephemeral` with an ephemeral auth key if you would rather the tailnet
identity vanish on exit than persist under a stable hostname.

## Flags

| Flag                     | Default          | Purpose                                                    |
| ------------------------ | ---------------- | ---------------------------------------------------------- |
| `-api-addr`              | `127.0.0.1:0`    | IPC/status HTTP API address; port 0 picks a free port       |
| `-token`                 | generated        | Shared secret for `X-RWK-Token`                            |
| `-edge-local-addr`       | `127.0.0.1:0`    | Loopback UDP socket for outbound edges                     |
| `-edge-callback-addr`    | *(none)*         | Loopback UDP endpoint for inbound edges                    |
| `-edge-tailnet-port`     | `0`              | Tailnet UDP port for edges; 0 picks a free port            |
| `-hostname`              | `rwk-node`       | Hostname presented to the control plane                    |
| `-state-dir`             | user config dir  | Tailscale identity and state directory                     |
| `-ephemeral`             | `false`          | Register as an ephemeral node                              |
| `-control-url`           | *(Tailscale)*    | Alternate coordination server                              |
| `-poll-interval`         | `2s`             | Status refresh and RTT probe interval                      |
| `-fault-after`           | `3`              | Consecutive failed probes before `Fault` (5.8)             |
| `-start-timeout`         | `90s`            | How long to wait for the tailnet to come up                |
| `-watchdog`              | `15s`            | Exit after this much IPC silence; 0 disables               |
| `-exit-on-stdin-close`   | `true`           | Exit on stdin EOF (parent death)                           |
| `-verbose`               | `false`          | Verbose Tailscale backend logging to stderr                |

`-api-addr`, `-edge-local-addr` and `-edge-callback-addr` are rejected unless the
host is a loopback literal. `:port` is rejected too, since an empty host binds
every interface.

If the .NET app uses a single `UdpClient` for both directions, the callback
address is learned from the source of the first outbound datagram and
`-edge-callback-addr` is optional. Setting it explicitly is still preferred.

## Building

Requires **Go 1.26.5 or newer**: `tailscale.com v1.102.2` declares `go 1.26.5`,
and a main module cannot declare a lower version than its dependencies require.

```powershell
cd src/RWK.TailscaleSidecar
go mod tidy          # generates go.sum and the indirect requirements
go build -o rwk-tailscale-sidecar.exe .
go test ./...
```

The binary is a standalone executable that ships alongside the .NET apps. It is
**not** an MSBuild project and is deliberately absent from `RWK.sln`; wire it
into publishing as a content file or a post-build copy step.

Single-file publish note: a self-contained single-file .NET publish cannot absorb
a native Go binary into its bundle in a way that keeps it executable, so the
sidecar has to be extracted or shipped next to the host executable. That
tradeoff is recorded in the ADR.

## Verification status

Toolchain: Go 1.26.5 windows/amd64, a portable extraction of the official
`dl.google.com` zip to `E:\go`. The archive's SHA256 was checked against the
Google-published checksum for `go1.26.5.windows-amd64.zip`
(`97e6b2a833b6d89f9ff17d25419ac0a7e3b482a044e9ab18cdef834bd834fd38`). `GOPATH` is
`E:\gopath` to keep the module cache off the system drive.

Verified:

- `go mod tidy` completed successfully (exit 0). The full `tailscale.com v1.102.2`
  dependency graph resolved and downloaded, and `go.sum` is now generated.
- `go build -o rwk-tailscale-sidecar.exe .` succeeds (exit 0), after fixing three
  compile errors (below).
- `go vet ./...` clean (exit 0).
- `go test ./...` passes: `ok rwk/tailscalesidecar 3.502s` (exit 0).
- Runtime smoke test, run as
  `.\rwk-tailscale-sidecar.exe -watchdog 3s -exit-on-stdin-close=false`:
  - The single-line stdout handshake JSON is emitted and well formed — `protocol`,
    `pid`, `apiAddress`, `token`, `edgeLocalAddress`, `edgeTransport`.
  - `edgeTransport` reports `udp`.
  - The API and edge-local ports are dynamically assigned rather than hardcoded
    (observed `127.0.0.1:40002` and `127.0.0.1:51597`).
  - Flag parsing works for `-watchdog` and `-exit-on-stdin-close`.
  - The watchdog fired at 3.001s against a 3s limit, then logged
    `shutting down (watchdog)` and `exited cleanly` with exit code 0.
  - The sidecar created its state directory at
    `%APPDATA%\RWK\tailscale\rwk-node` on first run.

Compile errors found and fixed by the first real build:

- Three occurrences in `node.go` used `netip.ParseAddr`'s second return value as a
  boolean. `netip.ParseAddr` returns `(Addr, error)`, so
  `wantAddr, isAddr := netip.ParseAddr(spec)` made `isAddr` an error and
  `if isAddr {` failed to compile with "non-boolean condition in if statement".
  Fixed by deriving the boolean from the error —
  `wantAddr, parseErr := netip.ParseAddr(spec)` then `isAddr := parseErr == nil` —
  matching the pattern already used correctly in `SetPeer`.

Still **not** verified:

- Joining a real tailnet. This needs a genuine Tailscale pre-auth key, which is
  not available here.
- `path`, `roundTripMs` and `derpRegion` against real Direct and DERP paths.
- End-to-end UDP datagram delivery over the mesh. The `ListenPacket` API's
  existence and semantics are confirmed from the tailscale v1.102.2 source, and
  the compile now proves the call sites type-check, but no datagram has crossed a
  real tailnet.
- The stdin-EOF parent-death exit path. It was explicitly disabled during the
  smoke test (`-exit-on-stdin-close=false`), so it remains unexercised.
- The SIGINT / SIGTERM shutdown path.

What should be checked next:

1. Launch with stdin piped from a parent, kill the parent, confirm the process
   exits.
2. Send SIGINT / SIGTERM (Ctrl+C, or a console control event) and confirm the
   same ordered teardown runs.
3. Join a real tailnet with a genuine pre-auth key, then confirm `path`,
   `roundTripMs` and `derpRegion` against Direct and DERP paths and observe an
   edge datagram cross the mesh.
