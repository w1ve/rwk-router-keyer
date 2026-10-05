//! Sidetone audio: a keyed sine oscillator with raised-cosine envelope shaping,
//! rendered by a continuously-running cpal output stream.
//!
//! Port of `RWK.Client.Audio.KeyedSineGenerator` / `LocalSidetoneEngine`.
//!
//! Two properties matter for good CW:
//!
//! * **No clicks.** The envelope ramps linearly between silence and full scale over
//!   [`Constants::ENVELOPE_RAMP_SECONDS`] and is then shaped with a raised cosine, so
//!   the waveform's slope is continuous at key-down and key-up.
//! * **No per-element stream churn.** The output stream runs continuously; keying only
//!   moves the envelope, so the first audio callback after a key-down already carries
//!   signal. Starting a stream per element would add device latency to every element.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};

use crate::primitives::limits;
use crate::Result;

/// Sidetone constants, shared with the front end.
pub mod constants {
    /// Sample rate used by the sidetone path.
    pub const DEFAULT_SAMPLE_RATE: u32 = 48_000;

    /// Raised-cosine attack/decay duration, in seconds (2ms).
    pub const ENVELOPE_RAMP_SECONDS: f64 = 0.002;

    /// Requested output buffer size, in milliseconds.
    pub const BUFFER_MILLISECONDS: u32 = 20;
}

/// A handle that controls the key state of a [`KeyedSineGenerator`] from another thread.
///
/// This is the only thing shared between the keyer thread and the audio callback: a
/// single atomic flag, so neither side ever blocks the other.
#[derive(Debug, Clone)]
pub struct KeyHandle {
    key_down: Arc<AtomicBool>,
}

impl KeyHandle {
    /// Requests key-down.
    pub fn key_down(&self) {
        self.key_down.store(true, Ordering::Release);
    }

    /// Requests key-up.
    pub fn key_up(&self) {
        self.key_down.store(false, Ordering::Release);
    }

    /// The current key state.
    #[must_use]
    pub fn is_key_down(&self) -> bool {
        self.key_down.load(Ordering::Acquire)
    }
}

/// Keyed sine oscillator with raised-cosine envelope shaping.
///
/// Owned by the audio render callback. All parameters are fixed at construction so the
/// render thread never reads a half-updated field.
pub struct KeyedSineGenerator {
    sample_rate: u32,
    frequency_hz: i32,
    amplitude: f64,
    ramp_samples: u32,
    envelope_step: f64,
    phase: f64,
    phase_increment: f64,
    envelope: f64,
    key_down: Arc<AtomicBool>,
}

impl KeyedSineGenerator {
    /// Creates a generator plus the [`KeyHandle`] that controls it.
    ///
    /// `frequency_hz` is clamped to 300-1500 and `amplitude` to 0.0-1.0.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when `sample_rate` is zero.
    pub fn new(sample_rate: u32, frequency_hz: i32, amplitude: f64) -> Result<(Self, KeyHandle)> {
        if sample_rate == 0 {
            return Err(crate::Error::Config("audio sample rate must be positive".into()));
        }

        let frequency_hz = clamp_frequency(frequency_hz);
        let amplitude = clamp_volume(amplitude);
        let ramp_samples = ((f64::from(sample_rate) * constants::ENVELOPE_RAMP_SECONDS) as u32).max(1);
        let key_down = Arc::new(AtomicBool::new(false));

        let generator = Self {
            sample_rate,
            frequency_hz,
            amplitude,
            ramp_samples,
            envelope_step: 1.0 / f64::from(ramp_samples),
            phase: 0.0,
            phase_increment: 2.0 * std::f64::consts::PI * f64::from(frequency_hz) / f64::from(sample_rate),
            envelope: 0.0,
            key_down: Arc::clone(&key_down),
        };

        Ok((generator, KeyHandle { key_down }))
    }

    /// Sample rate in Hz.
    #[must_use]
    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// Tone frequency in Hz, already clamped to 300-1500.
    #[must_use]
    pub fn frequency_hz(&self) -> i32 {
        self.frequency_hz
    }

    /// Peak amplitude, already clamped to 0.0-1.0.
    #[must_use]
    pub fn amplitude(&self) -> f64 {
        self.amplitude
    }

    /// Number of samples the envelope takes to travel between silence and full scale.
    #[must_use]
    pub fn ramp_samples(&self) -> u32 {
        self.ramp_samples
    }

