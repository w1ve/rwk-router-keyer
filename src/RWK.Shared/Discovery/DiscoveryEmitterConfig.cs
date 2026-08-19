/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
// ForwardRule lives with the persisted configuration model because rules are stored in the
// Client profile (10.7).
using RWK.Shared.Config;

namespace RWK.Shared.Discovery;

/// <summary>
/// Runtime settings handed to <see cref="IDiscoveryEmitter.Start"/>.
/// </summary>
/// <param name="BroadcastPort">
/// UDP port the rewritten payload is broadcast to, which must match what SmartSDR listens
/// on. Supplied by the caller from the Client profile — this type holds no default, because
/// the correct value is a property of the FlexRadio discovery protocol and is provisional
/// until the captured fixture confirms it. The profile field that carries it is marked
/// <c>[VERIFY]</c>.
/// </param>
/// <param name="BroadcastAddress">
/// Destination for the re-broadcast: the limited broadcast address
/// <see cref="DefaultBroadcastAddress"/>, or a subnet-directed broadcast address.
/// </param>
/// <param name="ExpiryInterval">
/// How long a radio may go unreported before the emitter stops broadcasting it and drops it
/// from the advertised list. Defaults to <see cref="DefaultExpiryInterval"/> (15.14).
/// </param>
/// <param name="CommandRuleResolver">
/// Resolves a radio serial to the enabled forward rule serving that radio's command
/// channel, whose bind address and Client port become the advertised endpoint (15.4).
/// Returns <c>null</c> when no such rule exists, which withholds the radio with
/// <see cref="RadioAdvertiseState.WithheldNoCommandRule"/> (15.11).
/// </param>
/// <remarks>
/// Rule resolution is a delegate rather than a rule list so the emitter reads the current
/// state of the port forward manager on every announce: a rule the operator disables between
/// two announces withholds the radio on the second one, with no cached copy to invalidate.
/// <para>
/// _Requirements: 15.4, 15.6, 15.8, 15.11, 15.14_
/// </para>
/// </remarks>
public record DiscoveryEmitterConfig(
    int BroadcastPort,
    string BroadcastAddress,
    TimeSpan ExpiryInterval,
    Func<string, ForwardRule?> CommandRuleResolver)
{
    /// <summary>Default expiry interval: 10 seconds (15.14).</summary>
    public static readonly TimeSpan DefaultExpiryInterval = TimeSpan.FromSeconds(10);

    /// <summary>The IPv4 limited broadcast address, which stays on the local link.</summary>
    public const string DefaultBroadcastAddress = "255.255.255.255";
}
