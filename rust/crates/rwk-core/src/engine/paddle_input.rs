//! Serial paddle input: reading a key/paddle wired to a serial port's control lines.
//!
//! This is the inbound counterpart to [`SerialKeyingOutput`](crate::engine::serial::SerialKeyingOutput).
//! Where the keying output *drives* DTR/RTS to key a rig, this *reads* the modem-status
//! input lines a paddle or straight key closes:
//!
//! | Line | Contact      | `serialport` read            |
//! |------|--------------|------------------------------|
//! | CTS  | dit          | `read_clear_to_send`         |
//! | DSR  | dah          | `read_data_set_ready`        |
//! | DCD  | straight key | `read_carrier_detect`        |
//!
//! This is the exact pin map the proven C# `PaddleInputPoller` uses (CTS→dit, DSR→dah,
//! DCD→straight). The paddle's contacts switch a voltage onto those input pins, and that
//! voltage source is **DTR**, which we assert on open — without it the inputs never change.
//! Some USB-to-serial drivers only honour one of the two ways to raise DTR, so, like the C#
//! code, we both open with DTR high and explicitly write it.
//!
//! ## Why the `serialport` crate rather than raw Win32
//!
//! The C# build reads the modem-status register with `GetCommModemStatus` over a raw
//! `CreateFile` handle. The `serialport` crate already wraps the same OS calls portably
//! (`ClearCommError`/`GetCommModemStatus` on Windows, `TIOCMGET` ioctls on Linux/macOS), so a
//! native UART and a USB-to-serial adapter present the identical control-line reads with no
//! driver-specific code. We reuse it rather than re-implementing the FFI, keeping the one
//! `unsafe` seam in [`crate::platform`].
//!
//! The port opened here is the paddle's own handle, independent of any keying-output port —
//! they are normally different physical ports anyway, and opening one port twice is an OS
//! conflict to avoid.

use std::time::Duration;

use serialport::SerialPort;

use crate::engine::keying::PaddleState;
use crate::Result;

/// The raw contact snapshot read from the control lines: the two paddle contacts plus a
/// straight-key contact, before any debounce or dit/dah reversal.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ContactSample {
    /// CTS: the dit paddle contact is closed.
    pub dit: bool,
    /// DSR: the dah paddle contact is closed.
    pub dah: bool,
    /// DCD: the straight-key contact is closed.
    pub straight: bool,
}

impl ContactSample {
    /// All contacts open.
    #[must_use]
    pub fn none() -> Self {
        Self::default()
    }

    /// This sample interpreted relative to a resting `baseline` (per-line XOR): a line that
    /// idles closed in the baseline only reads as a contact when it differs from rest. Used to
    /// neutralize a floating/idle-high port so it never produces a stuck key.
    #[must_use]
    pub fn relative_to(self, baseline: ContactSample) -> ContactSample {
        ContactSample {
            dit: self.dit ^ baseline.dit,
            dah: self.dah ^ baseline.dah,
            straight: self.straight ^ baseline.straight,
        }
    }

    /// Converts to a [`PaddleState`], optionally swapping dit/dah (paddle reverse). A closed
    /// straight-key contact folds into the dit contact, matching how
    /// [`KeyerMode::Straight`](crate::primitives::KeyerMode::Straight) keys from the dit line.
    #[must_use]
    pub fn to_paddle_state(self, reverse: bool) -> PaddleState {
        let (dit, dah) = if reverse { (self.dah, self.dit) } else { (self.dit, self.dah) };
        PaddleState { dit: dit || self.straight, dah }
    }
}

/// An open serial paddle input port.
///
/// Opening asserts DTR as the contact voltage source. Dropping the value closes the port and
/// releases the OS handle; no explicit close is required, but [`Self::close`] is provided for
/// deterministic teardown.
pub struct PaddleInputPort {
    port_name: String,
    port: Option<Box<dyn SerialPort>>,
    /// The resting state of each control line, captured just after open. A paddle/key at rest
    /// leaves its contacts OPEN, so the expected baseline is all-false. But a port with nothing
    /// wired (or a USB-serial adapter whose modem-status lines idle HIGH) reads a line as
    /// *asserted* at rest — which, taken literally, is a permanently-held key (a solid tone).
    /// Capturing the baseline lets [`Self::read`] interpret each line *relative to its own
    /// resting state* (effective = raw XOR baseline), so a line that is closed-at-open counts
    /// as idle and only a genuine change from rest registers as a contact. This auto-calibrates
    /// per line and makes a floating/unconfigured port never key.
    baseline: ContactSample,
}

impl PaddleInputPort {
    /// Opens `port_name` for reading paddle contacts and asserts DTR so the contacts have a
    /// voltage source. Baud rate is irrelevant to control-line sensing but the port must
    /// still open with a valid rate, so a conventional 9600 is used.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::Serial`] if the port cannot be opened, or
    /// [`crate::Error::SerialIo`] if DTR cannot be asserted.
    pub fn open(port_name: &str) -> Result<Self> {
        let mut port = serialport::new(port_name, 9600)
            .timeout(Duration::from_millis(50))
            .open()
            .map_err(|source| crate::Error::Serial { port: port_name.to_string(), source })?;

        // DTR is the voltage source the paddle contacts switch onto CTS/DSR/DCD. Assert it
        // explicitly; RTS is left low. This mirrors the C# AssertDtr step.
        port.write_data_terminal_ready(true)?;
        port.write_request_to_send(false)?;

        // Capture the resting state of the lines so a floating/idle-high port (nothing wired)
        // does not read as a permanently-held key. Read a couple of times and let DTR settle
        // first; a transient on the very first read would mis-calibrate the baseline.
        std::thread::sleep(Duration::from_millis(20));
        let baseline = Self::read_raw(&mut port).unwrap_or_default();

        Ok(Self { port_name: port_name.to_string(), port: Some(port), baseline })
    }

