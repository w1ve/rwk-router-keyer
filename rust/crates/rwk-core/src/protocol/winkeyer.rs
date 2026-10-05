//! WinKeyer command-byte set and status decoding.
//!
//! Faithful port of `RWK.Shared.Protocol.CommandDefinitions`. Values are
//! reverse-engineered from real K1EL hardware and from the byte stream N1MM+ emits;
//! do not "tidy" them.

/// Primary command bytes (first byte of a command sequence).
pub mod cmd {
    /// Admin command prefix (0x00). Followed by a sub-command byte.
    pub const ADMIN: u8 = 0x00;
    /// Sidetone command (0x01). Followed by frequency byte.
    pub const SIDETONE: u8 = 0x01;
    /// Speed command (0x02). Followed by WPM byte.
    pub const SPEED: u8 = 0x02;
    /// Weighting command (0x03). Followed by weight byte.
    pub const WEIGHTING: u8 = 0x03;
    /// PTT Lead/Tail command (0x04). Followed by two timing bytes (lead + tail).
    pub const PTT_LEAD_TAIL: u8 = 0x04;
    /// Speed Pot Setup command (0x05). Followed by 3 bytes.
    pub const SPEED_POT: u8 = 0x05;
    /// Pause command (0x06). Followed by pause byte.
    pub const PAUSE: u8 = 0x06;
    /// Get Speed Pot command (0x07). No additional bytes, and no response.
    pub const GET_SPEED_POT: u8 = 0x07;
    /// Backspace command (0x08). No additional bytes.
    pub const BACKSPACE: u8 = 0x08;
    /// Pin Configuration command (0x09). Followed by config byte.
    pub const PIN_CONFIG: u8 = 0x09;
    /// Clear Buffer command (0x0A). No additional bytes.
    pub const CLEAR_BUFFER: u8 = 0x0A;
    /// Key Immediate command (0x0B). Followed by key state byte.
    pub const KEY_IMMEDIATE: u8 = 0x0B;
    /// HSCW Speed command (0x0C). Followed by speed byte.
    pub const HSCW_SPEED: u8 = 0x0C;
    /// Farnsworth command (0x0D). Followed by speed byte.
    pub const FARNSWORTH: u8 = 0x0D;
    /// WinKeyer2 Mode command (0x0E). Followed by mode byte.
    pub const WK2_MODE: u8 = 0x0E;
    /// Load Defaults command (0x0F). Followed by 15 bytes.
    pub const LOAD_DEFAULTS: u8 = 0x0F;
    /// First Extension command (0x10). Followed by extension byte.
    pub const FIRST_EXT: u8 = 0x10;
    /// Key Compensation command (0x11). Followed by comp byte.
    pub const KEY_COMP: u8 = 0x11;
    /// Paddle Switchpoint command (0x12). Followed by switchpoint byte.
    pub const PADDLE_SWITCH: u8 = 0x12;
    /// Null command (0x13). No operation.
    pub const NULL: u8 = 0x13;
    /// Software Paddle command (0x14). Followed by paddle byte.
    pub const SOFT_PADDLE: u8 = 0x14;
    /// Request WinKeyer Status command (0x15). No additional bytes.
    pub const REQ_STATUS: u8 = 0x15;
    /// Pointer command (0x16). Followed by pointer byte.
    pub const POINTER: u8 = 0x16;
    /// Dit/Dah Ratio command (0x17). Followed by ratio byte.
    pub const DIT_DAH_RATIO: u8 = 0x17;
    /// PTT Control command (0x18). Followed by control byte.
    pub const PTT_CONTROL: u8 = 0x18;
    /// Timing Char Space command (0x19). No additional bytes.
    pub const TIM_CHAR_SPACE: u8 = 0x19;
    /// Buffer Speed command (0x1A). Followed by speed byte.
    pub const BUFF_SPEED: u8 = 0x1A;
    /// HSCW code (0x1B). Followed by code byte.
    pub const HSCW_CODE: u8 = 0x1B;
    /// Free Form Message command (0x1C). Followed by message byte.
    pub const FREE_FORM: u8 = 0x1C;
    /// End of immediate commands range.
    pub const LAST_IMMEDIATE: u8 = 0x1F;
}

/// Admin sub-command bytes (second byte after [`cmd::ADMIN`]).
pub mod admin {
    /// Admin Calibrate (0x00).
    pub const CALIBRATE: u8 = 0x00;
    /// Admin Reset (0x01).
    pub const RESET: u8 = 0x01;
    /// Admin Open Host Mode (0x02). Responds with version byte.
    pub const OPEN: u8 = 0x02;
    /// Admin Close Host Mode (0x03).
    pub const CLOSE: u8 = 0x03;
    /// Admin Echo (0x04). Followed by a byte to echo back.
    pub const ECHO: u8 = 0x04;
    /// Admin Paddle A2D (0x05).
    pub const PADDLE_A2D: u8 = 0x05;
    /// Admin Speed A2D (0x06).
    pub const SPEED_A2D: u8 = 0x06;
    /// Admin Get Values (0x07).
    pub const GET_VALUES: u8 = 0x07;
    /// Admin Get Calibrate (0x09).
    pub const GET_CALIBRATE: u8 = 0x09;
    /// Admin WK1 Mode (0x0A).
    pub const WK1_MODE: u8 = 0x0A;
    /// Admin WK2 Mode (0x0B).
    pub const WK2_MODE: u8 = 0x0B;
}

