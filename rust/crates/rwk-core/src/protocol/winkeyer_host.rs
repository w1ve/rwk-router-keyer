//! A WinKeyer2 host-stream decoder: the inbound half of the WinKeyer protocol.
//!
//! [`winkeyer`](crate::protocol::winkeyer) defines the command/admin/const byte tables and
//! builds commands the *host* sends to a keyer. This module is the other direction: a
//! stateful parser that consumes the byte stream a **logging program** (N1MM+, DXLog,
//! Win-Test, …) sends to what it believes is a K1EL WinKeyer, and turns it into
//! [`HostEvent`]s the Station can act on — set speed, key immediately, or send buffered CW
//! text.
//!
//! The Station uses this so an operator at the remote site can run their logger locally
//! (over Remote Desktop, say) and send CW macros straight to the radio, while remote paddle
//! keying from the Client rides the same output. The decoder is pure: feed it bytes, drain
//! [`HostEvent`]s, and separately ask it for the bytes to write back (the version reply on
//! Admin Open, and status/echo bytes). It never touches a serial port or a clock itself.
//!
//! ## Why a hand-written state machine
//!
//! The WinKeyer wire format is a byte stream with no framing: a command is a primary byte
//! optionally followed by a fixed number of payload bytes, and anything in the printable
//! ASCII range that is *not* mid-command is buffered text to be keyed. So the decoder must
//! know, for every opcode, how many payload bytes follow — the [`payload_len`] table — and
//! hold the half-read command across `feed` calls, because a USB serial read can split a
//! command anywhere.
//!
//! Only the commands the Station needs to honour produce a [`HostEvent`]; the rest are
//! consumed (their payload bytes skipped) so the stream stays in sync, exactly as real
//! hardware tolerates settings it does not use.

use super::winkeyer::{admin, cmd, consts};

/// How many payload bytes follow a given primary command byte.
///
/// This is the crux of staying byte-synchronised: after the opcode we must consume exactly
/// this many bytes before the next opcode or buffered character. Values come from the K1EL
/// WK2 datasheet and the .NET host's command table; do not "simplify" them.
///
/// Admin (0x00) is special — it is `1` here (the sub-command byte), and the sub-command may
/// itself take a further byte (e.g. Echo), handled in [`Decoder::feed`].
#[must_use]
pub fn payload_len(opcode: u8) -> usize {
    match opcode {
        cmd::ADMIN => 1,          // + sub-command; Echo adds one more (handled specially)
        cmd::SIDETONE => 1,
        cmd::SPEED => 1,
        cmd::WEIGHTING => 1,
        cmd::PTT_LEAD_TAIL => 2,
        cmd::SPEED_POT => 3,
        cmd::PAUSE => 1,
        cmd::GET_SPEED_POT => 0,
        cmd::BACKSPACE => 0,
        cmd::PIN_CONFIG => 1,
        cmd::CLEAR_BUFFER => 0,
        cmd::KEY_IMMEDIATE => 1,
        cmd::HSCW_SPEED => 1,
        cmd::FARNSWORTH => 1,
        cmd::WK2_MODE => 1,
        cmd::LOAD_DEFAULTS => 15,
        cmd::FIRST_EXT => 1,
        cmd::KEY_COMP => 1,
        cmd::PADDLE_SWITCH => 1,
        cmd::NULL => 0,
        cmd::SOFT_PADDLE => 1,
        cmd::REQ_STATUS => 0,
        cmd::POINTER => 1,
        cmd::DIT_DAH_RATIO => 1,
        cmd::PTT_CONTROL => 1,
        cmd::TIM_CHAR_SPACE => 0,
        cmd::BUFF_SPEED => 1,
        cmd::HSCW_CODE => 1,
        cmd::FREE_FORM => 1,
        // Any other low byte: treat as a no-payload immediate command so the stream
        // resynchronises rather than mis-reading the next byte as a payload.
        _ => 0,
    }
}

