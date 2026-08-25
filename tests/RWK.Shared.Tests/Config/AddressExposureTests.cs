/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Config;
using Xunit;

namespace RWK.Shared.Tests.Config;

public class AddressExposureTests
{
    // ── Loopback ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("127.255.255.255")]
    [InlineData("::1")]
    public void Loopback_Addresses_ClassifyAsLoopback(string address)
    {
        Assert.Equal(AddressExposure.Loopback, AddressExposureClassifier.Classify(address));
    }

    // ── Private / Link-Local ────────────────────────────────────────────────────

    [Theory]
    // RFC1918 ranges
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    // APIPA link-local
    [InlineData("169.254.1.1")]
    // Tailscale CGNAT range (100.64.0.0/10)
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    // IPv6 link-local (fe80::/10)
    [InlineData("fe80::1")]
    [InlineData("fe80::dead:beef")]
    // IPv6 ULA (fc00::/7 — covers fd00::/8 used by Tailscale)
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fd7a:115c:a1e0::1")] // Tailscale's own ULA range
    // Any-address (binds all interfaces = LAN exposure)
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Private_And_LinkLocal_ClassifyAsPrivateOrLinkLocal(string address)
    {
        Assert.Equal(AddressExposure.PrivateOrLinkLocal, AddressExposureClassifier.Classify(address));
    }

    // ── Global Unicast ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("8.8.8.8")]          // Google DNS
    [InlineData("1.1.1.1")]          // Cloudflare
    [InlineData("203.0.113.1")]      // TEST-NET-3
    [InlineData("44.130.0.1")]       // AMPRNet (ham radio)
    [InlineData("2001:db8::1")]      // Documentation prefix
    [InlineData("2607:f8b0::1")]     // Google IPv6
    [InlineData("2600::1")]          // Generic global unicast
    public void Global_Unicast_ClassifyAsGlobal(string address)
    {
        Assert.Equal(AddressExposure.GlobalUnicast, AddressExposureClassifier.Classify(address));
    }

    // ── Invalid ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("999.999.999.999")]
    [InlineData("hello::world::oops")]
    public void Invalid_Strings_ClassifyAsInvalid(string? address)
    {
        Assert.Equal(AddressExposure.Invalid, AddressExposureClassifier.Classify(address));
    }

    // ── IPv4-Mapped IPv6 ────────────────────────────────────────────────────────

    [Fact]
    public void IPv4Mapped_Private_ClassifiesAsPrivate()
    {
        // ::ffff:192.168.1.1 is an IPv4-mapped IPv6 address for a private IPv4
        Assert.Equal(AddressExposure.PrivateOrLinkLocal, AddressExposureClassifier.Classify("::ffff:192.168.1.1"));
    }

    [Fact]
    public void IPv4Mapped_Global_ClassifiesAsGlobal()
    {
        // ::ffff:8.8.8.8 is an IPv4-mapped IPv6 address for a global IPv4
        Assert.Equal(AddressExposure.GlobalUnicast, AddressExposureClassifier.Classify("::ffff:8.8.8.8"));
    }

    [Fact]
    public void IPv4Mapped_Loopback_ClassifiesAsLoopback()
    {
        // ::ffff:127.0.0.1 is an IPv4-mapped IPv6 address for loopback
        Assert.Equal(AddressExposure.Loopback, AddressExposureClassifier.Classify("::ffff:127.0.0.1"));
    }

    // ── ForwardRule.BindExposure integration ────────────────────────────────────

    [Fact]
    public void ForwardRule_IPv4Loopback_ExposureIsLoopback()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "127.0.0.1");
        Assert.Equal(AddressExposure.Loopback, rule.BindExposure);
        Assert.False(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_IPv6Loopback_ExposureIsLoopback()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "::1");
        Assert.Equal(AddressExposure.Loopback, rule.BindExposure);
        Assert.False(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_AnyAddress_ExposureIsPrivate()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "0.0.0.0");
        Assert.Equal(AddressExposure.PrivateOrLinkLocal, rule.BindExposure);
        Assert.True(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_IPv6Any_ExposureIsPrivate()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "::");
        Assert.Equal(AddressExposure.PrivateOrLinkLocal, rule.BindExposure);
        Assert.True(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_GlobalIPv6_ExposureIsGlobal()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "2001:db8::1");
        Assert.Equal(AddressExposure.GlobalUnicast, rule.BindExposure);
        Assert.True(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_TailscaleULA_ExposureIsPrivate()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "fd7a:115c:a1e0::1");
        Assert.Equal(AddressExposure.PrivateOrLinkLocal, rule.BindExposure);
        Assert.True(rule.IsNonLoopbackBind);
    }

    [Fact]
    public void ForwardRule_InvalidAddress_ExposureIsInvalid()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "test", ForwardProtocol.Tcp, 1234, 1234, true, "not-valid");
        Assert.Equal(AddressExposure.Invalid, rule.BindExposure);
        Assert.True(rule.IsNonLoopbackBind);
    }
}
