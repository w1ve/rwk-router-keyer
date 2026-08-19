namespace RWK.Shared.Timing;

/// <summary>
/// Abstraction over high-resolution system timing, enabling deterministic testing
/// of the timing engine without relying on real wall-clock time.
/// </summary>
/// <remarks>
/// Behavior-preserving copy of WinKeyerEmulator.Core.Timing.ISystemClock (RWK v1).
/// </remarks>
public interface ISystemClock
{
    /// <summary>
    /// Gets the current high-resolution timestamp in ticks.
    /// Equivalent to <see cref="System.Diagnostics.Stopwatch.GetTimestamp()"/>.
    /// </summary>
    long GetTimestamp();

    /// <summary>
    /// Gets the tick frequency (ticks per second) of the timestamp source.
    /// Equivalent to <see cref="System.Diagnostics.Stopwatch.Frequency"/>.
    /// </summary>
    long Frequency { get; }
}
