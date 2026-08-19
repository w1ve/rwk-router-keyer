/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
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
