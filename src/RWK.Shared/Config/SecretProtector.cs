namespace RWK.Shared.Config;

/// <summary>
/// Resolves the <see cref="ISecretProtector"/> to use on the current host.
/// </summary>
/// <remarks>
/// RWK.Shared targets <c>net9.0</c>, so the Windows-only DPAPI path is selected at runtime
/// behind an <see cref="OperatingSystem.IsWindows"/> check rather than at compile time. RWK
/// is a Windows product; the non-Windows path exists so that unit tests and tooling can run
/// anywhere without the store throwing.
/// <para>
/// _Requirements: 12.2, 12.3_
/// </para>
/// </remarks>
public static class SecretProtector
{
    /// <summary>
    /// Gets the protector for the current host: DPAPI on Windows, otherwise
    /// <see cref="UnavailableSecretProtector"/>.
    /// </summary>
    public static ISecretProtector Default { get; } = Create();

    private static ISecretProtector Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiSecretProtector();
        }

        return UnavailableSecretProtector.Instance;
    }
}

/// <summary>
/// A protector for hosts where DPAPI does not exist. It reports itself unavailable and
/// decrypts nothing.
/// </summary>
/// <remarks>
/// The important property is what this type does <em>not</em> do: it never falls back to
/// writing a secret in plaintext. A profile saved on a host without DPAPI simply carries no
/// auth key and no pairing secret, and the user is asked for them again.
/// <para>
/// _Requirements: 12.2_
/// </para>
/// </remarks>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    /// <summary>Gets the shared instance.</summary>
    public static UnavailableSecretProtector Instance { get; } = new();

    private UnavailableSecretProtector()
    {
    }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string Protect(string plaintext)
        => throw new PlatformNotSupportedException(
            "Configuration secrets can only be encrypted on Windows. Check ISecretProtector.IsAvailable before calling Protect.");

    /// <inheritdoc />
    public bool TryUnprotect(string protectedValue, out string? plaintext, out string? failureReason)
    {
        plaintext = null;
        failureReason = "secret protection is not available on this platform";
        return false;
    }
}