    /// True while the key target state is down.
    #[must_use]
    pub fn is_key_down(&self) -> bool {
        self.key_down.load(Ordering::Acquire)
    }

    /// Linear ramp position, 0.0 to 1.0.
    #[must_use]
    pub fn current_envelope(&self) -> f64 {
        self.envelope
    }

    /// Fills `out` with mono samples, ramping the envelope toward the current key state.
    ///
    /// Allocates nothing and never blocks; safe to call from the audio callback.
    pub fn generate(&mut self, out: &mut [f32]) {
        let key_down = self.key_down.load(Ordering::Acquire);
        let mut envelope = self.envelope;
        let mut phase = self.phase;

        for sample in out.iter_mut() {
            if key_down {
                envelope = (envelope + self.envelope_step).min(1.0);
            } else {
                envelope = (envelope - self.envelope_step).max(0.0);
            }

            if envelope > 0.0 {
                *sample = (phase.sin() * shape(envelope) * self.amplitude) as f32;
                phase += self.phase_increment;
                if phase > std::f64::consts::TAU {
                    phase -= std::f64::consts::TAU;
                }
            } else {
                *sample = 0.0;
            }
        }

        self.envelope = envelope;
        self.phase = phase;
    }
}

/// Clamps a frequency to the permitted 300-1500 Hz range.
#[must_use]
pub fn clamp_frequency(frequency_hz: i32) -> i32 {
    frequency_hz.clamp(limits::MIN_TONE_HZ, limits::MAX_TONE_HZ)
}

/// Clamps a volume to 0.0-1.0, mapping NaN to 0.0.
#[must_use]
pub fn clamp_volume(volume: f64) -> f64 {
    if volume.is_nan() {
        0.0
    } else {
        volume.clamp(0.0, 1.0)
    }
}

/// Raised-cosine shaping: 0→0, 1→1, zero slope at both ends.
#[must_use]
pub fn shape(linear_envelope: f64) -> f64 {
    0.5 * (1.0 - (std::f64::consts::PI * linear_envelope).cos())
}

/// A running cpal sidetone output.
///
/// The engine owns a `cpal::Stream`, which is **not** `Send` on all platforms, so the
/// value must live on — and be dropped from — the thread that created it. The Tauri
/// layer keeps it on a dedicated audio thread.
pub struct SidetoneEngine {
    key: KeyHandle,
    sample_rate: u32,
    frequency_hz: i32,
    volume: f64,
    stream: Option<cpal::Stream>,
    device_name: Option<String>,
}

impl SidetoneEngine {
    /// Creates a stopped engine for the given tone and volume (both clamped).
    ///
    /// # Errors
    ///
    /// Propagates [`KeyedSineGenerator::new`] errors.
    pub fn new(frequency_hz: i32, volume: f64) -> Result<Self> {
        let (_, key) =
            KeyedSineGenerator::new(constants::DEFAULT_SAMPLE_RATE, frequency_hz, volume)?;
        Ok(Self {
            key,
            sample_rate: constants::DEFAULT_SAMPLE_RATE,
            frequency_hz: clamp_frequency(frequency_hz),
            volume: clamp_volume(volume),
            stream: None,
            device_name: None,
        })
    }

    /// The key handle, so the keyer can drive the tone without holding the engine.
    #[must_use]
    pub fn key_handle(&self) -> KeyHandle {
        self.key.clone()
    }

    /// True while an output stream is running.
    #[must_use]
    pub fn is_playing(&self) -> bool {
        self.stream.is_some()
    }

    /// Name of the device currently open, if any.
    #[must_use]
    pub fn device_name(&self) -> Option<&str> {
        self.device_name.as_deref()
    }

    /// The tone frequency in use.
    #[must_use]
    pub fn frequency_hz(&self) -> i32 {
        self.frequency_hz
    }

    /// The volume in use.
    #[must_use]
    pub fn volume(&self) -> f64 {
        self.volume
    }

    /// Lists available output device names.
    ///
    /// A machine with no audio subsystem yields an empty list rather than an error, so
    /// the UI can still render a device picker.
    #[must_use]
    pub fn output_devices() -> Vec<String> {
        let host = cpal::default_host();
        match host.output_devices() {
            Ok(devices) => devices.filter_map(|d| d.name().ok()).collect(),
            Err(_) => Vec::new(),
        }
    }

