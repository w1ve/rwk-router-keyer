namespace RWK.Shared;

/// <summary>
/// Identifies a serial port control line used for key or PTT output (8.1, 8.2).
/// </summary>
/// <remarks>
/// Extends the RWK v1 enum (<c>WinKeyerEmulator.Core.IO.KeyingLine</c>) rather than
/// replacing it: <see cref="DTR"/> and <see cref="RTS"/> keep their v1 names and
/// numeric values so persisted v1 settings and the existing
/// <c>SerialKeyingOutput</c> mapping stay valid. <see cref="None"/> is appended for
/// the PTT case, where the design allows "RTS, DTR, or None" (8.2).
/// <para>
/// <see cref="None"/> is only meaningful for a PTT line. A key line of
/// <see cref="None"/> is a configuration error — there would be nothing to key.
/// </para>
/// <para>
/// Because <see cref="DTR"/> is 0 for v1 compatibility, <c>default(KeyingLine)</c>
/// is <see cref="DTR"/> and not <see cref="None"/>. Configuration records therefore
/// set PTT line defaults explicitly rather than relying on the default literal.
/// </para>
/// _Requirements: 8.1, 8.2_
/// </remarks>
public enum KeyingLine
{
    /// <summary>Data Terminal Ready. Numeric value matches RWK v1.</summary>
    DTR = 0,

    /// <summary>Request To Send. Numeric value matches RWK v1.</summary>
    RTS = 1,

    /// <summary>
    /// No line assigned. Valid for a PTT line only, meaning PTT is not driven and
    /// the lead/tail sequencing of 8.4-8.6 is skipped (8.2).
    /// </summary>
    None = 2
}
