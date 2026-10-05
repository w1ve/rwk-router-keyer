//! Replay driver: the dedicated threads that turn arriving datagrams into station keying.
//!
//! The [`EdgeReplayer`] core is synchronous and timestamp-explicit, which keeps the
//! safety logic testable but leaves it un-driven. This module supplies the driver,
//! mirroring the .NET thread layout:
//!
//! * **A dedicated replay thread** runs the loop: apply any pending fail-safe, drain
//!   inbound datagrams, [`EdgeReplayer::tick`] to schedule and fire, publish events, then
//!   wait on the absolute deadline with [`HybridWaiter`] — sleep coarsely, spin for the
//!   final sub-millisecond. It never awaits, so nothing on the keying path can be
//!   delayed by the runtime.
//! * **A separate watchdog thread** evaluates [`FailSafeMonitor`] (F1, F2, F3, F9) and
//!   [`SchedulerWatchdog`] (F10) every 50 ms against a snapshot. It must be separate: a
//!   thread cannot detect its own stall, which is exactly what F10 is for.
//!
//! The two threads never share the replayer — only a [`ReplaySnapshot`] (a `Copy` struct
//! behind a short lock) and a lock-free flag for a pending condition — so the keying
//! path takes no lock at all.
//!
//! **Who announces what.** The watchdog announces every condition *it* detects, because
//! it must still work when the replay thread is stalled (F10); the replay thread applies
//! those silently via [`EdgeReplayer::apply_condition_quiet`]. The replay thread announces
//! the conditions only it can see — F4, F5 and F8 from edge validation, and **F6** from the
//! keying output's latched fault. The upshot is that every fail-safe reaches the bus
//! exactly once, even when the replay thread is the one that is broken.
//!
//! **Event sources that were previously missing.** F6 is fed by
//! [`KeyingOutput::take_fault`], which [`SerialKeyingAdapter`] fills when a control-line
//! write fails; F9 is fed by [`PathHealth`], which the edge transport (or the future
//! tunnel) raises after a sustained run of send failures.
//!
//! **Scheduling protection.** Both threads raise themselves to time-critical priority and
//! the replay thread requests a 1 ms system timer period, mirroring the .NET
//! `THREAD_PRIORITY_TIME_CRITICAL` + `timeBeginPeriod(1)` pair. The negotiated result is
//! recorded in [`DriverDiagnostics`]; a refusal is never fatal.
//!
//! **Clean shutdown.** [`DriverHandle`] stops both threads and joins them on drop, and
//! the replay thread forces the key up as it exits (F8 if the key was down).

use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU32, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use tokio::sync::mpsc::{unbounded_channel, UnboundedReceiver, UnboundedSender};

use crate::engine::bus::{CoreEvent, EventBus};
use crate::engine::network::PathHealth;
use crate::engine::serial::SerialKeyingOutput;
use crate::platform::{PriorityOutcome, ThreadPriorityGuard, TimerResolutionGuard};
use crate::timing::{Clock, HybridWaiter};

use super::failsafe::{FailSafeCondition, FailSafeMonitor, ReplaySnapshot, SchedulerWatchdog};
use super::replayer::{EdgeReplayer, KeyingOutput, ReplayEvent};

/// System timer resolution requested on the replay thread, in milliseconds.
///
/// Matches the `timeBeginPeriod(1)` the .NET Station called: without it the Windows
/// scheduler can round a 1 ms sleep up to the ~15.6 ms default quantum.
const TIMER_RESOLUTION_MS: u32 = 1;

/// Sentinel for "this thread has not reported yet" in [`DriverDiagnostics`].
const PRIORITY_UNKNOWN: i32 = 0;
const PRIORITY_APPLIED: i32 = 1;
const PRIORITY_NOT_PERMITTED: i32 = 2;
const PRIORITY_UNSUPPORTED: i32 = 3;

