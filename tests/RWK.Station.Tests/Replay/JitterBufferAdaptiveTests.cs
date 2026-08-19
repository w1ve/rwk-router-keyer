/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Station.Replay;
using Xunit;

namespace RWK.Station.Tests.Replay;

/// <summary>
/// Tests for the adaptive jitter buffer algorithm (task 11.3):
/// - The formula: base + 2 × jitter_ewma clamped to band
/// - Path-type switching with deferred transitions
/// - Adaptive mode vs fixed mode
/// - Late-edge storm auto-bump
/// _Requirements: 7.6, 7.7_
/// </summary>
public class JitterBufferAdaptiveTests
{
    private static JitterBufferConfig Adaptive => JitterBufferConfig.Default;

    private static JitterBufferConfig NonAdaptive => new(
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(200),
        AdaptiveMode: false);

    // ─── Formula: base + 2 × jitter_ewma clamped ────────────────────────────────

    [Fact]
    public void AdaptiveDelayFormula_BasePlusTwiceJitter_DirectPath()
    {
        // Direct path, base 60ms. Feed RTT samples that produce a known jitter EWMA.
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // Seeds RTT=40, jitter=0
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(60)); // RTT= 0.2*60 + 0.8*40 = 44, jitter= 0.1*|60-40| = 2

