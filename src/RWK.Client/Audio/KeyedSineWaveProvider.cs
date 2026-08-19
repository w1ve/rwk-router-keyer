using NAudio.Wave;

namespace RWK.Client.Audio;

/// <summary>
/// Adapts <see cref="KeyedSineGenerator"/> to NAudio's pull model: a continuous stereo
/// IEEE-float stream that is silent until keyed.
/// </summary>
/// <remarks>
/// The stream never stops while the engine is initialized, so a key-down only has to reach
/// the next render callback rather than start a stream (4.1, 4.2). Both channels carry the
/// same signal, as in v1.
/// <para>
/// _Requirements: 4.1, 4.2_
/// </para>
/// </remarks>
public sealed class KeyedSineWaveProvider : IWaveProvider
{
    private const int Channels = 2;
    private const int BytesPerSample = 4;

    private volatile KeyedSineGenerator _generator;
    private float[] _monoScratch = Array.Empty<float>();

    public KeyedSineWaveProvider(KeyedSineGenerator generator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(generator.SampleRate, Channels);
    }

    /// <summary>The oscillator this provider pulls from.</summary>
    public KeyedSineGenerator Generator => _generator;

    /// <summary>
    /// Replaces the internal generator with a new one (e.g. with different ramp duration).
    /// Safe to call while the audio stream is running — the next Read call will use the new generator.
    /// </summary>
    public void ReplaceGenerator(KeyedSineGenerator newGenerator)
    {
        ArgumentNullException.ThrowIfNull(newGenerator);
        _generator = newGenerator;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Starts the shaped attack.</summary>
    public void KeyDown() => _generator.KeyDown();

    /// <summary>Starts the shaped decay.</summary>
    public void KeyUp() => _generator.KeyUp();

    public int Read(byte[] buffer, int offset, int count)
    {
        var waveBuffer = new WaveBuffer(buffer);
        int floatsRequested = count / BytesPerSample;
        int framePairs = floatsRequested / Channels;

        if (_monoScratch.Length < framePairs)
        {
            _monoScratch = new float[framePairs];
        }

        var frames = _monoScratch.AsSpan(0, framePairs);
        _generator.Generate(frames);

        int baseIndex = offset / BytesPerSample;
        for (int i = 0; i < framePairs; i++)
        {
            float sample = frames[i];
            int index = baseIndex + (i * Channels);
            waveBuffer.FloatBuffer[index] = sample;
            waveBuffer.FloatBuffer[index + 1] = sample;
        }

        // A keyed sidetone stream never ends, so the full requested count is always "produced",
        // including any trailing bytes that did not make a whole stereo frame. Same as v1.
        return count;
    }
}
