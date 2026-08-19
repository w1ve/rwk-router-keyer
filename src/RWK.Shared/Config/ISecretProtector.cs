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
/// Encrypts and decrypts the sensitive fields of a persisted profile (12.2, 12.3).
/// </summary>
/// <remarks>
/// The production implementation is <see cref="WindowsDpapiSecretProtector"/>, which uses
/// Windows DPAPI scoped to the current user. The abstraction exists so that RWK.Shared can
/// target <c>net9.0</c> without every caller carrying a Windows platform annotation, and so
/// that a non-Windows host degrades predictably instead of throwing — see
/// <see cref="UnavailableSecretProtector"/>.
/// <para>
/// Implementations MUST NOT throw from <see cref="TryUnprotect"/>: a value that cannot be
/// decrypted (profile copied to another machine or another user account, or a hand-edited
/// file) is a corrupt field, not a crash, and the rest of the profile must still load (12.6).
/// </para>
/// <para>
/// _Requirements: 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Gets whether this protector can actually encrypt on the current host. When
    /// <see langword="false"/>, callers MUST NOT call <see cref="Protect"/>; they write no
    /// value rather than falling back to plaintext.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns it as a base64 string suitable for
    /// storing in a JSON string field.
    /// </summary>
    /// <param name="plaintext">The secret value. Never <see langword="null"/>.</param>
    /// <returns>The encrypted value, base64 encoded.</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when <see cref="IsAvailable"/> is <see langword="false"/>.
    /// </exception>
    string Protect(string plaintext);

    /// <summary>
    /// Attempts to decrypt a value previously produced by <see cref="Protect"/>.
    /// </summary>
    /// <param name="protectedValue">The base64 encrypted value read from the profile.</param>
    /// <param name="plaintext">
    /// The decrypted secret on success; <see langword="null"/> on failure.
    /// </param>
    /// <param name="failureReason">
    /// A human-readable reason suitable for a log entry when the method returns
    /// <see langword="false"/>; <see langword="null"/> on success.
    /// </param>
    /// <returns><see langword="true"/> if the value was decrypted.</returns>
    bool TryUnprotect(string protectedValue, out string? plaintext, out string? failureReason);
}
