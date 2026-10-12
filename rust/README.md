# RWK Router Keyer — Rust port (`rwk-core`)

Native Rust engine for RWK Router Keyer. This directory is the start of the port away
from the .NET solution plus its Go/Tailscale sidecar, toward **one self-contained
executable with no child process**.

## Layout

```
rust/
  Cargo.toml                     workspace
  crates/rwk-core/               the engine library
    src/primitives.rs            shared enums (KeyingLine, KeyerMode, PathType, ...)
    src/timing.rs                HybridWaiter: sleep-then-spin, sub-millisecond
    src/platform.rs              the only `unsafe` seam: thread priority + timer resolution
    src/protocol/edge.rs         RWK-PADDLE frame codec (12-byte edges, 1-4 per frame)
    src/protocol/winkeyer.rs     WinKeyer command bytes + status decoding
    src/protocol/morse.rs        ITU pattern table
    src/engine/serial.rs         port enumeration, DTR/RTS keying, PTT sequencer
    src/engine/keying.rs         element timing, schedule builder, paddle decider
    src/engine/audio.rs          keyed sine sidetone (cpal) with envelope shaping
    src/engine/network.rs        UDP edge transport + UDP/TCP port forwarding
    src/engine/tunnel.rs         native WireGuard transport (static peers, no Tailscale)
    src/engine/netstack.rs       smoltcp userspace IP stack over the tunnel (sockets)
    src/engine/netstack_driver.rs  one task owns the stack; handles hold sockets over channels
    src/engine/tunnel_forward.rs   port forwards whose far leg rides the tunnel
    src/engine/relay/              key-addressed packet relay (NAT-traversal fallback)
      frame.rs                     the relay wire codec
      server.rs                    RelayServer: forwards datagrams by public key
      client.rs                    RelayClient: a datagram channel to one peer
    src/engine/tunnel/connect.rs   auto path selection (direct, then relay) from config
    src/engine/session.rs          a running session: netstack + forwards over the tunnel
    src/config.rs                  persisted keys + addresses (serde config.json)
    src/engine/replay/           Station edge replayer
      tracker.rs                 epoch/duplicate/gap/timestamp validation
      jitter.rs                  delay bands + adaptive delay
      anchor.rs                  stream time -> absolute deadlines
      failsafe.rs                F1-F10, latch policy, watchdogs
      replayer.rs                the replayer core
      driver.rs                  dedicated replay + watchdog threads
    src/engine/bus.rs            tokio broadcast event bus
  crates/rwk/                    the single `rwk` executable
  docs/NATIVE-NETWORK-SPIKE.md   plan for replacing the Tailscale sidecar natively
```

## Build and run

