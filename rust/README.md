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

```sh
cd rust
cargo build --release          # produces target/release/rwk.exe (one file, no sidecar)
cargo test                     # 146 unit tests
./target/release/rwk ports     # list serial ports
./target/release/rwk devices   # list audio output devices
./target/release/rwk selftest  # 10 checks: timing, protocol, audio, replay, driver, forwarding, tunnel
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

## What is not ported yet

* **The userspace netstack.** `engine::tunnel::WireGuardTunnel` carries *inner IP packets*,
  but nothing yet turns those into the UDP/TCP sockets the keying path and the forwarders
  want. That is the `smoltcp` increment, and it is what `EdgeTransport` must sit on; until it
  lands the tunnel is a verified transport rather than a socket provider. See
  [docs/NATIVE-NETWORK-SPIKE.md](docs/NATIVE-NETWORK-SPIKE.md).
* **DERP relay fallback.** `tsnet`'s NAT-traversal behaviour (a relay both ends can reach) is
  not implemented, so both ends of a link need a reachable endpoint. The next increment
  after the netstack.
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
| 6. Native encrypted transport, no Tailscale | ✅ boringtun userspace WireGuard: two static peers handshake and carry an encrypted inner packet with no control plane; DERP fallback and the netstack above it are not yet implemented |

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

Not verified: no serial radio, no audio device and no second host were available, so
keying, sidetone and port forwarding are verified by recorded transitions and loopback
sockets rather than on hardware. The tunnel's two peers also both live in one process, so
the handshake has never crossed a real network. The forwarders have not run across the
tunnel — they speak plain TCP/UDP until the `smoltcp` netstack lands.