/// Something the Station should act on, decoded from the logger's byte stream.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HostEvent {
    /// The logger opened host mode (Admin Open). The decoder has queued the version reply.
    HostOpened,
    /// The logger closed host mode (Admin Close).
    HostClosed,
    /// Set the keyer speed in WPM (already clamped to the protocol range).
    SetSpeed(u32),
    /// Immediate key down (`true`) or up (`false`) — not buffered, key the line now.
    KeyImmediate(bool),
    /// A printable character to key as CW through the keyer at the current speed.
    Text(char),
    /// Clear any pending buffered text (Clear Buffer).
    ClearBuffer,
    /// Remove the last buffered character (Backspace).
    Backspace,
}

/// Parser state across `feed` calls.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    /// Between commands: a byte is either an opcode or a buffered character.
    Idle,
    /// Mid-command: `opcode` seen, still need `remaining` payload bytes.
    Payload { opcode: u8, remaining: usize },
    /// Admin opcode seen; the next byte is the admin sub-command.
    AdminSub,
    /// Admin Echo seen; the next byte is the character to echo back.
    AdminEcho,
}

/// A stateful WinKeyer host-mode decoder.
///
/// Create one per logger connection, [`feed`](Self::feed) it bytes as they arrive, and drain
/// [`HostEvent`]s. Poll [`take_output`](Self::take_output) for bytes to write back to the
/// logger (the Admin Open version reply and any echo). The decoder tolerates a stream that
/// has not opened host mode: real loggers often send Admin Open then settings, but some send
/// a bare key-immediate first, so events are emitted regardless and `HostOpened` is advisory.
#[derive(Debug, Default)]
pub struct Decoder {
    state: State,
    /// Bytes queued to send back to the logger (version reply, echoes).
    outbound: Vec<u8>,
    /// Whether host mode has been opened, for the status/`is_open` query.
    open: bool,
}

impl Default for State {
    fn default() -> Self {
        State::Idle
    }
}

