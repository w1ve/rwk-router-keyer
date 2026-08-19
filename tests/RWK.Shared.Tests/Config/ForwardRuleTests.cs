/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared;
using RWK.Shared.Config;
using Xunit;

namespace RWK.Shared.Tests.Config;

/// <summary>
/// Tests for the <see cref="ForwardRule"/> model defaults and for
/// <see cref="ForwardRuleType"/> deserialization via <see cref="ForwardRuleTypeJsonConverter"/>.
/// </summary>
/// <remarks>
/// Two spec tasks live in this file because they cover the same narrow area:
/// <list type="bullet">
/// <item>Task 2.5 — example-based facts for the model defaults and the rule-type
/// forward-compatibility fallback, plus property coverage of that fallback across
/// arbitrary numbers and strings (Property 45, first clause).</item>
/// <item>Task 2.6 — Property 44: Loopback Bind Default.</item>
/// </list>
/// The loopback default is a safety property, not a formality: a regression that made the
/// default non-loopback would expose an unauthenticated tunnel path into the Station's
/// network to every host on the operator's LAN without the operator opting in (10.12).
/// <para>
/// _Requirements: 10.12, 10.16, 10.17_
/// </para>
/// </remarks>
public class ForwardRuleTests
{
    /// <summary>Deserializes a rule type from a raw JSON token.</summary>
    private static ForwardRuleType ReadRuleType(string json)
        => JsonSerializer.Deserialize<ForwardRuleType>(json);

    /// <summary>
    /// Builds profile JSON for a rule, optionally omitting the bind address and/or the
    /// rule type so the constructor defaults are exercised through the serializer.
    /// </summary>
    private static string RuleJson(
        Guid id,
        string name,
        ForwardProtocol protocol,
        int clientPort,
        int stationPort,
        bool enabled,
        string? bindAddress = null,
        ForwardRuleType? ruleType = null)
    {
        var fields = new List<string>
        {
            $"\"Id\":{JsonSerializer.Serialize(id)}",
            $"\"Name\":{JsonSerializer.Serialize(name)}",
            $"\"Protocol\":{(int)protocol}",
            $"\"ClientPort\":{clientPort}",
            $"\"StationPort\":{stationPort}",
            $"\"Enabled\":{(enabled ? "true" : "false")}"
        };

        if (bindAddress is not null)
        {
            fields.Add($"\"BindAddress\":{JsonSerializer.Serialize(bindAddress)}");
        }

        if (ruleType is not null)
        {
            fields.Add($"\"RuleType\":{(int)ruleType.Value}");
        }

        return "{" + string.Join(",", fields) + "}";
    }

