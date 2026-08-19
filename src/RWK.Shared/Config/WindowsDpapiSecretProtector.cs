using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RWK.Shared.Config;

/// <summary>
/// Protects profile secrets with Windows DPAPI scoped to the current user (12.2, 12.3).
/// </summary>
/// <remarks>
/// <see cref="DataProtectionScope.CurrentUser"/> is deliberate: the ciphertext is bound to
/// the logged-on user account, so another account on the same machine — and any other
/// machine — cannot decrypt the auth key or pairing secret even with read access to the
/// profile file.
/// <para>
/// The class is annotated <see cref="SupportedOSPlatformAttribute"/> because RWK.Shared
/// targets <c>net9.0</c> rather than <c>net9.0-windows</c>; the annotation keeps the platform
/// compatibility analyzer satisfied at the single point where DPAPI is called instead of at
/// every call site. Resolve an instance through <see cref="SecretProtector.Default"/>, which
/// performs the runtime platform check.
/// </para>
/// <para>
/// _Requirements: 12.2, 12.3_
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Additional entropy mixed into every blob. Not a secret — it exists so that a blob
    /// produced by this application cannot be decrypted by unrelated software running as the
    /// same user, and so that a blob from another product cannot be decrypted here.
    /// </summary>
    /// <remarks>
    /// This value is part of the on-disk format. Changing it makes every previously saved
    /// secret undecryptable, which surfaces as a corrupt field and a cleared secret (12.6).
    /// </remarks>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RWK.v2.Config.Secret");

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] clear = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        finally
        {
            // The managed copy of the secret is the only thing we can clear; do so promptly
            // so it does not linger in a pooled or collected buffer.
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    /// <inheritdoc />
    public bool TryUnprotect(string protectedValue, out string? plaintext, out string? failureReason)
    {
        plaintext = null;
        failureReason = null;

        if (string.IsNullOrEmpty(protectedValue))
        {
            failureReason = "the stored value was empty";
            return false;
        }

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            failureReason = "the stored value was not valid base64";
            return false;
        }

        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            plaintext = Encoding.UTF8.GetString(clear);
            return true;
        }
        catch (CryptographicException)
        {
            // The usual causes are a profile copied from another machine or another user
            // account, a truncated blob, or a hand-edited value. None of these is fatal.
            failureReason = "DPAPI could not decrypt the value for the current user account";
            return false;
        }
        finally
        {
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }
}
