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
/// Delay selection and adaptation rules of the jitter buffer (7.1, 7.6, 7.7).
/// </summary>
public class JitterBufferTests
{
    private static JitterBufferConfig NonAdaptive => new(
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(200),
        AdaptiveMode: false);

    [Fact]
    public void DirectPathUsesSixtyMillisecondDefault()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void DerpPathUsesTwoHundredMillisecondDefault()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Derp);

        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void UnknownPathUsesDerpBand()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.None);

        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void DerpClassOnlyProfileForcesDerpBandOnDirectPath()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.DerpClassOnly, PathType.Direct);

        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
        Assert.True(JitterBuffer.UsesDerpBand(PathType.Direct, EdgeJitterProfile.DerpClassOnly));
    }

    [Theory]
    [InlineData(1, 30)]      // below the direct minimum
    [InlineData(1000, 300)]  // above the direct maximum
    public void ConfiguredDirectDelayIsClampedIntoTheDirectBand(int configuredMs, int expectedMs)
    {
        var config = new JitterBufferConfig(
            TimeSpan.FromMilliseconds(configuredMs),
            TimeSpan.FromMilliseconds(200),
            AdaptiveMode: false);

        var buffer = new JitterBuffer(config, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), buffer.CurrentDelay);
    }

    [Theory]
    [InlineData(1, 100)]     // below the DERP minimum
    [InlineData(5000, 500)]  // above the DERP maximum
    public void ConfiguredDerpDelayIsClampedIntoTheDerpBand(int configuredMs, int expectedMs)
    {
        var config = new JitterBufferConfig(
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(configuredMs),
            AdaptiveMode: false);

        var buffer = new JitterBuffer(config, EdgeJitterProfile.PathAdaptive, PathType.Derp);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), buffer.CurrentDelay);
    }

    [Fact]
    public void NonAdaptiveModeIgnoresSamples()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(20));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(120));

        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void FirstSampleSeedsRttEwmaAndLeavesJitterAtZero()
    {
        var buffer = new JitterBuffer(JitterBufferConfig.Default, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));

        Assert.True(buffer.HasSamples);
        Assert.Equal(40.0, buffer.RttEwmaMs, 6);
        Assert.Equal(0.0, buffer.JitterEwmaMs, 6);

        // base + 2 x 0 == base
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void EwmaUsesAlphaPointTwoForRttAndPointOneForJitter()
    {
        var buffer = new JitterBuffer(JitterBufferConfig.Default, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(60));

        // rtt: 0.2 x 60 + 0.8 x 40 = 44
        Assert.Equal(44.0, buffer.RttEwmaMs, 6);

        // jitter: deviation |60 - 40| = 20 -> 0.1 x 20 + 0.9 x 0 = 2
        Assert.Equal(2.0, buffer.JitterEwmaMs, 6);
    }

    [Fact]
    public void AdaptiveDelayIsBasePlusTwiceJitterEwma()
    {
        var buffer = new JitterBuffer(JitterBufferConfig.Default, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(60));

        // 60ms base + 2 x 2ms jitter = 64ms
        Assert.Equal(64.0, buffer.CurrentDelay.TotalMilliseconds, 6);
    }

    [Fact]
    public void AdaptiveDelayIsClampedToTheBandMaximum()
    {
        TimeSpan delay = JitterBuffer.DelayFor(
            JitterBufferConfig.Default,
            PathType.Direct,
            EdgeJitterProfile.PathAdaptive,
            hasSamples: true,
            jitterEwmaMs: 5000);

        Assert.Equal(JitterBuffer.DirectMaxDelay, delay);
    }

    [Fact]
    public void ResetSamplesReturnsDelayToBase()
    {
        var buffer = new JitterBuffer(JitterBufferConfig.Default, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        buffer.ObserveRtt(TimeSpan.FromMilliseconds(40));
        buffer.ObserveRtt(TimeSpan.FromMilliseconds(200));
        Assert.True(buffer.CurrentDelay > TimeSpan.FromMilliseconds(60));

        buffer.ResetSamples();

        Assert.False(buffer.HasSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);
    }

    [Fact]
    public void ChangingPathSwitchesBandImmediately()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.CurrentDelay);

        buffer.SetPathImmediate(PathType.Derp);

        Assert.Equal(TimeSpan.FromMilliseconds(200), buffer.CurrentDelay);
    }

    [Fact]
    public void CurrentDelayInConvertsToClockTicks()
    {
        var buffer = new JitterBuffer(NonAdaptive, EdgeJitterProfile.PathAdaptive, PathType.Direct);

        // 60ms of a 10MHz clock is 600,000 ticks.
        Assert.Equal(600_000L, buffer.CurrentDelayIn(10_000_000L));

        // 60ms of a 1kHz clock is 60 ticks.
        Assert.Equal(60L, buffer.CurrentDelayIn(1_000L));
    }

    [Theory]
    [InlineData("PathAdaptive", EdgeJitterProfile.PathAdaptive)]
    [InlineData("DerpClassOnly", EdgeJitterProfile.DerpClassOnly)]
    [InlineData("something-else", EdgeJitterProfile.DerpClassOnly)]
    [InlineData(null, EdgeJitterProfile.DerpClassOnly)]
    public void ProfileDeclarationParsesConservatively(string? declaration, EdgeJitterProfile expected)
        => Assert.Equal(expected, EdgeJitterProfiles.FromDeclaration(declaration));
}
