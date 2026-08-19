namespace RWK.Shared.Timing;

/// <summary>
/// Provides a hybrid wait strategy combining Thread.Sleep for idle waiting
/// and pure SpinWait for element timing precision.
/// </summary>
/// <remarks>
/// For CW keying at 35+ WPM, element durations are 20-34ms. Thread.Sleep even
/// with timeBeginPeriod(1) has ~1ms jitter. The spin-wait approach uses 100%
/// of one core during active keying but achieves microsecond-level precision.
/// The sleep phase is only used when remaining time is large (>2ms), and transitions
/// to pure spin for the final 2ms to hit the exact target.
/// </remarks>
public static class HybridWaiter
{
    /// <summary>
    /// Blocks the current thread until the target timestamp is reached.
    /// Uses Thread.Sleep(0) + SpinWait for precision without burning CPU on long waits.
    /// </summary>
    /// <param name="targetTimestamp">The absolute timestamp to wait until.</param>
    /// <param name="clock">The system clock to use for timing.</param>
    /// <param name="shouldAbort">Optional function that returns true if the wait should be aborted.</param>
    public static void WaitUntil(long targetTimestamp, ISystemClock clock, Func<bool>? shouldAbort = null)
    {
        // Spin threshold: ~0.5ms worth of ticks. Below this we pure-spin for precision.
        long spinThreshold = clock.Frequency / 2000;
        if (spinThreshold <= 0) spinThreshold = 5000;

        long remaining = targetTimestamp - clock.GetTimestamp();

        // Coarse phase: yield timeslice while far from target (>2ms).
        // Thread.Sleep(0) yields to same-priority threads without the timer resolution issue.
        long yieldThreshold = clock.Frequency / 500; // ~2ms
        while (remaining > yieldThreshold)
        {
            if (shouldAbort?.Invoke() == true) return;
            Thread.Sleep(0); // Yield, not sleep — returns immediately if nothing else to run
            remaining = targetTimestamp - clock.GetTimestamp();
        }

        // Medium phase: SpinWait with iteration count to allow thread scheduler hints
        // without actually sleeping. This reduces power consumption slightly.
        while (remaining > spinThreshold)
        {
            if (shouldAbort?.Invoke() == true) return;
            Thread.SpinWait(10);
            remaining = targetTimestamp - clock.GetTimestamp();
        }

        // Final phase: tight spin for sub-microsecond precision
        while (clock.GetTimestamp() < targetTimestamp)
        {
            if (shouldAbort?.Invoke() == true) return;
            Thread.SpinWait(1);
        }
    }
}
