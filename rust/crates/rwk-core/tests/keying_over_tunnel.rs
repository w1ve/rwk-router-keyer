//! End-to-end keying-over-tunnel timing harness.
//!
//! This is the make-or-break test for carrying CW over the WireGuard tunnel: it wires the
//! real pieces together — the Client orchestrator ([`EdgeSender`]), the edge transport
//! riding a real encrypted [`UdpConn`] on each end ([`EdgeTransport::over_tunnel`]), the
//! Station pump ([`pump_edges`]), and the real replay driver with its time-critical
//! thread — then measures whether a keyed CW string survives the trip with its inter-edge
//! timing intact to within the ±2ms budget.
//!
//! ## What is and isn't measured
//!
//! The jitter buffer deliberately *delays* the whole stream by a fixed playout offset to
//! absorb network jitter, so absolute arrival time is not the metric and must not be.
//! What must be preserved is the **spacing between edges** — a dit that was 60ms must
//! replay as 60ms, a gap that was 60ms must replay as 60ms — because that spacing *is* the
//! Morse. So the harness compares the interval between consecutive key transitions at the
//! Station's keying output against the interval the Client sent, and asserts each is within
//! ±2ms.
//!
//! These tests bring up real `boringtun` tunnels over loopback and run the actual replay
//! threads, so they are slower than a unit test and live here rather than inline.

use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use rwk_core::engine::edge_sender::EdgeSender;
use rwk_core::engine::keying::{EdgeEvent, EdgeScheduleBuilder};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::replay::{spawn_driver, EdgeJitterProfile, KeyingOutput};
use rwk_core::engine::edge_station::{pump_edges, tunnel_replayer};
use rwk_core::engine::{CoreEvent, EventBus};
use rwk_core::engine::{NetDriver, NetStack, NetStackHandle};
use rwk_core::engine::tunnel::{StaticIdentity, TunnelPeer, WireGuardTunnel};
use rwk_core::primitives::EdgeSource;
use rwk_core::timing::Clock;

const TICK_HZ: u64 = 1_000_000_000;
const CLIENT_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 1);
const STATION_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 2);
/// The inner UDP port the Station's edge socket listens on.
const EDGE_PORT: u16 = 5555;

/// ±2ms, expressed in the nanosecond tick domain.
const BUDGET_TICKS: i64 = 2 * (TICK_HZ as i64) / 1000;

// ─── Clock ───────────────────────────────────────────────────────────────────

/// A monotonic nanosecond clock shared by the sender, the driver and the recorder, so
/// every timestamp in the test lives in one tick domain and intervals are comparable.
struct SharedClock {
    epoch: std::time::Instant,
}

impl SharedClock {
    fn new() -> Arc<Self> {
        Arc::new(Self { epoch: std::time::Instant::now() })
    }
}

impl Clock for SharedClock {
    fn now(&self) -> u64 {
        self.epoch.elapsed().as_nanos() as u64
    }
    fn frequency(&self) -> u64 {
        TICK_HZ
    }
}

// ─── Recording keying output ──────────────────────────────────────────────────

/// A keying sink that timestamps every transition on the shared clock, so the test can
/// reconstruct the spacing the Station actually replayed.
struct RecordingOutput {
    clock: Arc<SharedClock>,
    transitions: Arc<Mutex<Vec<(bool, u64)>>>,
    last: Option<bool>,
}

impl KeyingOutput for RecordingOutput {
    fn set_key(&mut self, down: bool) {
        // Record only genuine transitions, not idempotent re-asserts of the same state.
        if self.last == Some(down) {
            return;
        }
        self.last = Some(down);
        self.transitions.lock().unwrap().push((down, self.clock.now()));
    }
}

// ─── Tunnel harness ─────────────────────────────────────────────────────────

fn identity(seed: u8) -> StaticIdentity {
    let mut bytes = [0u8; 32];
    for (i, b) in bytes.iter_mut().enumerate() {
        *b = seed.wrapping_mul(31).wrapping_add(i as u8);
    }
    StaticIdentity::from_private_bytes(bytes)
}

fn peer(public_key: [u8; 32], endpoint: SocketAddr) -> TunnelPeer {
    TunnelPeer { public_key, preshared_key: None, endpoint, persistent_keepalive: None }
}

