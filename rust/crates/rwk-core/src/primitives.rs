//! Shared value and enum vocabulary.
//!
//! Faithful port of the enums in `RWK.Shared.Primitives` / `RWK.Shared.Config`.
//! Numeric values are preserved from the .NET source because they are persisted in
//! user profiles and pushed over the control channel; do not renumber them.

/// Identifies a serial port control line used for key or PTT output.
///
/// Values match RWK v1 (`DTR = 0`, `RTS = 1`) so persisted v1 settings stay valid;
/// [`KeyingLine::None`] is appended for the PTT case.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum KeyingLine {
    /// Data Terminal Ready.
    #[default]
    Dtr = 0,
    /// Request To Send.
    Rts = 1,
    /// No line assigned. Valid for a PTT line only.
    None = 2,
}

/// Keying mode used by the element engine when translating paddle contacts into
/// Morse elements.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum KeyerMode {
    /// Iambic B: a release during an element still yields the queued opposite element.
    #[default]
    IambicB = 0,
    /// Iambic A: alternation ceases when the paddles are released during an element.
    IambicA = 1,
    /// Ultimatic: in a squeeze, the element of the last paddle pressed repeats.
    Ultimatic = 2,
    /// Bug: dits are automatic, dahs are operator-timed.
    Bug = 3,
    /// Straight key: contacts pass straight through, no element generation.
    Straight = 4,
}

/// Which input path produced an [`EdgeEvent`](crate::keying::EdgeEvent).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EdgeSource {
    /// The iambic/bug/straight-key paddle path.
    Paddle = 0,
    /// Text decoded from the WinKeyer host path.
    Host = 1,
    /// A WinKeyer immediate key-down/key-up command.
    Immediate = 2,
}

/// How traffic currently reaches the peer; selects the jitter-buffer delay band.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PathType {
    /// No path established.
    #[default]
    None = 0,
    /// Direct peer-to-peer path.
    Direct = 1,
    /// Relayed via a DERP server.
    Derp = 2,
}

/// Transport protocol carried by a port forwarding rule.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ForwardProtocol {
    /// TCP, with bidirectional pumping and half-close propagation.
    #[default]
    Tcp = 0,
    /// UDP, with a NAT-style session table and a 60s idle timeout.
    Udp = 1,
}

/// Direction of a forwarding rule: which side initiates the connection.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ForwardDirection {
    /// Client → Station (the default for all pre-1.0.3 rules).
    #[default]
    ClientToStation = 0,
    /// Station → Client.
    StationToClient = 1,
}

/// Mesh link state reported to the UI.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TailscaleState {
    /// Not started.
    #[default]
    Disconnected = 0,
    /// Interactive login required.
    NeedsAuth = 1,
    /// Join in progress.
    Connecting = 2,
    /// Joined and healthy.
    Connected = 3,
    /// Link lost or unrecoverable.
    Fault = 4,
}

/// Range limits shared by the keyer, sidetone and PTT configuration.
pub mod limits {
    /// Lowest supported keying speed, in words per minute.
    pub const MIN_WPM: u32 = 5;
    /// Highest supported keying speed, in words per minute.
    pub const MAX_WPM: u32 = 60;
    /// Lightest supported weight, as a percentage.
    pub const MIN_WEIGHT: u32 = 25;
    /// Heaviest supported weight, as a percentage.
    pub const MAX_WEIGHT: u32 = 75;
    /// Default weight, as a percentage.
    pub const DEFAULT_WEIGHT: u32 = 50;

    /// Lowest permitted sidetone frequency, in Hz.
    pub const MIN_TONE_HZ: i32 = 300;
    /// Highest permitted sidetone frequency, in Hz.
    pub const MAX_TONE_HZ: i32 = 1500;
    /// Default sidetone frequency, in Hz.
    pub const DEFAULT_TONE_HZ: i32 = 700;

    /// Default PTT lead time, in milliseconds.
    pub const DEFAULT_PTT_LEAD_MS: u64 = 15;
    /// Default PTT tail time, in milliseconds.
    pub const DEFAULT_PTT_TAIL_MS: u64 = 500;

    /// Default direct-path jitter buffer delay, in milliseconds.
    pub const DEFAULT_DIRECT_JITTER_MS: u64 = 60;
    /// Default DERP-path jitter buffer delay, in milliseconds.
    pub const DEFAULT_DERP_JITTER_MS: u64 = 200;
}
