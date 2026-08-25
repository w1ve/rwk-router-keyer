/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
// ForwardProtocol is a shared enum in the parent RWK.Shared namespace, so it resolves
// without a using directive.
namespace RWK.Shared.Config;

/// <summary>
/// One TCP or UDP port forwarding rule, persisted with the Client profile (10.7).
/// </summary>
/// <param name="Id">Stable identifier used by the manager, the UI, and Station-side overrides.</param>
/// <param name="Name">Operator-facing label for the rule.</param>
/// <param name="Protocol">Whether the rule relays TCP or UDP (10.1).</param>
/// <param name="ClientPort">Port the Client-side listener accepts connections or datagrams on.</param>
/// <param name="StationPort">Port on the Station side the traffic is relayed to.</param>
/// <param name="Enabled">Whether the rule's listener is started.</param>
/// <param name="BindAddress">
/// Local address the Client-side listener binds to. Defaults to loopback so that
/// reachability from the Client's LAN is always an explicit opt-in (10.11, 10.12).
/// Stored as a string and parsed to <see cref="System.Net.IPAddress"/> at bind time;
/// <c>"0.0.0.0"</c> means any-address.
/// </param>
/// <param name="RuleType">
/// Traffic classification. A label only, except for
/// <see cref="ForwardRuleType.FlexDiscovery"/> (10.17).
/// </param>
/// <param name="StationTargetAddress">
/// The address on the Station's LAN where forwarded traffic is delivered. Defaults to
/// loopback (<c>127.0.0.1</c>) for applications running on the Station host itself.
/// For hardware on the Station's LAN (e.g. a radio at 192.168.1.50), specify that
/// device's IP here. The Station's sidecar dials this address for inbound forwards.
/// </param>
/// <param name="Direction">
/// Direction of traffic flow through the tunnel. <see cref="ForwardDirection.ClientToStation"/>
/// (default) means the Client binds a local listener and traffic is forwarded to the Station.
/// <see cref="ForwardDirection.StationToClient"/> means the Station originates traffic (e.g.
/// N1MM+ broadcasts) that is forwarded to the Client. Pre-1.0.3 rules without this field
/// default to <see cref="ForwardDirection.ClientToStation"/> for backward compatibility.
/// </param>
/// <remarks>
/// A rule created without an explicit bind address gets <see cref="LoopbackAddress"/>, and
/// a profile whose JSON omits the field deserializes to the same value, so LAN exposure is
/// never implicit (10.12). Binding a non-loopback address makes the listener reachable by
/// every host on the Client's LAN with no authentication of its own — the UI warns about
/// this (10.14), and the manager never substitutes a different address when the configured
/// one is absent from the host (10.15).
/// <para>
/// _Requirements: 10.1, 10.7, 10.11, 10.12, 10.16, 10.17, 12.4_
/// </para>
/// </remarks>
public record ForwardRule(
    Guid Id,
    string Name,
    ForwardProtocol Protocol,
    int ClientPort,
    int StationPort,
    bool Enabled,
    string BindAddress = ForwardRule.LoopbackAddress,
    ForwardRuleType RuleType = ForwardRuleType.Generic,
    string StationTargetAddress = ForwardRule.LoopbackAddress,
    ForwardDirection Direction = ForwardDirection.ClientToStation)
{
    /// <summary>
    /// The default bind address: loopback, reachable only from the Client host itself (10.12).
    /// </summary>
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>
    /// IPv6 loopback address, reachable only from the Client host itself.
    /// </summary>
    public const string LoopbackAddressV6 = "::1";

    /// <summary>
    /// The any-address, which binds every local IPv4 interface (10.13).
    /// </summary>
    public const string AnyAddress = "0.0.0.0";

    /// <summary>
    /// The IPv6 any-address, which binds every local IPv6 interface.
    /// </summary>
    public const string AnyAddressV6 = "::";

    /// <summary>
    /// Gets whether this rule's listener is reachable from hosts other than the Client
    /// itself, which is what triggers the UI exposure warning (10.14).
    /// </summary>
    /// <remarks>
    /// A <see cref="BindAddress"/> that does not parse counts as non-loopback: the warning
    /// errs toward being shown. Whether the address is actually present on the host is a
    /// separate question answered at bind time (10.15).
    /// </remarks>
    public bool IsNonLoopbackBind
        => BindExposure is not AddressExposure.Loopback;

    /// <summary>
    /// Classifies the <see cref="BindAddress"/> into its network exposure level.
    /// Used by the UI to differentiate warning severity (10.14, 10.28).
    /// </summary>
    public AddressExposure BindExposure
        => AddressExposureClassifier.Classify(BindAddress);

    /// <summary>
    /// Gets whether this rule is a reverse (Station → Client) forward.
    /// </summary>
    public bool IsReverse => Direction == ForwardDirection.StationToClient;
}