    // ---------------------------------------------------------------------
    // Task 2.5 — example-based facts: ForwardRuleType round-trip and fallback
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every defined rule type survives a serialize/deserialize cycle unchanged, and is
    /// written as its stable numeric value so profiles stay readable by other builds.
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData(ForwardRuleType.Generic, 0)]
    [InlineData(ForwardRuleType.Cat, 1)]
    [InlineData(ForwardRuleType.Audio, 2)]
    [InlineData(ForwardRuleType.RemoteRig, 3)]
    [InlineData(ForwardRuleType.FlexDiscovery, 4)]
    public void KnownRuleType_RoundTripsAsItsNumericValue(ForwardRuleType type, int expectedNumber)
    {
        string json = JsonSerializer.Serialize(type);

        Assert.Equal(expectedNumber.ToString(), json);
        Assert.Equal(type, ReadRuleType(json));
    }

    /// <summary>
    /// A numeric value this build does not define — including a negative one — falls back
    /// to <see cref="ForwardRuleType.Generic"/> instead of loading as an undefined enum
    /// value or throwing, so a profile written by a newer build still loads (10.16, 12.6).
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("99")]
    [InlineData("-1")]
    public void UnknownNumericRuleType_DeserializesToGeneric(string json)
        => Assert.Equal(ForwardRuleType.Generic, ReadRuleType(json));

    /// <summary>
    /// A number outside the range of the enum's underlying type falls back to
    /// <see cref="ForwardRuleType.Generic"/> rather than overflowing or throwing.
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData("2147483648")]            // int.MaxValue + 1
    [InlineData("-2147483649")]           // int.MinValue - 1
    [InlineData("9223372036854775807")]   // long.MaxValue
    [InlineData("1e40")]                  // not representable as an integer at all
    [InlineData("4.5")]                   // fractional
    public void OutOfRangeNumericRuleType_DeserializesToGeneric(string json)
        => Assert.Equal(ForwardRuleType.Generic, ReadRuleType(json));

    /// <summary>
    /// A string that does not name a defined member falls back to
    /// <see cref="ForwardRuleType.Generic"/>.
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData("\"Bluetooth\"")]
    [InlineData("\"FlexDiscovery2\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void UnrecognizedStringRuleType_DeserializesToGeneric(string json)
        => Assert.Equal(ForwardRuleType.Generic, ReadRuleType(json));

    /// <summary>
    /// A hand-edited profile naming a rule type loads as that type, case-insensitively.
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData("\"FlexDiscovery\"", ForwardRuleType.FlexDiscovery)]
    [InlineData("\"flexdiscovery\"", ForwardRuleType.FlexDiscovery)]
    [InlineData("\"REMOTERIG\"", ForwardRuleType.RemoteRig)]
    [InlineData("\"Cat\"", ForwardRuleType.Cat)]
    public void KnownStringRuleType_DeserializesCaseInsensitively(string json, ForwardRuleType expected)
        => Assert.Equal(expected, ReadRuleType(json));

    /// <summary>
    /// A null token, or any other token shape a future build might write, falls back to
    /// <see cref="ForwardRuleType.Generic"/> without failing the profile load.
    /// </summary>
    /// <remarks>_Requirements: 10.16, 10.17_</remarks>
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"FlexDiscovery\",\"rev\":2}")]
    [InlineData("[1,2,3]")]
    public void UnexpectedTokenShape_DeserializesToGeneric(string json)
        => Assert.Equal(ForwardRuleType.Generic, ReadRuleType(json));

    // ---------------------------------------------------------------------
    // Task 2.5 — example-based facts: ForwardRule defaults and bind helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// A rule created without an explicit bind address binds loopback, and its rule type
    /// defaults to the generic relay.
    /// </summary>
    /// <remarks>_Requirements: 10.12, 10.17_</remarks>
    [Fact]
    public void RuleCreatedWithoutBindAddress_UsesLoopback()
    {
        var rule = new ForwardRule(Guid.NewGuid(), "CAT", ForwardProtocol.Tcp, 4532, 4532, true);

        Assert.Equal("127.0.0.1", rule.BindAddress);
        Assert.Equal(ForwardRule.LoopbackAddress, rule.BindAddress);
        Assert.Equal(ForwardRuleType.Generic, rule.RuleType);
        Assert.False(rule.IsNonLoopbackBind);
    }

    /// <summary>
    /// The published constants are the exact strings the UI and the manager compare
    /// against; they are persisted, so they must not drift.
    /// </summary>
    /// <remarks>_Requirements: 10.12_</remarks>
    [Fact]
    public void BindAddressConstants_HaveExpectedValues()
    {
        Assert.Equal("127.0.0.1", ForwardRule.LoopbackAddress);
        Assert.Equal("0.0.0.0", ForwardRule.AnyAddress);
    }

    /// <summary>
    /// Profile JSON that omits the bind address deserializes to loopback rather than to
    /// null or empty, so an older profile — or one hand-edited to drop the field — never
    /// silently becomes LAN-reachable.
    /// </summary>
    /// <remarks>_Requirements: 10.12_</remarks>
    [Fact]
    public void RuleJsonOmittingBindAddress_DeserializesToLoopback()
    {
        string json = RuleJson(Guid.NewGuid(), "CAT", ForwardProtocol.Tcp, 4532, 4532, true);

        ForwardRule? rule = JsonSerializer.Deserialize<ForwardRule>(json);

        Assert.NotNull(rule);
        Assert.Equal(ForwardRule.LoopbackAddress, rule!.BindAddress);
        Assert.Equal(ForwardRuleType.Generic, rule.RuleType);
        Assert.False(rule.IsNonLoopbackBind);
    }

    /// <summary>
    /// Profile JSON that carries an explicit bind address keeps that address verbatim.
    /// </summary>
    /// <remarks>_Requirements: 10.12, 10.13_</remarks>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.7")]
    public void RuleJsonWithExplicitBindAddress_KeepsThatAddress(string bindAddress)
    {
        string json = RuleJson(
            Guid.NewGuid(), "RRC", ForwardProtocol.Udp, 12000, 12000, true, bindAddress);

        ForwardRule? rule = JsonSerializer.Deserialize<ForwardRule>(json);

        Assert.NotNull(rule);
        Assert.Equal(bindAddress, rule!.BindAddress);
        Assert.True(rule.IsNonLoopbackBind);
    }

    /// <summary>
    /// <see cref="ForwardRule.IsNonLoopbackBind"/> drives the UI exposure warning, so an
    /// address it cannot parse counts as non-loopback: the warning errs toward being shown.
    /// </summary>
    /// <remarks>_Requirements: 10.12, 10.14_</remarks>
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.5", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("not-an-address", true)]
    [InlineData("", true)]
    [InlineData("eth0", true)]
    [InlineData("999.1.1.1", true)]
    public void IsNonLoopbackBind_TreatsUnparseableAddressAsExposed(string bindAddress, bool expected)
    {
        var rule = new ForwardRule(
            Guid.NewGuid(), "R", ForwardProtocol.Tcp, 1, 1, true, bindAddress);

        Assert.Equal(expected, rule.IsNonLoopbackBind);
    }

    // ---------------------------------------------------------------------
    // Generators shared by the property tests
    // ---------------------------------------------------------------------

    /// <summary>Guids derived from an arbitrary int so cases stay reproducible.</summary>
    private static Gen<Guid> GuidGen =>
        Arb.Generate<int>().Select(seed =>
        {
            byte[] bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, seed);
            return new Guid(bytes);
        });

    /// <summary>Rule names, including empty, never null.</summary>
    private static Gen<string> NameGen =>
        Arb.Generate<string>().Select(name => name ?? string.Empty);

    /// <summary>Any port number expressible in the model, including the invalid extremes.</summary>
    private static Gen<int> PortGen => Gen.Choose(0, 65535);

    private static Gen<ForwardProtocol> ProtocolGen =>
        Gen.Elements(Enum.GetValues<ForwardProtocol>());

    private static Gen<ForwardRuleType> RuleTypeGen =>
        Gen.Elements(Enum.GetValues<ForwardRuleType>());

    /// <summary>
    /// The full parameter space of a rule apart from the bind address, which the loopback
    /// default properties deliberately leave unset.
    /// </summary>
    private static Gen<(Guid Id, string Name, ForwardProtocol Protocol, int ClientPort, int StationPort, bool Enabled, ForwardRuleType RuleType)> RuleParamsGen =>
        from id in GuidGen
        from name in NameGen
        from protocol in ProtocolGen
        from clientPort in PortGen
        from stationPort in PortGen
        from enabled in Arb.Generate<bool>()
        from ruleType in RuleTypeGen
        select (Id: id, Name: name, Protocol: protocol, ClientPort: clientPort,
                StationPort: stationPort, Enabled: enabled, RuleType: ruleType);

    /// <summary>
    /// Bind addresses an operator could plausibly type, including malformed ones. All are
    /// treated as explicit user intent and must be preserved exactly.
    /// </summary>
    private static Gen<string> ExplicitBindAddressGen =>
        Gen.Elements(
            "0.0.0.0",
            "127.0.0.1",
            "127.0.0.2",
            "192.168.1.50",
            "10.0.0.7",
            "172.16.4.9",
            "::1",
            "fe80::1",
            "",
            "not-an-address",
            "999.1.1.1");

    // ---------------------------------------------------------------------
    // Task 2.6 — Property 44: Loopback Bind Default
    // ---------------------------------------------------------------------

    /// <summary>
    /// Property 44: for any newly created forward rule across the whole parameter space,
    /// omitting the bind address yields loopback — never the any-address, a LAN address,
    /// null, or empty.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.12**</remarks>
    [Property]
    public Property Property44_RuleCreatedWithoutBindAddress_IsAlwaysLoopback()
    {
        return Prop.ForAll(RuleParamsGen.ToArbitrary(), p =>
        {
            var rule = new ForwardRule(
                p.Id, p.Name, p.Protocol, p.ClientPort, p.StationPort, p.Enabled, RuleType: p.RuleType);

            return rule.BindAddress == ForwardRule.LoopbackAddress
                   && rule.BindAddress == "127.0.0.1"
                   && !rule.IsNonLoopbackBind;
        });
    }

    /// <summary>
    /// Property 44: the same guarantee holds through the persistence path — profile JSON
    /// that omits the bind address deserializes to loopback for any rule.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.12**</remarks>
    [Property]
    public Property Property44_RuleJsonOmittingBindAddress_IsAlwaysLoopback()
    {
        return Prop.ForAll(RuleParamsGen.ToArbitrary(), p =>
        {
            string json = RuleJson(
                p.Id, p.Name, p.Protocol, p.ClientPort, p.StationPort, p.Enabled, ruleType: p.RuleType);

            ForwardRule? rule = JsonSerializer.Deserialize<ForwardRule>(json);

            return rule is not null
                   && rule.BindAddress == ForwardRule.LoopbackAddress
                   && !rule.IsNonLoopbackBind;
        });
    }

    /// <summary>
    /// Property 44, converse half: an explicitly set bind address is honored exactly and
    /// is never silently replaced by loopback — through both the constructor and JSON.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.12**</remarks>
    [Property]
    public Property Property44_ExplicitBindAddress_IsHonoredExactly()
    {
        var gen = from p in RuleParamsGen
                  from bindAddress in ExplicitBindAddressGen
                  select (p, bindAddress);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (p, bindAddress) = tuple;

            var constructed = new ForwardRule(
                p.Id, p.Name, p.Protocol, p.ClientPort, p.StationPort, p.Enabled, bindAddress, p.RuleType);

            string json = RuleJson(
                p.Id, p.Name, p.Protocol, p.ClientPort, p.StationPort, p.Enabled, bindAddress, p.RuleType);
            ForwardRule? deserialized = JsonSerializer.Deserialize<ForwardRule>(json);

            return constructed.BindAddress == bindAddress
                   && deserialized is not null
                   && deserialized.BindAddress == bindAddress;
        });
    }

    // ---------------------------------------------------------------------
    // Task 2.5 — property coverage of the rule-type fallback
    // (Property 45, forward-compatibility clause)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Any numeric value — in range, out of range, or negative — either names a defined
    /// member and round-trips, or deserializes to <see cref="ForwardRuleType.Generic"/>.
    /// It never throws and never produces an undefined enum value.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.16, 10.17**</remarks>
    [Property]
    public Property RuleType_AnyNumericValue_FallsBackToGenericAndNeverThrows()
    {
        return Prop.ForAll(Arb.Generate<long>().ToArbitrary(), numeric =>
        {
            ForwardRuleType actual = ReadRuleType(numeric.ToString());

            bool isDefined = numeric is >= int.MinValue and <= int.MaxValue
                             && Enum.IsDefined(typeof(ForwardRuleType), (ForwardRuleType)(int)numeric);

            return Enum.IsDefined(typeof(ForwardRuleType), actual)
                   && (isDefined
                       ? actual == (ForwardRuleType)(int)numeric
                       : actual == ForwardRuleType.Generic);
        });
    }

    /// <summary>
    /// Any string that does not name a defined member deserializes to
    /// <see cref="ForwardRuleType.Generic"/> without throwing.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.16, 10.17**</remarks>
    [Property]
    public Property RuleType_UnrecognizedString_FallsBackToGeneric()
    {
        Gen<string> unrecognizedGen =
            Arb.Generate<string>()
               .Select(text => text ?? string.Empty)
               .Where(text => !Enum.TryParse(text, ignoreCase: true, out ForwardRuleType _));

        return Prop.ForAll(unrecognizedGen.ToArbitrary(), text =>
            ReadRuleType(JsonSerializer.Serialize(text)) == ForwardRuleType.Generic);
    }

    /// <summary>
    /// Any string at all — recognized, unrecognized, or nonsense — yields a defined enum
    /// value rather than throwing, so a hand-edited or newer profile still loads.
    /// </summary>
    /// <remarks>**Validates: Requirements 10.16, 10.17**</remarks>
    [Property]
    public Property RuleType_AnyString_NeverThrowsAndYieldsDefinedValue()
    {
        Gen<string> anyStringGen = Arb.Generate<string>().Select(text => text ?? string.Empty);

        return Prop.ForAll(anyStringGen.ToArbitrary(), text =>
        {
            ForwardRuleType actual = ReadRuleType(JsonSerializer.Serialize(text));
            return Enum.IsDefined(typeof(ForwardRuleType), actual);
        });
    }
}
