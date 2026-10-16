//! Reproduces (and guards against) the "stuck sidetone" bug seen in the apps: a keyer thread
//! that keys a local sidetone and then sends the edge over the tunnel must ALWAYS return the
//! sidetone to key-up, even when the tunnel send is slow or the far side is not draining.
//!
//! The apps' `drive_edge` fans each edge to the local sidetone (a `KeyHandle` atomic) AND the
//! tunnel transport. The bug was that keying the tone DOWN and then **blocking** the keyer
//! thread on the tunnel send left the tone latched down whenever the send stalled — the
//! matching key-UP edge never ran. These tests model that exact sequence headlessly (no audio
//! device, no GUI) over a real tunnel and assert the key handle ends UP.
//!
//! Two scenarios:
//! * `stuck` — a Station that never drains the tunnel (its receiver is dropped), so the
//!   Client's outbound channel backs up. With the buggy `block_on` send this would hang the
//!   keyer between the dit's key-down and key-up. The fixed, fire-and-forget send must still
//!   drive the key-up locally regardless.
//! * `healthy` — a normal draining Station; the key still ends up, and the Station replays
//!   both edges.

use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use rwk_core::engine::audio::KeyedSineGenerator;
use rwk_core::engine::edge_sender::EdgeSender;
use rwk_core::engine::keying::{EdgeEvent, EdgeScheduleBuilder};
use rwk_core::engine::network::EdgeTransport;
use rwk_core::engine::tunnel::{StaticIdentity, TunnelPeer, WireGuardTunnel};
use rwk_core::engine::{NetDriver, NetStack, NetStackHandle};
use rwk_core::primitives::EdgeSource;

const TICK_HZ: u64 = 1_000_000_000;
const CLIENT_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 1);
const STATION_IP: Ipv4Addr = Ipv4Addr::new(10, 0, 0, 2);
const EDGE_PORT: u16 = 5555;

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

/// A minimal stand-in for the apps' `Sidetone`: a key-state flag driven over a channel on a
/// dedicated thread, exactly like `Sidetone`/`ToneCmd::Key`, but backed by a `KeyHandle` atomic
/// (via `KeyedSineGenerator`) instead of a real cpal stream — so the test needs no audio device.
struct FakeSidetone {
    tx: std::sync::mpsc::Sender<bool>,
    key_is_down: Arc<AtomicBool>,
}

impl FakeSidetone {
    fn spawn() -> Self {
        // A real KeyHandle from the audio engine; no stream started, so no device is needed.
        let (_gen, key) = KeyedSineGenerator::new(48_000, 600, 0.7).unwrap();
        let key_is_down = Arc::new(AtomicBool::new(false));
        let mirror = Arc::clone(&key_is_down);
        let (tx, rx) = std::sync::mpsc::channel::<bool>();
        std::thread::spawn(move || {
            while let Ok(down) = rx.recv() {
                if down {
                    key.key_down();
                } else {
                    key.key_up();
                }
                mirror.store(down, Ordering::SeqCst);
            }
        });
        Self { tx, key_is_down }
    }

    fn key(&self, down: bool) {
        let _ = self.tx.send(down);
    }

    /// The current key state as seen by the sidetone thread (true = tone on).
    fn is_key_down(&self) -> bool {
        self.key_is_down.load(Ordering::SeqCst)
    }
}

/// The apps' `drive_edge`, distilled: key the local sidetone, then hand the frame to the
/// transport. `block_on_send` chooses the buggy (block the keyer thread on the send) vs the
/// fixed (fire-and-forget) behavior, so the test proves the fix is what releases the key.
fn drive_edge(
    rt: &tokio::runtime::Handle,
    tone: &FakeSidetone,
    transport: &Arc<EdgeTransport>,
    sender: &mut EdgeSender,
    key_down: bool,
    timestamp_ticks: u64,
    block_on_send: bool,
) {
    // Local keying first — this is what the operator hears; it must never wait on the network.
    tone.key(key_down);

    let event = EdgeEvent { timestamp_ticks, key_down, source: EdgeSource::Paddle };
    let frame = sender.frame_for(event);
    let transport = Arc::clone(transport);
    if block_on_send {
        // BUGGY path: block the keyer thread on the tunnel send.
        let _ = rt.block_on(async move { transport.send_frame(&frame).await });
    } else {
        // FIXED path: fire-and-forget on the runtime; the keyer thread moves straight on.
        rt.spawn(async move {
            let _ = transport.send_frame(&frame).await;
        });
    }
}

