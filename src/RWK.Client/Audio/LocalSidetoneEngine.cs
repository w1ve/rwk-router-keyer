using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RWK.Client.Audio;

/// <summary>
/// WASAPI shared-mode sidetone output (design Component 4).
/// </summary>
/// <remarks>
/// Carried forward from the v1 <c>SidetoneOutput</c> in
/// <c>WinKeyerEmulator.App/Audio/SidetoneOutput.cs</c>: same 20ms shared-mode buffer, same
/// continuously-running keyed oscillator, same <see cref="MMDeviceEnumerator"/> device list.
/// Two things changed.
/// <list type="number">
/// <item>
/// The envelope ramp is 5ms rather than v1's 2ms, per 4.4. See
/// <see cref="KeyedSineGenerator.EnvelopeRampSeconds"/>.
/// </item>
/// <item>
/// v1's <c>SidetoneKeyingOutput</c> decorator is gone. v1 wrapped an <c>IKeyingOutput</c> so
/// sidetone rode along with serial keying; in v2 the Client has no keying output at all and
/// <c>SoftWinKeyerCore</c> calls <see cref="KeyDown"/>/<see cref="KeyUp"/> here directly. That
/// is what makes practice mode work: nothing in this class consults connection state, session
/// state, or a keying output, so sidetone is unaffected by the network (4.7).
/// </item>
/// </list>
/// <para>
/// Frequency and volume changes take effect on the next <see cref="Initialize"/> call, as in
/// v1 — the oscillator is immutable once the stream is running so the render thread never
/// reads a half-updated parameter.
/// </para>
/// <para>
/// Latency: shared mode with a 20ms buffer puts the render callback boundary well inside the
/// 15ms paddle-to-audio target only in combination with the continuously-running stream — the
/// key-down does not start a stream, it moves an envelope that the next callback picks up
/// (4.1, 4.2, 14.3). The end-to-end figure can only be confirmed with real audio hardware.
/// </para>
/// _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 14.3_
/// </remarks>
public sealed class LocalSidetoneEngine : ILocalSidetoneEngine
{
    /// <summary>Sample rate of the sidetone stream.</summary>
    public const int SampleRate = KeyedSineGenerator.DefaultSampleRate;

    /// <summary>Requested WASAPI shared-mode buffer size in milliseconds (4.1).</summary>
    public const int BufferMilliseconds = 20;

    private readonly object _sync = new();

    private WasapiOut? _waveOut;
    private KeyedSineWaveProvider? _provider;
    private int _toneHz = KeyedSineGenerator.DefaultFrequencyHz;
    private double _volume = 0.5;
    private int _speedWpm = 20;
    private bool _disposed;

    /// <summary>
    /// Raised when a persisted device could not be opened and the default endpoint was used
    /// instead. Non-blocking: playback has already started by the time this fires (4.6).
    /// </summary>
    public event EventHandler<SidetoneDeviceFallbackEventArgs>? DeviceFallback;

    /// <summary>
    /// Identifier of the endpoint currently open, or <see langword="null"/> when stopped.
    /// The UI persists this to <see cref="RWK.Shared.Config.SidetoneConfig.DeviceId"/> so the
    /// operator's choice survives a restart (4.6).
    /// </summary>
    public string? ActiveDeviceId { get; private set; }

    /// <summary>Friendly name of the endpoint currently open, or <see langword="null"/> when stopped.</summary>
    public string? ActiveDeviceName { get; private set; }

    /// <summary>True while the output stream is running.</summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <inheritdoc />
    /// <remarks>Clamped to 300-1500 Hz rather than throwing (4.3). Applies on next <see cref="Initialize"/>.</remarks>
    public int ToneFrequency
    {
        get => _toneHz;
        set => _toneHz = KeyedSineGenerator.ClampFrequency(value);
    }

    /// <inheritdoc />
    /// <remarks>Clamped to 0.0-1.0 rather than throwing (4.5). Applies on next <see cref="Initialize"/>.</remarks>
    public double Volume
    {
        get => _volume;
        set => _volume = KeyedSineGenerator.ClampVolume(value);
    }

    /// <summary>
    /// Current WPM speed. Stored for reference but the envelope ramp is fixed at 2ms
    /// and does not change with speed (rebuilding the generator mid-stream causes glitches).
    /// </summary>
    public int SpeedWpm
    {
        get => _speedWpm;
        set { _speedWpm = Math.Clamp(value, 5, 60); }
    }

    /// <summary>
    /// Enumerates active render endpoints, always with a leading synthetic "default" entry so
    /// the operator can choose to follow the system default rather than pin a device (4.6).
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo> { AudioDeviceInfo.Default };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // No audio subsystem, or enumeration denied. The default entry is still offered so
            // the UI has something to show and Initialize can still try the default endpoint.
        }

        return devices;
    }

    /// <inheritdoc />
    public void Initialize(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SidetoneDeviceFallbackEventArgs? fallback;

        lock (_sync)
        {
            StopCore();

            var generator = new KeyedSineGenerator(SampleRate, _toneHz, _volume);
            var provider = new KeyedSineWaveProvider(generator);

            // Not disposed here: WasapiOut keeps the returned MMDevice for the life of the
            // stream, which outlives this method.
            var enumerator = new MMDeviceEnumerator();
            var device = ResolveDevice(enumerator, deviceId, out fallback);

            // Shared mode, event-driven callbacks, 20ms buffer (4.1).
            var waveOut = new WasapiOut(device, AudioClientShareMode.Shared, true, BufferMilliseconds);
            waveOut.Init(provider);
            waveOut.Play();

            _provider = provider;
            _waveOut = waveOut;
            ActiveDeviceId = device.ID;
            ActiveDeviceName = SafeFriendlyName(device);
        }

        // Fired outside the lock and after playback is running: a missing device is a warning,
        // never a hard failure (4.6).
        if (fallback is not null)
        {
            DeviceFallback?.Invoke(this, fallback with
            {
                ActiveDeviceId = ActiveDeviceId ?? string.Empty,
                ActiveDeviceName = ActiveDeviceName ?? string.Empty
            });
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public void KeyDown() => _provider?.KeyDown();

    /// <inheritdoc />
    public void KeyUp() => _provider?.KeyUp();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Picks the endpoint to open. A persisted identifier that no longer resolves — the
    /// interface was unplugged, the profile moved machines — falls back to the default
    /// endpoint and reports why, rather than throwing (4.6).
    /// </summary>
    private static MMDevice ResolveDevice(
        MMDeviceEnumerator enumerator,
        string? deviceId,
        out SidetoneDeviceFallbackEventArgs? fallback)
    {
        fallback = null;

        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active)
                {
                    return device;
                }

                fallback = new SidetoneDeviceFallbackEventArgs(
                    deviceId,
                    string.Empty,
                    string.Empty,
                    $"Saved sidetone device is {device.State}; using the default output device instead.");
            }
            catch (Exception ex)
            {
                fallback = new SidetoneDeviceFallbackEventArgs(
                    deviceId,
                    string.Empty,
                    string.Empty,
                    $"Saved sidetone device is no longer present ({ex.GetType().Name}); using the default output device instead.");
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return device.ID;
        }
    }

    private void StopCore()
    {
        try
        {
            _waveOut?.Stop();
        }
        catch (Exception)
        {
            // Device already gone. Nothing to recover; disposal below releases the handle.
        }

        _waveOut?.Dispose();
        _waveOut = null;
        _provider = null;
        ActiveDeviceId = null;
        ActiveDeviceName = null;
    }
}
