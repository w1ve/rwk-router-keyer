/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWK.Shared.Config;

/// <summary>
/// Writes a <see cref="DpapiProtectedString"/> as a DPAPI-encrypted base64 JSON string and
/// reads it back by decrypting (12.2, 12.3).
/// </summary>
/// <remarks>
/// The converter never throws and never writes plaintext. Three degenerate cases all resolve
/// to an absent secret plus a diagnostic, so that the surrounding profile still loads (12.6):
/// <list type="bullet">
///   <item><description>the stored value is not a JSON string;</description></item>
///   <item><description>the stored value cannot be decrypted, which is what happens when a
///   profile is copied to another machine or another user account;</description></item>
///   <item><description>encryption is unavailable on this host, in which case
///   <see langword="null"/> is written rather than the secret.</description></item>
/// </list>
/// <para>
/// _Requirements: 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public sealed class DpapiProtectedStringJsonConverter : JsonConverter<DpapiProtectedString>
{
    private readonly ISecretProtector _protector;
    private readonly Action<string>? _diagnostics;

    /// <summary>
    /// Initializes a converter using <see cref="SecretProtector.Default"/> and no
    /// diagnostics sink.
    /// </summary>
    public DpapiProtectedStringJsonConverter()
        : this(SecretProtector.Default, diagnostics: null)
    {
    }

    /// <summary>Initializes a converter with an explicit protector.</summary>
    /// <param name="protector">Performs the encryption and decryption.</param>
    /// <param name="diagnostics">
    /// Optional sink receiving a message whenever a secret could not be decrypted or could
    /// not be encrypted. Never receives the secret itself.
    /// </param>
    public DpapiProtectedStringJsonConverter(ISecretProtector protector, Action<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public override DpapiProtectedString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            _diagnostics?.Invoke("A protected configuration field was not a string; treating it as unset.");
            return default;
        }

        string? stored = reader.GetString();
        if (string.IsNullOrEmpty(stored))
        {
            return default;
        }

        if (!_protector.TryUnprotect(stored, out string? plaintext, out string? failureReason))
        {
            _diagnostics?.Invoke(
                $"A protected configuration field could not be decrypted ({failureReason}); treating it as unset.");
            return default;
        }

        return new DpapiProtectedString(plaintext);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DpapiProtectedString value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        if (!_protector.IsAvailable)
        {
            _diagnostics?.Invoke(
                "Secret protection is unavailable on this host; the protected configuration field was written as null rather than plaintext.");
            writer.WriteNullValue();
            return;
        }

        string encrypted;
        try
        {
            encrypted = _protector.Protect(value.Plaintext!);
        }
        catch (CryptographicException ex)
        {
            // Failing the whole save because DPAPI hiccupped would lose the rest of the
            // profile. Drop the secret instead, and never substitute plaintext.
            _diagnostics?.Invoke(
                $"A protected configuration field could not be encrypted ({ex.GetType().Name}); it was written as null rather than plaintext.");
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(encrypted);
    }
}
