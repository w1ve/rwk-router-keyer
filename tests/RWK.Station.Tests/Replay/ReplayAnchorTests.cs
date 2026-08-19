/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Protocol.Edge;
using RWK.Station.Replay;
using Xunit;

namespace RWK.Station.Tests.Replay;

/// <summary>
/// Anchor establishment, relative scheduling, and idle reset (7.2, 7.3).
/// </summary>
public class ReplayAnchorTests
{
    private const long Freq = 10_000_000L; // 10MHz, as on Windows
    private static long Ms(long milliseconds) => ReplayAnchor.TicksForMilliseconds(milliseconds, Freq);

    [Fact]
    public void FirstEdgeReplaysAtArrivalPlusDelay()
    {
        var anchor = new ReplayAnchor(Freq);
        long arrival = 1_000_000_000L;
        long delay = Ms(60);

        long deadline = anchor.Schedule(arrival, timestampMs: 12_345, delay, out bool reanchored);

        Assert.True(reanchored);
        Assert.Equal(arrival + delay, deadline);
    }

    [Fact]
    public void SubsequentEdgesPreserveClientSpacingRegardlessOfArrivalJitter()
    {
        var anchor = new ReplayAnchor(Freq);
        long arrival = 5_000_000_000L;
        long delay = Ms(60);

        long first = anchor.Schedule(arrival, timestampMs: 1_000, delay, out _);

        // Second edge is 50ms later at the Client but arrives 20ms late on the wire.
        long second = anchor.Schedule(arrival + Ms(70), timestampMs: 1_050, delay, out bool reanchored);

        Assert.False(reanchored);
        Assert.Equal(first + Ms(50), second);
    }

    [Fact]
    public void DeadlineIsAnchorPlusRelativeTimestamp()
    {
        var anchor = new ReplayAnchor(Freq);
        anchor.Schedule(1_000L, timestampMs: 500, Ms(60), out _);

        long deadline = anchor.Schedule(1_000L + Ms(10), timestampMs: 700, Ms(60), out _);

        Assert.Equal(anchor.AnchorQpc + Ms(700), deadline);
    }

    [Fact]
    public void AnchorIsNotResetBelowTheIdleThreshold()
    {
        var anchor = new ReplayAnchor(Freq);
        long arrival = 100_000L;
        anchor.Schedule(arrival, timestampMs: 0, Ms(60), out _);

        anchor.Schedule(arrival + Ms(1_999), timestampMs: 1_999, Ms(60), out bool reanchored);

        Assert.False(reanchored);
        Assert.Equal(1, anchor.AnchorCount);
    }

    [Fact]
    public void AnchorIsResetAtTwoSecondsOfIdle()
    {
        var anchor = new ReplayAnchor(Freq);
        long arrival = 100_000L;
        anchor.Schedule(arrival, timestampMs: 0, Ms(60), out _);

        long lateArrival = arrival + Ms(2_000);
        long deadline = anchor.Schedule(lateArrival, timestampMs: 9_999, Ms(60), out bool reanchored);

        Assert.True(reanchored);
        Assert.Equal(2, anchor.AnchorCount);

        // Re-anchored, so this edge lands at its own arrival + D rather than far in the future.
        Assert.Equal(lateArrival + Ms(60), deadline);
    }

    [Fact]
    public void ResetForcesTheNextEdgeToReanchor()
    {
        var anchor = new ReplayAnchor(Freq);
        anchor.Schedule(1_000L, timestampMs: 0, Ms(60), out _);
        Assert.True(anchor.IsAnchored);

        anchor.Reset();

        Assert.False(anchor.IsAnchored);
        anchor.Schedule(2_000L, timestampMs: 100, Ms(60), out bool reanchored);
        Assert.True(reanchored);
    }

    [Fact]
    public void EdgeOverloadAgreesWithTimestampOverload()
    {
        var byEdge = new ReplayAnchor(Freq);
        var byTimestamp = new ReplayAnchor(Freq);
        EdgeEntry edge = EdgeEntry.KeyDownAt(sequence: 7, timestampMs: 4_321);

        long a = byEdge.Schedule(50_000L, edge, Ms(60), out _);
        long b = byTimestamp.Schedule(50_000L, edge.TimestampMs, Ms(60), out _);

        Assert.Equal(b, a);
    }

    [Fact]
    public void IdleResetDefaultsToTwoSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(2), new ReplayAnchor(Freq).IdleReset);

    [Fact]
    public void TicksForMillisecondsHandlesFullUnsignedRangeWithoutOverflow()
    {
        long ticks = ReplayAnchor.TicksForMilliseconds(uint.MaxValue, Freq);

        Assert.True(ticks > 0);
        Assert.Equal(uint.MaxValue / 1000.0, ticks / (double)Freq, 3);
    }

    [Fact]
    public void NegativeDelayIsTreatedAsZero()
    {
        var anchor = new ReplayAnchor(Freq);

        long deadline = anchor.Schedule(7_000L, timestampMs: 10, delayTicks: -500, out _);

        Assert.Equal(7_000L, deadline);
    }
}
