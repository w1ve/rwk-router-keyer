namespace RWK.Shared.Config;

/// <summary>
/// Local sidetone settings persisted with the Client profile.
/// </summary>
/// <remarks>
/// Part of the Client configuration required by 12.4. Ranges are enforced by the
/// sidetone engine and the UI, not by this record: a profile written by a future
/// build must still load rather than throw (12.6).
/// <para>
/// _Requirements: 12.4_
/// </para>
/// </remarks>
public record SidetoneConfig
{
    /// <summary>
    /// MMDevice identifier of the chosen output device, or <see langword="null"/> to use
    /// the system default device (4.6).
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>Tone frequency in Hz. Default 700; useful range 300-1500 (4.3).</summary>
    public int FrequencyHz { get; init; } = 700;

    /// <summary>Output level from 0.0 to 1.0. Default 0.5 (4.5).</summary>
    public double Volume { get; init; } = 0.5;
}
