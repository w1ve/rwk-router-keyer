//! Guards the sidetone audio engine against a "stuck tone" at the AUDIO layer: a freshly
//! started engine, with the key never pressed, must be silent — the key state must be UP and
//! the envelope must stay at zero. This exercises the real `SidetoneEngine::start`/`start_on`
//! path (the device-picker change), which the tunnel/keying tests never touch.
//!
//! If no audio output device is present (headless CI), the start call returns an error and the
//! engine is simply not playing; the test still asserts the key-state invariant, which needs
//! no device.

use std::time::Duration;

use rwk_core::engine::audio::{KeyedSineGenerator, SidetoneEngine};

/// A fresh engine must report key-UP and, once started on the default device, must not be
/// keying. Starting a stream must never implicitly key down.
#[test]
fn a_fresh_sidetone_engine_is_not_keyed() {
    let mut engine = SidetoneEngine::new(600, 0.7).expect("engine");
    // Not started yet: definitely silent.
    assert!(!engine.is_playing(), "a new engine has no stream");

    // Try to start on the default device. On a headless box with no device this errors; that
    // is fine — we only require that *if* it starts, it starts key-UP (silent).
    match engine.start() {
        Ok(()) => {
            assert!(engine.is_playing(), "start() opened a stream");
            // The key handle must be UP immediately after start — nothing should key it down.
            assert!(
                !engine.key_handle().is_key_down(),
                "a just-started sidetone must be key-UP (silent), not a solid tone"
            );
            // Let the stream render for a moment, then confirm it is still key-up.
            std::thread::sleep(Duration::from_millis(100));
            assert!(
                !engine.key_handle().is_key_down(),
                "an idle sidetone must stay key-UP"
            );
            engine.stop();
        }
        Err(_) => {
            // No audio device available; the key-state invariant still holds via the handle.
            assert!(!engine.key_handle().is_key_down());
        }
    }
}

/// Keying the engine down then up must leave it UP — and the envelope must decay to zero so
/// no tone lingers. Uses the generator directly so the envelope is observable without a device.
#[test]
fn key_down_then_up_decays_the_envelope_to_silence() {
    let (mut gen, key) = KeyedSineGenerator::new(48_000, 600, 0.7).unwrap();
    let mut buf = vec![0f32; 4800]; // 100ms at 48k

    // Key down, render: envelope rises, tone present.
    key.key_down();
    gen.generate(&mut buf);
    assert!(gen.current_envelope() > 0.0, "key-down must open the envelope");
    assert!(buf.iter().any(|s| s.abs() > 0.0), "key-down must produce audio");

    // Key up, render enough to fully decay: envelope returns to zero, output is silent.
    key.key_up();
    gen.generate(&mut buf);
    assert_eq!(gen.current_envelope(), 0.0, "key-up must close the envelope to zero");
    // After decay the tail of the buffer must be pure silence.
    let tail = &buf[buf.len() - 480..];
    assert!(tail.iter().all(|s| *s == 0.0), "a keyed-up sidetone must be silent");
}

/// `start_on(None)` (the device-picker default path) must behave exactly like `start()` —
/// key-UP, not a solid tone — so the device-picker change did not introduce a stuck tone.
#[test]
fn start_on_default_device_is_not_keyed() {
    let mut engine = SidetoneEngine::new(700, 0.5).expect("engine");
    match engine.start_on(None) {
        Ok(()) => {
            assert!(!engine.key_handle().is_key_down(), "start_on(None) must be key-UP");
            engine.stop();
        }
        Err(_) => {
            assert!(!engine.key_handle().is_key_down());
        }
    }
}