/// Encoding for the lock-free priority handoff.
///
/// Successful Win32/POSIX calls return non-negative codes, so a negated failure code can
/// never collide with the sentinels above.
fn encode_priority(outcome: PriorityOutcome) -> i32 {
    match outcome {
        PriorityOutcome::Applied => PRIORITY_APPLIED,
        PriorityOutcome::NotPermitted => PRIORITY_NOT_PERMITTED,
        PriorityOutcome::Unsupported => PRIORITY_UNSUPPORTED,
        PriorityOutcome::Failed(code) => -(code.max(1)),
    }
}

fn decode_priority(value: i32) -> Option<PriorityOutcome> {
    match value {
        PRIORITY_UNKNOWN => None,
        PRIORITY_APPLIED => Some(PriorityOutcome::Applied),
        PRIORITY_NOT_PERMITTED => Some(PriorityOutcome::NotPermitted),
        PRIORITY_UNSUPPORTED => Some(PriorityOutcome::Unsupported),
        v if v < 0 => Some(PriorityOutcome::Failed(-v)),
        _ => None,
    }
}

/// What the driver's threads managed to negotiate with the OS scheduler.
///
/// Reported through [`DriverHandle::diagnostics`] and shown by `rwk selftest`, so the
/// scheduling-protection state is observable rather than assumed.
#[derive(Debug, Default)]
pub struct DriverDiagnostics {
    replay_priority: AtomicI32,
    watchdog_priority: AtomicI32,
    timer_resolution_ms: AtomicU32,
    timer_resolution_applied: AtomicBool,
}

impl DriverDiagnostics {
    /// The replay thread's priority outcome, or [`None`] before it has run a pass.
    #[must_use]
    pub fn replay_priority(&self) -> Option<PriorityOutcome> {
        decode_priority(self.replay_priority.load(Ordering::Relaxed))
    }

    /// The watchdog thread's priority outcome, or [`None`] before it has run a pass.
    #[must_use]
    pub fn watchdog_priority(&self) -> Option<PriorityOutcome> {
        decode_priority(self.watchdog_priority.load(Ordering::Relaxed))
    }

    /// The timer resolution the replay thread requested, in milliseconds.
    #[must_use]
    pub fn timer_resolution_ms(&self) -> u32 {
        self.timer_resolution_ms.load(Ordering::Relaxed)
    }

    /// Whether the finer timer resolution was actually granted.
    #[must_use]
    pub fn timer_resolution_applied(&self) -> bool {
        self.timer_resolution_applied.load(Ordering::Relaxed)
    }
}

/// A datagram handed to the driver, stamped with its arrival time.
#[derive(Debug, Clone)]
pub struct InboundPacket {
    /// Raw `RWK-PADDLE` datagram bytes.
    pub data: Vec<u8>,
    /// Arrival time on the driver's clock, in ticks.
    pub arrival: i64,
}

/// Encodes "no pending condition" for the lock-free [`AtomicU8`] handoff.
const NO_CONDITION: u8 = 0;

/// Handle controlling a running driver.
///
/// Dropping the handle shuts the driver down and joins its threads.
pub struct DriverHandle {
    stop: Arc<AtomicBool>,
    inbound: UnboundedSender<InboundPacket>,
    clock: Arc<dyn Clock>,
    diagnostics: Arc<DriverDiagnostics>,
    threads: Vec<JoinHandle<()>>,
}

impl DriverHandle {
    /// Queues a datagram, stamped with the current clock time.
    ///
    /// Non-blocking; the replay thread picks it up on its next pass.
    pub fn submit(&self, data: Vec<u8>) {
        let packet = InboundPacket { data, arrival: self.clock.now() as i64 };
        // A closed channel means the driver is shutting down; dropping the datagram is
        // the correct response, not an error the keying path should handle.
        let _ = self.inbound.send(packet);
    }

    /// The driver's clock reading, in ticks.
    #[must_use]
    pub fn now(&self) -> i64 {
        self.clock.now() as i64
    }

