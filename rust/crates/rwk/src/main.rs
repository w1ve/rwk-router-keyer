//! `rwk` — the single self-contained RWK Router Keyer executable.
//!
//! There is no Node or Go sidecar and no child process: this binary *is* the engine.
//! The subcommands below exercise the `rwk-core` hardware paths so the port can be
//! verified on real hardware without the Tauri shell:
//!
//! ```text
//! rwk ports                        # list serial ports
//! rwk devices                      # list audio output devices
//! rwk key COM3 "CQ TEST" --wpm 25  # key text on DTR with local sidetone
//! rwk selftest                     # timing, protocol and audio checks; exit 0 on success
//! ```

use std::process::ExitCode;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use rwk_core::engine::audio::{KeyedSineGenerator, SidetoneEngine};
use rwk_core::engine::keying::{EdgeScheduleBuilder, ElementKeyer};
use rwk_core::engine::serial::{enumerate_ports, KeyingOutputConfig, SerialKeyingOutput, SerialPortType};
use rwk_core::engine::replay::{
    spawn_driver, EdgeJitterProfile, EdgeReplayer, JitterBufferConfig, KeyingOutput,
};
use rwk_core::protocol::edge::{EdgeEntry, RwkPaddleFrame};
use rwk_core::primitives::{KeyingLine, PathType};
use rwk_core::timing::{Clock, HybridWaiter, MonotonicClock};

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let command = args.first().map(String::as_str).unwrap_or("help");

    let result = match command {
        "ports" => cmd_ports(),
        "devices" => cmd_devices(),
        "key" => cmd_key(&args[1..]),
        "selftest" => cmd_selftest(),
        "help" | "--help" | "-h" => {
            print_usage();
            Ok(())
        }
        other => {
            eprintln!("error: unknown command '{other}'\n");
            print_usage();
            return ExitCode::FAILURE;
        }
    };

    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e}");
            ExitCode::FAILURE
        }
    }
}

fn print_usage() {
    println!(
        "rwk — RWK Router Keyer engine (single executable, no sidecar)\n\n\
         USAGE:\n  \
         rwk <command> [options]\n\n\
         COMMANDS:\n  \
         ports                          List serial ports\n  \
         devices                        List audio output devices\n  \
         key <port> <text> [options]    Key text on a serial control line\n  \
         selftest                       Run timing/protocol/audio checks\n  \
         help                           Show this help\n\n\
         KEY OPTIONS:\n  \
         --wpm <n>        Keying speed, 5-60 (default 25)\n  \
         --weight <n>     Weight percentage, 25-75 (default 50)\n  \
         --line <dtr|rts> Keying line (default dtr)\n  \
         --tone <hz>      Sidetone frequency, 300-1500 (default 700)\n  \
         --volume <f>     Sidetone volume, 0.0-1.0 (default 0.5)\n  \
         --no-sidetone    Key without local audio\n"
    );
}

fn cmd_ports() -> Result<(), Box<dyn std::error::Error>> {
    let ports = enumerate_ports()?;
    if ports.is_empty() {
        println!("no serial ports found");
    } else {
        for port in ports {
            let detail = match &port.port_type {
                SerialPortType::UsbPort(info) => format!(
                    "usb {:04x}:{:04x}{}",
                    info.vid,
                    info.pid,
                    info.product.as_deref().map(|p| format!(" {p}")).unwrap_or_default()
                ),
                SerialPortType::PciPort => "pci".to_string(),
                SerialPortType::BluetoothPort => "bluetooth".to_string(),
                SerialPortType::Unknown => "unknown".to_string(),
            };
            println!("{}  ({detail})", port.port_name);
        }
    }
    Ok(())
}

fn cmd_devices() -> Result<(), Box<dyn std::error::Error>> {
    let devices = SidetoneEngine::output_devices();
    if devices.is_empty() {
        println!("no audio output devices found");
    } else {
        for name in devices {
            println!("{name}");
        }
    }
    Ok(())
}

