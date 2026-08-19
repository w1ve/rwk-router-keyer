/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
// ForwardRule lives in the configuration model (RWK.Shared.Config) because rules are
// persisted with the rest of the profile (10.7).
using System.Net;
using RWK.Shared.Config;

namespace RWK.Shared.Net;

/// <summary>
/// Manages TCP and UDP port forwarding rules carried over the Tailscale tunnel.
/// </summary>
/// <remarks>
/// Design Component 6. Each rule's Client-side listener binds to that rule's
/// <c>BindAddress</c> rather than implicitly to loopback (10.11, 10.13). The default is
/// the loopback address, so reachability from the Client's local network is always an
/// explicit opt-in. An address that is not present on the Client host puts the rule into
/// an error state with the listener left unbound — implementations MUST never silently
/// fall back to loopback or to the any-address (10.15).
/// <para>
/// <c>RuleType</c> is a label only, except for <c>FlexDiscovery</c>: <c>Cat</c>,
/// <c>Audio</c>, and <c>RemoteRig</c> take the identical generic path for their protocol
/// (10.17).
/// </para>
/// _Requirements: 10.1, 10.7, 10.11, 10.13, 10.15_
/// </remarks>
public interface IPortForwardManager : IDisposable
{
    /// <summary>
    /// Gets the currently configured forwarding rules.
    /// </summary>
    IReadOnlyList<ForwardRule> Rules { get; }

    /// <summary>
    /// Adds a forwarding rule (10.1).
    /// </summary>
    void AddRule(ForwardRule rule);

    /// <summary>
    /// Removes the rule with the given identifier, stopping its listener if running.
    /// </summary>
    void RemoveRule(Guid ruleId);

    /// <summary>
    /// Enables or disables a single rule without affecting the others.
    /// </summary>
    void SetRuleEnabled(Guid ruleId, bool enabled);

    /// <summary>
    /// Changes the local address a rule's Client-side listener binds to, restarting only
    /// that rule's listener (10.11, 10.13).
    /// </summary>
    /// <param name="ruleId">The rule to update.</param>
    /// <param name="bindAddress">
    /// The local address to bind, for example <c>127.0.0.1</c>, a specific local interface
    /// address, or <c>0.0.0.0</c> for the any-address.
    /// </param>
    void SetRuleBindAddress(Guid ruleId, string bindAddress);

    /// <summary>
    /// Starts listeners for all enabled rules.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops all listeners and closes active relayed connections.
    /// </summary>
    void Stop();

    /// <summary>
    /// Raised when a rule's status or byte counters change, including the error state
    /// produced by an unavailable bind address (10.15).
    /// </summary>
    event EventHandler<ForwardRuleStatusChangedEventArgs>? RuleStatusChanged;

    /// <summary>
    /// Delegate that opens a TCP stream to the given Station port via the Tailscale tunnel.
    /// Set by the controller after session establishment. When null, TCP forwards cannot relay.
    /// </summary>
    Func<int, CancellationToken, Task<Stream>>? TunnelDial { get; set; }

    /// <summary>
    /// Delegate that creates an outbound UDP forward via the sidecar and returns the
    /// loopback endpoint to send datagrams to. When null, UDP forwards relay locally only.
    /// </summary>
    Func<int, CancellationToken, Task<System.Net.IPEndPoint>>? UdpTunnelBind { get; set; }
}
