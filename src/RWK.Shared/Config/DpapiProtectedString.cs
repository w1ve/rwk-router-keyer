/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Config;

/// <summary>
/// Marks a string as a profile secret: held in memory as plaintext, written to disk
/// DPAPI-encrypted (12.2, 12.3).
/// </summary>
/// <remarks>
/// The wrapper carries no encryption of its own. It exists so that a sensitive field is
/// distinguishable from an ordinary string at the point of serialization — see
/// <see cref="DpapiProtectedStringJsonConverter"/> — and so that a secret cannot be leaked
/// into a log line by accident: <see cref="ToString"/> is redacted.
/// <para>
/// Implicit conversions to and from <see cref="string"/> keep call sites readable, which
/// matters because the config records themselves store these fields as plain
/// <c>string?</c> at runtime.
/// </para>
/// <para>
/// _Requirements: 12.2, 12.3_
/// </para>
/// </remarks>
public readonly record struct DpapiProtectedString
{
    /// <summary>Initializes a new secret holding <paramref name="plaintext"/>.</summary>
    /// <param name="plaintext">The unencrypted secret, or <see langword="null"/> if unset.</param>
    public DpapiProtectedString(string? plaintext) => Plaintext = plaintext;

    /// <summary>Gets the unencrypted secret, or <see langword="null"/> if unset.</summary>
    public string? Plaintext { get; }

    /// <summary>
    /// Gets whether a secret is actually present. An empty string counts as absent, so a
    /// cleared field is not written as an encrypted empty value.
    /// </summary>
    public bool HasValue => !string.IsNullOrEmpty(Plaintext);

    /// <summary>Wraps a plaintext secret.</summary>
    /// <param name="plaintext">The unencrypted secret.</param>
    public static implicit operator DpapiProtectedString(string? plaintext) => new(plaintext);

    /// <summary>Unwraps the plaintext secret.</summary>
    /// <param name="secret">The wrapper.</param>
    public static implicit operator string?(DpapiProtectedString secret) => secret.Plaintext;

    /// <summary>
    /// Returns a redacted placeholder rather than the secret, so that interpolating this
    /// value into a log message or an exception cannot disclose it.
    /// </summary>
    /// <returns><c>"***"</c> when a secret is present, otherwise an empty string.</returns>
    public override string ToString() => HasValue ? "***" : string.Empty;
}