fn cmd_key(args: &[String]) -> Result<(), Box<dyn std::error::Error>> {
    if args.len() < 2 {
        return Err("usage: rwk key <port> <text> [--wpm N] [--weight N] ...".into());
    }
    let port = args[0].clone();
    let text = args[1].clone();

    let mut wpm = 25u32;
    let mut weight = 50u32;
    let mut line = KeyingLine::Dtr;
    let mut tone = 700i32;
    let mut volume = 0.5f64;
    let mut sidetone_enabled = true;

    let mut i = 2;
    while i < args.len() {
        let take = |i: usize| -> Result<&String, Box<dyn std::error::Error>> {
            args.get(i + 1).ok_or_else(|| format!("missing value for {}", args[i]).into())
        };
        match args[i].as_str() {
            "--wpm" => {
                wpm = take(i)?.parse()?;
                i += 2;
            }
            "--weight" => {
                weight = take(i)?.parse()?;
                i += 2;
            }
            "--line" => {
                line = match take(i)?.to_ascii_lowercase().as_str() {
                    "dtr" => KeyingLine::Dtr,
                    "rts" => KeyingLine::Rts,
                    other => return Err(format!("unknown line '{other}' (want dtr or rts)").into()),
                };
                i += 2;
            }
            "--tone" => {
                tone = take(i)?.parse()?;
                i += 2;
            }
            "--volume" => {
                volume = take(i)?.parse()?;
                i += 2;
            }
            "--no-sidetone" => {
                sidetone_enabled = false;
                i += 1;
            }
            other => return Err(format!("unknown option '{other}'").into()),
        }
    }

    let config = KeyingOutputConfig { port_name: port.clone(), key_line: line, ..Default::default() };
    let mut output = SerialKeyingOutput::open(config)?;
    println!("opened {port} (key line {line:?})");

    // Optional local sidetone. A missing audio device is a warning, not a failure.
    let mut sidetone = if sidetone_enabled {
        match SidetoneEngine::new(tone, volume).and_then(|mut e| e.start().map(|()| e)) {
            Ok(engine) => {
                println!("sidetone on {} at {tone} Hz", engine.device_name().unwrap_or("output"));
                Some(engine)
            }
            Err(e) => {
                eprintln!("warning: sidetone unavailable ({e}); keying without audio");
                None
            }
        }
    } else {
        None
    };
    let key_handle = sidetone.as_ref().map(|s| s.key_handle());

    let clock: Arc<dyn Clock> = Arc::new(MonotonicClock);
    let keyer = ElementKeyer::new(wpm, weight, Arc::clone(&clock))?;
    let schedule = EdgeScheduleBuilder::build(&text, wpm, weight, 0, 1_000_000_000)?;
    println!("keying \"{text}\" at {wpm} WPM ({} edges)", schedule.edges.len());

    // Replay with the schedule's own timeline: shift edges to "now" on the real clock.
    let start = clock.now();
    let shifted: Vec<_> = schedule
        .edges
        .iter()
        .map(|e| rwk_core::engine::keying::EdgeEvent { timestamp_ticks: start + e.timestamp_ticks, ..*e })
        .collect();
    let live = rwk_core::engine::EdgeSchedule { edges: shifted };

    keyer.play(
        &live,
        &mut |edge| {
            let _ = output.apply(edge.key_down, false);
            if let Some(handle) = &key_handle {
                if edge.key_down {
                    handle.key_down();
                } else {
                    handle.key_up();
                }
            }
        },
        &|| false,
    );

    output.release_lines()?;
    if let Some(mut s) = sidetone.take() {
        s.key_up();
        s.stop();
    }
    println!("done");
    Ok(())
}

fn cmd_selftest() -> Result<(), Box<dyn std::error::Error>> {
    let mut failures = 0usize;
    let mut check = |name: &str, ok: bool, detail: String| {
        if ok {
            println!("  PASS  {name}: {detail}");
        } else {
            println!("  FAIL  {name}: {detail}");
            failures += 1;
        }
    };

    println!("rwk selftest");

    // 1. Protocol: edge frame round-trip.
    let edges = [EdgeEntry::key_down_at(1, 10, 0), EdgeEntry::key_up_at(2, 40, 0)];
    let frame = RwkPaddleFrame::try_new(9, &edges).ok_or("frame build")?;
    let bytes = frame.to_vec();
    let parsed = RwkPaddleFrame::read_from(&bytes).map(|(f, _)| f);
    check(
        "protocol",
        parsed == Some(frame),
        format!("{} byte frame round-trips", bytes.len()),
    );

    // 2. Timing precision: distribution of overshoot over element-length waits.
    let probe = Duration::from_millis(30);
    let mut overshoots: Vec<Duration> = Vec::with_capacity(200);
    for _ in 0..200 {
        let deadline = Instant::now() + probe;
        HybridWaiter::wait_until(deadline, &|| false);
        overshoots.push(Instant::now().saturating_duration_since(deadline));
    }
    overshoots.sort_unstable();
    let samples = overshoots.len();
    let median = overshoots[samples / 2];
    let worst = *overshoots.last().unwrap();
    // Count samples within target rather than a percentile: with 200 samples a "p99" is
    // the third-worst, so an OS preemption of the spinning thread would fail the check
    // while saying nothing about the timer itself.
    let within_target = overshoots.iter().filter(|o| **o < Duration::from_millis(1)).count();
    check(
        "timing",
        median < Duration::from_micros(500) && within_target >= samples - 5,
        format!(
            "median {:.3} ms, {within_target}/{samples} waits within 1 ms (worst {:.3} ms)",
            median.as_secs_f64() * 1000.0,
            worst.as_secs_f64() * 1000.0
        ),
    );

    // 3. Audio: envelope shaping never clips and starts smoothly.
    let (mut generator, key) = KeyedSineGenerator::new(48_000, 700, 0.5)?;
    key.key_down();
    let mut buf = vec![0.0f32; 480];
    generator.generate(&mut buf);
    let peak = buf.iter().fold(0.0f32, |m, s| m.max(s.abs()));
    check(
        "audio",
        peak <= 0.5 + 1e-6 && buf[0].abs() < 0.02,
        format!("peak {peak:.4} (limit 0.5000), first sample {:.4} (ramped)", buf[0]),
    );

    // 4. Scheduling: 'S' produces three evenly spaced dits.
    let schedule = EdgeScheduleBuilder::build("S", 20, 50, 0, 1_000_000_000)?;
    let downs: Vec<u64> = schedule.edges.iter().filter(|e| e.key_down).map(|e| e.timestamp_ticks).collect();
    check(
        "schedule",
        downs == vec![0, 120_000_000, 240_000_000],
        format!("S element starts {downs:?}"),
    );

    // 5. Station replay: jitter removed, and a key-down behind an unhealed gap latches.
    let replay = replay_check();
    check("replay", replay.0, replay.1);

    // 6. Driver: dedicated threads key an arriving frame and release it on shutdown.
    let driver = driver_check();
    check("driver", driver.0, driver.1);

    if failures == 0 {
        println!("all checks passed");
        Ok(())
    } else {
        Err(format!("{failures} check(s) failed").into())
    }
}