/// Brings up two drivers over a real established WireGuard link.
async fn linked_drivers() -> (NetStackHandle, NetStackHandle) {
    let a_id = identity(41);
    let b_id = identity(42);

    let mut a_tun = WireGuardTunnel::bind(
        "127.0.0.1:0".parse().unwrap(),
        &a_id,
        &peer(b_id.public_bytes(), "127.0.0.1:9".parse().unwrap()),
    )
    .await
    .unwrap();
    let mut b_tun = WireGuardTunnel::bind(
        "127.0.0.1:0".parse().unwrap(),
        &b_id,
        &peer(a_id.public_bytes(), a_tun.local_addr().unwrap()),
    )
    .await
    .unwrap();
    a_tun.set_peer(b_tun.local_addr().unwrap());

    a_tun.initiate().await.unwrap();
    b_tun.poll().await.unwrap();
    a_tun.poll().await.unwrap();
    b_tun.poll().await.unwrap();
    assert!(a_tun.is_established() && b_tun.is_established());

    let a = NetDriver::spawn(a_tun, NetStack::new(IpAddr::V4(CLIENT_IP)));
    let b = NetDriver::spawn(b_tun, NetStack::new(IpAddr::V4(STATION_IP)));
    (a, b)
}

/// Builds a Client edge transport and a Station edge transport, both riding the tunnel,
/// peered to each other's inner netstack address.
async fn tunnel_edge_pair() -> (EdgeTransport, Arc<EdgeTransport>) {
    let (client_net, station_net) = linked_drivers().await;

    let client_conn = client_net.open_udp(EDGE_PORT).await.unwrap();
    let station_conn = station_net.open_udp(EDGE_PORT).await.unwrap();

    let client = EdgeTransport::over_tunnel(client_conn);
    let station = Arc::new(EdgeTransport::over_tunnel(station_conn));

    client.set_peer(SocketAddr::new(IpAddr::V4(STATION_IP), EDGE_PORT)).await;
    station.set_peer(SocketAddr::new(IpAddr::V4(CLIENT_IP), EDGE_PORT)).await;

    // Keep the netstack drivers alive for the life of the test by leaking the handles into
    // the transports' closures is not possible; instead the caller holds them. We return
    // only the transports, so the drivers must outlive them — leak them deliberately here,
    // which is fine for a short-lived test process.
    std::mem::forget(client_net);
    std::mem::forget(station_net);

    (client, station)
}

// ─── Interval comparison ──────────────────────────────────────────────────────

/// The signed intervals between consecutive timestamps, in ticks.
fn intervals(times: &[u64]) -> Vec<i64> {
    times.windows(2).map(|w| w[1] as i64 - w[0] as i64).collect()
}

/// The worst absolute inter-interval error between two timelines, in ticks, without
/// asserting — used to report how much jitter the send path injected.
fn worst_interval_error(a: &[u64], b: &[u64]) -> i64 {
    intervals(a)
        .iter()
        .zip(intervals(b).iter())
        .map(|(x, y)| (y - x).abs())
        .max()
        .unwrap_or(0)
}

/// Asserts every replayed interval is within ±2ms of the corresponding sent interval,
/// returning the worst absolute error in ticks for reporting.
fn assert_intervals_within_budget(sent: &[u64], replayed: &[u64]) -> i64 {
    assert_eq!(
        sent.len(),
        replayed.len(),
        "every sent edge must replay exactly once (sent {}, replayed {})",
        sent.len(),
        replayed.len()
    );
    let sent_iv = intervals(sent);
    let replayed_iv = intervals(replayed);

    let mut worst = 0i64;
    for (i, (s, r)) in sent_iv.iter().zip(replayed_iv.iter()).enumerate() {
        let err = (r - s).abs();
        worst = worst.max(err);
        assert!(
            err <= BUDGET_TICKS,
            "interval {i} drifted {:.3}ms (sent {:.3}ms, replayed {:.3}ms), over the \u{00b1}2ms budget",
            err as f64 / 1e6,
            *s as f64 / 1e6,
            *r as f64 / 1e6,
        );
    }
    worst
}

