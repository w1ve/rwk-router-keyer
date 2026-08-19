/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Client.Audio;
using Xunit;

namespace RWK.Client.Tests.Audio;

/// <summary>
/// Device-independent tests for the sidetone DSP. Everything here runs without opening a
/// WASAPI endpoint, which is the reason the oscillator lives in its own class.
/// </summary>
public class KeyedSineGeneratorTests
{
    private const int SampleRate = KeyedSineGenerator.DefaultSampleRate;

    /// <summary>4.4: 5ms ramp, which is 240 samples at 48 kHz.</summary>
    [Fact]
    public void EnvelopeRampIsFiveMilliseconds()
    {
        var generator = new KeyedSineGenerator(SampleRate);

        Assert.Equal(240, generator.EnvelopeRampSamples);
        Assert.Equal(0.005, generator.EnvelopeRampSamples / (double)SampleRate, 6);
    }

    /// <summary>4.4: attack reaches full scale after exactly one ramp, and not before.</summary>
    [Fact]
    public void AttackReachesFullScaleAfterExactlyOneRamp()
    {
        var generator = new KeyedSineGenerator(SampleRate);
        int ramp = generator.EnvelopeRampSamples;

        generator.KeyDown();
        generator.Generate(new float[ramp - 1]);
        Assert.True(generator.CurrentLinearEnvelope < 1.0);

        generator.Generate(new float[1]);
        Assert.Equal(1.0, generator.CurrentLinearEnvelope, 10);
    }

    /// <summary>4.4: decay reaches silence after exactly one ramp, and not before.</summary>
    [Fact]
    public void DecayReachesSilenceAfterExactlyOneRamp()
    {
        var generator = new KeyedSineGenerator(SampleRate);
        int ramp = generator.EnvelopeRampSamples;

        generator.KeyDown();
        generator.Generate(new float[ramp]);
        generator.KeyUp();

        generator.Generate(new float[ramp - 1]);
        Assert.True(generator.CurrentLinearEnvelope > 0.0);

        generator.Generate(new float[1]);
        Assert.Equal(0.0, generator.CurrentLinearEnvelope, 10);
    }

    /// <summary>4.4: the attack envelope never dips, so there is no amplitude discontinuity.</summary>
    [Fact]
    public void AttackEnvelopeIsMonotonicNonDecreasing()
    {
        var generator = new KeyedSineGenerator(SampleRate);
        generator.KeyDown();

        double previous = generator.CurrentShapedEnvelope;
        for (int i = 0; i < generator.EnvelopeRampSamples; i++)
        {
            generator.Generate(new float[1]);
            double current = generator.CurrentShapedEnvelope;
            Assert.True(current >= previous, $"Envelope fell at sample {i}: {previous} -> {current}");
            previous = current;
        }

        Assert.Equal(1.0, previous, 10);
    }

    /// <summary>4.4: the decay envelope never rises.</summary>
    [Fact]
    public void DecayEnvelopeIsMonotonicNonIncreasing()
    {
        var generator = new KeyedSineGenerator(SampleRate);
        generator.KeyDown();
        generator.Generate(new float[generator.EnvelopeRampSamples]);
        generator.KeyUp();

        double previous = generator.CurrentShapedEnvelope;
        for (int i = 0; i < generator.EnvelopeRampSamples; i++)
        {
            generator.Generate(new float[1]);
            double current = generator.CurrentShapedEnvelope;
            Assert.True(current <= previous, $"Envelope rose at sample {i}: {previous} -> {current}");
            previous = current;
        }

        Assert.Equal(0.0, previous, 10);
    }