impl Decoder {
    /// Creates a fresh decoder in the idle (pre-open) state.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Whether the logger has opened host mode (seen Admin Open, not yet Admin Close).
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open
    }

    /// Feeds a run of received bytes, appending any decoded [`HostEvent`]s to `out`.
    ///
    /// Safe to call with any slice length, including a single byte or a buffer that splits a
    /// command: the parser state persists between calls. Buffered printable characters each
    /// become one [`HostEvent::Text`]; non-printable bytes outside a command are ignored.
    pub fn feed(&mut self, bytes: &[u8], out: &mut Vec<HostEvent>) {
        for &byte in bytes {
            self.feed_one(byte, out);
        }
    }

    fn feed_one(&mut self, byte: u8, out: &mut Vec<HostEvent>) {
        match self.state {
            State::Idle => self.feed_idle(byte, out),
            State::AdminSub => self.feed_admin_sub(byte, out),
            State::AdminEcho => {
                // Echo the requested byte straight back, as the hardware does.
                self.outbound.push(byte);
                self.state = State::Idle;
            }
            State::Payload { opcode, remaining } => {
                self.apply_payload_byte(opcode, byte, out);
                let left = remaining - 1;
                self.state = if left == 0 { State::Idle } else { State::Payload { opcode, remaining: left } };
            }
        }
    }

    /// Handles a byte when not mid-command: an opcode starts a command, a printable
    /// character is buffered text, anything else is ignored.
    fn feed_idle(&mut self, byte: u8, out: &mut Vec<HostEvent>) {
        // Printable ASCII (and not a command byte) is buffered CW text.
        if (consts::PRINTABLE_ASCII_START..=consts::PRINTABLE_ASCII_END).contains(&byte) {
            out.push(HostEvent::Text(byte as char));
            return;
        }

        match byte {
            cmd::ADMIN => self.state = State::AdminSub,
            cmd::CLEAR_BUFFER => out.push(HostEvent::ClearBuffer),
            cmd::BACKSPACE => out.push(HostEvent::Backspace),
            // Zero-payload commands we simply consume.
            cmd::GET_SPEED_POT | cmd::NULL | cmd::REQ_STATUS | cmd::TIM_CHAR_SPACE => {}
            // Commands with a payload: enter the Payload state to consume/decode it.
            _ => {
                let len = payload_len(byte);
                if len == 0 {
                    // Unknown or no-payload byte: consume and resynchronise.
                } else {
                    self.state = State::Payload { opcode: byte, remaining: len };
                }
            }
        }
    }

    /// Handles the admin sub-command byte following [`cmd::ADMIN`].
    fn feed_admin_sub(&mut self, sub: u8, out: &mut Vec<HostEvent>) {
        match sub {
            admin::OPEN => {
                self.open = true;
                // Host-mode open: reply with the version byte, as the hardware does.
                self.outbound.push(consts::WINKEYER_VERSION);
                out.push(HostEvent::HostOpened);
                self.state = State::Idle;
            }
            admin::CLOSE => {
                self.open = false;
                out.push(HostEvent::HostClosed);
                self.state = State::Idle;
            }
            admin::ECHO => {
                // The next byte is echoed back verbatim.
                self.state = State::AdminEcho;
            }
            // Other admin sub-commands take no further byte we need; return to idle.
            _ => self.state = State::Idle,
        }
    }

    /// Decodes (or discards) the payload byte of a non-admin command.
    fn apply_payload_byte(&mut self, opcode: u8, byte: u8, out: &mut Vec<HostEvent>) {
        match opcode {
            cmd::SPEED => {
                let wpm = u32::from(byte).clamp(consts::MIN_WPM, consts::MAX_WPM);
                out.push(HostEvent::SetSpeed(wpm));
            }
            cmd::KEY_IMMEDIATE => out.push(HostEvent::KeyImmediate(byte != 0)),
            // Every other payload byte is a setting we accept but do not act on here
            // (sidetone, weighting, PTT timing, …). Consuming it keeps the stream in sync.
            _ => {}
        }
    }

    /// Takes the bytes queued to send back to the logger, clearing the queue.
    ///
    /// Call after [`feed`](Self::feed) and write the result to the logger's serial port. The
    /// bytes are the Admin Open version reply and any Admin Echo responses.
    #[must_use]
    pub fn take_output(&mut self) -> Vec<u8> {
        std::mem::take(&mut self.outbound)
    }

    /// Builds a status byte to report to the logger, with the mandatory status prefix set.
    ///
    /// Loggers that poll Request Status expect `0xC0 | bits`. `sending` reflects whether the
    /// keyer is currently playing buffered CW.
    #[must_use]
    pub fn status_byte(sending: bool) -> u8 {
        let mut raw = consts::STATUS_PREFIX | consts::STATUS_BUFFER_SPACE_BIT;
        if sending {
            raw |= consts::STATUS_SENDING_BIT | consts::STATUS_BUSY_BIT;
        }
        raw
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode(bytes: &[u8]) -> (Vec<HostEvent>, Vec<u8>) {
        let mut d = Decoder::new();
        let mut events = Vec::new();
        d.feed(bytes, &mut events);
        (events, d.take_output())
    }

    #[test]
    fn admin_open_opens_and_replies_with_version() {
        let (events, out) = decode(&[cmd::ADMIN, admin::OPEN]);
        assert_eq!(events, vec![HostEvent::HostOpened]);
        assert_eq!(out, vec![consts::WINKEYER_VERSION]);
    }

    #[test]
    fn admin_close_closes() {
        let mut d = Decoder::new();
        let mut events = Vec::new();
        d.feed(&[cmd::ADMIN, admin::OPEN], &mut events);
        assert!(d.is_open());
        events.clear();
        d.feed(&[cmd::ADMIN, admin::CLOSE], &mut events);
        assert_eq!(events, vec![HostEvent::HostClosed]);
        assert!(!d.is_open());
    }

    #[test]
    fn set_speed_is_decoded_and_clamped() {
        let (events, _) = decode(&[cmd::SPEED, 28]);
        assert_eq!(events, vec![HostEvent::SetSpeed(28)]);
        // Over the max clamps down.
        let (events, _) = decode(&[cmd::SPEED, 200]);
        assert_eq!(events, vec![HostEvent::SetSpeed(consts::MAX_WPM)]);
        // Under the min clamps up.
        let (events, _) = decode(&[cmd::SPEED, 1]);
        assert_eq!(events, vec![HostEvent::SetSpeed(consts::MIN_WPM)]);
    }

    #[test]
    fn key_immediate_maps_the_state_byte() {
        let (events, _) = decode(&[cmd::KEY_IMMEDIATE, 1]);
        assert_eq!(events, vec![HostEvent::KeyImmediate(true)]);
        let (events, _) = decode(&[cmd::KEY_IMMEDIATE, 0]);
        assert_eq!(events, vec![HostEvent::KeyImmediate(false)]);
    }

    #[test]
    fn printable_text_becomes_text_events() {
        let (events, _) = decode(b"CQ");
        assert_eq!(events, vec![HostEvent::Text('C'), HostEvent::Text('Q')]);
    }

    #[test]
    fn text_and_commands_interleave_and_stay_in_sync() {
        // Open, set speed 25, send "A", key down immediate.
        let bytes = [cmd::ADMIN, admin::OPEN, cmd::SPEED, 25, b'A', cmd::KEY_IMMEDIATE, 1];
        let (events, out) = decode(&bytes);
        assert_eq!(
            events,
            vec![
                HostEvent::HostOpened,
                HostEvent::SetSpeed(25),
                HostEvent::Text('A'),
                HostEvent::KeyImmediate(true),
            ]
        );
        assert_eq!(out, vec![consts::WINKEYER_VERSION]);
    }

    #[test]
    fn a_command_split_across_feeds_is_reassembled() {
        let mut d = Decoder::new();
        let mut events = Vec::new();
        // Speed command split between the opcode and its payload byte.
        d.feed(&[cmd::SPEED], &mut events);
        assert!(events.is_empty(), "no event until the payload arrives");
        d.feed(&[30], &mut events);
        assert_eq!(events, vec![HostEvent::SetSpeed(30)]);
    }

    #[test]
    fn multi_byte_payloads_are_fully_consumed() {
        // PTT lead/tail takes two payload bytes; the following char must still decode.
        let (events, _) = decode(&[cmd::PTT_LEAD_TAIL, 10, 20, b'E']);
        assert_eq!(events, vec![HostEvent::Text('E')]);
        // Load defaults takes 15 bytes; a trailing speed command must still decode.
        let mut bytes = vec![cmd::LOAD_DEFAULTS];
        bytes.extend(std::iter::repeat(0u8).take(15));
        bytes.extend_from_slice(&[cmd::SPEED, 22]);
        let (events, _) = decode(&bytes);
        assert_eq!(events, vec![HostEvent::SetSpeed(22)]);
    }

    #[test]
    fn admin_echo_echoes_the_next_byte() {
        let (_events, out) = decode(&[cmd::ADMIN, admin::ECHO, b'Z']);
        assert_eq!(out, vec![b'Z']);
    }

    #[test]
    fn clear_buffer_and_backspace_decode() {
        let (events, _) = decode(&[cmd::CLEAR_BUFFER, cmd::BACKSPACE]);
        assert_eq!(events, vec![HostEvent::ClearBuffer, HostEvent::Backspace]);
    }

    #[test]
    fn status_byte_sets_the_prefix_and_sending_bits() {
        let idle = Decoder::status_byte(false);
        assert_eq!(idle & consts::STATUS_PREFIX, consts::STATUS_PREFIX);
        assert_eq!(idle & consts::STATUS_SENDING_BIT, 0);
        let sending = Decoder::status_byte(true);
        assert_ne!(sending & consts::STATUS_SENDING_BIT, 0);
    }
}
