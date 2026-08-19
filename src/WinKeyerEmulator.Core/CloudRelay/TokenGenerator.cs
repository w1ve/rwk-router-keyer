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

namespace WinKeyerEmulator.Core.CloudRelay;

/// <summary>
/// Utility for generating and validating pairing tokens.
/// </summary>
public static class TokenGenerator
{
    /// <summary>
    /// Generates a cryptographically random 64-character lowercase hex pairing token.
    /// </summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates that a token is a 64-character lowercase hex string.
    /// </summary>
    public static bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64)
            return false;

        foreach (char c in token)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }

        return true;
    }
}
