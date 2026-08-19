namespace RWK.Shared.Config;

/// <summary>
/// Jitter buffer settings for the Station's edge replayer (7.1).
/// </summary>
/// <param name="DirectDelay">
/// Buffer delay applied on a direct Tailscale path. Default 60ms; useful range 30-150ms.
/// </param>
/// <param name="DerpDelay">
/// Buffer delay applied on a DERP-relayed path. Default 200ms; useful range 100-500ms.
/// </param>
/// <param name="AdaptiveMode">
/// When true, the replayer adjusts the delay from measured RTT and jitter (7.6, 7.7)
/// within the range for the current path type.
/// </param>
/// <remarks>
/// _Requirements: 7.1, 12.5_
/// </remarks>
public record JitterBufferConfig(
    TimeSpan DirectDelay,
    TimeSpan DerpDelay,
    bool AdaptiveMode)
{
    /// <summary>Default direct-path buffer delay (7.1).</summary>
    public static readonly TimeSpan DefaultDirectDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>Default DERP-path buffer delay (7.1).</summary>
    public static readonly TimeSpan DefaultDerpDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The default configuration: 60ms direct, 200ms DERP, adaptive enabled.
    /// </summary>
    /// <remarks>
    /// Exposed as a factory rather than a second constructor so that the record keeps a
    /// single public constructor and <c>System.Text.Json</c> has no ambiguity to resolve.
    /// </remarks>
    public static JitterBufferConfig Default { get; } =
        new(DefaultDirectDelay, DefaultDerpDelay, AdaptiveMode: true);
}
