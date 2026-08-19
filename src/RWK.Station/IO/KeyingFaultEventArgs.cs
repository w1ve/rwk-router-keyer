namespace RWK.Station.IO;

/// <summary>
/// Reports a serial keying fault, raised after the keying output has already forced every
/// configured line to its inactive state.
/// </summary>
/// <param name="Operation">
/// The operation that failed, for example <c>KeyDown</c>, <c>KeyUp</c>, <c>PttDown</c>.
/// </param>
/// <param name="Message">Human-readable description including the Win32 error where available.</param>
/// <param name="Cause">The underlying exception, when the failure surfaced as one.</param>
/// <param name="PortClosed">
/// <see langword="true"/> when the fail-safe had to close the port handle because a line could
/// not be driven inactive. Closing the handle makes the driver drop DTR and RTS, which is the
/// last-resort fail-safe described in 9.8.
/// </param>
/// <remarks>
/// The Edge Replayer subscribes to this to raise fail-safe condition F6 and latch SAFE (9.6);
/// clearing SAFE then requires a manual Re-Arm (9.11).
/// <para>
/// _Requirements: 8.7, 9.6, 9.8_
/// </para>
/// </remarks>
public record KeyingFaultEventArgs(
    string Operation,
    string Message,
    Exception? Cause,
    bool PortClosed);