    /// Reads the three control lines into a raw [`ContactSample`], with no baseline applied.
    fn read_raw(port: &mut Box<dyn SerialPort>) -> Result<ContactSample> {
        // CTS → dit, DSR → dah, DCD → straight key.
        let dit = port.read_clear_to_send()?;
        let dah = port.read_data_set_ready()?;
        let straight = port.read_carrier_detect()?;
        Ok(ContactSample { dit, dah, straight })
    }

    /// The port name.
    #[must_use]
    pub fn port_name(&self) -> &str {
        &self.port_name
    }

    /// Reads the current contact states from the control lines.
    ///
    /// Returns the raw (un-debounced, un-reversed) sample. A read failure almost always means
    /// the adapter was unplugged mid-session; the caller should treat it as a fault, release
    /// the port, and stop polling — exactly as the C# poller does on a failed
    /// `GetCommModemStatus`.
    ///
    /// # Errors
    ///
    /// Returns [`crate::Error::SerialIo`] if any control line cannot be read.
    pub fn read(&mut self) -> Result<ContactSample> {
        let baseline = self.baseline;
        let Some(port) = self.port.as_mut() else {
            return Ok(ContactSample::none());
        };
        let raw = Self::read_raw(port)?;
        // Interpret each line relative to its resting state: a line that idles closed
        // (floating/unwired) only registers a contact when it *changes* from rest, so an
        // unconfigured port never produces a stuck key.
        Ok(raw.relative_to(baseline))
    }

    /// Closes the port, dropping DTR and releasing the OS handle.
    pub fn close(&mut self) {
        if let Some(mut port) = self.port.take() {
            let _ = port.write_data_terminal_ready(false);
            // Dropping the boxed port closes the handle.
        }
    }
}

impl Drop for PaddleInputPort {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn contact_sample_maps_to_paddle_state() {
        let s = ContactSample { dit: true, dah: false, straight: false };
        let ps = s.to_paddle_state(false);
        assert!(ps.dit);
        assert!(!ps.dah);
    }

    #[test]
    fn paddle_reverse_swaps_dit_and_dah() {
        let s = ContactSample { dit: true, dah: false, straight: false };
        let ps = s.to_paddle_state(true);
        assert!(!ps.dit, "reversed: dit contact becomes dah");
        assert!(ps.dah);
    }

    #[test]
    fn straight_key_folds_into_the_dit_contact() {
        let s = ContactSample { dit: false, dah: false, straight: true };
        let ps = s.to_paddle_state(false);
        assert!(ps.dit, "straight key keys through the dit line");
        assert!(!ps.dah);
        // Even reversed, a straight-key contact still keys (it ORs into whichever is dit).
        let reversed = s.to_paddle_state(true);
        assert!(reversed.dit);
    }

    #[test]
    fn none_sample_is_idle() {
        assert!(ContactSample::none().to_paddle_state(false).is_idle());
    }

    #[test]
    fn a_floating_port_that_idles_closed_reads_as_idle_not_a_stuck_key() {
        // A port with nothing wired idles with all lines asserted (this is the bug source).
        let baseline = ContactSample { dit: true, dah: true, straight: true };
        // At rest, the raw read equals the baseline → relative reads all-open (idle), so the
        // keyer never sees a held contact and the tone never sticks on.
        let at_rest = baseline.relative_to(baseline);
        assert!(at_rest.to_paddle_state(false).is_idle(), "a floating port must read idle");
        assert!(!at_rest.dit && !at_rest.dah && !at_rest.straight);
    }

    #[test]
    fn a_normal_port_idles_open_and_keys_on_contact() {
        // A properly-wired paddle idles with lines open (baseline all-false).
        let baseline = ContactSample::none();
        // Resting: idle.
        assert!(ContactSample::none().relative_to(baseline).to_paddle_state(false).is_idle());
        // Dit contact closes → registers as dit.
        let dit_closed = ContactSample { dit: true, dah: false, straight: false };
        assert!(dit_closed.relative_to(baseline).to_paddle_state(false).dit);
    }

    #[test]
    fn a_floating_line_registers_a_contact_only_when_it_changes_from_rest() {
        // Straight-key line idles closed (floating high); the others idle open.
        let baseline = ContactSample { dit: false, dah: false, straight: true };
        // Rest: straight reads idle (open), because it matches its closed baseline.
        let rest = ContactSample { dit: false, dah: false, straight: true }.relative_to(baseline);
        assert!(!rest.straight, "a closed-at-rest straight line reads idle");
        // The operator actually keys the straight contact → the line now reads OPEN (changed
        // from its closed rest) → registers as a press.
        let pressed = ContactSample { dit: false, dah: false, straight: false }.relative_to(baseline);
        assert!(pressed.straight, "a change from the closed rest registers as a contact");
    }
}
