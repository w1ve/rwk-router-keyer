//! The relay server: forwards WireGuard datagrams between nodes by public key.
//!
//! A node connects over TCP and sends a [`Frame::Register`] naming its public key. The server
//! records a channel to that connection in a shared table. When any node sends a
//! [`Frame::Send`] addressed to a destination key, the server looks the destination up and
//! writes it a [`Frame::Recv`] tagged with the sender's key. When a connection drops, its key
//! is removed so a stale route cannot deliver to a dead socket.
//!
//! The server holds no cryptographic state: the payloads are WireGuard transport messages it
//! cannot read, so it is a pure forwarder. That is what lets the relay be operated by anyone
//! (including the operator themselves on a cheap VPS) without trusting it with keying traffic.
//!
//! ## Metrics and caps
//!
//! A hosted relay is a shared resource with a bandwidth budget, so it carries two extra
//! concerns the embedded path does not need but does not pay for either:
//!
//! * [`RelayMetrics`] — atomics the standalone server surfaces on its dashboard: how many
//!   nodes are connected, how many it has served, bytes relayed, and why connections were
//!   refused. Cloning is cheap (`Arc`), so the HTTP layer reads the same counters the relay
//!   writes.
//! * [`RelayLimits`] — a cap on concurrent registrations and a per-connection byte budget, so
//!   one busy pair (say, forwarding FlexRadio audio through the relay) cannot exhaust the
//!   operator's monthly transfer, and a flood of registrations cannot exhaust memory.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{mpsc, Mutex};

use super::frame::{parse_header, Frame, HEADER_LEN, KEY_LEN};
use crate::Result;

/// Depth of a connection's outbound frame queue.
///
/// Each queued item is one datagram to deliver. A bound applies backpressure to a flood aimed
/// at one slow recipient instead of letting the server buffer without limit; a relay is a
/// shared resource, so an unbounded queue would be a denial-of-service foothold.
const OUTBOUND_DEPTH: usize = 1024;

/// How often the per-connection relayed-byte budget refills, and the window it is measured
/// over. A one-second window turns the configured cap into a plain bytes-per-second rate.
const RATE_WINDOW: Duration = Duration::from_secs(1);

/// Operator-tunable limits that protect a hosted relay.
///
/// Defaults are sized for a modest VPS (a few mbit per pair, a couple thousand pairs); the
/// standalone server overrides them from its environment.
#[derive(Debug, Clone, Copy)]
pub struct RelayLimits {
    /// Largest number of nodes that may be registered at once. A new registration past this
    /// is refused (counted as [`RelayMetrics::rejected_full`]).
    pub max_registrations: usize,
    /// Per-connection relay budget in bytes per [`RATE_WINDOW`] (i.e. bytes/second). A sender
    /// that exceeds it has its excess datagrams dropped for the rest of the window, so one
    /// heavy pair cannot eat the whole transfer allowance. Zero disables the cap.
    pub per_conn_bytes_per_sec: u64,
}

impl Default for RelayLimits {
    fn default() -> Self {
        Self {
            max_registrations: 2000,
            // 4 Mbit/s ≈ 500 KB/s: comfortably above CW + control + Opus, below a flat-out
            // FlexRadio stream, so audio-over-relay is throttled rather than unbounded.
            per_conn_bytes_per_sec: 500_000,
        }
    }
}

impl RelayLimits {
    /// Limits with every cap disabled — the embedded relay, where the two ends are the only
    /// users and there is no operator budget to protect.
    #[must_use]
    pub fn unlimited() -> Self {
        Self { max_registrations: usize::MAX, per_conn_bytes_per_sec: 0 }
    }
}

/// Live counters for a relay, read by the dashboard and written by the serving tasks.
///
/// Every field is an atomic so the HTTP layer can snapshot it without locking the relay. The
/// standalone server holds an [`Arc`] and shares clones with its serving tasks.
#[derive(Debug, Default)]
pub struct RelayMetrics {
    active: AtomicUsize,
    total_connections: AtomicU64,
    total_pairs_served: AtomicU64,
    bytes_relayed: AtomicU64,
    frames_relayed: AtomicU64,
    frames_dropped_no_route: AtomicU64,
    frames_dropped_rate: AtomicU64,
    rejected_full: AtomicU64,
    rejected_bad_register: AtomicU64,
}

