namespace RWK.Shared.Config;

/// <summary>
/// Embedded Tailscale node settings, shared by the Client and the Station profiles.
/// </summary>
/// <remarks>
/// <see cref="AuthKey"/> and <see cref="PairingSecret"/> are secrets: they are written to
/// disk encrypted with Windows DPAPI and decrypted on load (12.2, 12.3). The encryption
/// itself lives in the configuration store and its DPAPI JSON converter (task 2.2), not in
/// this record — the values held here at runtime are plaintext.
/// <para>
/// _Requirements: 12.4, 12.5_
/// </para>
/// </remarks>
public record TailscaleConfig
{
    /// <summary>
    /// Reusable tailnet pre-auth key used to join the mesh (5.2).
    /// Persisted DPAPI-encrypted (12.2).
    /// </summary>
    public string? AuthKey { get; init; }

    /// <summary>
    /// Shared secret backing the HMAC challenge/response pairing between Client and
    /// Station (11.1, 11.3). Persisted DPAPI-encrypted (12.2).
    /// </summary>
    public string? PairingSecret { get; init; }

    /// <summary>
    /// Tailnet address or name of the peer Station. Not a secret.
    /// </summary>
    public string? StationAddress { get; init; }
}