/// Protocol constants.
pub mod consts {
    /// WinKeyer version byte reported on Admin Open. Version 23 = WinKeyer 2.
    pub const WINKEYER_VERSION: u8 = 23;
    /// Minimum allowed WPM speed.
    pub const MIN_WPM: u32 = 5;
    /// Maximum allowed WPM speed.
    pub const MAX_WPM: u32 = 45;
    /// Default WPM speed.
    pub const DEFAULT_WPM: u32 = 15;
    /// Status bit: keyer is idle/busy (bit 0).
    pub const STATUS_BUSY_BIT: u8 = 0x01;
    /// Status bit: currently sending (bit 1).
    pub const STATUS_SENDING_BIT: u8 = 0x02;
    /// Status bit: buffer space available (bit 2).
    pub const STATUS_BUFFER_SPACE_BIT: u8 = 0x04;
    /// Prefix applied to every status byte (bits 7:6 set); distinguishes a status
    /// byte from an echoed character on the wire — N1MM+ relies on this.
    pub const STATUS_PREFIX: u8 = 0xC0;
    /// Maximum capacity of the text buffer.
    pub const MAX_BUFFER_CAPACITY: usize = 128;
    /// First printable ASCII character (space).
    pub const PRINTABLE_ASCII_START: u8 = 0x20;
    /// Last printable ASCII character (tilde).
    pub const PRINTABLE_ASCII_END: u8 = 0x7E;
}

/// Decoded WinKeyer status byte.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct StatusByte {
    /// Raw byte as received.
    pub raw: u8,
    /// Keyer is busy/idle (bit 0).
    pub busy: bool,
    /// Currently sending (bit 1).
    pub sending: bool,
    /// Buffer space available (bit 2).
    pub buffer_space: bool,
}

impl StatusByte {
    /// Decodes a raw status byte.
    #[must_use]
    pub fn decode(raw: u8) -> Self {
        Self {
            raw,
            busy: raw & consts::STATUS_BUSY_BIT != 0,
            sending: raw & consts::STATUS_SENDING_BIT != 0,
            buffer_space: raw & consts::STATUS_BUFFER_SPACE_BIT != 0,
        }
    }

    /// True when `raw` looks like a status byte rather than an echoed character
    /// (the top two bits are set).
    #[must_use]
    pub fn is_status_byte(raw: u8) -> bool {
        raw & consts::STATUS_PREFIX == consts::STATUS_PREFIX
    }
}

/// A decoded WinKeyer command frame: the primary byte plus its payload bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Command {
    /// Primary command byte.
    pub opcode: u8,
    /// Payload bytes following the opcode.
    pub payload: Vec<u8>,
}

impl Command {
    /// Builds a `Set Speed` command.
    #[must_use]
    pub fn set_speed(wpm: u32) -> Self {
        Self { opcode: cmd::SPEED, payload: vec![wpm.clamp(consts::MIN_WPM, consts::MAX_WPM) as u8] }
    }

    /// Builds a `Set Sidetone` command.
    #[must_use]
    pub fn set_sidetone(hz: u32) -> Self {
        Self { opcode: cmd::SIDETONE, payload: vec![hz.min(u32::from(u8::MAX)) as u8] }
    }

    /// Builds a `Set Weighting` command.
    #[must_use]
    pub fn set_weighting(weight: u8) -> Self {
        Self { opcode: cmd::WEIGHTING, payload: vec![weight] }
    }

    /// Builds an `Admin / Open Host Mode` command.
    #[must_use]
    pub fn admin_open() -> Self {
        Self { opcode: cmd::ADMIN, payload: vec![admin::OPEN] }
    }

    /// Builds a `Key Immediate` command.
    #[must_use]
    pub fn key_immediate(key_down: bool) -> Self {
        Self { opcode: cmd::KEY_IMMEDIATE, payload: vec![u8::from(key_down)] }
    }

    /// Serializes the command to its wire bytes.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(1 + self.payload.len());
        out.push(self.opcode);
        out.extend_from_slice(&self.payload);
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_byte_decodes_bits() {
        let s = StatusByte::decode(consts::STATUS_PREFIX | consts::STATUS_SENDING_BIT);
        assert!(s.sending);
        assert!(!s.busy);
        assert!(!s.buffer_space);
        assert!(StatusByte::is_status_byte(s.raw));
        assert!(!StatusByte::is_status_byte(b'A'));
    }

    #[test]
    fn speed_is_clamped_to_protocol_range() {
        assert_eq!(Command::set_speed(100).payload, vec![consts::MAX_WPM as u8]);
        assert_eq!(Command::set_speed(1).payload, vec![consts::MIN_WPM as u8]);
        assert_eq!(Command::set_speed(20).payload, vec![20]);
    }

    #[test]
    fn admin_open_serializes_to_expected_bytes() {
        assert_eq!(Command::admin_open().to_bytes(), vec![0x00, 0x02]);
    }

    #[test]
    fn key_immediate_maps_bool_to_state_byte() {
        assert_eq!(Command::key_immediate(true).to_bytes(), vec![0x0B, 0x01]);
        assert_eq!(Command::key_immediate(false).to_bytes(), vec![0x0B, 0x00]);
    }

    #[test]
    fn command_bytes_match_hardware_constants() {
        assert_eq!(cmd::SPEED, 2);
        assert_eq!(cmd::KEY_IMMEDIATE, 0x0B);
        assert_eq!(admin::OPEN, 2);
        assert_eq!(consts::WINKEYER_VERSION, 23);
    }
}
