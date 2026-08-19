using System.Diagnostics;

namespace RWK.Shared.Timing;

/// <summary>
/// Production implementation of <see cref="ISystemClock"/> that wraps
/// <see cref="Stopwatch.GetTimestamp()"/> and <see cref="Stopwatch.Frequency"/>.
/// </summary>
/// <remarks>
/// Behavior-preserving copy of WinKeyerEmulator.Core.Timing.StopwatchClock (RWK v1).
/// </remarks>
public sealed class StopwatchClock : ISystemClock
{
    /// <inheritdoc/>
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <inheritdoc/>
    public long Frequency => Stopwatch.Frequency;
}
