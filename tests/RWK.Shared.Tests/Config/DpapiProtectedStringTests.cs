using System.Security.Cryptography;
using RWK.Shared.Config;
using Xunit;

namespace RWK.Shared.Tests.Config;

/// <summary>
/// Unit tests for <see cref="DpapiProtectedString"/> and the secret protectors behind it.
/// </summary>
/// <remarks>
/// _Requirements: 12.2, 12.3, 12.6_
/// </remarks>
public sealed class DpapiProtectedStringTests
{
    [Fact]
    public void ToString_DoesNotDiscloseTheSecret()
    {
        DpapiProtectedString secret = "tskey-auth-DO-NOT-LOG-ME";

        Assert.Equal("***", secret.ToString());
        Assert.Equal(string.Empty, new DpapiProtectedString(null).ToString());
        Assert.Equal(string.Empty, new DpapiProtectedString(string.Empty).ToString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("x", true)]
    public void HasValue_TreatsNullAndEmptyAsAbsent(string? plaintext, bool expected)
        => Assert.Equal(expected, new DpapiProtectedString(plaintext).HasValue);

    [Fact]
    public void ImplicitConversions_PreservePlaintext()
    {
        DpapiProtectedString secret = "pairing-secret";
        string? unwrapped = secret;

        Assert.Equal("pairing-secret", unwrapped);
        Assert.Equal("pairing-secret", secret.Plaintext);
    }

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsAndProducesDifferentCiphertext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new WindowsDpapiSecretProtector();
        const string plaintext = "tskey-auth-round-trip-value";

        string first = protector.Protect(plaintext);
        string second = protector.Protect(plaintext);

        Assert.DoesNotContain(plaintext, first, StringComparison.Ordinal);
        // DPAPI is randomized, so the same secret must not produce a stable ciphertext that
        // could be recognized across saves.
        Assert.NotEqual(first, second);

        Assert.True(protector.TryUnprotect(first, out string? decrypted, out string? reason));
        Assert.Equal(plaintext, decrypted);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all !!")]
    public void TryUnprotect_MalformedValue_FailsWithReasonInsteadOfThrowing(string stored)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new WindowsDpapiSecretProtector();

        Assert.False(protector.TryUnprotect(stored, out string? plaintext, out string? reason));
        Assert.Null(plaintext);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void TryUnprotect_ForeignBlob_FailsWithReasonInsteadOfThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new WindowsDpapiSecretProtector();
        string foreignBlob = Convert.ToBase64String(RandomNumberGenerator.GetBytes(96));

        Assert.False(protector.TryUnprotect(foreignBlob, out string? plaintext, out string? reason));
        Assert.Null(plaintext);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void UnavailableProtector_NeverReturnsPlaintext()
    {
        ISecretProtector protector = UnavailableSecretProtector.Instance;

        Assert.False(protector.IsAvailable);
        Assert.False(protector.TryUnprotect("anything", out string? plaintext, out string? reason));
        Assert.Null(plaintext);
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Throws<PlatformNotSupportedException>(() => protector.Protect("secret"));
    }

    [Fact]
    public void Default_OnWindows_IsTheDpapiProtector()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(SecretProtector.Default.IsAvailable);
        Assert.IsType<WindowsDpapiSecretProtector>(SecretProtector.Default);
    }
}