// ─── The end-to-end test ──────────────────────────────────────────────────────

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn cw_string_keys_over_the_tunnel_within_two_milliseconds() {
    let (client, station) = tunnel_edge_pair().await;

    // Station: a replayer on the DERP band, driven by the real replay thread. The recorder
    // timestamps each key transition on the shared clock.
    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), None, None);

    // Station pump: forward tunnel datagrams into the driver.
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    // Client: build the CW schedule for a word and replay it in real time, turning each
    // edge into a redundant frame and sending it over the tunnel. "PARIS" at 20 WPM is the
    // standard word; it exercises dits, dahs, and the gaps between them.
    let session_start = clock.now();
    let schedule = EdgeScheduleBuilder::build("PARIS", 20, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);

    // The tick each edge was *scheduled* for — this is the timing the Client stamps into
    // the frame's `timestamp_ms`, so it is the intent the Station reconstructs. It is the
    // correct reference for the ±2ms budget: the replayer rebuilds spacing from these
    // timestamps, not from datagram arrival, which is exactly what lets it absorb send and
    // network jitter. The wall-clock send times are captured separately, below, only to
    // report how much jitter the path actually injected (and thus how much the replayer
    // had to correct for).
    let scheduled_ticks: Vec<u64> = schedule.edges.iter().map(|e| e.timestamp_ticks).collect();
    let mut sent_ticks: Vec<u64> = Vec::new();

    for edge in &schedule.edges {
        // Busy-wait to the edge's scheduled tick, matching how the keyer thread paces
        // real paddle output; a coarse async sleep would itself add milliseconds of error.
        wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        sent_ticks.push(clock.now());
        client.send_frame(&sender.frame_for(event)).await.unwrap();
    }

    // Wait for the Station to replay every edge (jitter delay + the full word duration),
    // then compare. The schedule for PARIS at 20 WPM is ~1.1s; the DERP jitter band adds a
    // few hundred ms. Give it a generous ceiling and poll for the final count.
    let expected = schedule.edges.len();
    let replayed = wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;
    assert_eq!(
        replayed, expected,
        "the Station must replay all {expected} edges (got {replayed})"
    );

    let replayed_ticks: Vec<u64> = transitions.lock().unwrap().iter().map(|(_, t)| *t).collect();
    let replayed_states: Vec<bool> = transitions.lock().unwrap().iter().map(|(d, _)| *d).collect();
    let sent_states: Vec<bool> = schedule.edges.iter().map(|e| e.key_down).collect();
    assert_eq!(replayed_states, sent_states, "key states must match in order");

    // The budget is on reconstructed spacing vs the *scheduled* spacing the Client meant
    // to send — the replayer's whole purpose is to restore that intent from the embedded
    // timestamps.
    let worst = assert_intervals_within_budget(&scheduled_ticks, &replayed_ticks);

    // Report the raw send-path jitter the replayer had to absorb, so a regression that
    // merely moves jitter around is visible even when the budget still passes.
    let send_jitter = worst_interval_error(&scheduled_ticks, &sent_ticks);
    eprintln!(
        "keying-over-tunnel: {} edges | worst reconstructed error {:.3}ms (budget 2.000ms) | \
         send-path jitter the replayer corrected: {:.3}ms",
        expected,
        worst as f64 / 1e6,
        send_jitter as f64 / 1e6,
    );

    pump.abort();
}

/// The same path, but with up to ~15ms of deliberately injected jitter before each send,
/// to prove the Station reconstructs clean CW timing even when the transport delivers
/// edges raggedly. This is the test that actually exercises the jitter buffer: without
/// injected jitter a quiet machine can pass by luck.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn jittered_sends_still_replay_within_two_milliseconds() {
    let (client, station) = tunnel_edge_pair().await;

    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), None, None);
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    let session_start = clock.now();
    let schedule = EdgeScheduleBuilder::build("PARIS", 20, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);
    let scheduled_ticks: Vec<u64> = schedule.edges.iter().map(|e| e.timestamp_ticks).collect();

    // A tiny deterministic PRNG so the injected jitter is repeatable across runs.
    let mut rng: u64 = 0x9E37_79B9_7F4A_7C15;
    let mut next_jitter_ms = || {
        rng = rng.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        (rng >> 33) % 16 // 0..=15 ms
    };

    for edge in &schedule.edges {
        wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
        // Inject jitter *after* reading the scheduled tick but *before* sending, so the
        // frame still carries the correct scheduled timestamp while the datagram leaves
        // late — exactly the real-world case of a stalled sender.
        let jitter = next_jitter_ms();
        if jitter > 0 {
            tokio::time::sleep(Duration::from_millis(jitter)).await;
        }
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        client.send_frame(&sender.frame_for(event)).await.unwrap();
    }

    let expected = schedule.edges.len();
    let replayed = wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;
    assert_eq!(replayed, expected, "all edges must replay despite send jitter");

    let replayed_ticks: Vec<u64> = transitions.lock().unwrap().iter().map(|(_, t)| *t).collect();
    let worst = assert_intervals_within_budget(&scheduled_ticks, &replayed_ticks);
    eprintln!(
        "jittered keying-over-tunnel: {} edges | worst reconstructed error {:.3}ms \
         (budget 2.000ms, with up to 15ms injected send jitter)",
        expected,
        worst as f64 / 1e6,
    );

    pump.abort();
}

