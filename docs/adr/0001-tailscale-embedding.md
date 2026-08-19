# ADR 0001: Embed Tailscale via a Go `tsnet` sidecar process

- **Status:** Accepted
- **Date:** 2026-02-14
- **Context:** RWK v2.0, design Component 5 (`ITailscaleNode`), requirements 5.1–5.8
- **Supersedes:** nothing
- **Implementation:** `src/RWK.TailscaleSidecar/` (builds and tests clean; not yet exercised on a live tailnet — see Verification status)

## Context

RWK v2.0 replaces the v1 Cloudflare WebSocket relay with a Tailscale mesh between
the Client and the Station. The Client and Station are .NET 9 Windows
applications, so Tailscale has to be reachable from C#. Three routes exist:

1. **The Tailscale system service** (`tailscaled` with a TUN adapter) — control it
   from C# via the LocalAPI.
2. **`libtailscale`** — the C ABI over `tsnet`, called from C# with P/Invoke.
3. **A `tsnet` sidecar** — a small Go program embedding a userspace node,
   supervised as a child process and driven over loopback IPC.

The choice is load bearing rather than cosmetic. It determines whether edge
events cross the mesh as UDP datagrams, and that in turn determines which jitter
buffer profile the Station may use — which the operator feels directly as
latency.

## Decision

**Use a Go `tsnet` sidecar process.**

`src/RWK.TailscaleSidecar/` embeds `tsnet.Server`, exposes status over a
loopback HTTP API and edge datagrams over a loopback UDP socket, and is
supervised as a child process by both applications.

## Decision criteria

### 1. No administrator rights, userspace only (requirement 5.1, hard constraint)

Requirement 5.1 forbids a TUN adapter and forbids requiring elevation. Amateur
operators install this on their own machines and a station may be unattended;
an install that demands elevation or a system service is a support burden and a
larger attack surface.

- **System service:** fails outright. Installing a TUN adapter requires
  elevation, and the service is machine-wide state RWK does not own — the
  operator may already run Tailscale for other purposes, and RWK reconfiguring
  it would be hostile.
- **`libtailscale`:** satisfies it. Same userspace gVisor stack as `tsnet`.
- **`tsnet` sidecar:** satisfies it. `tsnet` runs a gVisor userspace TCP/IP
  stack; no TUN, no elevation. `tsnet.Server.Tun` is left `nil` and documented
  as untouchable.

### 2. UDP datagram fidelity for edge events (requirement 5.6) — the deciding criterion

The design flagged this as the open question: if the embedding route cannot
expose true UDP datagrams over the mesh in userspace mode, the fallback is a
dedicated low-traffic TCP stream with `TCP_NODELAY`, and the Station must then use
the conservative DERP-class jitter profile **at all times** — roughly 200 ms of
added delay even on a Direct path where 60 ms would do.

**Finding: true UDP datagrams are achievable.** `tsnet.Server.ListenPacket`,
added in tailscale v1.68.0, returns a `net.PacketConn` served by the gVisor
userspace stack:

