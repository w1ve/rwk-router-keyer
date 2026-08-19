/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using NAudio.Wave;
using RWK.Client.Audio;
using Xunit;

namespace RWK.Client.Tests.Audio;

/// <summary>
/// Buffer-level tests for the NAudio adapter. No audio device is opened: Read is called
/// directly, exactly as the render thread would.
/// </summary>
public class KeyedSineWaveProviderTests
{
    private static KeyedSineWaveProvider CreateProvider(double volume = 1.0) =>
        new(new KeyedSineGenerator(KeyedSineGenerator.DefaultSampleRate, 700, volume));

    [Fact]
    public void WaveFormatIsStereoIeeeFloatAtEngineSampleRate()
    {
        var provider = CreateProvider();

        Assert.Equal(WaveFormatEncoding.IeeeFloat, provider.WaveFormat.Encoding);
        Assert.Equal(2, provider.WaveFormat.Channels);
        Assert.Equal(LocalSidetoneEngine.SampleRate, provider.WaveFormat.SampleRate);
    }

    [Fact]
    public void ReadReturnsRequestedCountAndFillsBothChannelsIdentically()
    {
        var provider = CreateProvider();
        provider.KeyDown();

        int frames = 512;
        var buffer = new byte[frames * 2 * 4];
        int read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);

        var floats = new float[frames * 2];
        Buffer.BlockCopy(buffer, 0, floats, 0, buffer.Length);
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(floats[i * 2], floats[(i * 2) + 1]);
        }

        Assert.Contains(floats, sample => sample != 0f);
    }

    [Fact]
    public void ReadProducesSilenceWhileUnkeyed()
    {
        var provider = CreateProvider();

        var buffer = new byte[512 * 2 * 4];
        provider.Read(buffer, 0, buffer.Length);

        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ReadHonoursOffsetAndLeavesEarlierBytesUntouched()
    {
        var provider = CreateProvider();
        provider.KeyDown();

        const int offsetFrames = 8;
        int offsetBytes = offsetFrames * 4;
        var buffer = new byte[offsetBytes + (256 * 2 * 4)];
        for (int i = 0; i < offsetBytes; i++)
        {
            buffer[i] = 0xAB;
        }

        provider.Read(buffer, offsetBytes, buffer.Length - offsetBytes);

        for (int i = 0; i < offsetBytes; i++)
        {
            Assert.Equal(0xAB, buffer[i]);
        }
    }

    [Fact]
    public void SuccessiveReadsContinueTheEnvelopeRatherThanRestartingIt()
    {
        var provider = CreateProvider();
        provider.KeyDown();

        // 64 stereo frames per read, well short of the 240-sample ramp.
        var buffer = new byte[64 * 2 * 4];
        provider.Read(buffer, 0, buffer.Length);
        double afterFirst = provider.Generator.CurrentLinearEnvelope;

        provider.Read(buffer, 0, buffer.Length);
        double afterSecond = provider.Generator.CurrentLinearEnvelope;

        Assert.True(afterSecond > afterFirst);
    }
}
