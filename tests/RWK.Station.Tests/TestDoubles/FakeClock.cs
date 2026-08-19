/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Timing;

namespace RWK.Station.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="ISystemClock"/> that provides controllable timestamps
/// for the fail-safe monitor and scheduler watchdog tests.
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
    /// Creates a new FakeClock with the specified initial timestamp and auto-advance step.
    /// </summary>
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
        return value;
    }

    /// <summary>
    /// Advances the clock by the specified number of milliseconds.
    /// </summary>
    public void AdvanceMs(long ms)
    {
        _currentTimestamp += (ms * Frequency) / 1000;
    }

    /// <summary>
    /// Advances the clock by the specified number of ticks.
    /// </summary>
    public void AdvanceTicks(long ticks)
    {
        _currentTimestamp += ticks;
    }
}