    /// Starts (or restarts) the output stream on the default output device.
    ///
    /// Key-up requests are honoured immediately; use [`Self::key_down`] to gate audio.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Audio`] when no output device is available or the stream
    /// cannot be built. Callers treat this as a warning and continue without sidetone.
    pub fn start(&mut self) -> Result<()> {
        self.stop();

        let host = cpal::default_host();
        let device = host
            .default_output_device()
            .ok_or_else(|| crate::Error::Audio("no default output device".into()))?;
        let device_name = device.name().unwrap_or_else(|_| "<unknown>".into());

        let supported = device
            .default_output_config()
            .map_err(|e| crate::Error::Audio(format!("default output config: {e}")))?;
        let sample_format = supported.sample_format();
        let stream_config: cpal::StreamConfig = supported.config();
        let channels = stream_config.channels as usize;

        let (mut generator, key) =
            KeyedSineGenerator::new(self.sample_rate, self.frequency_hz, self.volume)?;
        self.key = key;

        let err_fn = |e| eprintln!("sidetone stream error: {e}");

        let stream = match sample_format {
            cpal::SampleFormat::F32 => device.build_output_stream(
                &stream_config,
                move |data: &mut [f32], _| fill_channels(&mut generator, data, channels),
                err_fn,
                None,
            ),
            cpal::SampleFormat::I16 => device.build_output_stream(
                &stream_config,
                move |data: &mut [i16], _| fill_channels_i16(&mut generator, data, channels),
                err_fn,
                None,
            ),
            cpal::SampleFormat::U16 => device.build_output_stream(
                &stream_config,
                move |data: &mut [u16], _| fill_channels_u16(&mut generator, data, channels),
                err_fn,
                None,
            ),
            other => {
                return Err(crate::Error::Audio(format!("unsupported sample format {other:?}")))
            }
        }
        .map_err(|e| crate::Error::Audio(format!("build output stream: {e}")))?;

        stream.play().map_err(|e| crate::Error::Audio(format!("start stream: {e}")))?;

        self.stream = Some(stream);
        self.device_name = Some(device_name);
        Ok(())
    }

    /// Requests key-down on the sidetone.
    pub fn key_down(&self) {
        self.key.key_down();
    }

    /// Requests key-up on the sidetone.
    pub fn key_up(&self) {
        self.key.key_up();
    }

    /// Stops the output stream, releasing the audio device.
    pub fn stop(&mut self) {
        if let Some(stream) = self.stream.take() {
            // Pause before dropping so the callback is not mid-render on teardown.
            let _ = stream.pause();
            drop(stream);
        }
        self.device_name = None;
    }
}

impl Drop for SidetoneEngine {
    fn drop(&mut self) {
        self.key.key_up();
        self.stop();
    }
}

/// Renders `generator` into an interleaved f32 buffer, duplicating mono samples across
/// every output channel.
fn fill_channels(generator: &mut KeyedSineGenerator, data: &mut [f32], channels: usize) {
    let frames = data.len() / channels.max(1);
    let mut scratch = vec![0.0f32; frames];
    generator.generate(&mut scratch);
    for (frame, sample) in scratch.iter().enumerate() {
        for channel in 0..channels {
            data[frame * channels + channel] = *sample;
        }
    }
}

/// As [`fill_channels`], for 16-bit signed integer output.
fn fill_channels_i16(generator: &mut KeyedSineGenerator, data: &mut [i16], channels: usize) {
    let frames = data.len() / channels.max(1);
    let mut scratch = vec![0.0f32; frames];
    generator.generate(&mut scratch);
    for (frame, sample) in scratch.iter().enumerate() {
        let value = (sample.clamp(-1.0, 1.0) * f32::from(i16::MAX)) as i16;
        for channel in 0..channels {
            data[frame * channels + channel] = value;
        }
    }
}

