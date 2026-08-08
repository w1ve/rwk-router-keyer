using System.Diagnostics;

namespace WinKeyerEmulator.Core.Timing;

/// <summary>
/// Production implementation of <see cref="ISystemClock"/> that wraps
/// <see cref="Stopwatch.GetTimestamp()"/> and <see cref="Stopwatch.Frequency"/>.
/// </summary>
public sealed class StopwatchClock : ISystemClock
{
    /// <inheritdoc/>
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <inheritdoc/>
    public long Frequency => Stopwatch.Frequency;
}
