/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace WinKeyerEmulator.Core.Timing;

/// <summary>
/// Provides a hybrid wait strategy combining Thread.Sleep for coarse waiting
/// and SpinWait for precise sub-millisecond timing at the end.
/// </summary>
public static class HybridWaiter
{
    /// <summary>
    /// Approximately 1.5ms worth of ticks at 10MHz frequency.
    /// Used as fallback when frequency-based calculation isn't needed.
    /// </summary>
    private const long DefaultSpinThresholdTicks = 15000;

    /// <summary>
    /// Blocks the current thread until the target timestamp is reached,
    /// using a coarse sleep phase followed by a spin-wait phase for precision.
    /// </summary>
    /// <param name="targetTimestamp">The absolute timestamp to wait until.</param>
    /// <param name="clock">The system clock to use for timing.</param>
    /// <param name="shouldAbort">Optional function that returns true if the wait should be aborted.</param>
    public static void WaitUntil(long targetTimestamp, ISystemClock clock, Func<bool>? shouldAbort = null)
    {
        // Calculate spin threshold calibrated to clock frequency (~1.5ms worth of ticks)
        long spinThreshold = clock.Frequency * 15 / 10000;
        if (spinThreshold <= 0)
            spinThreshold = DefaultSpinThresholdTicks;

        long remaining = targetTimestamp - clock.GetTimestamp();

        // Coarse sleep phase: sleep while > threshold away
        while (remaining > spinThreshold)
        {
            if (shouldAbort?.Invoke() == true) return;
            Thread.Sleep(1);
            remaining = targetTimestamp - clock.GetTimestamp();
        }

        // Spin phase: busy-wait for final precision
        while (clock.GetTimestamp() < targetTimestamp)
        {
            if (shouldAbort?.Invoke() == true) return;
            Thread.SpinWait(1);
        }
    }
}