    /// <summary>4.4: raised-cosine endpoints are flat, which is what suppresses the click.</summary>
    [Fact]
    public void RaisedCosineShapeIsFlatAtBothEnds()
    {
        Assert.Equal(0.0, KeyedSineGenerator.Shape(0.0), 12);
        Assert.Equal(1.0, KeyedSineGenerator.Shape(1.0), 12);
        Assert.Equal(0.5, KeyedSineGenerator.Shape(0.5), 12);

        // Slope at the ends is far smaller than a linear ramp's constant slope.
        const double step = 0.001;
        double slopeAtStart = KeyedSineGenerator.Shape(step) - KeyedSineGenerator.Shape(0.0);
        double slopeAtEnd = KeyedSineGenerator.Shape(1.0) - KeyedSineGenerator.Shape(1.0 - step);
        Assert.True(slopeAtStart < step / 10);
        Assert.True(slopeAtEnd < step / 10);
    }

    /// <summary>4.5: output never exceeds the configured volume.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(1.0)]
    public void OutputAmplitudeNeverExceedsVolume(double volume)
    {
        var generator = new KeyedSineGenerator(SampleRate, 700, volume);
        generator.KeyDown();

        var frames = new float[SampleRate / 10];
        generator.Generate(frames);

        foreach (float sample in frames)
        {
            Assert.True(Math.Abs(sample) <= volume + 1e-6, $"Sample {sample} exceeded volume {volume}");
        }
    }

    /// <summary>An un-keyed stream is silent, so the engine can run continuously (4.1).</summary>
    [Fact]
    public void UnkeyedStreamIsSilent()
    {
        var generator = new KeyedSineGenerator(SampleRate);

        var frames = new float[SampleRate / 10];
        generator.Generate(frames);

        Assert.All(frames, sample => Assert.Equal(0f, sample));
    }

    /// <summary>4.3: out-of-range frequencies clamp rather than throw.</summary>
    [Theory]
    [InlineData(0, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(700, 700)]
    [InlineData(1500, 1500)]
    [InlineData(1501, 1500)]
    [InlineData(48000, 1500)]
    [InlineData(-100, 300)]
    public void FrequencyIsClamped(int requested, int expected)
    {
        Assert.Equal(expected, KeyedSineGenerator.ClampFrequency(requested));
        Assert.Equal(expected, new KeyedSineGenerator(SampleRate, requested).FrequencyHz);
    }

    /// <summary>4.5: out-of-range volumes clamp rather than throw.</summary>
    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.5, 1.0)]
    [InlineData(double.NaN, 0.0)]
    public void VolumeIsClamped(double requested, double expected)
    {
        Assert.Equal(expected, KeyedSineGenerator.ClampVolume(requested));
        Assert.Equal(expected, new KeyedSineGenerator(SampleRate, 700, requested).Amplitude);
    }

    /// <summary>Defaults match SidetoneConfig (700 Hz, 0.5).</summary>
    [Fact]
    public void DefaultsMatchSidetoneConfig()
    {
        var config = new RWK.Shared.Config.SidetoneConfig();
        var generator = new KeyedSineGenerator();

        Assert.Equal(config.FrequencyHz, generator.FrequencyHz);
        Assert.Equal(config.Volume, generator.Amplitude);
        Assert.Equal(700, KeyedSineGenerator.DefaultFrequencyHz);
    }

    /// <summary>
    /// The tone is at the requested frequency: count zero crossings over a fully-keyed second.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(700)]
    [InlineData(1500)]
    public void ToneIsGeneratedAtRequestedFrequency(int frequencyHz)
    {
        var generator = new KeyedSineGenerator(SampleRate, frequencyHz, 1.0);
        generator.KeyDown();

        // Discard the attack so only the steady-state tone is measured.
        generator.Generate(new float[generator.EnvelopeRampSamples]);

        var frames = new float[SampleRate];
        generator.Generate(frames);

        int crossings = 0;
        for (int i = 1; i < frames.Length; i++)
        {
            if ((frames[i - 1] < 0f && frames[i] >= 0f) || (frames[i - 1] >= 0f && frames[i] < 0f))
            {
                crossings++;
            }
        }

        // Two crossings per cycle; allow one cycle of slack for the window edges.
        Assert.InRange(crossings / 2.0, frequencyHz - 1, frequencyHz + 1);
    }
}
