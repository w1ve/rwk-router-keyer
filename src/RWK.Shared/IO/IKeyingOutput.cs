namespace RWK.Shared.IO;

/// <summary>
/// Abstraction for physical keying output via a serial port control line.
/// </summary>
/// <remarks>
/// Carried forward unchanged in shape from RWK v1
/// (<c>WinKeyerEmulator.Core.IO.IKeyingOutput</c>) so that the existing
/// <c>SerialKeyingOutput</c> implementation can be extended against it
/// rather than rewritten.
/// <para>
/// PTT is deliberately not part of this contract; see <see cref="IPttOutput"/>.
/// A Station keying output implements both.
/// </para>
/// _Requirements: 8.1, 8.7_
/// </remarks>
public interface IKeyingOutput : IDisposable
{
    /// <summary>
    /// Opens the specified serial port and configures it for keying on the given control line.
    /// </summary>
    /// <param name="portName">The serial port name, for example <c>COM3</c>.</param>
    /// <param name="line">The control line (RTS or DTR) used to assert key-down (8.1).</param>
    void Open(string portName, KeyingLine line);

    /// <summary>
    /// Closes the keying port and releases resources.
    /// </summary>
    void Close();

    /// <summary>
    /// Asserts the configured control line (key down).
    /// </summary>
    void KeyDown();

    /// <summary>
    /// De-asserts the configured control line (key up).
    /// </summary>
    void KeyUp();

    /// <summary>
    /// Gets whether the keying port is currently open.
    /// </summary>
    bool IsOpen { get; }
}