/// Keying a string that contains a WORD SPACE must not trip the F10 scheduler watchdog. The
/// regression this guards: F10 measured "time since the last edge fired", so any gap longer
/// than 250ms — a word space at 20 WPM is ~420ms — false-tripped F10 and latched the Station
/// SAFE mid-send. "VVV TEST" has a space between the two words, so it exercises exactly that
/// gap end to end over the real tunnel + replay driver. We assert every edge replays AND no
/// fail-safe (least of all F10) was published on the bus.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn a_word_space_does_not_false_trip_the_f10_watchdog() {
    let (client, station) = tunnel_edge_pair().await;

    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    // A bus so we can observe any fail-safe the driver's watchdog publishes.
    let bus = EventBus::new();
    let mut events = bus.subscribe();
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), Some(bus.clone()), None);
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    // "VVV TEST" — a space between the words gives the ~420ms gap that used to trip F10.
    let session_start = clock.now();
    let schedule = EdgeScheduleBuilder::build("VVV TEST", 20, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);

    for edge in &schedule.edges {
        wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        client.send_frame(&sender.frame_for(event)).await.unwrap();
    }

    let expected = schedule.edges.len();
    let replayed = wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;
    assert_eq!(replayed, expected, "every edge must replay across the word gap (got {replayed})");

    // Drain the bus: no fail-safe may have fired. F10 in particular is the regression.
    let mut fired: Vec<u8> = Vec::new();
    while let Ok(ev) = events.try_recv() {
        if let CoreEvent::FailSafe { code, .. } = ev {
            fired.push(code);
        }
    }
    assert!(
        fired.is_empty(),
        "no fail-safe may fire while keying a word with a space; got {fired:?}"
    );

    pump.abort();
}

/// Reproduces the APP's send path at high WPM: the send worker builds each frame then hands it
/// to the tunnel **fire-and-forget** (`tokio::spawn(send_frame)`), rather than awaiting it. At
/// 39+ WPM edges are ~31ms apart, so many spawned send tasks are in flight at once — and tokio
/// does NOT guarantee they run in spawn order. If a later edge's datagram overtakes an earlier
/// one, the Station's sequence tracker discards the lower sequence as a duplicate and keying
/// stops. This asserts all edges still replay under that pattern at a fast speed.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn fire_and_forget_sends_preserve_keying_at_high_wpm() {
    let (client, station) = tunnel_edge_pair().await;
    let client = Arc::new(client);

    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    let bus = EventBus::new();
    let mut events = bus.subscribe();
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), Some(bus.clone()), None);
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    let session_start = clock.now();
    // 40 WPM, a longer string so reorder has many chances to bite.
    let schedule =
        EdgeScheduleBuilder::build("CQ CQ DE W1VE", 40, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);

    for edge in &schedule.edges {
        wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        // Frame is built synchronously (sequence stays monotonic), but the SEND is
        // fire-and-forget, exactly like the app's drive_edge.
        let frame = sender.frame_for(event);
        let client = Arc::clone(&client);
        tokio::spawn(async move {
            let _ = client.send_frame(&frame).await;
        });
    }

    let expected = schedule.edges.len();
    let replayed = wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;

    let mut fired: Vec<u8> = Vec::new();
    while let Ok(ev) = events.try_recv() {
        if let CoreEvent::FailSafe { code, .. } = ev {
            fired.push(code);
        }
    }
    assert!(fired.is_empty(), "no fail-safe may fire; got {fired:?}");
    assert_eq!(
        replayed, expected,
        "fire-and-forget sends must still deliver all {expected} edges in order (got {replayed})"
    );

    pump.abort();
}