/// Records key/PTT transitions so the replay check can assert the keying waveform.
#[derive(Default)]
struct TransitionLog {
    keys: Vec<bool>,
}

impl KeyingOutput for TransitionLog {
    fn set_key(&mut self, key_down: bool) {
        self.keys.push(key_down);
    }
    fn set_ptt(&mut self, _asserted: bool) {}
}

/// Key transitions collected through a shared handle, for the driver check.
#[derive(Default)]
struct SharedKeys(Arc<Mutex<Vec<bool>>>);

impl KeyingOutput for SharedKeys {
    fn set_key(&mut self, key_down: bool) {
        self.0.lock().unwrap_or_else(|e| e.into_inner()).push(key_down);
    }
}

/// Runs the threaded replay driver briefly and returns (passed, detail).
fn driver_check() -> (bool, String) {
    let log = Arc::new(Mutex::new(Vec::new()));
    let config = JitterBufferConfig {
        direct_delay: Duration::from_millis(40),
        derp_delay: Duration::from_millis(40),
        adaptive_mode: false,
    };
    let mut replayer = EdgeReplayer::new(1_000_000_000, config, EdgeJitterProfile::PathAdaptive, PathType::Direct, None);
    replayer.begin_session(1);

    let mut handle = spawn_driver(
        replayer,
        Box::new(SharedKeys(Arc::clone(&log))),
        Arc::new(MonotonicClock),
        None,
    );

    let frame = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(1, 0, 0)]).unwrap().to_vec();
    handle.submit(frame);

    let deadline = Instant::now() + Duration::from_millis(500);
    while Instant::now() < deadline && !log.lock().unwrap_or_else(|e| e.into_inner()).contains(&true) {
        std::thread::sleep(Duration::from_millis(5));
    }
    let keyed = log.lock().unwrap_or_else(|e| e.into_inner()).contains(&true);

    // Shutdown must force the key up (F8) and join both threads.
    handle.stop();
    let keys = log.lock().unwrap_or_else(|e| e.into_inner()).clone();
    let released = keys.last() == Some(&false);

    (keyed && released, format!("keyed={keyed}, released on shutdown={released}"))
}

/// Exercises the Station replayer end to end and returns (passed, detail).
fn replay_check() -> (bool, String) {
    const GHZ: u64 = 1_000_000_000;
    const MS: i64 = 1_000_000;
    // A fixed 60ms buffer with adaptation off, so the deadlines are exact.
    let config = JitterBufferConfig {
        direct_delay: Duration::from_millis(60),
        derp_delay: Duration::from_millis(60),
        adaptive_mode: false,
    };

    let mut replayer = EdgeReplayer::new(GHZ, config, EdgeJitterProfile::PathAdaptive, PathType::Direct, None);
    let mut log = TransitionLog::default();
    replayer.begin_session(1);

    // Key-down at stream time 0; key-up at stream 100ms arriving 20ms late.
    let down = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(1, 0, 0)]).unwrap().to_vec();
    replayer.process_datagram(&down, 0);
    replayer.tick(60 * MS, &mut log);

    let up = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_up_at(2, 100, 0)]).unwrap().to_vec();
    replayer.process_datagram(&up, 80 * MS);
    replayer.tick(160 * MS, &mut log);

    let jitter_removed = log.keys == vec![true, false];

    // Edge 3 never arrives; edge 4 is a key-down, so the gap must latch SAFE.
    let behind_gap = RwkPaddleFrame::try_new(1, &[EdgeEntry::key_down_at(4, 200, 0)]).unwrap().to_vec();
    replayer.process_datagram(&behind_gap, 180 * MS);
    replayer.tick(240 * MS, &mut log);
    let latched = replayer.is_safe_latched() && !replayer.is_key_down();

    let passed = jitter_removed && latched;
    (
        passed,
        format!(
            "keyed {:?} with jitter removed={jitter_removed}, uninferable gap latched={latched}",
            log.keys
        ),
    )
}
