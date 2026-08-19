/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Discovery;
using Xunit;

namespace RWK.Shared.Tests.Discovery;

/// <summary>
/// Guards the discovery broker contract shapes declared by task 3.3: the advertise states
/// the UI depends on to name why a radio is not being advertised, and the config defaults.
/// </summary>
public class DiscoveryContractTests
{
    [Fact]
    public void RadioAdvertiseState_names_advertising_and_each_withheld_reason()
    {
        // 13.20 and 15.11 require the UI to say why a radio is absent, so the withheld
        // reasons must stay distinct values rather than collapsing into one.
        Assert.Equal(
            new[]
            {
                "Advertising",
                "WithheldNoCommandRule",
                "WithheldRewriteFailed",
                "WithheldDisabled",
                "Expired",
            },
            Enum.GetNames<RadioAdvertiseState>());
        Assert.Equal(0, (int)RadioAdvertiseState.Advertising);
    }

    [Fact]
    public void DiscoveryListenerConfig_reuses_the_address_by_default()
    {
        // A local SmartSDR at the Station must keep receiving the same broadcasts.
        DiscoveryListenerConfig config = new(ListenPort: 1234, BindAddress: null);

        Assert.True(config.ReuseAddress);
        Assert.Null(config.BindAddress);
    }

    [Fact]
    public void DiscoveryEmitterConfig_expiry_default_is_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), DiscoveryEmitterConfig.DefaultExpiryInterval);
        Assert.Equal("255.255.255.255", DiscoveryEmitterConfig.DefaultBroadcastAddress);
    }

    [Fact]
    public void DiscoveryEmitterConfig_resolver_returning_null_is_the_no_command_rule_case()
    {
        DiscoveryEmitterConfig config = new(
            BroadcastPort: 1234,
            BroadcastAddress: DiscoveryEmitterConfig.DefaultBroadcastAddress,
            ExpiryInterval: DiscoveryEmitterConfig.DefaultExpiryInterval,
            CommandRuleResolver: _ => null);

        Assert.Null(config.CommandRuleResolver("1234-5678-9012"));
    }
}
