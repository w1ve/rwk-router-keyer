// KeyingLine is a shared enum in the parent RWK.Shared namespace, so it resolves without
// a using directive.
namespace RWK.Shared.Config;

/// <summary>
/// Serial keying output settings for the Station (8.1, 8.2, 8.3).
/// </summary>
/// <param name="PortName">Serial port used for keying, for example <c>COM3</c>.</param>
/// <param name="KeyLine">Control line asserted for key-down: RTS or DTR (8.1).</param>
/// <param name="PttLine">Control line asserted for PTT: RTS, DTR, or none (8.2).</param>
/// <param name="KeyInvert">Inverts the key line's polarity (8.3).</param>
/// <param name="PttInvert">Inverts the PTT line's polarity (8.3).</param>
/// <remarks>
/// _Requirements: 8.1, 8.2, 8.3, 12.5_
/// </remarks>
public record KeyingOutputConfig(
    string PortName,
    KeyingLine KeyLine,
    KeyingLine PttLine,
    bool KeyInvert,
    bool PttInvert);