impl RelayMetrics {
    /// Nodes currently registered.
    #[must_use]
    pub fn active(&self) -> usize {
        self.active.load(Ordering::Relaxed)
    }
    /// Connections accepted and registered since start.
    #[must_use]
    pub fn total_connections(&self) -> u64 {
        self.total_connections.load(Ordering::Relaxed)
    }
    /// Times a datagram was delivered to a *newly* seen destination pairing — a rough count of
    /// distinct conversations the relay has carried.
    #[must_use]
    pub fn total_pairs_served(&self) -> u64 {
        self.total_pairs_served.load(Ordering::Relaxed)
    }
    /// Payload bytes relayed (sum of forwarded datagram payloads).
    #[must_use]
    pub fn bytes_relayed(&self) -> u64 {
        self.bytes_relayed.load(Ordering::Relaxed)
    }
    /// Datagrams relayed.
    #[must_use]
    pub fn frames_relayed(&self) -> u64 {
        self.frames_relayed.load(Ordering::Relaxed)
    }
    /// Datagrams dropped because the destination was not registered.
    #[must_use]
    pub fn frames_dropped_no_route(&self) -> u64 {
        self.frames_dropped_no_route.load(Ordering::Relaxed)
    }
    /// Datagrams dropped because the sender exceeded its rate cap.
    #[must_use]
    pub fn frames_dropped_rate(&self) -> u64 {
        self.frames_dropped_rate.load(Ordering::Relaxed)
    }
    /// Registrations refused because the relay was at its registration cap.
    #[must_use]
    pub fn rejected_full(&self) -> u64 {
        self.rejected_full.load(Ordering::Relaxed)
    }
    /// Connections closed because their first frame was not a valid registration.
    #[must_use]
    pub fn rejected_bad_register(&self) -> u64 {
        self.rejected_bad_register.load(Ordering::Relaxed)
    }
}

/// The shared routing table: public key → a sender into that connection's writer task.
type Routes = Arc<Mutex<HashMap<[u8; KEY_LEN], mpsc::Sender<Frame>>>>;

/// How long a node may wait for its pairing partner before its entry is considered stale and
/// a new `Pair` on the same code replaces it. Prevents an abandoned half-pairing from pinning
/// a code forever.
const PAIRING_TTL: Duration = Duration::from_secs(120);

/// A node waiting to be matched on a pairing code.
struct PairWaiter {
    /// The waiter's public key, handed to its partner.
    key: [u8; KEY_LEN],
    /// The address the relay will tell the partner to punch to: observed public IP + the
    /// waiter's reported UDP port.
    observed: Option<SocketAddr>,
    /// A sender into the waiter's writer task, to deliver its `Paired` frame.
    out: mpsc::Sender<Frame>,
    /// When the waiter registered, for TTL eviction.
    since: Instant,
}

/// The shared pairing table: pairing code → the first node waiting on it.
type Pairings = Arc<Mutex<HashMap<String, PairWaiter>>>;

/// A running relay server.
///
/// Construct with [`RelayServer::bind`] (unlimited, for the embedded path and tests) or
/// [`RelayServer::bind_with`] (operator limits, for the hosted server), then
/// [`run`](RelayServer::run) it. Hold [`metrics`](RelayServer::metrics) to observe it live.
pub struct RelayServer {
    listener: TcpListener,
    routes: Routes,
    pairings: Pairings,
    limits: RelayLimits,
    metrics: Arc<RelayMetrics>,
}

impl RelayServer {
    /// Binds the relay to `addr` with no caps (use port 0 for an ephemeral port in tests).
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the listener cannot be bound.
    pub async fn bind(addr: SocketAddr) -> Result<Self> {
        Self::bind_with(addr, RelayLimits::unlimited()).await
    }

    /// Binds the relay to `addr` with operator `limits`.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the listener cannot be bound.
    pub async fn bind_with(addr: SocketAddr, limits: RelayLimits) -> Result<Self> {
        let listener = TcpListener::bind(addr).await?;
        Ok(Self {
            listener,
            routes: Arc::new(Mutex::new(HashMap::new())),
            pairings: Arc::new(Mutex::new(HashMap::new())),
            limits,
            metrics: Arc::new(RelayMetrics::default()),
        })
    }

