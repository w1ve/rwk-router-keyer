/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using WinKeyerEmulator.Core.CloudRelay;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.CloudRelay;

/// <summary>
/// Tests for pairing token generation and validation.
/// </summary>
public class TokenGeneratorTests
{
    // ===== Generation tests =====

    [Fact]
    public void Generate_Returns64CharString()
    {
        var token = TokenGenerator.Generate();
        Assert.Equal(64, token.Length);
    }

    [Fact]
    public void Generate_ReturnsLowercaseHex()
    {
        var token = TokenGenerator.Generate();
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public void Generate_ProducesUniqueTokens()
    {
        var token1 = TokenGenerator.Generate();
        var token2 = TokenGenerator.Generate();
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void Generate_MultipleTokens_AllValid()
    {
        for (int i = 0; i < 100; i++)
        {
            var token = TokenGenerator.Generate();
            Assert.True(TokenGenerator.IsValid(token), $"Token {i} was invalid: {token}");
        }
    }

    // ===== Validation: valid inputs =====

    [Fact]
    public void IsValid_CorrectToken_ReturnsTrue()
    {
        // 64 lowercase hex chars
        var token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.True(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_AllZeros_ReturnsTrue()
    {
        var token = new string('0', 64);
        Assert.True(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_AllFs_ReturnsTrue()
    {
        var token = new string('f', 64);
        Assert.True(TokenGenerator.IsValid(token));
    }

    // ===== Validation: invalid inputs =====

    [Fact]
    public void IsValid_Null_ReturnsFalse()
    {
        Assert.False(TokenGenerator.IsValid(null));
    }

    [Fact]
    public void IsValid_Empty_ReturnsFalse()
    {
        Assert.False(TokenGenerator.IsValid(""));
    }

    [Fact]
    public void IsValid_TooShort_ReturnsFalse()
    {
        Assert.False(TokenGenerator.IsValid("0123456789abcdef")); // 16 chars
    }

    [Fact]
    public void IsValid_TooLong_ReturnsFalse()
    {
        var token = new string('a', 65);
        Assert.False(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_UppercaseHex_ReturnsFalse()
    {
        // Must be lowercase
        var token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        Assert.False(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_MixedCase_ReturnsFalse()
    {
        var token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789Abcdef";
        Assert.False(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_NonHexChars_ReturnsFalse()
    {
        // 'g' is not hex
        var token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg";
        Assert.False(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_Spaces_ReturnsFalse()
    {
        var token = "0123456789abcdef 123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.False(TokenGenerator.IsValid(token));
    }

    [Fact]
    public void IsValid_SpecialChars_ReturnsFalse()
    {
        var token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde!";
        Assert.False(TokenGenerator.IsValid(token));
    }
}