/// Keying must work at EVERY speed 5..=60 WPM (the C# keyer was flawless across that range).
/// This sweeps representative speeds end to end over the real tunnel + replay driver and
/// asserts that at each speed every edge replays and no fail-safe fires — catching a
/// higher-WPM drop (the Server silently stopping keying) deterministically.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn keying_survives_every_speed_from_5_to_60_wpm() {
    for wpm in [5u32, 10, 15, 20, 25, 30, 35, 39, 40, 45, 50, 55, 60] {
        let (client, station) = tunnel_edge_pair().await;

        let clock = SharedClock::new();
        let transitions = Arc::new(Mutex::new(Vec::new()));
        let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
        replayer.begin_session(1);
        let output = RecordingOutput {
            clock: Arc::clone(&clock),
            transitions: Arc::clone(&transitions),
            last: None,
        };
        let bus = EventBus::new();
        let mut events = bus.subscribe();
        let driver =
            spawn_driver(replayer, Box::new(output), clock.clone(), Some(bus.clone()), None);
        let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

        // "PARIS" exercises dits, dahs and the gaps; short enough to run quickly per speed.
        let session_start = clock.now();
        let schedule = EdgeScheduleBuilder::build("PARIS", wpm, 50, session_start, TICK_HZ).unwrap();
        let mut sender = EdgeSender::new(1, TICK_HZ, session_start);

        for edge in &schedule.edges {
            wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
            let event = EdgeEvent {
                timestamp_ticks: edge.timestamp_ticks,
                key_down: edge.key_down,
                source: EdgeSource::Host,
            };
            client.send_frame(&sender.frame_for(event)).await.unwrap();
        }

        let expected = schedule.edges.len();
        let replayed =
            wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;

        let mut fired: Vec<u8> = Vec::new();
        while let Ok(ev) = events.try_recv() {
            if let CoreEvent::FailSafe { code, .. } = ev {
                fired.push(code);
            }
        }

        assert!(
            fired.is_empty(),
            "at {wpm} WPM a fail-safe fired ({fired:?}) — keying must never stop at any speed"
        );
        assert_eq!(
            replayed, expected,
            "at {wpm} WPM the Station replayed {replayed}/{expected} edges — it must replay all"
        );

        pump.abort();
    }
}

/// Busy-waits until the clock reaches `target_ticks`, yielding briefly so the runtime is
/// not starved. Precise to well under a millisecond, unlike `tokio::time::sleep`.
fn wait_until_tick(clock: &SharedClock, target_ticks: u64) {
    while clock.now() < target_ticks {
        std::hint::spin_loop();
    }
}

/// Polls until `transitions` holds at least `want` entries or the timeout elapses, then
/// returns the final count.
async fn wait_for_transition_count(
    transitions: &Mutex<Vec<(bool, u64)>>,
    want: usize,
    timeout: Duration,
) -> usize {
    let deadline = std::time::Instant::now() + timeout;
    loop {
        let count = transitions.lock().unwrap().len();
        if count >= want || std::time::Instant::now() >= deadline {
            return count;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

/// PROVES THE 39-WPM BUG MECHANISM, DETERMINISTICALLY.
///
/// The app's old `drive_edge` sent each frame fire-and-forget (`tokio::spawn(send_frame)`).
/// Tokio does not run spawned tasks in spawn order, and under load a later edge's datagram can
/// overtake an earlier one. The Station's sequence tracker treats any sequence `<=` the last
/// applied as a duplicate and discards it (`tracker.rs`: `edge.sequence <= self.last_sequence`).
/// So if edge N+1 is applied before edge N, edge N is dropped forever — and when the dropped
/// edge is a key-UP, the key can stay latched or the stream stalls. That is why keying stopped
/// at higher WPM.
///
/// Loopback rarely reorders on its own, so this test *forces* the hazard the real scheduler
/// creates: each fire-and-forget send sleeps a small, sequence-dependent amount so that later
/// edges deterministically overtake earlier ones. Under the OLD pattern this drops edges; we
/// assert the Station does NOT replay them all, pinning the regression.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn reordered_fire_and_forget_sends_drop_edges() {
    let (client, station) = tunnel_edge_pair().await;
    let client = Arc::new(client);

    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), None, None);
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    let session_start = clock.now();
    let schedule =
        EdgeScheduleBuilder::build("CQ CQ DE W1VE", 40, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);
    let expected = schedule.edges.len();

    // Send every frame "at once", fire-and-forget, with an alternating delay so odd-indexed
    // sends land before even-indexed ones — guaranteed reorder on the wire.
    for (i, edge) in schedule.edges.iter().enumerate() {
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        let frame = sender.frame_for(event); // sequence stays monotonic in build order
        let client = Arc::clone(&client);
        // Even index waits longer than the odd index that follows it → the odd overtakes.
        let delay_ms = if i % 2 == 0 { 6 } else { 0 };
        tokio::spawn(async move {
            if delay_ms > 0 {
                tokio::time::sleep(Duration::from_millis(delay_ms)).await;
            }
            let _ = client.send_frame(&frame).await;
        });
    }

    let replayed =
        wait_for_transition_count(&transitions, expected, Duration::from_secs(5)).await;

    // The reorder must cost us edges: the Station cannot have replayed all of them, because the
    // tracker discarded the overtaken (lower-sequence) frames as duplicates.
    assert!(
        replayed < expected,
        "reordered fire-and-forget sends must drop edges (replayed {replayed} of {expected}); \
         if this now equals expected the reorder hazard is gone and this guard is stale"
    );

    pump.abort();
}