> The network must be "udp", "udp4" or "udp6". The addr must be of the form
> "ip:port" … IP must be specified.
>
> — [`tsnet` package documentation](https://pkg.go.dev/tailscale.com/tsnet#Server.ListenPacket)
> (v1.102.2; content rephrased for compliance with licensing restrictions where
> quoted elsewhere in this document)

Confirmed against the [v1.102.2 implementation](https://github.com/tailscale/tailscale/blob/v1.102.2/tsnet/tsnet.go),
which delegates to the netstack's `ListenPacket` and supports port 0 for
automatic allocation. Datagram boundaries are preserved end to end, so the
Station may use the path-adaptive jitter profile (Direct 30–150 ms, DERP
100–500 ms) as design 7.1 intends.

This is the reason the sidecar is not merely "preferred for simplicity". The
alternatives fare differently:

- A **SOCKS5 proxy** onto the tailnet — the obvious userspace escape hatch, and
  the route `tailscaled --tun=userspace-networking` offers — carries TCP only.
  Relying on it would have forced the TCP fallback and the permanent DERP-class
  buffer.
- **`libtailscale`** exposes a deliberately small C surface built around
  listen/dial/accept for streams. Reaching `ListenPacket` semantics through it
  would mean either upstream additions or unsupported interop, whereas from Go
  it is one documented call.

The sidecar makes the outcome machine-checkable rather than assumed: the status
document declares `edge.transport` and `edge.jitterProfile`, and the Station
selects its buffer profile from those fields. If a future change ever loses
datagram fidelity, the declaration flips to `tcp` / `DerpClassOnly` and the
Station's behaviour follows automatically instead of silently mismatching.

### 3. Single-file publish impact

- **`libtailscale`:** a native DLL P/Invoked from C#. It sits inside the
  single-file bundle but is extracted to a temp directory at run time, so
  "single file" is already qualified. Native interop also means marshalling
  callbacks across the boundary for every status change, and a crash in the
  native stack takes the UI process with it.
- **`tsnet` sidecar:** a separate `.exe` that cannot live inside a .NET
  single-file bundle as an executable. It ships beside the host executable or is
  extracted on first run. This is a genuine cost: two files to deploy instead of
  one, and process supervision to get right.

The cost is accepted because process isolation is worth more than a single file
here. The Station's edge replay thread runs at `THREAD_PRIORITY_TIME_CRITICAL`
and drives a transmitter under a strict key-up-on-any-failure policy. A fault in
the network stack must not be able to take down the process holding the keying
line: if the sidecar dies, the .NET side observes it, the F9 fail-safe fires, and
the key drops. With P/Invoke the same fault is an in-process crash.

### 4. Operational and maintenance factors

- Go is Tailscale's own language; `tsnet` is what Tailscale uses internally and
  is a first-class supported API. `libtailscale` is a thinner, less-exercised
  surface.
- Tracking upstream is a version bump in `go.mod` rather than a rebuild of native
  bindings.
- The sidecar is a normal process: it can be inspected, killed, and logged
  independently of the UI.
- The added cost is a second toolchain (Go 1.26.5+, since `tailscale.com v1.102.2`
  declares `go 1.26.5`) and a second build step in CI.

## Consequences

### Positive

- Requirement 5.1 is met structurally, not by discipline: no code path in the
  sidecar can create a TUN adapter without an explicit, obvious change.
- Edge events cross the mesh as real UDP datagrams, so the Station keeps the fast
  Direct jitter profile and the operator keeps the lower latency.
- A crash in the network stack cannot take down the process that owns the keying
  line.
- The auth key is sent in an HTTP request body rather than as a command-line
  argument, so it never appears in the process list.
- Cross-platform potential is retained at no extra cost, should a non-Windows
  Station ever be wanted.

### Negative

- Two-file deployment and process supervision, including making sure a sidecar is
  never stranded holding a tailnet identity. Mitigated by three independent
  exits: stdin EOF on parent death, an IPC idle watchdog, and signals.
- A second toolchain in the build.
- IPC on loopback is reachable by any local process, so the HTTP API requires a
  shared token. The loopback **UDP** edge socket is not token protected — a local
  process could inject an edge frame. Accepted: edge frames are unauthenticated
  over the wire by design, and the Station gates sessions with the HMAC pairing
  secret. Recorded here rather than left implicit.
- Two extra loopback sockets per application.

### Neutral

- The `tsnet` loopback SOCKS5 proxy is exposed in the status document for callers
  that want it, but the control channel uses a loopback TCP forward instead,
  because that needs no SOCKS5 handshake from .NET.

## Verification status

The finding about `ListenPacket` is from the tailscale v1.102.2 source and
package documentation, which is authoritative for what the API offers.

A Go 1.26.5 windows/amd64 toolchain is now installed (portable extraction of the
official `dl.google.com` zip to `E:\go`, SHA256 checked against the
Google-published checksum for `go1.26.5.windows-amd64.zip`,
`97e6b2a833b6d89f9ff17d25419ac0a7e3b482a044e9ab18cdef834bd834fd38`, with `GOPATH`
at `E:\gopath`). Verified since:

- `go mod tidy` completed successfully (exit 0); the full `tailscale.com v1.102.2`
  dependency graph resolved and downloaded and `go.sum` is generated.
- `go build -o rwk-tailscale-sidecar.exe .` succeeds (exit 0), `go vet ./...` is
  clean (exit 0), and `go test ./...` passes (`ok rwk/tailscalesidecar 3.502s`,
  exit 0).
- A runtime smoke test
  (`-watchdog 3s -exit-on-stdin-close=false`) confirmed the single-line stdout
  handshake JSON is well formed, `edgeTransport` reports `udp`, the API and
  edge-local ports are dynamically assigned (observed `127.0.0.1:40002` and
  `127.0.0.1:51597`), flag parsing works, and the watchdog fired at 3.001s against
  a 3s limit and exited cleanly with code 0.

The first real build surfaced three concrete defects, which is the value the
toolchain install delivered: three occurrences in `node.go` treated
`netip.ParseAddr`'s second return value as a boolean, so
`wantAddr, isAddr := netip.ParseAddr(spec)` made `isAddr` an error and
`if isAddr {` failed to compile ("non-boolean condition in if statement"). Fixed
by deriving the boolean from the error, matching the pattern already used
correctly in `SetPeer`.

What has still **not** been verified:

- Joining a real tailnet. That needs a genuine Tailscale pre-auth key, which is
  not available here.
- No datagram has crossed a real tailnet. The compile now proves the
  `ListenPacket` call sites type-check, but end-to-end delivery over the mesh
  remains unobserved.
- RTT, path-type and DERP-region reporting have not been observed against real
  Direct and DERP paths.
- The stdin-EOF parent-death exit path, which was explicitly disabled during the
  smoke test (`-exit-on-stdin-close=false`).
- The SIGINT / SIGTERM shutdown path.

The conclusion that true UDP is achievable still rests on the documented and
source-confirmed behaviour of `ListenPacket` plus a type-checked build, not on an
observed end-to-end datagram. If live testing contradicts it, this ADR must be
revisited and `edge.transport` / `edge.jitterProfile` changed to `tcp` /
`DerpClassOnly`, which is the mechanism that keeps the Station honest in that
case.

## References

- [`tsnet` package documentation](https://pkg.go.dev/tailscale.com/tsnet) — userspace gVisor stack, `ListenPacket`, `Loopback`
- [`tsnet/tsnet.go` at v1.102.2](https://github.com/tailscale/tailscale/blob/v1.102.2/tsnet/tsnet.go) — `ListenPacket` implementation
- [Userspace networking mode](https://tailscale.com/docs/concepts/userspace-networking) — why the SOCKS5 route is TCP only
- `.kiro/specs/rwk-v2/design.md` — Component 5, External Components
- `.kiro/specs/rwk-v2/requirements.md` — Requirement 5

*Content from external sources was rephrased for compliance with licensing restrictions.*