        // delay = 60 + 2*2 = 64ms
        Assert.Equal(64.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    [Fact]
    public void AdaptiveDelayFormula_BasePlusTwiceJitter_DerpPath()
    {
        // DERP path, base 200ms.
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Derp);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(100)); // Seed
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(150)); // RTT=110, jitter=0.1*|150-100|=5

        // delay = 200 + 2*5 = 210ms
        Assert.Equal(210.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    [Fact]
    public void AdaptiveDelayFormula_ClampedToDirectMaximum()
    {
        // Push jitter high enough to exceed Direct band max (150ms).
        TimeSpan result = JitterBuffer.DelayFor(
            Adaptive,
            PathType.Direct,
            EdgeJitterProfile.PathAdaptive,
            hasSamples: true,
            jitterEwmaMs: 500); // 60 + 2*500 = 1060 → clamp to 150

        Assert.Equal(JitterBuffer.DirectMaxDelay, result);
    }

    [Fact]
    public void AdaptiveDelayFormula_ClampedToDirectMinimum()
    {
        // A very low base with negative-like jitter (won't happen but tests the floor).
        var config = new JitterBufferConfig(
            TimeSpan.FromMilliseconds(25), // below minimum, base will be clamped to 30
            TimeSpan.FromMilliseconds(200),
            AdaptiveMode: true);

        TimeSpan result = JitterBuffer.DelayFor(
            config,
            PathType.Direct,
            EdgeJitterProfile.PathAdaptive,
            hasSamples: true,
            jitterEwmaMs: 0);

        // base is clamped to 30ms, 30 + 2*0 = 30ms (at floor)
        Assert.Equal(JitterBuffer.DirectMinDelay, result);
    }

    [Fact]
    public void AdaptiveDelayFormula_ClampedToDerpMaximum()
    {
        TimeSpan result = JitterBuffer.DelayFor(
            Adaptive,
            PathType.Derp,
            EdgeJitterProfile.PathAdaptive,
            hasSamples: true,
            jitterEwmaMs: 500); // 200 + 2*500 = 1200 → clamp to 500

        Assert.Equal(JitterBuffer.DerpMaxDelay, result);
    }

    [Fact]
    public void AdaptiveDelayFormula_ClampedToDerpMinimum()
    {
        var config = new JitterBufferConfig(
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(50), // below minimum, base will be clamped to 100
            AdaptiveMode: true);

        TimeSpan result = JitterBuffer.DelayFor(
            config,
            PathType.Derp,
            EdgeJitterProfile.PathAdaptive,
            hasSamples: true,
            jitterEwmaMs: 0);

        Assert.Equal(JitterBuffer.DerpMinDelay, result);
    }

    [Fact]
    public void EwmaAlpha_RttIsPointTwo_JitterIsPointOne()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(50));  // seed: RTT=50, jitter=0
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(70));  // RTT=0.2*70+0.8*50=54, jitter=0.1*|70-50|=2
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(90));  // RTT=0.2*90+0.8*54=61.2, jitter=0.1*|90-54|+0.9*2=3.6+1.8=5.4

        // Actually: deviation = |90-54| = 36, jitter = 0.1*36 + 0.9*2 = 3.6+1.8 = 5.4
        Assert.Equal(61.2, buffer.RttEwmaMs, 3);
        Assert.Equal(5.4, buffer.JitterEwmaMs, 3);

        // delay = 60 + 2*5.4 = 70.8ms
        Assert.Equal(70.8, buffer.CurrentDelay.TotalMilliseconds, 3);
    }

    // ─── Adaptive mode vs fixed mode ────────────────────────────────────────────

    [Fact]
    public void FixedMode_IgnoresRttSamples()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(20));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(200));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(500));

        // Should stay at base delay of 60ms
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void FixedMode_LateEdgeStormDoesNotBump()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        long now = DateTime.UtcNow.Ticks;
        // Report 5 late edges — should not bump because not adaptive
        for (int i = 0; i < 5; i++)
        {
            bool bumped = buffer.ReportLateEdge(now + i * TimeSpan.TicksPerSecond);
            Assert.False(bumped);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void AdaptiveMode_BeforeSamples_UsesBaseDelay()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        // No samples observed yet
        Assert.False(buffer.HasSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void AdaptiveMode_FirstSample_SeedsRttButLeavesJitterZero()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(30));

        Assert.True(buffer.HasSamples);
        Assert.Equal(30.0, buffer.RttEwmaMs, 6);
        Assert.Equal(0.0, buffer.JitterEwmaMs, 6);
        // base + 2*0 = 60ms (unchanged from base)
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void AdaptiveMode_NegativeRttSampleIsIgnored()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(-10));

        Assert.False(buffer.HasSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    // ─── Path-type switching (deferred transitions) ─────────────────────────────

    [Fact]
    public void PathChange_IsDeferred_NotAppliedImmediately()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);

        // Change path: should be deferred
        buffer.Path = PathType.Derp;

        // Delay should still be 60ms (Direct) since change is pending
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
        Assert.True(buffer.HasPendingPathChange);
        Assert.Equal(PathType.Derp, buffer.PendingPath);
    }

    [Fact]
    public void PathChange_AppliedAtAnchorReset()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.Path = PathType.Derp;

        // Apply the pending change (simulating anchor reset)
        bool applied = buffer.ApplyPendingPathChange();

        Assert.True(applied);
        Assert.False(buffer.HasPendingPathChange);
        Assert.Equal(PathType.Derp, buffer.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void PathChange_DerpToDirect_DefersAndApplies()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Derp);
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);

        buffer.Path = PathType.Direct;

        // Still DERP until applied
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);

        buffer.ApplyPendingPathChange();

        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void PathChange_SamePathType_NoPendingChange()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.Path = PathType.Direct; // same as current

        Assert.False(buffer.HasPendingPathChange);
    }

    [Fact]
    public void PathChange_NoPending_ApplyReturnsFalse()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        bool applied = buffer.ApplyPendingPathChange();

        Assert.False(applied);
    }

    [Fact]
    public void SetPathImmediate_AppliesWithoutDefer()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.SetPathImmediate(PathType.Derp);

        Assert.False(buffer.HasPendingPathChange);
        Assert.Equal(PathType.Derp, buffer.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void SetPathImmediate_ClearsPendingChange()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.Path = PathType.Derp; // deferred
        Assert.True(buffer.HasPendingPathChange);

        buffer.SetPathImmediate(PathType.Derp); // immediate overrides pending

        Assert.False(buffer.HasPendingPathChange);
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void DerpClassOnlyProfile_UseDerpBandEvenOnDirectPath()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.DerpClassOnly, PathType.Direct);

        // DerpClassOnly forces DERP band regardless of path
        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void DerpClassOnlyProfile_PathChangeStillDeferred()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.DerpClassOnly, PathType.Direct);

        buffer.Path = PathType.Derp;

        // With DerpClassOnly both bands are DERP, but the path is still tracked
        Assert.True(buffer.HasPendingPathChange);

        buffer.ApplyPendingPathChange();
        Assert.Equal(PathType.Derp, buffer.Path);
    }

    // ─── Late-edge storm auto-bump ──────────────────────────────────────────────

    [Fact]
    public void LateEdgeStorm_ThreeOrFewerDoesNotBump()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed to enable adaptive

        long now = DateTime.UtcNow.Ticks;

        // 3 late edges within the window should NOT trigger (threshold is >3)
        Assert.False(buffer.ReportLateEdge(now));
        Assert.False(buffer.ReportLateEdge(now + TimeSpan.TicksPerSecond));
        Assert.False(buffer.ReportLateEdge(now + 2 * TimeSpan.TicksPerSecond));

        Assert.Equal(0.0, buffer.AutoBumpMs);
    }

    [Fact]
    public void LateEdgeStorm_FourLateEdgesTriggersAutoBump()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed

        long now = DateTime.UtcNow.Ticks;

        // 4 late edges within 10s → triggers auto-bump
        buffer.ReportLateEdge(now);
        buffer.ReportLateEdge(now + TimeSpan.TicksPerSecond);
        buffer.ReportLateEdge(now + 2 * TimeSpan.TicksPerSecond);
        bool bumped = buffer.ReportLateEdge(now + 3 * TimeSpan.TicksPerSecond);

        Assert.True(bumped);
        Assert.Equal(JitterBuffer.AutoBumpStepMs, buffer.AutoBumpMs);
    }

    [Fact]
    public void LateEdgeStorm_AutoBumpIncreasesDelay()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed: RTT=40, jitter=0

        double basePlusJitter = 60.0; // 60 + 2*0 = 60
        Assert.Equal(basePlusJitter, buffer.CurrentDelay.TotalMilliseconds, 6);

        long now = DateTime.UtcNow.Ticks;
        buffer.ReportLateEdge(now);
        buffer.ReportLateEdge(now + 1);
        buffer.ReportLateEdge(now + 2);
        buffer.ReportLateEdge(now + 3); // triggers bump

        // Now delay = 60 + 2*0 + 10 (bump) = 70ms
        Assert.Equal(70.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    [Fact]
    public void LateEdgeStorm_MultipleBumpsAccumulate()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed

        long now = DateTime.UtcNow.Ticks;

        // First storm
        for (int i = 0; i < 4; i++)
            buffer.ReportLateEdge(now + i);
        Assert.Equal(10.0, buffer.AutoBumpMs);

        // Second storm (window was cleared after bump, so 4 more needed)
        long later = now + 5 * TimeSpan.TicksPerSecond;
        for (int i = 0; i < 4; i++)
            buffer.ReportLateEdge(later + i);
        Assert.Equal(20.0, buffer.AutoBumpMs);

        // delay = 60 + 2*0 + 20 = 80ms
        Assert.Equal(80.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    [Fact]
    public void LateEdgeStorm_BumpClampedToMaxMinusBase()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed

        long now = DateTime.UtcNow.Ticks;

        // Trigger many storms to push bump beyond max
        for (int storm = 0; storm < 20; storm++)
        {
            long stormStart = now + storm * 100;
            for (int i = 0; i < 4; i++)
                buffer.ReportLateEdge(stormStart + i);
        }

        // Max Direct band is 150ms, base is 60ms, so max bump is 90ms
        Assert.True(buffer.AutoBumpMs <= 90.0);
        // Delay should be clamped to Direct max
        Assert.True(buffer.CurrentDelay <= JitterBuffer.DirectMaxDelay);
    }

    [Fact]
    public void LateEdgeStorm_OldEdgesExpireOutOfWindow()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed

        long now = DateTime.UtcNow.Ticks;

        // 3 edges early in the window
        buffer.ReportLateEdge(now);
        buffer.ReportLateEdge(now + TimeSpan.TicksPerSecond);
        buffer.ReportLateEdge(now + 2 * TimeSpan.TicksPerSecond);

        // 4th edge arrives 11 seconds later — first 3 have expired from window
        long later = now + 11 * TimeSpan.TicksPerSecond;
        bool bumped = buffer.ReportLateEdge(later);

        Assert.False(bumped);
        Assert.Equal(0.0, buffer.AutoBumpMs);
    }

    [Fact]
    public void LateEdgeStorm_ResetSamplesClearsBump()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));

        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < 4; i++)
            buffer.ReportLateEdge(now + i);

        Assert.True(buffer.AutoBumpMs > 0);

        buffer.ResetSamples();

        Assert.Equal(0.0, buffer.AutoBumpMs);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void LateEdgeStorm_CombinesWithJitterEwma()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40)); // seed
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(60)); // RTT=44, jitter=2

        // delay = 60 + 2*2 = 64ms
        Assert.Equal(64.0, buffer.CurrentDelay.TotalMilliseconds, 6);

        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < 4; i++)
            buffer.ReportLateEdge(now + i);

        // delay = 60 + 2*2 + 10 = 74ms
        Assert.Equal(74.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    // ─── Integration: path transitions with adaptive mode ───────────────────────

    [Fact]
    public void PathTransition_DirectToDepr_PreservesEwmaAndBump()
    {
        var buffer = new JitterBuffer(Adaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(60)); // jitter=2

        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < 4; i++)
            buffer.ReportLateEdge(now + i); // bump=10

        // Direct delay = 60 + 2*2 + 10 = 74
        Assert.Equal(74.0, buffer.CurrentDelay.TotalMilliseconds, 6);

        // Switch path (deferred)
        buffer.Path = PathType.Derp;
        Assert.Equal(74.0, buffer.CurrentDelay.TotalMilliseconds, 6); // still Direct

        buffer.ApplyPendingPathChange();
        // DERP delay = 200 + 2*2 + 10 = 214
        Assert.Equal(214.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }
}