    /// The address the relay is listening on.
    ///
    /// # Errors
    ///
    /// Propagates `std::io::Error` if the local address cannot be read.
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.listener.local_addr()?)
    }

    /// The live metrics, so a dashboard (or a test) can observe the relay without stopping it.
    #[must_use]
    pub fn metrics(&self) -> Arc<RelayMetrics> {
        Arc::clone(&self.metrics)
    }

    /// Accepts and services connections forever.
    ///
    /// Each connection runs on its own task. A transient `accept` failure must not stop the
    /// relay serving everyone else, so it is ignored and the loop continues.
    ///
    /// # Errors
    ///
    /// Runs until the task is cancelled; returns `Ok` only if the listener is closed.
    pub async fn run(self) -> Result<()> {
        loop {
            match self.listener.accept().await {
                Ok((stream, peer)) => {
                    let routes = Arc::clone(&self.routes);
                    let pairings = Arc::clone(&self.pairings);
                    let metrics = Arc::clone(&self.metrics);
                    let limits = self.limits;
                    tokio::spawn(async move {
                        let _ = serve_connection(stream, peer, routes, pairings, metrics, limits).await;
                    });
                }
                Err(_) => continue,
            }
        }
    }
}

/// Serves one connection: reads its registration, then shuttles frames until it closes.
async fn serve_connection(
    stream: TcpStream,
    peer: SocketAddr,
    routes: Routes,
    pairings: Pairings,
    metrics: Arc<RelayMetrics>,
    limits: RelayLimits,
) -> Result<()> {
    let _ = stream.set_nodelay(true);
    let (mut read_half, mut write_half) = stream.into_split();

    // A writer task owns the write half, draining an outbound queue. Decoupling the writer
    // from the reader means a sender is never blocked by a slow recipient's socket — the
    // bounded queue absorbs a burst and applies backpressure past it.
    let (out_tx, mut out_rx) = mpsc::channel::<Frame>(OUTBOUND_DEPTH);
    let writer = tokio::spawn(async move {
        while let Some(frame) = out_rx.recv().await {
            if write_half.write_all(&frame.encode()).await.is_err() {
                break;
            }
        }
    });

    // The connection's first frame selects its mode:
    //   * Register → a data-relay connection (registers a route, then relays datagrams).
    //   * Pair     → a signalling connection (rendezvous, then the relay closes it).
    // Anything else is a protocol violation and the connection is dropped.
    let first = read_frame(&mut read_half).await?;
    let key = match first {
        Some(Frame::Register { key }) => key,
        Some(Frame::Pair { key, udp_port, code }) => {
            // Signalling-only: match this node and deliver its Paired reply, then stop. The
            // short-lived connection is not registered for relaying.
            handle_pair(&code, key, peer.ip(), udp_port, &out_tx, &pairings).await;
            // Keep the connection open briefly so the writer can flush the Paired frame, then
            // drain until the client closes it (the client closes after receiving Paired).
            let result = drain_until_closed(&mut read_half).await;
            writer.abort();
            return result;
        }
        _ => {
            metrics.rejected_bad_register.fetch_add(1, Ordering::Relaxed);
            writer.abort();
            return Ok(()); // not a valid opening frame; nothing to do.
        }
    };

    // Register, honouring the cap. A relay at capacity refuses the newcomer rather than
    // evicting an existing pair; the existing conversation keeps working.
    {
        let mut table = routes.lock().await;
        if table.len() >= limits.max_registrations && !table.contains_key(&key) {
            metrics.rejected_full.fetch_add(1, Ordering::Relaxed);
            writer.abort();
            return Ok(());
        }
        table.insert(key, out_tx.clone());
    }
    metrics.active.fetch_add(1, Ordering::Relaxed);
    metrics.total_connections.fetch_add(1, Ordering::Relaxed);

    // Reader loop: forward each Send to its destination, handle Pair rendezvous, else ignore.
    let result = reader_loop(
        &mut read_half,
        key,
        peer.ip(),
        &out_tx,
        &routes,
        &pairings,
        &metrics,
        &limits,
    )
    .await;

    // Clean up this node's route so a later Send to it is dropped, not delivered to a dead
    // connection. Remove only if the table still points at *our* sender: a reconnect under
    // the same key may have replaced it, and we must not evict the newer connection.
    {
        let mut table = routes.lock().await;
        let ours = table.get(&key).is_some_and(|s| s.same_channel(&out_tx));
        if ours {
            table.remove(&key);
        }
    }
    metrics.active.fetch_sub(1, Ordering::Relaxed);
    writer.abort();
    result
}

