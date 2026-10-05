//! Serial keying output: port enumeration, DTR/RTS control, and PTT sequencing.
//!
//! Port of `RWK.Shared.Config.KeyingOutputConfig` / `PttTimingConfig` and the
//! Station's `SerialKeyingOutput` / `PttSequencer`.
//!
//! Key and PTT are driven by the hardware control lines directly — no bytes are
//! written to the port — which is what makes sub-millisecond keying possible:
//!
//! * Windows / macOS / Linux are handled by `serialport`; the port name is whatever
//!   the OS reports (`COM3`, `/dev/tty.usbmodem1234`, `/dev/ttyUSB0`). Use
//!   [`enumerate_ports`] rather than hard-coding names.
//! * [`KeyingOutputConfig::line_state`] computes the DTR/RTS levels for a given
//!   key/PTT state, including polarity inversion, without touching hardware — so the
//!   mapping is unit-testable.

use std::time::Duration;

use serialport::{SerialPort, SerialPortInfo};

pub use serialport::{SerialPortType, UsbPortInfo};

use crate::primitives::{limits, KeyingLine};
use crate::Result;

/// Returns the serial ports currently present on the machine.
///
/// Portable across Windows (`COMx`), macOS (`/dev/tty.usbmodem*`) and Linux
/// (`/dev/ttyUSB*`). A machine with no ports returns an empty vector, not an error.
///
/// # Errors
///
/// Returns [`crate::Error::SerialIo`] if the platform enumeration call fails.
pub fn enumerate_ports() -> Result<Vec<SerialPortInfo>> {
    Ok(serialport::available_ports()?)
}

/// Serial keying output settings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeyingOutputConfig {
    /// Serial port used for keying, e.g. `COM3`.
    pub port_name: String,
    /// Control line asserted for key-down: RTS or DTR.
    pub key_line: KeyingLine,
    /// Control line asserted for PTT: RTS, DTR, or none.
    pub ptt_line: KeyingLine,
    /// Inverts the key line's polarity.
    pub key_invert: bool,
    /// Inverts the PTT line's polarity.
    pub ptt_invert: bool,
    /// Baud rate. Control-line keying ignores the data rate; a conventional value is
    /// used so the port opens on every platform.
    pub baud_rate: u32,
}

impl Default for KeyingOutputConfig {
    fn default() -> Self {
        Self {
            port_name: String::new(),
            key_line: KeyingLine::Dtr,
            // A default PTT line of DTR would collide with the default key line, so
            // PTT is explicitly off rather than left at `Default::default()`.
            ptt_line: KeyingLine::None,
            key_invert: false,
            ptt_invert: false,
            baud_rate: 9600,
        }
    }
}

/// The DTR/RTS levels implied by a key/PTT state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LineState {
    /// Data Terminal Ready level.
    pub dtr: bool,
    /// Request To Send level.
    pub rts: bool,
}

impl KeyingOutputConfig {
    /// Computes the control-line levels to apply for a key/PTT state.
    ///
    /// `key_down` is the CW key state; `ptt_asserted` is the PTT state *after* the
    /// lead/tail sequencer has decided it should be on. Inversion is applied per line.
    #[must_use]
    pub fn line_state(&self, key_down: bool, ptt_asserted: bool) -> LineState {
        let mut state = LineState::default();

        let key_level = key_down ^ self.key_invert;
        match self.key_line {
            KeyingLine::Dtr => state.dtr = key_level,
            KeyingLine::Rts => state.rts = key_level,
            KeyingLine::None => {}
        }

        let ptt_level = ptt_asserted ^ self.ptt_invert;
        match self.ptt_line {
            KeyingLine::Dtr => state.dtr = state.dtr || ptt_level,
            KeyingLine::Rts => state.rts = state.rts || ptt_level,
            KeyingLine::None => {}
        }

        state
    }

    /// True when a key line is assigned.
    #[must_use]
    pub fn has_key_line(&self) -> bool {
        self.key_line != KeyingLine::None
    }

    /// True when PTT is driven by this port.
    #[must_use]
    pub fn has_ptt_line(&self) -> bool {
        self.ptt_line != KeyingLine::None
    }
}

/// PTT lead and tail timing for the Station keying output.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PttTiming {
    /// How far ahead of key-down PTT is asserted.
    pub lead: Duration,
    /// How long PTT is held after key-up before being de-asserted; extended by each
    /// subsequent key-down.
    pub tail: Duration,
}

impl Default for PttTiming {
    fn default() -> Self {
        Self {
            lead: Duration::from_millis(limits::DEFAULT_PTT_LEAD_MS),
            tail: Duration::from_millis(limits::DEFAULT_PTT_TAIL_MS),
        }
    }
}

