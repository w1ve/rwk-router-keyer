using NAudio.Wave;

namespace MorseTest;

/// <summary>
/// Handles audio output for the Morse tone generator.
/// Uses a continuous sine wave generator that can be keyed on/off.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private WasapiOut? _waveOut;
    private KeyedSineWaveProvider? _sineProvider;
    private bool _disposed;

    public const int SampleRate = 48000;

    public AudioOutput(int toneHz = 750, double amplitude = 0.5)
    {
        _sineProvider = new KeyedSineWaveProvider(SampleRate, toneHz, amplitude);
    }

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

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
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(
            NAudio.CoreAudioApi.DataFlow.Render, 
            NAudio.CoreAudioApi.DeviceState.Active))
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

        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        NAudio.CoreAudioApi.MMDevice? device = null;

        if (string.IsNullOrEmpty(deviceId))
        {
            device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia);
        }
        else
        {
            device = enumerator.GetDevice(deviceId);
        }

        _waveOut = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 20);
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
    /// Plays a single tone burst (for testing).
    /// </summary>
    public void PlayTestTone(int durationMs = 500)
    {
        _sineProvider?.KeyDown();
        Thread.Sleep(durationMs);
        _sineProvider?.KeyUp();
    }

    public void Stop()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
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
/// </summary>
public class KeyedSineWaveProvider : IWaveProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly double _frequency;
    private readonly double _amplitude;
    private readonly int _sampleRate;
    private double _phase;
    private double _currentEnvelope;
    private bool _keyDown;
    
    // Envelope shaping - 5ms rise/fall for click-free keying
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
            // Update envelope (linear ramp for simplicity, very fast)
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

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public override string ToString() => Name;
}
