using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.Core.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="ISystemClock"/> that provides controllable timestamps.
/// Each call to <see cref="GetTimestamp"/> advances the internal timestamp by
/// <see cref="AutoAdvanceStep"/> ticks, allowing HybridWaiter tests to eventually
/// reach a target timestamp without hanging.
/// </summary>
public sealed class FakeClock : ISystemClock
{
    private long _currentTimestamp;

    /// <summary>
    /// Gets or sets the current timestamp value returned by <see cref="GetTimestamp"/>.
    /// </summary>
    public long CurrentTimestamp
    {
        get => _currentTimestamp;
        set => _currentTimestamp = value;
    }

    /// <summary>
    /// Gets or sets the amount by which the timestamp auto-advances on each
    /// <see cref="GetTimestamp"/> call. Set to 0 to disable auto-advance.
    /// </summary>
    public long AutoAdvanceStep { get; set; }

    /// <summary>
    /// Gets or sets the tick frequency (ticks per second).
    /// Defaults to 10,000,000 (10 MHz), matching typical Stopwatch.Frequency on Windows.
    /// </summary>
    public long Frequency { get; set; } = 10_000_000L;

    /// <summary>
    /// Gets the number of times <see cref="GetTimestamp"/> has been called.
    /// </summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Creates a new FakeClock with the specified initial timestamp and auto-advance step.
    /// </summary>
    /// <param name="initialTimestamp">Starting timestamp value.</param>
    /// <param name="autoAdvanceStep">Ticks to advance on each GetTimestamp call.</param>
    public FakeClock(long initialTimestamp = 0, long autoAdvanceStep = 0)
    {
        _currentTimestamp = initialTimestamp;
        AutoAdvanceStep = autoAdvanceStep;
    }

    /// <inheritdoc/>
    public long GetTimestamp()
    {
        long value = _currentTimestamp;
        _currentTimestamp += AutoAdvanceStep;
        CallCount++;
        return value;
    }
}