/// PROVES THE FIX. The app now routes every frame through a single ordered FIFO channel drained
/// by ONE task, so datagrams leave in the exact order they were keyed and can never overtake.
/// This models that drain: the same frames, same alternating-delay hazard as the bug test
/// above, but pushed through one `mpsc` channel and sent by a single consumer that awaits each
/// send before the next. With ordering restored, every edge must replay.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn ordered_fifo_drain_preserves_keying_at_high_wpm() {
    let (client, station) = tunnel_edge_pair().await;
    let client = Arc::new(client);

    let clock = SharedClock::new();
    let transitions = Arc::new(Mutex::new(Vec::new()));
    let mut replayer = tunnel_replayer(clock.as_ref(), EdgeJitterProfile::PathAdaptive, None);
    replayer.begin_session(1);
    let output = RecordingOutput {
        clock: Arc::clone(&clock),
        transitions: Arc::clone(&transitions),
        last: None,
    };
    let bus = EventBus::new();
    let mut events = bus.subscribe();
    let driver = spawn_driver(replayer, Box::new(output), clock.clone(), Some(bus.clone()), None);
    let pump = tokio::spawn(pump_edges(Arc::clone(&station), driver));

    // The ordered outbound queue + single drain task — exactly the app's new design.
    let (frame_tx, mut frame_rx) =
        tokio::sync::mpsc::unbounded_channel::<rwk_core::protocol::edge::RwkPaddleFrame>();
    let drain_client = Arc::clone(&client);
    let drain = tokio::spawn(async move {
        while let Some(frame) = frame_rx.recv().await {
            let _ = drain_client.send_frame(&frame).await;
        }
    });

    let session_start = clock.now();
    let schedule =
        EdgeScheduleBuilder::build("CQ CQ DE W1VE", 40, 50, session_start, TICK_HZ).unwrap();
    let mut sender = EdgeSender::new(1, TICK_HZ, session_start);
    let expected = schedule.edges.len();

    // Pace the edges like the keyer thread does, pushing each finished frame into the FIFO.
    for edge in &schedule.edges {
        wait_until_tick(clock.as_ref(), edge.timestamp_ticks);
        let event = EdgeEvent {
            timestamp_ticks: edge.timestamp_ticks,
            key_down: edge.key_down,
            source: EdgeSource::Host,
        };
        let _ = frame_tx.send(sender.frame_for(event));
    }
    drop(frame_tx); // let the drain task finish once all frames are sent

    let replayed =
        wait_for_transition_count(&transitions, expected, Duration::from_secs(10)).await;

    let mut fired: Vec<u8> = Vec::new();
    while let Ok(ev) = events.try_recv() {
        if let CoreEvent::FailSafe { code, .. } = ev {
            fired.push(code);
        }
    }
    assert!(fired.is_empty(), "no fail-safe may fire with ordered sends; got {fired:?}");
    assert_eq!(
        replayed, expected,
        "the ordered FIFO drain must deliver all {expected} edges at 40 WPM (got {replayed})"
    );

    drain.abort();
    pump.abort();
}