/// Reads and routes frames for a registered connection until it closes or errors.
#[allow(clippy::too_many_arguments)]
async fn reader_loop<R>(
    read_half: &mut R,
    src: [u8; KEY_LEN],
    observed_ip: std::net::IpAddr,
    out: &mpsc::Sender<Frame>,
    routes: &Routes,
    pairings: &Pairings,
    metrics: &RelayMetrics,
    limits: &RelayLimits,
) -> Result<()>
where
    R: AsyncReadExt + Unpin,
{
    let mut budget = RateBudget::new(limits.per_conn_bytes_per_sec);
    // Destinations this connection has sent to, so a new one counts as a fresh pairing once.
    let mut seen_dsts: std::collections::HashSet<[u8; KEY_LEN]> = std::collections::HashSet::new();

    loop {
        match read_frame(read_half).await? {
            Some(Frame::Send { dst, payload }) => {
                // Enforce the per-connection rate cap: a sender over budget has this datagram
                // dropped, protecting the operator's transfer allowance.
                if !budget.allow(payload.len() as u64) {
                    metrics.frames_dropped_rate.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                // Look up the destination's writer and hand it a Recv tagged with our key.
                // A missing or full destination simply drops the datagram — the relay is a
                // best-effort datagram path, like the UDP it stands in for.
                let sender = { routes.lock().await.get(&dst).cloned() };
                if let Some(sender) = sender {
                    let bytes = payload.len() as u64;
                    if sender.try_send(Frame::Recv { src, payload }).is_ok() {
                        metrics.frames_relayed.fetch_add(1, Ordering::Relaxed);
                        metrics.bytes_relayed.fetch_add(bytes, Ordering::Relaxed);
                        if seen_dsts.insert(dst) {
                            metrics.total_pairs_served.fetch_add(1, Ordering::Relaxed);
                        }
                    } else {
                        metrics.frames_dropped_no_route.fetch_add(1, Ordering::Relaxed);
                    }
                } else {
                    metrics.frames_dropped_no_route.fetch_add(1, Ordering::Relaxed);
                }
            }
            // A keepalive keeps the TCP connection and NAT mapping alive; nothing to do.
            Some(Frame::KeepAlive) => {}
            // A pairing request: match with whoever else holds the same code, or wait.
            Some(Frame::Pair { key, udp_port, code }) => {
                handle_pair(&code, key, observed_ip, udp_port, out, pairings).await;
            }
            // A stray Recv or Paired from a client (relay-only frames) is ignored.
            Some(_) => {}
            // Clean end of stream.
            None => return Ok(()),
        }
    }
}

/// Reads and discards from a signalling connection until the client closes it.
///
/// A pairing connection sends one `Pair` and then only waits for its `Paired` reply, after
/// which the client disconnects. Draining here keeps the socket (and the writer task) alive
/// long enough to flush that reply, and returns cleanly on EOF.
async fn drain_until_closed<R>(read_half: &mut R) -> Result<()>
where
    R: AsyncReadExt + Unpin,
{
    loop {
        match read_frame(read_half).await? {
            // A client might send a keepalive while waiting; ignore anything it sends.
            Some(_) => {}
            None => return Ok(()),
        }
    }
}

/// Matches a pairing request against a waiting partner, or parks it to wait.
///
/// The observed address handed to each side is the relay-observed public IP of the *other*
/// node combined with the UDP port that node reported — the two facts needed to attempt a
/// direct WireGuard handshake through the peer's NAT. The relay never needs the keys for
/// anything but passing them along.
async fn handle_pair(
    code: &str,
    key: [u8; KEY_LEN],
    observed_ip: std::net::IpAddr,
    udp_port: u16,
    out: &mpsc::Sender<Frame>,
    pairings: &Pairings,
) {
    let my_observed = SocketAddr::new(observed_ip, udp_port);
    let mut table = pairings.lock().await;

    // Evict a stale waiter on this code so an abandoned half-pairing cannot pin it.
    if let Some(existing) = table.get(code) {
        if existing.since.elapsed() > PAIRING_TTL {
            table.remove(code);
        }
    }

    match table.remove(code) {
        // A partner is already waiting: introduce the two and clear the code.
        Some(partner) => {
            // Tell the partner about us…
            let _ = partner.out.try_send(Frame::Paired {
                peer_key: key,
                peer_addr: Some(my_observed),
            });
            // …and tell us about the partner.
            let _ = out.try_send(Frame::Paired {
                peer_key: partner.key,
                peer_addr: partner.observed,
            });
        }
        // Nobody waiting yet: park this node as the waiter for the code.
        None => {
            table.insert(
                code.to_string(),
                PairWaiter {
                    key,
                    observed: Some(my_observed),
                    out: out.clone(),
                    since: Instant::now(),
                },
            );
        }
    }
}

/// A sliding one-second byte budget: a simple token-ish window that caps relayed throughput
/// per connection without a timer, checked inline as datagrams arrive.
struct RateBudget {
    /// Bytes allowed per window; `0` disables the cap.
    per_window: u64,
    used: u64,
    window_start: Instant,
}

impl RateBudget {
    fn new(per_window: u64) -> Self {
        Self { per_window, used: 0, window_start: Instant::now() }
    }

    /// Returns whether `bytes` may be relayed now, accounting for the current window.
    fn allow(&mut self, bytes: u64) -> bool {
        if self.per_window == 0 {
            return true; // cap disabled
        }
        let now = Instant::now();
        if now.duration_since(self.window_start) >= RATE_WINDOW {
            self.window_start = now;
            self.used = 0;
        }
        if self.used + bytes > self.per_window {
            return false;
        }
        self.used += bytes;
        true
    }
}

/// Reads one complete frame from the stream, or [`None`] at a clean end of stream.
///
/// # Errors
///
/// Propagates `std::io::Error`, and maps a malformed header to [`crate::Error::Protocol`].
async fn read_frame<R>(read_half: &mut R) -> Result<Option<Frame>>
where
    R: AsyncReadExt + Unpin,
{
    let mut header = [0u8; HEADER_LEN];
    match read_half.read_exact(&mut header).await {
        Ok(_) => {}
        // A clean EOF at a frame boundary is a normal disconnect, not an error.
        Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e.into()),
    }
    let (ty, len) = parse_header(&header).map_err(|e| crate::Error::Protocol(e.to_string()))?;

    // A keepalive has no body; skip the read so a zero-length `read_exact` is not issued.
    let body = if len == 0 {
        Vec::new()
    } else {
        let mut body = vec![0u8; len];
        read_half.read_exact(&mut body).await?;
        body
    };
    let frame = Frame::decode_body(ty, &body).map_err(|e| crate::Error::Protocol(e.to_string()))?;
    Ok(Some(frame))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::relay::RelayClient;
    use std::time::Duration;

    const A: [u8; KEY_LEN] = [0xA1; KEY_LEN];
    const B: [u8; KEY_LEN] = [0xB2; KEY_LEN];
    const C: [u8; KEY_LEN] = [0xC3; KEY_LEN];

    async fn spawn(limits: RelayLimits) -> (SocketAddr, Arc<RelayMetrics>) {
        let server = RelayServer::bind_with("127.0.0.1:0".parse().unwrap(), limits).await.unwrap();
        let addr = server.local_addr().unwrap();
        let metrics = server.metrics();
        tokio::spawn(async move {
            let _ = server.run().await;
        });
        (addr, metrics)
    }

    #[tokio::test]
    async fn metrics_count_active_and_relayed_traffic() {
        let (addr, metrics) = spawn(RelayLimits::unlimited()).await;
        let a = RelayClient::connect(addr, A, B).await.unwrap();
        let mut b = RelayClient::connect(addr, B, A).await.unwrap();
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(metrics.active(), 2, "two nodes registered");

        a.send(b"hello").await.unwrap();
        let got = tokio::time::timeout(Duration::from_secs(2), b.recv()).await.unwrap().unwrap();
        assert_eq!(got, b"hello");
        // Give the server a moment to record the relayed frame.
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(metrics.frames_relayed(), 1);
        assert_eq!(metrics.bytes_relayed(), 5);
        assert_eq!(metrics.total_pairs_served(), 1);
    }

    #[tokio::test]
    async fn registration_cap_refuses_a_third_node() {
        let limits = RelayLimits { max_registrations: 2, per_conn_bytes_per_sec: 0 };
        let (addr, metrics) = spawn(limits).await;
        let _a = RelayClient::connect(addr, A, B).await.unwrap();
        let _b = RelayClient::connect(addr, B, A).await.unwrap();
        tokio::time::sleep(Duration::from_millis(50)).await;

        // The third registration is accepted at the TCP layer but refused by the relay, which
        // closes it; the active count stays at 2 and a rejection is recorded.
        let _c = RelayClient::connect(addr, C, A).await.unwrap();
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert_eq!(metrics.active(), 2, "the cap holds the active count at 2");
        assert_eq!(metrics.rejected_full(), 1);
    }

    #[tokio::test]
    async fn the_rate_budget_drops_over_cap_bytes_within_a_window() {
        // 10 bytes/sec: the first 10-byte datagram passes, an immediate second is dropped.
        let mut budget = RateBudget::new(10);
        assert!(budget.allow(10), "first datagram fits the budget");
        assert!(!budget.allow(1), "the next byte is over budget this window");
    }

    #[tokio::test]
    async fn a_disabled_rate_budget_always_allows() {
        let mut budget = RateBudget::new(0);
        for _ in 0..1000 {
            assert!(budget.allow(100_000));
        }
    }
}