    /// Scheduling diagnostics reported by the driver's own threads.
    #[must_use]
    pub fn diagnostics(&self) -> Arc<DriverDiagnostics> {
        Arc::clone(&self.diagnostics)
    }

    /// Requests shutdown and joins both threads.
    pub fn stop(&mut self) {
        self.shutdown();
    }

    fn shutdown(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        for handle in self.threads.drain(..) {
            let _ = handle.join();
        }
    }
}

impl Drop for DriverHandle {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// Spawns the replay driver.
///
/// The replayer must already be bound to a session (call `begin_session` first) if
/// datagrams are to be accepted.
///
/// * `keying` — the key/PTT output, moved onto the replay thread.
/// * `clock` — the timestamp source; its frequency defines the tick domain.
/// * `bus` — optional event bus for UI delivery of fail-safes.
/// * `mesh` — optional mesh path-health flag; a raised flag becomes **F9**.
#[must_use]
pub fn spawn(
    replayer: EdgeReplayer,
    keying: Box<dyn KeyingOutput + Send>,
    clock: Arc<dyn Clock>,
    bus: Option<EventBus>,
    mesh: Option<PathHealth>,
) -> DriverHandle {
    let (inbound_tx, inbound_rx) = unbounded_channel();
    let stop = Arc::new(AtomicBool::new(false));
    let pending = Arc::new(AtomicU8::new(NO_CONDITION));
    let snapshot = Arc::new(Mutex::new(replayer.snapshot()));
    let diagnostics = Arc::new(DriverDiagnostics::default());

    let replay_thread = spawn_replay_thread(
        replayer,
        keying,
        Arc::clone(&clock),
        Arc::clone(&stop),
        Arc::clone(&pending),
        Arc::clone(&snapshot),
        inbound_rx,
        bus.clone(),
        Arc::clone(&diagnostics),
    );

    let watchdog_thread = spawn_watchdog_thread(
        clock.frequency(),
        Arc::clone(&stop),
        pending,
        snapshot,
        Arc::clone(&clock),
        bus,
        mesh,
        Arc::clone(&diagnostics),
    );

    DriverHandle {
        stop,
        inbound: inbound_tx,
        clock,
        diagnostics,
        threads: vec![replay_thread, watchdog_thread],
    }
}

#[allow(clippy::too_many_arguments)]
fn spawn_replay_thread(
    mut replayer: EdgeReplayer,
    mut keying: Box<dyn KeyingOutput + Send>,
    clock: Arc<dyn Clock>,
    stop: Arc<AtomicBool>,
    pending: Arc<AtomicU8>,
    snapshot: Arc<Mutex<ReplaySnapshot>>,
    mut inbound: UnboundedReceiver<InboundPacket>,
    bus: Option<EventBus>,
    diagnostics: Arc<DriverDiagnostics>,
) -> JoinHandle<()> {
    thread::Builder::new()
        .name("rwk-replay".into())
        .spawn(move || {
            // Raise this thread before the first pass, and hold the guards for the whole
            // loop so the OS setting is released when the thread exits. Both are best
            // effort: a refusal is recorded, never fatal.
            let _priority = ThreadPriorityGuard::raise_time_critical();
            let _timer = TimerResolutionGuard::raise(TIMER_RESOLUTION_MS);
            diagnostics.replay_priority.store(encode_priority(_priority.outcome()), Ordering::Relaxed);
            diagnostics.timer_resolution_ms.store(_timer.resolution_ms(), Ordering::Relaxed);
            diagnostics.timer_resolution_applied.store(_timer.applied(), Ordering::Relaxed);

            while !stop.load(Ordering::SeqCst) {
                // Apply any condition the watchdog raised since the last pass.
                let code = pending.swap(NO_CONDITION, Ordering::SeqCst);
                if let Some(condition) = condition_from_code(code) {
                    apply_condition(&mut replayer, keying.as_mut(), condition);
                }

                while let Ok(packet) = inbound.try_recv() {
                    replayer.process_datagram(&packet.data, packet.arrival);
                }

                let now = clock.now() as i64;
                replayer.tick(now, keying.as_mut());

                // F6: the keying hardware faulted (write error or device removal). The
                // watchdog has no view of the serial port, so this is one of the
                // conditions the replay thread announces itself.
                if let Some(detail) = keying.take_fault() {
                    if !replayer.is_safe_latched() {
                        replayer.apply_condition_quiet(FailSafeCondition::F6, keying.as_mut());
                        publish_detail(&bus, FailSafeCondition::F6, Some(&detail));
                    }
                }

                {
                    let mut guard = snapshot.lock().unwrap_or_else(|e| e.into_inner());
                    *guard = replayer.snapshot();
                }

                for event in replayer.take_events() {
                    if let ReplayEvent::FailSafe { condition } = event {
                        publish(&bus, condition);
                    }
                }

                let wake = replayer.next_wake(now).max(0) as u64;
                let should_abort = || {
                    stop.load(Ordering::SeqCst)
                        || !inbound.is_empty()
                        || pending.load(Ordering::SeqCst) != NO_CONDITION
                };
                HybridWaiter::wait_until_ticks(clock.as_ref(), wake, &should_abort);
            }

            // F8: closing while the key is down. Force it up before the thread exits.
            if replayer.is_key_down() {
                replayer.force_key_up(keying.as_mut());
                publish(&bus, FailSafeCondition::F8);
            }
        })
        .expect("spawn rwk-replay thread")
}

#[allow(clippy::too_many_arguments)]
fn spawn_watchdog_thread(
    frequency: u64,
    stop: Arc<AtomicBool>,
    pending: Arc<AtomicU8>,
    snapshot: Arc<Mutex<ReplaySnapshot>>,
    clock: Arc<dyn Clock>,
    bus: Option<EventBus>,
    mesh: Option<PathHealth>,
    diagnostics: Arc<DriverDiagnostics>,
) -> JoinHandle<()> {
    thread::Builder::new()
        .name("rwk-watchdog".into())
        .spawn(move || {
            let _priority = ThreadPriorityGuard::raise_time_critical();
            diagnostics.watchdog_priority.store(encode_priority(_priority.outcome()), Ordering::Relaxed);

            let mut monitor = FailSafeMonitor::new(frequency);
            let watchdog = SchedulerWatchdog::new(frequency);

            while !stop.load(Ordering::SeqCst) {
                thread::sleep(FailSafeMonitor::CHECK_INTERVAL);
                if stop.load(Ordering::SeqCst) {
                    break;
                }

                // F9's event source: any mesh layer that lost the path raises this flag,
                // and the very next check turns it into a fail-safe.
                if let Some(health) = &mesh {
                    if health.take_lost() {
                        monitor.report_path_lost();
                    }
                }

                let view = {
                    let guard = snapshot.lock().unwrap_or_else(|e| e.into_inner());
                    *guard
                };
                let now = clock.now() as i64;

                let fired = monitor
                    .check(&view, now)
                    .or_else(|| watchdog.check(&view, now));

                if let Some(condition) = fired {
                    // The watchdog announces everything it detects, because it must keep
                    // working when the replay thread is stalled — the F10 case. The replay
                    // thread applies these silently, so each fail-safe is published once.
                    publish(&bus, condition);
                    pending.store(condition as u8, Ordering::SeqCst);
                }
            }
        })
        .expect("spawn rwk-watchdog thread")
}

/// Applies a condition detected by the watchdog, honouring its latch policy.
///
/// The watchdog has already announced the condition on the bus, so this applies it
/// silently — otherwise every fail-safe the watchdog finds would be reported twice.
fn apply_condition(
    replayer: &mut EdgeReplayer,
    keying: &mut dyn KeyingOutput,
    condition: FailSafeCondition,
) {
    replayer.apply_condition_quiet(condition, keying);
}

/// Recovers a condition from its lock-free handoff code; 0 means "none".
fn condition_from_code(code: u8) -> Option<FailSafeCondition> {
    match code {
        1 => Some(FailSafeCondition::F1),
        2 => Some(FailSafeCondition::F2),
        3 => Some(FailSafeCondition::F3),
        4 => Some(FailSafeCondition::F4),
        5 => Some(FailSafeCondition::F5),
        6 => Some(FailSafeCondition::F6),
        7 => Some(FailSafeCondition::F7),
        8 => Some(FailSafeCondition::F8),
        9 => Some(FailSafeCondition::F9),
        10 => Some(FailSafeCondition::F10),
        _ => None,
    }
}

fn publish(bus: &Option<EventBus>, condition: FailSafeCondition) {
    publish_detail(bus, condition, None);
}

/// Publishes a fail-safe, optionally appending a more specific detail than the generic
/// condition description (for example the serial error behind F6).
fn publish_detail(bus: &Option<EventBus>, condition: FailSafeCondition, detail: Option<&str>) {
    if let Some(bus) = bus {
        let message = match detail {
            Some(detail) if !detail.is_empty() => format!("{} ({detail})", condition.description()),
            _ => condition.description().to_string(),
        };
        bus.publish(CoreEvent::FailSafe { code: condition as u8, message });
    }
}

/// Adapts a [`SerialKeyingOutput`] to the replayer's [`KeyingOutput`].
///
/// The output is driven with both the key and the PTT state together, because PTT lead
/// and tail are computed by the replayer and handed over one line at a time.
pub struct SerialKeyingAdapter {
    output: SerialKeyingOutput,
    key_down: bool,
    ptt: bool,
    /// The first fault since the last poll, surfaced as F6 by the replay thread.
    fault: Option<String>,
}

impl SerialKeyingAdapter {
    /// Wraps an open keying output.
    #[must_use]
    pub fn new(output: SerialKeyingOutput) -> Self {
        Self { output, key_down: false, ptt: false, fault: None }
    }