> **This dev machine's toolchain.** `boringtun` pulls in `ring`, which compiles C,
> and the Visual Studio install here is a stripped Insiders build with no MSVC CRT
> headers. The build is wired to a self-contained toolchain on `E:` instead:
> clang + lld-link from `E:\LLVM` and the MSVC CRT + Windows SDK fetched to
> `E:\winsdk` with [`xwin`](https://github.com/Jake-Shadle/xwin). Nothing lives on
> `C:`. Source `build-env.ps1` (sets `CC`/`INCLUDE`/`LIB` and the cargo linker)
> once per shell before any cargo command:
>
> ```powershell
> . .\build-env.ps1
> ```
>
> On a machine with a complete MSVC "Desktop development with C++" install this is
> unnecessary — plain `cargo build` works.

```sh
cd rust
. .\build-env.ps1              # this machine only; see note above
cargo build --release          # produces target/release/rwk.exe (one file, no sidecar)
cargo test                     # 183 unit tests
./target/release/rwk ports     # list serial ports
./target/release/rwk devices   # list audio output devices
./target/release/rwk selftest  # 14 checks: ...netstack, forward-bridge, relay, config

# Bring a native tunnel up (no Tailscale, no sidecar):
./target/release/rwk keygen client.json --role client   # prints this node's public key
./target/release/rwk keygen station.json --role station
# exchange the printed public keys into each config's peer.public_key, set a relay or a
# direct peer.endpoint, then on each host:
./target/release/rwk relay 0.0.0.0:9999   # on a reachable host (optional, for NAT fallback)
./target/release/rwk connect station.json # the remote radio site
./target/release/rwk connect client.json  # the operating position
./target/release/rwk key COM3 "CQ TEST" --wpm 25 --line dtr
```

## What is ported

| .NET source | Rust | Notes |
|---|---|---|
| `RWK.Shared.Protocol.Edge.EdgeEntry` / `RwkPaddleFrame` | `protocol::edge` | byte-exact wire layout |
| `RWK.Shared.Protocol.CommandDefinitions` | `protocol::winkeyer` | same constant values |
| `RWK.Shared.Protocol.MorseTable` | `protocol::morse` | ITU patterns |
| `RWK.Shared.Keying.KeyerElementTiming` | `engine::keying::KeyerElementTiming` | identical weight maths |
| `RWK.Shared.Timing.EdgeScheduleBuilder` | `engine::keying::EdgeScheduleBuilder` | gaps added *before* characters |
| `RWK.Shared.Keying.KeyerElementEngine` | `engine::keying::PaddleElementEngine` | Iambic A/B, Ultimatic, Bug, Straight |
| `RWK.Shared.Timing.HybridWaiter` | `timing::HybridWaiter` | wider spin window; no `timeBeginPeriod` |
| `RWK.Shared.Config.KeyingOutputConfig` / `PttTimingConfig` | `engine::serial` | + `PttSequencer` |
| `RWK.Client.Audio.KeyedSineGenerator` | `engine::audio::KeyedSineGenerator` | 2 ms raised-cosine ramp |
| `RWK.Client.Audio.LocalSidetoneEngine` | `engine::audio::SidetoneEngine` | cpal instead of WASAPI/NAudio |
| Go sidecar `edgeRelay` (UDP) | `engine::network::EdgeTransport` | native UDP, same source filtering |
| Go sidecar `out-udp` / `in-udp` | `engine::network::UdpForwarder` | native UDP relay |
| Go sidecar `out` / `in` | `engine::network::TcpForwarder` | native TCP relay; `TCP_NODELAY` on both legs, half-close propagated per direction |
| `tsnet` WireGuard (wireguard-go) | `engine::tunnel::WireGuardTunnel` | boringtun; static-keyed peer, handshake, keepalives, roaming endpoint |
| Tailscale coordination server (keys, peers, ACLs) | `engine::tunnel::TunnelPeer` | by design nothing to discover: one client, one station, operator-supplied key + address |
| `EdgeSequenceTracker` + validation types | `engine::replay::tracker` | redundancy healing, never guesses a key-down |
| `JitterBuffer` + `EdgeJitterProfile` | `engine::replay::jitter` | bands, EWMA adaptation, late-edge storm |
| `ReplayAnchor` | `engine::replay::anchor` | deadline = anchor + relative timestamp |
| `FailSafeCondition` / `EdgeReplayerState` | `engine::replay::failsafe` | same F-numbering, same latch policy |
| `FailSafeMonitor` / `SchedulerWatchdog` | `engine::replay::failsafe` | threshold checks (F1, F2, F3, F10) |
| `EdgeReplayer` + `EdgeReplayerTelemetry` | `engine::replay::replayer` | synchronous, timestamp-explicit core |
| `EdgeReplayer`'s replay thread + monitor/watchdog threads | `engine::replay::driver` | dedicated std threads, `HybridWaiter` deadlines, F8 on shutdown |
| .NET `THREAD_PRIORITY_TIME_CRITICAL` + `timeBeginPeriod(1)` | `platform` | RAII guards: Win32 `SetThreadPriority`/`timeBeginPeriod`, Linux `sched_setscheduler`/`nice`, no-op fallback |
| Go sidecar mesh path state (for F9) | `engine::network::PathHealth` | shared flag; the edge transport *and* the WireGuard tunnel raise it on sustained send failures |
| `tsnet` gVisor userspace netstack | `engine::netstack::NetStack` (`smoltcp`) | `Medium::Ip` device over the tunnel's inner packets; UDP + TCP sockets, no TUN, no admin |
| `tsnet` socket multiplexing over one node | `engine::netstack_driver::NetDriver` + `NetStackHandle` | one task owns the stack+tunnel; forwards hold `UdpConn`/`TcpConn` sockets over channels |
| Go sidecar `out` / `out-udp` *over the tunnel* | `engine::tunnel_forward::TunnelTcpForward` / `TunnelUdpForward` | Client leg: local host socket, far leg a tunnel socket on the Station's inner address |
| Go sidecar far-leg dial on the Station | `engine::tunnel_forward::StationTcpResponder` / `StationUdpResponder` | Station leg: listen on the tunnel inner address, dial the real LAN device |
| sidecar wiring config rules → live forwards | `engine::session::Session` | spawns the `NetDriver` and starts a forward per enabled rule by role; `rwk connect` builds it |
| `tsnet` DERP relay (NAT-traversal fallback) | `engine::relay` + `engine::tunnel::wire::Wire` | a minimal key-addressed relay (not Tailscale DERP); the tunnel rides direct UDP or the relay with identical crypto |
| `tsnet` auto path selection (direct ↔ DERP) | `engine::tunnel::connect` | tries the peer's direct endpoint, falls back to the relay; role-aware (Client initiates, Station waits) |
| `config.json` (keys, peer, relay, forwards) | `config::Config` (serde) | base64 keys, secrets redacted in `Debug`; `rwk keygen`/`pubkey`/`relay`/`connect` drive it |

## What is not ported yet

* **The reverse forward direction and keying over the tunnel.** The outbound (Client →
  Station) forwards now ride the tunnel end to end — a `Session` built by `rwk connect` wires
  the Client's `TunnelTcpForward`/`TunnelUdpForward` to the Station's
  `StationTcpResponder`/`StationUdpResponder`, which dial the real LAN device. Still to do:
  the Station → Client direction, and pointing `EdgeTransport` (keying datagrams) at the
  session's `NetStackHandle` so CW rides the tunnel too — the `open_udp` seam is the same.
  Over a LAN or hardware VPN the host-socket `network` forwarders keep working unchanged. See
  [docs/NATIVE-NETWORK-SPIKE.md](docs/NATIVE-NETWORK-SPIKE.md).
* **Automatic path selection and a relay subcommand.** The relay fallback exists
  (`engine::relay` + `tunnel::wire::Wire`): the tunnel runs over direct UDP (`bind`) or the
  relay (`bind_relay`). Still to do: trying direct first and falling back to the relay on its
  own, relay keepalives on an idle link, and a `rwk relay` subcommand to run the server.
* **Key and address plumbing.** `StaticIdentity` and `TunnelPeer` are configured in code;
  there is no CLI or config-file path yet for generating an identity and persisting a peer.
* **Linux scheduling verified on paper only.** The Linux backend compiles and follows the
  POSIX contract (`sched_setscheduler(SCHED_FIFO)` with a `nice` fallback), but it has not
  run on a Raspberry Pi or any Linux host yet.
* **F6 latency.** A serial fault is latched on the next control-line write, as on real
  hardware; a cable pulled while the key is idle produces no write and is caught by the
  F1/F2 heartbeat watchdogs instead.
* **WinKeyer protocol host** and legacy emulator paths.
* **The Tauri v2 UI** (web front end styled from FancyMumble's CSS) and the
  `#[tauri::command]` / `emit()` bridge over the event bus.

## Acceptance status

| Criterion | Status |
|---|---|
| 1. `cargo check` / `cargo build`, no warnings | ✅ 0 errors, 0 warnings |
| 2. Zero Node runtime, single EXE | ✅ one Rust binary; no Node, no sidecar |
| 3. Sub-millisecond timing | ✅ both the selftest and the unit test measure at the time-critical priority the engine runs at: selftest median < 1 µs, 200/200 waits within 1 ms (worst 0.06 ms); unit test asserts median < 500 µs and ≥95/100 waits within 1 ms |
| 4. Clean shutdown | ✅ RAII: dropping `SerialKeyingOutput`/`SidetoneEngine` releases port/stream; forwarders stop via a `watch` channel |
| 5. Scheduling protection | ✅ replay + watchdog threads raise themselves to time-critical priority and request a 1 ms timer period; a refusal is reported, never fatal (`rwk selftest` shows what the OS granted) |
| 6. Native encrypted transport, no Tailscale | ✅ boringtun userspace WireGuard: two static peers handshake and carry an encrypted inner packet with no control plane; a `smoltcp` userspace netstack rides it to present UDP/TCP sockets with no TUN and no admin; and a minimal key-addressed relay carries the same traffic as a NAT-traversal fallback when no direct path exists |

Station replay safety, verified by unit tests: redundancy heals a lost datagram; a key-up
behind an unhealed gap is applied; a key-down behind one forces key-up and latches SAFE
(F5); duplicate edges are discarded; jitter is removed from element spacing; F1/F2/F3/F10
fire at their documented thresholds. The driver is verified end to end: it keys an
arriving frame from its own thread, publishes fail-safes to the event bus exactly once,
releases the key on shutdown (F8), and both previously unsubscribed fail-safes now have a
source — F6 from a latched keying-output write fault, F9 from `PathHealth`.

Both native forwarders are verified on loopback: the UDP relay carries a datagram each
way and forwards the reply to the last sender; the TCP relay echoes both directions,
propagates a half-close without cutting the reply direction, serves three connections
concurrently on separate tasks, and counts a refused dial without disturbing the listener.

The tunnel is verified with real cryptography over loopback: two peers derived from
independent static keys complete a WireGuard handshake and round-trip an inner IPv4 packet
byte for byte; a peer configured with the wrong key cannot establish a session and its
datagram is counted as undecryptable; an oversized inner packet is refused; sending before
the handshake fails immediately instead of blocking the keying path; and a sustained run of
send failures raises `PathHealth`, so the tunnel is its own F9 source. `Debug` output is
asserted to print the public key only — never the private or pre-shared key.

The netstack (`engine::netstack`) is verified over the device seam and over the real tunnel:
a UDP datagram and a bidirectional TCP stream each cross between two `NetStack` instances; a
TCP half-close is propagated without cutting the reply direction; and a UDP datagram survives
the full encrypted path (socket → smoltcp → WireGuard encrypt → wire → decrypt → smoltcp →
socket) between two live tunnels, with its source port preserved end to end and no
decapsulation errors.

The concurrency seam and the tunnel-riding forwards (`engine::netstack_driver`,
`engine::tunnel_forward`) are verified over a real tunnel too: a `NetDriver` owns the stack
and tunnel on one task while handles open sockets over channels; UDP and TCP each cross two
independent drivers; and a host-OS TCP connection is bridged — local host socket →
`TunnelTcpForward` → WireGuard → the far stack's inner socket → echo → back (also the
`rwk selftest` `forward-bridge` check). The TCP close is forwarded only after the handshake,
so a still-connecting stream is never torn down early.

The relay fallback (`engine::relay`, `engine::tunnel::wire`) is verified end to end: the frame
codec round-trips and rejects oversized, short and unknown frames; two clients exchange a
datagram both ways through a `RelayServer`; and a full WireGuard handshake plus an encrypted
inner packet are carried entirely through the relay by public key, with zero decapsulation
errors (also the `rwk selftest` `relay` check). The relay holds no key that can decrypt the
traffic it forwards. The direct-path tunnel tests are unchanged, so the transport seam did not
disturb the fast path.

The config and auto path-selection (`config`, `engine::tunnel::connect`) are verified in unit
tests (JSON round-trip, secrets redacted in `Debug`, mirrored inner addresses, relay fallback
with a real handshake) and the `rwk selftest` `config` check. They are also verified from the
CLI: two `rwk keygen` configs cross-wired with each other's `rwk pubkey` and a shared relay,
with `rwk relay` running, bring both ends of `rwk connect` to "link established over Relay
path" — a real WireGuard handshake over the relay with no direct path, driven entirely by the
config files and the commands.

The session integration (`engine::session`) ties it together: `rwk connect` builds a `Session`
that drives the netstack over the tunnel and starts each enabled forward rule. Verified in
module tests (a TCP and a UDP forward rule each carry a flow end to end over a real relayed
tunnel; a malformed rule is skipped) and, crucially, **across two separate `rwk connect`
processes**: an application connecting to the Client's forward is echoed by a stand-in LAN
device the Station dials — `app → Client forward → relay tunnel → Station responder → LAN
echo → back`. The full session path is deliberately *not* a `selftest` check: standing up two
drivers, a relay and a forward at once is too timing-dependent for a fast deterministic smoke
test, so it is covered by the reliable module tests instead.

Not verified: no serial radio, no audio device and no second host were available, so
keying and sidetone are verified by recorded transitions and loopback sockets rather than on
hardware, and the port-forward path is verified across two processes on one host rather than
two machines on a real network. Not yet wired: the Station → Client forward direction, the
keying edge transport over the session's tunnel (CW still uses the plain `EdgeTransport`
socket), and the Tauri UI.
