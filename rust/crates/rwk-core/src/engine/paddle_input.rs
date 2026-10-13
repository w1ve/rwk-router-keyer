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

        Ok(Self { port_name: port_name.to_string(), port: Some(port) })
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
        let Some(port) = self.port.as_mut() else {
            return Ok(ContactSample::none());
        };
        // CTS → dit, DSR → dah, DCD → straight key.
        let dit = port.read_clear_to_send()?;
        let dah = port.read_data_set_ready()?;
        let straight = port.read_carrier_detect()?;
        Ok(ContactSample { dit, dah, straight })
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
}