    /// The port name, for logging.
    #[must_use]
    pub fn port_name(&self) -> String {
        self.output.port_name().to_string()
    }

    /// Returns the underlying output, so the caller can close it explicitly.
    #[must_use]
    pub fn into_inner(self) -> SerialKeyingOutput {
        self.output
    }

    fn apply(&mut self) {
        // `set_key` has no error channel, so the failure is latched for the driver to
        // poll and turn into F6, exactly as the .NET Station did. A latched fault is only
        // observed on a write, so a cable pulled while the key is idle is caught by the
        // F1/F2 heartbeat watchdogs rather than F6.
        if let Err(error) = self.output.apply(self.key_down, self.ptt) {
            if self.fault.is_none() {
                self.fault = Some(error.to_string());
            }
        }
    }
}

impl KeyingOutput for SerialKeyingAdapter {
    fn set_key(&mut self, key_down: bool) {
        self.key_down = key_down;
        self.apply();
    }

    fn set_ptt(&mut self, asserted: bool) {
        self.ptt = asserted;
        self.apply();
    }

    fn take_fault(&mut self) -> Option<String> {
        self.fault.take()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::replay::{EdgeJitterProfile, JitterBufferConfig};
    use crate::primitives::PathType;
    use crate::protocol::edge::{EdgeEntry, RwkPaddleFrame};
    use crate::timing::MonotonicClock;
    use std::time::{Duration, Instant};

    /// Records key transitions into shared storage the test can inspect.
    struct SharedLog(Arc<Mutex<Vec<bool>>>);

    impl KeyingOutput for SharedLog {
        fn set_key(&mut self, key_down: bool) {
            self.0.lock().unwrap_or_else(|e| e.into_inner()).push(key_down);
        }
    }

    /// A log that fails *writes* once `fail` is set, standing in for a serial port that
    /// is unplugged mid-session: like real hardware, it reports the failure on the next
    /// control-line write, not spontaneously.
    struct FaultingLog {
        keys: Arc<Mutex<Vec<bool>>>,
        fail: Arc<AtomicBool>,
        pending_faults: Arc<AtomicU32>,
    }

    impl KeyingOutput for FaultingLog {
        fn set_key(&mut self, key_down: bool) {
            self.keys.lock().unwrap_or_else(|e| e.into_inner()).push(key_down);
            if self.fail.load(Ordering::SeqCst) {
                self.pending_faults.fetch_add(1, Ordering::SeqCst);
            }
        }

        fn take_fault(&mut self) -> Option<String> {
            if self.pending_faults.swap(0, Ordering::SeqCst) > 0 {
                Some("simulated serial write failure".to_string())
            } else {
                None
            }
        }
    }

    fn frame(edges: &[EdgeEntry]) -> Vec<u8> {
        RwkPaddleFrame::try_new(1, edges).unwrap().to_vec()
    }

    fn replayer_with_session() -> EdgeReplayer {
        let config = JitterBufferConfig {
            direct_delay: Duration::from_millis(40),
            derp_delay: Duration::from_millis(40),
            adaptive_mode: false,
        };
        let mut rp = EdgeReplayer::new(
            1_000_000_000,
            config,
            EdgeJitterProfile::PathAdaptive,
            PathType::Direct,
            None,
        );
        rp.begin_session(1);
        rp
    }

    #[test]
    fn driver_keys_an_arriving_edge_and_stops_cleanly() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let handle = spawn(
            replayer_with_session(),
            Box::new(SharedLog(Arc::clone(&log))),
            Arc::new(MonotonicClock),
            None,
            None,
        );

        handle.submit(frame(&[EdgeEntry::key_down_at(1, 0, 0)]));

        // Wait for the key to go down (40ms buffer, plus scheduling slack).
        let deadline = Instant::now() + Duration::from_millis(500);
        loop {
            if log.lock().unwrap_or_else(|e| e.into_inner()).contains(&true) {
                break;
            }
            assert!(Instant::now() < deadline, "driver never keyed down");
            thread::sleep(Duration::from_millis(5));
        }

        let mut handle = handle;
        handle.stop();

        // F8: shutdown must not leave the key down.
        let keys = log.lock().unwrap_or_else(|e| e.into_inner()).clone();
        assert_eq!(keys.last(), Some(&false), "shutdown must release the key");
    }