/// An action the PTT sequencer wants taken at a given tick.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PttAction {
    /// Assert PTT (after applying line inversion).
    Assert {
        /// Tick at which to assert.
        at_ticks: u64,
    },
    /// De-assert PTT.
    Release {
        /// Tick at which to release.
        at_ticks: u64,
    },
}

/// Decides when PTT is asserted and released around key transitions.
///
/// PTT is asserted `lead` before the first key-down and released `tail` after the
/// last key-up, with each subsequent key-down extending the release.
#[derive(Debug, Clone)]
pub struct PttSequencer {
    timing: PttTiming,
    tick_hz: u64,
    asserted: bool,
    release_at: Option<u64>,
}

impl PttSequencer {
    /// Creates a sequencer for the given timing and clock tick rate.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Config`] when `tick_hz` is zero.
    pub fn new(timing: PttTiming, tick_hz: u64) -> Result<Self> {
        if tick_hz == 0 {
            return Err(crate::Error::Config("tick rate must be positive".into()));
        }
        Ok(Self { timing, tick_hz, asserted: false, release_at: None })
    }

    /// True while PTT is currently asserted.
    #[must_use]
    pub fn is_asserted(&self) -> bool {
        self.asserted
    }

    fn ticks(&self, d: Duration) -> u64 {
        (d.as_nanos() as u64) * self.tick_hz / 1_000_000_000
    }

    /// Feeds a key transition, returning the PTT action to take.
    #[must_use]
    pub fn on_key_edge(&mut self, key_down: bool, at_ticks: u64) -> Option<PttAction> {
        if key_down {
            // Every key-down pushes the release out by the tail.
            self.release_at = Some(at_ticks + self.ticks(self.timing.tail));
            if !self.asserted {
                self.asserted = true;
                let assert_at = at_ticks.saturating_sub(self.ticks(self.timing.lead));
                return Some(PttAction::Assert { at_ticks: assert_at });
            }
            None
        } else {
            let release_at = at_ticks + self.ticks(self.timing.tail);
            self.release_at = Some(release_at);
            None
        }
    }

    /// If the idle deadline has passed, de-asserts PTT and returns the release action.
    pub fn poll(&mut self, now_ticks: u64) -> Option<PttAction> {
        if self.asserted {
            if let Some(at) = self.release_at {
                if now_ticks >= at {
                    self.asserted = false;
                    self.release_at = None;
                    return Some(PttAction::Release { at_ticks: at });
                }
            }
        }
        None
    }

    /// Forces an immediate release, e.g. on shutdown or an emergency stop.
    pub fn force_release(&mut self) -> Option<PttAction> {
        if self.asserted {
            self.asserted = false;
            self.release_at = None;
            return Some(PttAction::Release { at_ticks: 0 });
        }
        None
    }
}

/// An open serial keying output.
///
/// Dropping this value closes the port and releases the OS handle, which is what the
/// clean-shutdown requirement depends on — no explicit `close()` call is required, but
/// [`Self::close`] is provided for deterministic teardown.
pub struct SerialKeyingOutput {
    config: KeyingOutputConfig,
    port: Option<Box<dyn SerialPort>>,
}

impl SerialKeyingOutput {
    /// Opens the configured port and prepares it for control-line keying.
    ///
    /// The port is opened at the configured baud rate with no flow control, since only
    /// DTR/RTS are used.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Serial`] if the port cannot be opened.
    pub fn open(config: KeyingOutputConfig) -> Result<Self> {
        let port = serialport::new(&config.port_name, config.baud_rate)
            .timeout(Duration::from_millis(100))
            .open()
            .map_err(|source| crate::Error::Serial { port: config.port_name.clone(), source })?;

        let mut output = Self { config, port: Some(port) };
        // Start from a known state: nothing keyed, no PTT.
        output.apply(true, false)?;
        Ok(output)
    }

    /// The configuration in use.
    #[must_use]
    pub fn config(&self) -> &KeyingOutputConfig {
        &self.config
    }