/// As [`fill_channels`], for 16-bit unsigned integer output.
fn fill_channels_u16(generator: &mut KeyedSineGenerator, data: &mut [u16], channels: usize) {
    let frames = data.len() / channels.max(1);
    let mut scratch = vec![0.0f32; frames];
    generator.generate(&mut scratch);
    for (frame, sample) in scratch.iter().enumerate() {
        let scaled = (sample.clamp(-1.0, 1.0) + 1.0) * 0.5 * f32::from(u16::MAX);
        let value = scaled as u16;
        for channel in 0..channels {
            data[frame * channels + channel] = value;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frequency_and_volume_are_clamped() {
        assert_eq!(clamp_frequency(10), limits::MIN_TONE_HZ);
        assert_eq!(clamp_frequency(9_999), limits::MAX_TONE_HZ);
        assert_eq!(clamp_volume(-1.0), 0.0);
        assert_eq!(clamp_volume(2.0), 1.0);
        assert_eq!(clamp_volume(f64::NAN), 0.0);
    }

    #[test]
    fn shape_endpoints_match_raised_cosine() {
        assert!((shape(0.0)).abs() < 1e-12);
        assert!((shape(1.0) - 1.0).abs() < 1e-12);
        // Zero slope at both ends: derivative is ~0, so near 0 the value stays tiny.
        assert!(shape(0.01) < 0.001);
        assert!(shape(0.99) > 0.999);
    }

    #[test]
    fn ramp_is_two_milliseconds_at_48k() {
        let (gen, _key) = KeyedSineGenerator::new(48_000, 700, 0.5).unwrap();
        assert_eq!(gen.ramp_samples(), 96);
    }

    #[test]
    fn key_up_starts_silent_and_never_clips() {
        let (mut gen, key) = KeyedSineGenerator::new(48_000, 700, 0.5).unwrap();
        // While keyed up and never keyed down, output is exactly silence.
        let mut buf = vec![0.0f32; 480];
        gen.generate(&mut buf);
        assert!(buf.iter().all(|s| *s == 0.0), "silent while key-up");

        // After key-down the envelope ramps; output must never exceed the amplitude.
        key.key_down();
        for _ in 0..100 {
            gen.generate(&mut buf);
            assert!(
                buf.iter().all(|s| s.abs() <= 0.5 + 1e-6),
                "sidetone exceeded the configured amplitude"
            );
        }
    }

    #[test]
    fn first_sound_after_key_down_is_ramped_not_a_step() {
        let (mut gen, key) = KeyedSineGenerator::new(48_000, 700, 1.0).unwrap();
        key.key_down();
        let mut buf = vec![0.0f32; 1];
        gen.generate(&mut buf);
        // One sample into a 96-sample ramp: the raised-cosine envelope is still tiny,
        // so there is no click at the boundary.
        assert!(buf[0].abs() < 0.02, "expected a smooth attack, got {}", buf[0]);
        // But the envelope is genuinely moving.
        assert!(gen.current_envelope() > 0.0);
    }

    #[test]
    fn envelope_reaches_full_scale_after_the_ramp() {
        let (mut gen, key) = KeyedSineGenerator::new(48_000, 700, 0.5).unwrap();
        key.key_down();
        let mut buf = vec![0.0f32; 96];
        gen.generate(&mut buf);
        assert!((gen.current_envelope() - 1.0).abs() < 1e-9);
    }

    #[test]
    fn key_up_decays_then_goes_silent() {
        let (mut gen, key) = KeyedSineGenerator::new(48_000, 700, 0.5).unwrap();
        key.key_down();
        let mut buf = vec![0.0f32; 96];
        gen.generate(&mut buf);
        key.key_up();
        gen.generate(&mut buf);
        assert!(gen.current_envelope() < 1.0);
        // Enough buffers to fully decay.
        for _ in 0..3 {
            gen.generate(&mut buf);
        }
        assert_eq!(gen.current_envelope(), 0.0);
    }

    #[test]
    fn zero_sample_rate_is_rejected() {
        assert!(KeyedSineGenerator::new(0, 700, 0.5).is_err());
    }

    #[test]
    fn key_handle_reflects_state() {
        let (_gen, key) = KeyedSineGenerator::new(48_000, 700, 0.5).unwrap();
        assert!(!key.is_key_down());
        key.key_down();
        assert!(key.is_key_down());
        key.key_up();
        assert!(!key.is_key_down());
    }

    #[test]
    fn engine_constructs_without_hardware() {
        let engine = SidetoneEngine::new(1_000, 0.4).unwrap();
        assert_eq!(engine.frequency_hz(), 1_000);
        assert!((engine.volume() - 0.4).abs() < 1e-9);
        assert!(!engine.is_playing());
    }
}