    #[test]
    fn dropping_the_handle_shuts_the_driver_down() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let handle = spawn(
            replayer_with_session(),
            Box::new(SharedLog(Arc::clone(&log))),
            Arc::new(MonotonicClock),
            None,
            None,
        );
        handle.submit(frame(&[EdgeEntry::key_down_at(1, 0, 0)]));
        // Dropping joins both threads; this test passes if it returns.
        drop(handle);
    }

    #[test]
    fn fail_safe_events_reach_the_bus() {
        let bus = EventBus::new();
        let mut rx = bus.subscribe();
        let log = Arc::new(Mutex::new(Vec::new()));
        let handle = spawn(
            replayer_with_session(),
            Box::new(SharedLog(Arc::clone(&log))),
            Arc::new(MonotonicClock),
            Some(bus),
            None,
        );
        handle.submit(frame(&[EdgeEntry::key_down_at(1, 0, 0)]));

        // The watchdog fires F2 once no heartbeat arrives for 3 seconds; waiting that long
        // in a unit test is wasteful, so instead drive a condition the replayer detects:
        // a key-down behind an unhealed gap latches F5 immediately.
        thread::sleep(Duration::from_millis(120));
        handle.submit(frame(&[EdgeEntry::key_down_at(3, 200, 0)]));

        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            match rx.try_recv() {
                Ok(CoreEvent::FailSafe { code, .. }) => {
                    assert_eq!(code, 5, "expected F5");
                    return;
                }
                Ok(_) => continue,
                Err(_) => {
                    assert!(Instant::now() < deadline, "no fail-safe event reached the bus");
                    thread::sleep(Duration::from_millis(10));
                }
            }
        }
    }

    /// Waits for a fail-safe with `code` on the bus, returning its message.
    fn await_fail_safe(rx: &mut crate::engine::bus::EventReceiver, code: u8) -> String {
        let deadline = Instant::now() + Duration::from_secs(3);
        loop {
            match rx.try_recv() {
                Ok(CoreEvent::FailSafe { code: got, message }) if got == code => return message,
                Ok(_) => continue,
                Err(_) => {
                    assert!(Instant::now() < deadline, "fail-safe F{code} never reached the bus");
                    thread::sleep(Duration::from_millis(10));
                }
            }
        }
    }

    #[test]
    fn a_keying_output_fault_is_raised_as_f6_with_the_underlying_detail() {
        let bus = EventBus::new();
        let mut rx = bus.subscribe();
        let keys = Arc::new(Mutex::new(Vec::new()));
        let fail = Arc::new(AtomicBool::new(false));
        let pending_faults = Arc::new(AtomicU32::new(0));

        let handle = spawn(
            replayer_with_session(),
            Box::new(FaultingLog {
                keys: Arc::clone(&keys),
                fail: Arc::clone(&fail),
                pending_faults: Arc::clone(&pending_faults),
            }),
            Arc::new(MonotonicClock),
            Some(bus),
            None,
        );

        // Key down, then break the port.
        handle.submit(frame(&[EdgeEntry::key_down_at(1, 0, 0)]));
        let deadline = Instant::now() + Duration::from_millis(500);
        while Instant::now() < deadline && !keys.lock().unwrap_or_else(|e| e.into_inner()).contains(&true)
        {
            thread::sleep(Duration::from_millis(5));
        }
        assert!(keys.lock().unwrap_or_else(|e| e.into_inner()).contains(&true), "test setup failed to key");
        fail.store(true, Ordering::SeqCst);

        // The next key transition is when the port discovers it is gone — the driver has
        // to notice that write failure and turn it into F6.
        handle.submit(frame(&[EdgeEntry::key_up_at(2, 50, 0)]));

        let message = await_fail_safe(&mut rx, 6);
        assert!(message.contains("simulated serial write failure"), "detail missing: {message}");

        // F6 latches SAFE and forces the key up on the hardware.
        assert_eq!(
            keys.lock().unwrap_or_else(|e| e.into_inner()).last(),
            Some(&false),
            "F6 must release the key"
        );
    }

    #[test]
    fn a_lost_mesh_path_is_raised_as_f9_and_consumed() {
        let bus = EventBus::new();
        let mut rx = bus.subscribe();
        let health = PathHealth::new();
        let log = Arc::new(Mutex::new(Vec::new()));

        let mut handle = spawn(
            replayer_with_session(),
            Box::new(SharedLog(Arc::clone(&log))),
            Arc::new(MonotonicClock),
            Some(bus),
            Some(health.clone()),
        );

        health.report_lost();
        let message = await_fail_safe(&mut rx, 9);
        assert!(message.contains("Mesh path lost"), "unexpected F9 message: {message}");
        assert!(!health.is_lost(), "the watchdog consumes the flag");

        handle.stop();
    }

    #[test]
    fn both_threads_report_their_scheduling_outcome() {
        let handle = spawn(
            replayer_with_session(),
            Box::new(SharedLog(Arc::new(Mutex::new(Vec::new())))),
            Arc::new(MonotonicClock),
            None,
            None,
        );
        let diagnostics = handle.diagnostics();

        let deadline = Instant::now() + Duration::from_secs(2);
        while diagnostics.replay_priority().is_none() || diagnostics.watchdog_priority().is_none() {
            assert!(Instant::now() < deadline, "threads never reported their priority");
            thread::sleep(Duration::from_millis(5));
        }
        assert_eq!(diagnostics.timer_resolution_ms(), 1);
    }
}
