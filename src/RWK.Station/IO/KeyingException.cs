namespace RWK.Station.IO;

/// <summary>
/// Thrown when a keying or PTT line operation on the serial port fails.
/// </summary>
/// <remarks>
/// Carried forward from RWK v1 (<c>WinKeyerEmulator.App.IO.KeyingException</c>). A thrown
/// <see cref="KeyingException"/> always means the keying output has already driven every
/// configured line to its key-up / PTT-up state (or closed the port so the lines drop), so a
/// caller never has to unwind the hardware itself. The Edge Replayer treats it as fail-safe
/// condition F6 and latches SAFE (9.6).
/// </remarks>
public sealed class KeyingException : Exception
{
    /// <summary>Creates an exception with the supplied message.</summary>
    public KeyingException(string message) : base(message) { }

    /// <summary>Creates an exception with the supplied message and inner cause.</summary>
    public KeyingException(string message, Exception? inner) : base(message, inner) { }
}