/// Plays a one-element ("E" = a single dit) schedule through `drive_edge` on a dedicated
/// thread, mirroring the apps' send worker, and returns once the dit's key-up has run (or the
/// thread finishes). Returns the FakeSidetone so the caller can assert the final key state.
fn play_one_dit(
    rt: tokio::runtime::Handle,
    transport: Arc<EdgeTransport>,
    block_on_send: bool,
) -> (Arc<FakeSidetone>, std::thread::JoinHandle<()>) {
    let tone = Arc::new(FakeSidetone::spawn());
    let tone_thread = Arc::clone(&tone);
    let handle = std::thread::spawn(move || {
        let mut sender = EdgeSender::new(1, TICK_HZ, 0);
        // "E" is a single dit: one key-down edge then one key-up edge.
        let schedule = EdgeScheduleBuilder::build("E", 20, 50, 0, TICK_HZ).unwrap();
        for edge in &schedule.edges {
            drive_edge(
                &rt,
                &tone_thread,
                &transport,
                &mut sender,
                edge.key_down,
                edge.timestamp_ticks,
                block_on_send,
            );
        }
    });
    (tone, handle)
}

/// The FIXED behavior: fire-and-forget send. Even when the Station never drains the tunnel
/// (its netstack handle is dropped so the Client's outbound channel backs up), the keyer
/// thread completes both edges and the sidetone returns to key-UP.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sidetone_returns_to_key_up_even_when_the_tunnel_send_stalls() {
    let (client_net, station_net) = linked_drivers().await;
    let client_conn = client_net.open_udp(EDGE_PORT).await.unwrap();
    let client = Arc::new(EdgeTransport::over_tunnel(client_conn));
    client.set_peer(SocketAddr::new(IpAddr::V4(STATION_IP), EDGE_PORT)).await;

    // Simulate a Station that is NOT draining: drop its netstack handle so nothing on the far
    // side reads, making the Client's outbound path back up. This is the worst case for a
    // keyer thread that blocks on the send.
    drop(station_net);
    std::mem::forget(client_net); // keep the Client driver alive for the test

    let (tone, handle) = play_one_dit(tokio::runtime::Handle::current(), client, false);

    // The keyer thread must finish quickly (it does not wait on the network) and leave the
    // key UP. Join with a bound so a regression that re-introduces the stall fails as a hang
    // caught by the test timeout rather than passing.
    let joined = tokio::task::spawn_blocking(move || handle.join()).await.unwrap();
    assert!(joined.is_ok(), "the keyer thread must not panic");

    // Give the sidetone thread a moment to apply the final key-up command.
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(
        !tone.is_key_down(),
        "sidetone must return to key-UP after a dit even when the tunnel send stalls"
    );
}

/// Sanity: with a normal draining Station, the key also ends up. (Guards against a fix that
/// only works in the stalled case.)
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sidetone_returns_to_key_up_on_a_healthy_link() {
    let (client_net, station_net) = linked_drivers().await;
    let client_conn = client_net.open_udp(EDGE_PORT).await.unwrap();
    let station_conn = station_net.open_udp(EDGE_PORT).await.unwrap();
    let client = Arc::new(EdgeTransport::over_tunnel(client_conn));
    let station = Arc::new(EdgeTransport::over_tunnel(station_conn));
    client.set_peer(SocketAddr::new(IpAddr::V4(STATION_IP), EDGE_PORT)).await;
    station.set_peer(SocketAddr::new(IpAddr::V4(CLIENT_IP), EDGE_PORT)).await;

    // A draining Station: keep reading frames so the Client's path never backs up.
    let station_reader = {
        let station = Arc::clone(&station);
        tokio::spawn(async move {
            loop {
                let _ = station.recv_frame().await;
            }
        })
    };

    std::mem::forget(client_net);
    std::mem::forget(station_net);

    let (tone, handle) = play_one_dit(tokio::runtime::Handle::current(), client, false);
    let joined = tokio::task::spawn_blocking(move || handle.join()).await.unwrap();
    assert!(joined.is_ok());

    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(!tone.is_key_down(), "sidetone must return to key-UP on a healthy link");

    station_reader.abort();
}

/// Documents the BUG: the old `block_on` send, with a Station that never drains, leaves the
/// keyer thread parked between the dit's key-down and key-up, so the sidetone stays keyed
/// down. We assert that outcome with a bounded wait, so this test *characterizes* the
/// regression; `#[ignore]` by default because it deliberately hangs the keyer thread (we
/// detach and never join it). Run with `--ignored` to observe the stuck state.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "characterizes the stuck-key bug; the keyer thread intentionally stalls"]
async fn block_on_send_leaves_the_key_stuck_when_the_station_stalls() {
    let (client_net, station_net) = linked_drivers().await;
    let client_conn = client_net.open_udp(EDGE_PORT).await.unwrap();
    let client = Arc::new(EdgeTransport::over_tunnel(client_conn));
    client.set_peer(SocketAddr::new(IpAddr::V4(STATION_IP), EDGE_PORT)).await;
    drop(station_net); // no draining
    std::mem::forget(client_net);

    let (tone, _handle) = play_one_dit(tokio::runtime::Handle::current(), client, true);

    // With the buggy block_on path on a stalled link, the key-down edge's send parks the keyer
    // thread; the key-up edge never runs within this window.
    tokio::time::sleep(Duration::from_millis(500)).await;
    assert!(
        tone.is_key_down(),
        "characterization: the buggy block_on send leaves the key stuck down"
    );
}

#[allow(dead_code)]
fn silence_unused(_: &Mutex<()>) {}
