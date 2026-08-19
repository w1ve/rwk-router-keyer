using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.Audio;

/// <summary>
/// Decorator for IKeyingOutput that adds sidetone audio.
/// Wraps an existing keying output and forwards all calls while also
/// triggering sidetone audio on key transitions.
/// </summary>
public sealed class SidetoneKeyingOutput : IKeyingOutput
{
    private readonly IKeyingOutput _inner;
    private SidetoneOutput? _sidetone;
    private bool _sidetoneEnabled;

    /// <summary>
    /// Creates a new SidetoneKeyingOutput wrapping the specified keying output.
    /// </summary>
    /// <param name="inner">The underlying keying output to wrap.</param>
    public SidetoneKeyingOutput(IKeyingOutput inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Gets or sets whether sidetone is enabled.
    /// </summary>
    public bool SidetoneEnabled
    {
        get => _sidetoneEnabled;
        set => _sidetoneEnabled = value;
    }

    /// <summary>
    /// Gets the sidetone output instance. Returns null if not configured.
    /// </summary>
    public SidetoneOutput? Sidetone => _sidetone;

    /// <summary>
    /// Configures the sidetone with the specified settings.
    /// </summary>
    /// <param name="deviceId">Audio device ID, or null/empty for default.</param>
    /// <param name="frequency">Tone frequency in Hz.</param>
    /// <param name="volume">Volume from 0.0 to 1.0.</param>
    public void ConfigureSidetone(string? deviceId, int frequency, double volume = 0.5)
    {
        // Dispose existing sidetone
        _sidetone?.Dispose();
        _sidetone = null;

        // Create and initialize new sidetone
        _sidetone = new SidetoneOutput
        {
            ToneFrequency = frequency,
            Volume = volume
        };
        _sidetone.Initialize(deviceId);
    }

    /// <summary>
    /// Stops and disposes the sidetone output.
    /// </summary>
    public void StopSidetone()
    {
        _sidetone?.Stop();
        _sidetone?.Dispose();
        _sidetone = null;
    }

    /// <inheritdoc/>
    public bool IsOpen => _inner.IsOpen;

    /// <inheritdoc/>
    public void Open(string portName, KeyingLine line)
    {
        _inner.Open(portName, line);
    }

    /// <inheritdoc/>
    public void KeyDown()
    {
        _inner.KeyDown();
        
        if (_sidetoneEnabled && _sidetone != null)
        {
            _sidetone.KeyDown();
        }
    }

    /// <inheritdoc/>
    public void KeyUp()
    {
        _inner.KeyUp();
        
        if (_sidetoneEnabled && _sidetone != null)
        {
            _sidetone.KeyUp();
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        _inner.Close();
        StopSidetone();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _inner.Dispose();
        StopSidetone();
    }
}
