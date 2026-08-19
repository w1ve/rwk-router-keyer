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
using NAudio.CoreAudioApi;

namespace WinKeyerEmulator.App.Audio;

/// <summary>
/// Provides sidetone audio output that can be keyed on/off.
/// Uses WASAPI for low-latency audio with shaped envelope to eliminate key clicks.
/// </summary>
public sealed class SidetoneOutput : IDisposable
{
    private WasapiOut? _waveOut;
    private KeyedSineWaveProvider? _sineProvider;
    private bool _disposed;
    private int _toneHz;
    private double _amplitude;

    public const int SampleRate = 48000;

    /// <summary>
    /// Creates a new SidetoneOutput with default settings.
    /// Call Initialize() before use.
    /// </summary>
    public SidetoneOutput()
    {
        _toneHz = 700;
        _amplitude = 0.5;
    }

    /// <summary>
    /// Gets whether audio is currently playing (initialized and running).
    /// </summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// Gets or sets the tone frequency in Hz (300-1500).
    /// Changes take effect on next Initialize() call.
    /// </summary>
    public int ToneFrequency
    {
        get => _toneHz;
        set => _toneHz = Math.Clamp(value, 300, 1500);
    }

    /// <summary>
    /// Gets or sets the volume (0.0-1.0).
    /// Changes take effect on next Initialize() call.
    /// </summary>
    public double Volume
    {
        get => _amplitude;
        set => _amplitude = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets all available audio output devices.
    /// </summary>
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // Add default device
        devices.Add(new AudioDeviceInfo
        {
            Id = "",
            Name = "(Default Device)"
        });

        // Enumerate WASAPI devices
        var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(new AudioDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName
            });
        }

        return devices;
    }

    /// <summary>
    /// Initializes audio output with the specified device.
    /// </summary>
    /// <param name="deviceId">Device ID, or empty/null for default device.</param>
    public void Initialize(string? deviceId)
    {
        Stop();

        _sineProvider = new KeyedSineWaveProvider(SampleRate, _toneHz, _amplitude);

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device;

        if (string.IsNullOrEmpty(deviceId))
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        else
        {
            device = enumerator.GetDevice(deviceId);
        }

        // Use shared mode with 20ms latency buffer for low latency
        _waveOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
        _waveOut.Init(_sineProvider);
        _waveOut.Play();
    }

    /// <summary>
    /// Call when the key goes down - starts tone.
    /// </summary>
    public void KeyDown()
    {
        _sineProvider?.KeyDown();
    }

    /// <summary>
    /// Call when the key goes up - stops tone.
    /// </summary>
    public void KeyUp()
    {
        _sineProvider?.KeyUp();
    }

    /// <summary>
    /// Stops audio output and releases the device.
    /// </summary>
    public void Stop()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _sineProvider = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }
}

/// <summary>
/// A continuous sine wave provider that can be keyed on/off with shaped envelope.
/// This runs continuously and produces silence when not keyed.
/// Uses a 2ms raised-cosine envelope for click-free keying.
/// </summary>
internal class KeyedSineWaveProvider : IWaveProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly double _frequency;
    private readonly double _amplitude;
    private readonly int _sampleRate;
    private double _phase;
    private double _currentEnvelope;
    private bool _keyDown;

    // Envelope shaping - 2ms rise/fall for click-free keying
    private readonly double _envelopeAttackPerSample;
    private readonly double _envelopeReleasePerSample;

    public KeyedSineWaveProvider(int sampleRate, double frequency, double amplitude)
    {
        _sampleRate = sampleRate;
        _frequency = frequency;
        _amplitude = amplitude;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2); // Stereo float
        _phase = 0;
        _currentEnvelope = 0;
        _keyDown = false;

        // 2ms attack/release - fast enough to not shorten dits at high WPM
        double envelopeTimeSeconds = 0.002;
        int envelopeSamples = (int)(sampleRate * envelopeTimeSeconds);
        _envelopeAttackPerSample = 1.0 / envelopeSamples;
        _envelopeReleasePerSample = 1.0 / envelopeSamples;
    }

    public WaveFormat WaveFormat => _waveFormat;

    public void KeyDown() => _keyDown = true;
    public void KeyUp() => _keyDown = false;

    public int Read(byte[] buffer, int offset, int count)
    {
        var waveBuffer = new WaveBuffer(buffer);
        int samplesRequired = count / 4; // 4 bytes per float sample
        int samplePairs = samplesRequired / 2; // Stereo pairs

        double phaseIncrement = 2 * Math.PI * _frequency / _sampleRate;

        for (int i = 0; i < samplePairs; i++)
        {
            // Update envelope (linear ramp)
            if (_keyDown)
            {
                _currentEnvelope += _envelopeAttackPerSample;
                if (_currentEnvelope > 1.0) _currentEnvelope = 1.0;
            }
            else
            {
                _currentEnvelope -= _envelopeReleasePerSample;
                if (_currentEnvelope < 0.0) _currentEnvelope = 0.0;
            }

            // Generate sample with envelope
            float sample = 0;
            if (_currentEnvelope > 0)
            {
                // Apply raised-cosine shaping to the linear envelope for smoother transitions
                double shapedEnvelope = 0.5 * (1.0 - Math.Cos(Math.PI * _currentEnvelope));
                sample = (float)(Math.Sin(_phase) * shapedEnvelope * _amplitude);
                _phase += phaseIncrement;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }

            // Write stereo (same on both channels)
            int bufferIndex = offset / 4 + i * 2;
            waveBuffer.FloatBuffer[bufferIndex] = sample;
            waveBuffer.FloatBuffer[bufferIndex + 1] = sample;
        }

        return count;
    }
}

/// <summary>
/// Information about an audio output device.
/// </summary>
public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public override string ToString() => Name;
}
