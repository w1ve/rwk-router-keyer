namespace RWK.Client.Audio;

/// <summary>
/// Local sidetone audio feedback for the operator (design Component 4).
/// </summary>
/// <remarks>
/// The engine is deliberately isolated from every network and keying concern. The Client
/// has no keying output of its own in v2 — the Station keys the radio — so the keyer core
/// invokes <see cref="KeyDown"/> and <see cref="KeyUp"/> on this interface directly rather
/// than through a decorator over a keying output as v1 did. That direct call path is what
/// makes practice mode work: with no
/// Station and no tailnet, keying still produces audio (4.7).
/// <para>
/// Nothing in an implementation may consult connection state, session state, or a keying
/// output. Introducing such a dependency would break 4.7.
/// </para>
/// _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7_
/// </remarks>
public interface ILocalSidetoneEngine : IDisposable
{
    /// <summary>
    /// Opens the audio device and begins the continuous (initially silent) output stream.
    /// </summary>
    /// <param name="deviceId">
    /// MMDevice identifier to use, or <see langword="null"/>/empty for the system default
    /// render endpoint. If the identifier names a device that is no longer present — a saved
    /// configuration pointing at an unplugged interface — the implementation falls back to the
    /// default endpoint rather than throwing, because a missing audio device must not stop the
    /// operator sending (4.6).
    /// </param>
    void Initialize(string? deviceId);

    /// <summary>
    /// Stops output and releases the audio device. Safe to call when not initialized.
    /// </summary>
    void Stop();

    /// <summary>
    /// Begins the shaped attack of the tone. Called on every key-down edge (4.2).
    /// </summary>
    void KeyDown();

    /// <summary>
    /// Begins the shaped decay of the tone. Called on every key-up edge.
    /// </summary>
    void KeyUp();

    /// <summary>
    /// Tone frequency in Hz, clamped to 300-1500 (4.3). Default 700.
    /// </summary>
    int ToneFrequency { get; set; }

    /// <summary>
    /// Output level, clamped to 0.0-1.0 (4.5).
    /// </summary>
    double Volume { get; set; }

    /// <summary>
    /// Current keyer speed in WPM. The engine uses this to scale the envelope ramp
    /// duration so fast dits sound clean (shorter ramp at higher speeds).
    /// </summary>
    int SpeedWpm { get; set; }

    /// <summary>
    /// Enumerates the available render endpoints for user selection (4.6).
    /// </summary>
    static abstract IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}