    /// True while the port is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.port.is_some()
    }

    /// The port name.
    #[must_use]
    pub fn port_name(&self) -> &str {
        &self.config.port_name
    }

    /// Applies a key/PTT state to the port's control lines.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::SerialIo`] if either control line cannot be written.
    pub fn apply(&mut self, key_down: bool, ptt_asserted: bool) -> Result<()> {
        let levels = self.config.line_state(key_down, ptt_asserted);
        let Some(port) = self.port.as_mut() else {
            return Ok(());
        };
        port.write_data_terminal_ready(levels.dtr)?;
        port.write_request_to_send(levels.rts)?;
        Ok(())
    }

    /// Returns the port to a safe state (key up, PTT released).
    ///
    /// # Errors
    ///
    /// Propagates write errors from [`Self::apply`].
    pub fn release_lines(&mut self) -> Result<()> {
        self.apply(false, false)
    }

    /// Closes the port, releasing the OS handle.
    pub fn close(&mut self) {
        if let Some(mut port) = self.port.take() {
            let _ = port.write_data_terminal_ready(false);
            let _ = port.write_request_to_send(false);
            // Dropping the boxed port closes the handle.
        }
    }
}

impl Drop for SerialKeyingOutput {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_config_does_not_drive_ptt_on_the_key_line() {
        let cfg = KeyingOutputConfig::default();
        assert_eq!(cfg.key_line, KeyingLine::Dtr);
        assert_eq!(cfg.ptt_line, KeyingLine::None);
        assert!(cfg.has_key_line());
        assert!(!cfg.has_ptt_line());
    }

    #[test]
    fn line_state_maps_dtr_keying() {
        let cfg = KeyingOutputConfig::default();
        assert_eq!(cfg.line_state(false, false), LineState { dtr: false, rts: false });
        assert_eq!(cfg.line_state(true, false), LineState { dtr: true, rts: false });
    }

    #[test]
    fn line_state_maps_rts_keying() {
        let cfg = KeyingOutputConfig { key_line: KeyingLine::Rts, ..Default::default() };
        assert_eq!(cfg.line_state(true, false), LineState { dtr: false, rts: true });
    }

    #[test]
    fn key_inversion_flips_polarity() {
        let cfg = KeyingOutputConfig { key_invert: true, ..Default::default() };
        assert_eq!(cfg.line_state(false, false), LineState { dtr: true, rts: false });
        assert_eq!(cfg.line_state(true, false), LineState { dtr: false, rts: false });
    }

    #[test]
    fn ptt_and_key_can_share_distinct_lines() {
        let cfg = KeyingOutputConfig {
            key_line: KeyingLine::Dtr,
            ptt_line: KeyingLine::Rts,
            ..Default::default()
        };
        assert_eq!(cfg.line_state(true, true), LineState { dtr: true, rts: true });
        assert_eq!(cfg.line_state(false, true), LineState { dtr: false, rts: true });
    }

    #[test]
    fn ptt_sequencer_asserts_lead_before_key_down() {
        let mut seq = PttSequencer::new(PttTiming::default(), 1_000).unwrap();
        let action = seq.on_key_edge(true, 1_000_000); // key down at tick 1_000_000 (ms scale)
        assert_eq!(action, Some(PttAction::Assert { at_ticks: 1_000_000 - 15 }));
        assert!(seq.is_asserted());
    }

    #[test]
    fn ptt_sequencer_holds_tail_then_releases() {
        let mut seq = PttSequencer::new(PttTiming::default(), 1_000).unwrap();
        let _ = seq.on_key_edge(true, 10_000);
        let _ = seq.on_key_edge(false, 10_100);
        // Tail is 500ms; still asserted at 10_500.
        assert!(seq.poll(10_500).is_none());
        assert!(seq.poll(10_600).is_some());
        assert!(!seq.is_asserted());
    }

    #[test]
    fn subsequent_key_down_extends_the_tail() {
        let mut seq = PttSequencer::new(PttTiming::default(), 1_000).unwrap();
        let _ = seq.on_key_edge(true, 1_000);
        let _ = seq.on_key_edge(false, 2_000);
        let _ = seq.on_key_edge(true, 2_100); // extends release to 2_600
        assert!(seq.poll(2_400).is_none(), "should still be asserted");
        assert!(seq.poll(2_600).is_some());
    }

    #[test]
    fn force_release_drops_ptt_immediately() {
        let mut seq = PttSequencer::new(PttTiming::default(), 1_000).unwrap();
        let _ = seq.on_key_edge(true, 5_000);
        assert!(seq.force_release().is_some());
        assert!(!seq.is_asserted());
        assert!(seq.force_release().is_none());
    }

    #[test]
    fn ptt_sequencer_rejects_zero_tick_rate() {
        assert!(PttSequencer::new(PttTiming::default(), 0).is_err());
    }

    #[test]
    fn enumerate_ports_does_not_error_on_a_machine_with_no_ports() {
        // Must return Ok (possibly empty); never panic.
        let _ = enumerate_ports().expect("enumeration");
    }
}
