/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Net;
using System.Net.Sockets;
using RWK.Client.Controls;
using Xunit;

namespace RWK.Client.Tests.Controls;

public class IpAddressValidatorTests
{
    // ── Both mode (default) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("255.255.255.255")]
    public void Both_ValidIPv4_ReturnsValid(string input)
    {
        var result = IpAddressValidator.Validate(input);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Address);
        Assert.Equal(AddressFamily.InterNetwork, result.Address!.AddressFamily);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.168.1.1")]
    public void Both_ValidIPv6_ReturnsValid(string input)
    {
        var result = IpAddressValidator.Validate(input);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Address);
        Assert.Equal(AddressFamily.InterNetworkV6, result.Address!.AddressFamily);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Both_EmptyOrNull_ReturnsInvalid(string? input)
    {
        var result = IpAddressValidator.Validate(input);
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("999.999.999.999")]
    [InlineData("abc::xyz::123")]
    public void Both_MalformedString_ReturnsInvalid(string input)
    {
        var result = IpAddressValidator.Validate(input);
        Assert.False(result.IsValid);
        Assert.Contains("not a valid", result.ErrorMessage!);
    }

    // ── IPv4-only mode ──────────────────────────────────────────────────────────

    [Fact]
    public void IPv4Only_IPv4Address_ReturnsValid()
    {
        var result = IpAddressValidator.Validate("192.168.1.1", IpAddressMode.IPv4Only);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void IPv4Only_IPv6Address_ReturnsInvalid()
    {
        var result = IpAddressValidator.Validate("::1", IpAddressMode.IPv4Only);
        Assert.False(result.IsValid);
        Assert.Contains("IPv4", result.ErrorMessage!);
    }

    // ── IPv6-only mode ──────────────────────────────────────────────────────────

    [Fact]
    public void IPv6Only_IPv6Address_ReturnsValid()
    {
        var result = IpAddressValidator.Validate("fe80::1", IpAddressMode.IPv6Only);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void IPv6Only_IPv4Address_ReturnsInvalid()
    {
        var result = IpAddressValidator.Validate("192.168.1.1", IpAddressMode.IPv6Only);
        Assert.False(result.IsValid);
        Assert.Contains("IPv6", result.ErrorMessage!);
    }

    // ── Edge cases ──────────────────────────────────────────────────────────────

    [Fact]
    public void BracketedIPv6_ParsesCorrectly()
    {
        // User might type [::1] with brackets
        var result = IpAddressValidator.Validate("[::1]");
        Assert.True(result.IsValid);
        Assert.Equal(IPAddress.IPv6Loopback, result.Address);
    }

    [Fact]
    public void WhitespaceAroundAddress_ParsesCorrectly()
    {
        var result = IpAddressValidator.Validate("  192.168.1.1  ");
        Assert.True(result.IsValid);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), result.Address);
    }

    [Fact]
    public void LinkLocal_WithZoneId_ParsesCorrectly()
    {
        // .NET handles zone IDs in IPv6 link-local addresses
        var result = IpAddressValidator.Validate("fe80::1%1");
        Assert.True(result.IsValid);
        Assert.NotNull(result.Address);
    }

    // ── Describe ────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_Loopback_IPv4()
    {
        var desc = IpAddressValidator.Describe(IPAddress.Loopback);
        Assert.Contains("loopback", desc);
        Assert.Contains("IPv4", desc);
    }

    [Fact]
    public void Describe_Loopback_IPv6()
    {
        var desc = IpAddressValidator.Describe(IPAddress.IPv6Loopback);
        Assert.Contains("loopback", desc);
        Assert.Contains("IPv6", desc);
    }

    [Fact]
    public void Describe_Any_IPv4()
    {
        var desc = IpAddressValidator.Describe(IPAddress.Any);
        Assert.Contains("any", desc);
    }

    [Fact]
    public void Describe_LinkLocal_IPv6()
    {
        var desc = IpAddressValidator.Describe(IPAddress.Parse("fe80::1"));
        Assert.Contains("link-local", desc);
    }
}
