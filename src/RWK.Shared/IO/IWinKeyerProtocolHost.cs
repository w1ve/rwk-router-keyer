using RWK.Shared.Protocol;

namespace RWK.Shared.IO;

/// <summary>
/// Wraps the WK2 protocol state machine behind a serial port, surfacing high-level events
/// that the controller (task 22.1) wires to <see cref="RWK.Shared.Keying.ISoftWinKeyerCore"/>.
/// </summary>
/// <remarks>
/// This is design Component 2 (WinKeyerProtocolHost). The host:
/// <list type="bullet">
///   <item>Opens a serial port at 1200 baud, 8-N-2.</item>
///   <item>Reads bytes on a background thread and feeds them to the protocol state machine.</item>
///   <item>Surfaces events for text, speed changes, immediate key commands, buffer clears, and
///         raw response bytes.</item>
///   <item>Provides callbacks for the keyer core to echo characters and send status bytes back
///         to the host application (e.g., N1MM+).</item>
///   <item>Supports two-way speed synchronization: host speed commands update the keyer, and
///         pot/UI speed changes report back to the host.</item>
/// </list>
/// _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7_
/// </remarks>
public interface IWinKeyerProtocolHost : IDisposable
{
    /// <summary>
    /// Raised when buffered ASCII text is received from the host (2.3).
    /// Each character is delivered individually as it arrives.
    /// </summary>
    event EventHandler<char>? TextReceived;

    /// <summary>
    /// Raised when a speed command is received from the host (2.6).
    /// The integer value is the new WPM setting.
    /// </summary>
    event EventHandler<int>? SpeedChanged;

    /// <summary>
    /// Raised when a Key Immediate command is received (2.4).
    /// True = key down, False = key up.
    /// </summary>
    event EventHandler<bool>? KeyImmediate;

    /// <summary>
    /// Raised when the host sends a buffer-clear command.
    /// </summary>
    event EventHandler? BufferCleared;

    /// <summary>
    /// Raised when the protocol state machine produces response bytes that must be sent
    /// back to the host (e.g., Admin Open version/status, echo bytes, status responses).
    /// </summary>
    event EventHandler<byte[]>? ResponseReady;

    /// <summary>
    /// Opens the serial port and begins processing WK2 protocol bytes.
    /// </summary>
    /// <param name="portName">Serial port name, for example <c>COM5</c>.</param>
    void Start(string portName);

    /// <summary>
    /// Stops the reader thread and closes the serial port. Safe to call when not started.
    /// </summary>
    void Stop();

    /// <summary>
    /// Sends a status byte back to the host application.
    /// Called by the keyer core when buffer state changes.
    /// </summary>
    /// <param name="status">The WK2 status byte (bits 7:6 = 0xC0).</param>
    void SendStatus(byte status);

    /// <summary>
    /// Echoes a completed character back to the host per WK2 specification (2.5).
    /// Called by the keyer core when a character finishes sending.
    /// </summary>
    /// <param name="c">The character to echo.</param>
    void SendCharacterEcho(char c);

    /// <summary>
    /// Reports a speed change from the paddle/UI side back to the host (2.7).
    /// This allows logging software to display the current speed.
    /// </summary>
    /// <param name="wpm">The new speed in words per minute.</param>
    void ReportSpeedToHost(int wpm);

    /// <summary>
    /// Gets the current protocol state for inspection.
    /// </summary>
    ProtocolState State { get; }
}
