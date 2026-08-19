using RWK.Shared.Keying;

namespace RWK.Shared.Tests.TestDoubles;

/// <summary>
/// A <see cref="KeyerWait"/> that jumps a <see cref="FakeClock"/> straight to the target
/// instead of waiting, making keyer element timing deterministic.
/// </summary>
/// <remarks>
/// The RWK v1 <c>SoftKeyer</c> tests drove the keyer with <c>Thread.Sleep</c> against real
/// wall-clock time and asserted on how many elements came out. At 40 WPM a dit is 30ms and
/// the next-element decision lands about 60ms after the element starts, so releasing the
/// paddle at 40ms left roughly 20ms of slack — less than Windows' 15.6ms timer granularity
/// plus one GC pause. When the slack ran out a second element appeared, which is correct
/// iambic behavior, and the test failed anyway. Widening the sleeps only moves that
/// boundary; removing wall-clock time from the loop removes the race.
/// <para>
/// Aborting is modelled honestly: when the abort predicate is true the clock is left short
/// of the target, which is exactly how the pump distinguishes an abandoned wait from a
/// completed one.
/// </para>
/// </remarks>
public sealed class VirtualKeyerWait
{
    private readonly FakeClock _clock;

    /// <summary>
    /// Creates a virtual waiter over a fake clock.
    /// </summary>
    /// <param name="clock">
    /// The clock to advance. Its <see cref="FakeClock.AutoAdvanceStep"/> should stay at zero
    /// so that only this waiter moves time.
    /// </param>
    public VirtualKeyerWait(FakeClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Gets the number of waits requested so far.</summary>
    public int WaitCount { get; private set; }

    /// <summary>
    /// Optional hook invoked at the start of each wait, receiving the zero-based wait index
    /// and the target timestamp. Used to inject an event "mid-element" — a paddle press
    /// during a host character, for instance — at an exact point.
    /// </summary>
    public Action<int, long>? BeforeWait { get; set; }

    /// <summary>Gets the delegate to hand to the pump.</summary>
    public KeyerWait Wait => WaitUntil;

    private void WaitUntil(long targetTimestamp, Func<bool> shouldAbort)
    {
        int index = WaitCount++;
        BeforeWait?.Invoke(index, targetTimestamp);

        if (shouldAbort())
            return; // Leave the clock short of the target: the wait was abandoned.

        if (_clock.CurrentTimestamp < targetTimestamp)
            _clock.CurrentTimestamp = targetTimestamp;
    }
}
