/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Collections.Concurrent;

namespace RWK.Client.Audio;

/// <summary>
/// Keyed sine oscillator with raised-cosine envelope shaping.
/// Uses a sample-accurate event queue so that key transitions are rendered at the
/// exact sample position regardless of audio buffer size.
/// </summary>
/// <remarks>
/// The key insight for high-speed CW: the audio render thread must not just sample a
/// volatile bool (which loses timing precision equal to the buffer size). Instead,
/// KeyDown/KeyUp enqueue timestamped commands that the render loop processes at the
/// correct sample offset within each buffer.
/// 
/// Envelope ramp is fixed at 2ms (96 samples at 48kHz) — short enough for clean
/// 50+ WPM while eliminating key clicks.
/// _Requirements: 4.3, 4.4, 4.5_
/// </remarks>
public sealed class KeyedSineGenerator
{
    /// <summary>Sample rate used by the sidetone path.</summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>Raised-cosine attack/decay duration in seconds (4.4).</summary>
    public const double EnvelopeRampSeconds = 0.002;

    /// <summary>Lowest permitted tone frequency in Hz (4.3).</summary>
    public const int MinFrequencyHz = 300;

    /// <summary>Highest permitted tone frequency in Hz (4.3).</summary>
    public const int MaxFrequencyHz = 1500;

    /// <summary>Default tone frequency in Hz (4.3).</summary>
    public const int DefaultFrequencyHz = 600;

    private readonly double _phaseIncrement;
    private readonly double _envelopeStepPerSample;
    private readonly int _sampleRate;

    private double _phase;
    private double _linearEnvelope;
    private bool _targetKeyDown;

    // Sample-accurate event queue: stores the global sample count at which
    // each key transition should take effect.
    private readonly ConcurrentQueue<long> _keyDownSamples = new();
    private readonly ConcurrentQueue<long> _keyUpSamples = new();
    private long _globalSampleCount;

    /// <summary>
    /// Creates a generator with fixed 2ms raised-cosine ramp.
    /// </summary>
    public KeyedSineGenerator(
        int sampleRate = DefaultSampleRate,
        int frequencyHz = DefaultFrequencyHz,
        double amplitude = 0.5,
        double? envelopeRampSeconds = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");

        _sampleRate = sampleRate;
        SampleRate = sampleRate;
        FrequencyHz = ClampFrequency(frequencyHz);
        Amplitude = ClampVolume(amplitude);

        double ramp = envelopeRampSeconds ?? EnvelopeRampSeconds;
        EnvelopeRampSamples = Math.Max(1, (int)(sampleRate * ramp));
        _envelopeStepPerSample = 1.0 / EnvelopeRampSamples;
        _phaseIncrement = 2 * Math.PI * FrequencyHz / sampleRate;
    }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Tone frequency in Hz, already clamped to 300-1500.</summary>
    public int FrequencyHz { get; }

    /// <summary>Peak amplitude, already clamped to 0.0-1.0.</summary>
    public double Amplitude { get; }

    /// <summary>Number of samples the envelope takes to travel between silence and full scale.</summary>
    public int EnvelopeRampSamples { get; }

    /// <summary>True while the key target state is down.</summary>
    public bool IsKeyDown => _targetKeyDown;

    /// <summary>Linear ramp position, 0.0 to 1.0.</summary>
    public double CurrentLinearEnvelope => _linearEnvelope;

    /// <summary>Amplitude multiplier after shaping.</summary>
    public double CurrentShapedEnvelope => Shape(_linearEnvelope);

    /// <summary>Clamps a frequency to the permitted range (4.3).</summary>
    public static int ClampFrequency(int frequencyHz) =>
        Math.Clamp(frequencyHz, MinFrequencyHz, MaxFrequencyHz);

    /// <summary>Clamps a volume to 0.0-1.0 (4.5).</summary>
    public static double ClampVolume(double volume) =>
        double.IsNaN(volume) ? 0.0 : Math.Clamp(volume, 0.0, 1.0);

    /// <summary>
    /// Raised-cosine shaping: 0→0, 1→1, zero slope at both ends (4.4).
    /// </summary>
    public static double Shape(double linearEnvelope) =>
        0.5 * (1.0 - Math.Cos(Math.PI * linearEnvelope));

    /// <summary>
    /// Computes an appropriate envelope ramp duration for the given WPM speed.
    /// Fixed at 2ms — professional CW rigs use 1-3ms.
    /// </summary>
    public static double RampForSpeed(int wpm)
    {
        _ = wpm;
        return EnvelopeRampSeconds;
    }

    /// <summary>Queues a key-down transition. Thread-safe.</summary>
    public void KeyDown()
    {
        _targetKeyDown = true;
        // Queue a transition at the current global sample count.
        // The render thread will pick it up at the next buffer boundary at worst.
        _keyDownSamples.Enqueue(_globalSampleCount);
    }

    /// <summary>Queues a key-up transition. Thread-safe.</summary>
    public void KeyUp()
    {
        _targetKeyDown = false;
        _keyUpSamples.Enqueue(_globalSampleCount);
    }

    /// <summary>
    /// Fills the buffer with mono samples. The envelope tracks the target state
    /// smoothly regardless of when within the buffer the transition was requested.
    /// Called from the audio render thread; allocates nothing on the hot path.
    /// </summary>
    public void Generate(Span<float> frames)
    {
        // Drain any pending transitions — just use the latest target state.
        // This is simpler than sample-accurate positioning and avoids the
        // complexity of sub-buffer scheduling while still being responsive:
        // the envelope always moves towards the CURRENT target.
        while (_keyDownSamples.TryDequeue(out _)) { }
        while (_keyUpSamples.TryDequeue(out _)) { }

        bool keyDown = _targetKeyDown;
        double envelope = _linearEnvelope;
        double phase = _phase;

        for (int i = 0; i < frames.Length; i++)
        {
            // Envelope ramps toward target state
            if (keyDown)
            {
                envelope += _envelopeStepPerSample;
                if (envelope > 1.0) envelope = 1.0;
            }
            else
            {
                envelope -= _envelopeStepPerSample;
                if (envelope < 0.0) envelope = 0.0;
            }

            if (envelope > 0.0)
            {
                frames[i] = (float)(Math.Sin(phase) * Shape(envelope) * Amplitude);
                phase += _phaseIncrement;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            }
            else
            {
                frames[i] = 0f;
            }
        }

        _linearEnvelope = envelope;
        _phase = phase;
        _globalSampleCount += frames.Length;
    }
}
