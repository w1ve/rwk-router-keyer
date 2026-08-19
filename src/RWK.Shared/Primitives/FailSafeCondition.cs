namespace RWK.Shared;

/// <summary>
/// The ten enumerated fail-safe conditions of Requirement 9. Every condition forces
/// key-up; they differ in whether the SAFE latch is set and how it clears.
/// </summary>
/// <remarks>
/// Names are kept as the specification's F-numbers rather than descriptive names so
/// that log entries, UI text, and the requirements document all use one vocabulary.
/// Numeric values match the F-number.
/// <para>
/// SAFE latch behavior, from 9.11 and 9.12:
/// <list type="bullet">
///   <item><description>Latch requiring manual Re-Arm: F2, F5, F6, F7, F10.</description></item>
///   <item><description>Degraded session, latch clears automatically when valid edges resume: F1, F9.</description></item>
///   <item><description>No latch: F3 (key-up only), F4 (frame discarded), F8 (shutdown).</description></item>
/// </list>
/// </para>
/// _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 9.8, 9.9, 9.10, 9.11, 9.12_
/// </remarks>
public enum FailSafeCondition
{
    /// <summary>
    /// F1: no heartbeat or edge for 750ms while the key is down. Force key-up and mark
    /// the session degraded; the latch clears automatically when edges resume (9.1, 9.12).
    /// </summary>
    F1 = 1,

    /// <summary>
    /// F2: no heartbeat for 3 seconds while the key is up. Close the session and set the
    /// SAFE latch, which requires manual Re-Arm (9.2, 9.11).
    /// </summary>
    F2 = 2,

    /// <summary>
    /// F3: key down continuously for longer than the maximum down time (10 seconds).
    /// Force key-up but do not latch (9.3).
    /// </summary>
    F3 = 3,

    /// <summary>
    /// F4: frame received with an epoch that does not match the current session. Discard
    /// the frame and force key-up if currently keyed (9.4, 6.5).
    /// </summary>
    F4 = 4,

    /// <summary>
    /// F5: sequence gap from which key state cannot be inferred. Force key-up and set the
    /// SAFE latch (9.5, 9.11).
    /// </summary>
    F5 = 5,

    /// <summary>
    /// F6: serial port error or device removal. Force key-up and set the SAFE latch
    /// (9.6, 9.11).
    /// </summary>
    F6 = 6,

    /// <summary>
    /// F7: unhandled exception on the keying thread. Force key-up and set the SAFE latch
    /// (9.7, 9.11).
    /// </summary>
    F7 = 7,

    /// <summary>
    /// F8: application closing while the key is down. Force key-up during disposal (9.8, 8.7).
    /// </summary>
    F8 = 8,

    /// <summary>
    /// F9: Tailscale reports the path lost. Force key-up and mark the session degraded;
    /// the latch clears automatically when edges resume (9.9, 9.12).
    /// </summary>
    F9 = 9,

    /// <summary>
    /// F10: scheduler timing overrun greater than 250ms. Force key-up and set the SAFE
    /// latch (9.10, 9.11).
    /// </summary>
    F10 = 10
}
