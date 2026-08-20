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
using RWK.Shared.Config;

namespace RWK.Client.Wizard;

/// <summary>
/// Severity of a conflict detection result.
/// </summary>
public enum ConflictSeverity
{
    /// <summary>Blocks Apply — the configuration cannot work.</summary>
    Error,
    /// <summary>Does not block Apply — the operator should be aware.</summary>
    Warning
}

/// <summary>
/// A single conflict detected during Wizard validation (§9 of the spec).
/// </summary>
public sealed record ConflictResult(ConflictSeverity Severity, string Message);

/// <summary>
/// Validates proposed Wizard rules against existing rules, port identity constraints,
/// and live socket availability (§9 of the spec).
/// </summary>
public static class ConflictDetector
{
    /// <summary>
    /// Runs all conflict checks against the proposed rules.
    /// </summary>
    /// <param name="proposed">The rules the Wizard wants to create.</param>
    /// <param name="existing">Rules already in the Client's forwarding table.</param>
    /// <param name="trialBind">If true, attempts a trial socket bind on each proposed local endpoint.</param>
    /// <returns>List of errors and warnings (empty = clean).</returns>
    public static List<ConflictResult> Detect(
        IReadOnlyList<ProfileForwardRule> proposed,
        IReadOnlyList<ForwardRule> existing,
        bool trialBind = true)
    {
        var results = new List<ConflictResult>();

        // 1. Basic validation on each proposed rule.
        foreach (var rule in proposed)
        {
            // Port range check.
            if (rule.ClientPort < 1 || rule.ClientPort > 65535)
                results.Add(new(ConflictSeverity.Error, $"'{rule.Name}': client port {rule.ClientPort} is outside valid range 1-65535."));
            if (rule.StationPort < 1 || rule.StationPort > 65535)
                results.Add(new(ConflictSeverity.Error, $"'{rule.Name}': station port {rule.StationPort} is outside valid range 1-65535."));

            // Port identity check: required means client port must equal station port.
            if (rule.PortIdentity is "required" or "unknown" && rule.ClientPort != rule.StationPort)
                results.Add(new(ConflictSeverity.Error,
                    $"'{rule.Name}': protocol requires matching ports (portIdentity={rule.PortIdentity}) but client port {rule.ClientPort} != station port {rule.StationPort}."));

            // Station target validation.
            if (string.IsNullOrWhiteSpace(rule.StationTarget))
                results.Add(new(ConflictSeverity.Error, $"'{rule.Name}': station target address is empty."));
            else if (IPAddress.TryParse(rule.StationTarget, out var addr))
            {
                // Check if it looks like a Tailscale address (100.64.0.0/10).
                byte[] bytes = addr.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                    results.Add(new(ConflictSeverity.Error,
                        $"'{rule.Name}': station target {rule.StationTarget} looks like a Tailscale address (100.64.0.0/10). Station Target should be a LAN device, not the Station itself."));
            }

            // Bind address warning: 0.0.0.0 means LAN-reachable.
            if (rule.BindAddress == "0.0.0.0")
                results.Add(new(ConflictSeverity.Warning,
                    $"'{rule.Name}': bind address 0.0.0.0 makes this rule reachable from your entire local network."));

            // Ephemeral port range warning.
            if (rule.ClientPort >= 49152 && rule.ClientPort <= 65535)
                results.Add(new(ConflictSeverity.Warning,
                    $"'{rule.Name}': client port {rule.ClientPort} is in the ephemeral range (49152-65535) and may collide with OS-assigned ports."));
        }

        // 2. Duplicate detection within the proposed set.
        var proposedEndpoints = proposed
            .Select(r => (r.Protocol.ToUpperInvariant(), r.BindAddress, r.ClientPort))
            .ToList();

        for (int i = 0; i < proposedEndpoints.Count; i++)
        {
            for (int j = i + 1; j < proposedEndpoints.Count; j++)
            {
                if (proposedEndpoints[i] == proposedEndpoints[j])
                {
                    results.Add(new(ConflictSeverity.Error,
                        $"Duplicate: '{proposed[i].Name}' and '{proposed[j].Name}' both bind {proposed[i].Protocol} {proposed[i].BindAddress}:{proposed[i].ClientPort}."));
                }
            }
        }

        // 3. Conflicts with existing rules (by endpoint, not by name — name match is a merge).
        foreach (var rule in proposed)
        {
            foreach (var ex in existing)
            {
                // Same name = merge (update in place), not a conflict.
                if (string.Equals(rule.Name, ex.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool sameProto = string.Equals(rule.Protocol, ex.Protocol.ToString(), StringComparison.OrdinalIgnoreCase);
                bool samePort = rule.ClientPort == ex.ClientPort;
                bool sameBind = string.Equals(rule.BindAddress, ex.BindAddress, StringComparison.OrdinalIgnoreCase)
                    || rule.BindAddress == "0.0.0.0" || ex.BindAddress == "0.0.0.0";

                if (sameProto && samePort && sameBind)
                {
                    results.Add(new(ConflictSeverity.Error,
                        $"'{rule.Name}' conflicts with existing rule '{ex.Name}': both bind {rule.Protocol} port {rule.ClientPort}."));
                }
            }
        }

        // 4. Trial bind — attempt to actually bind the socket (§9 in-process checks).
        if (trialBind)
        {
            foreach (var rule in proposed)
            {
                // Skip rules that already have errors.
                if (rule.ClientPort < 1 || rule.ClientPort > 65535) continue;

                // Skip if there's already a matching existing rule by name (it'll be updated).
                if (existing.Any(ex => string.Equals(rule.Name, ex.Name, StringComparison.OrdinalIgnoreCase) && ex.Enabled))
                    continue;

                if (!IPAddress.TryParse(rule.BindAddress, out var bindAddr))
                    bindAddr = IPAddress.Loopback;

                var result = TryBind(rule.Protocol, bindAddr, rule.ClientPort);
                if (result is not null)
                {
                    results.Add(new(ConflictSeverity.Warning,
                        $"'{rule.Name}': port {rule.ClientPort} on {rule.BindAddress} may already be in use ({result})."));
                }
            }
        }

        // 5. Known application conflicts (AnyDesk on 50001-50003).
        var knownConflicts = new Dictionary<int, string>
        {
            { 50001, "AnyDesk local discovery" },
            { 50002, "AnyDesk local discovery" },
            { 50003, "AnyDesk local discovery" }
        };

        foreach (var rule in proposed)
        {
            if (knownConflicts.TryGetValue(rule.ClientPort, out string? app))
            {
                results.Add(new(ConflictSeverity.Warning,
                    $"'{rule.Name}': port {rule.ClientPort} is also used by {app}. If {app.Split(' ')[0]} is installed, these rules may not bind."));
            }
        }

        return results;
    }

    /// <summary>
    /// Attempts a trial bind on the specified endpoint. Returns null on success,
    /// or an error description on failure.
    /// </summary>
    private static string? TryBind(string protocol, IPAddress bindAddr, int port)
    {
        try
        {
            if (string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase))
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                socket.Bind(new IPEndPoint(bindAddr, port));
                // Success — port is available.
                return null;
            }
            else
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                socket.Bind(new IPEndPoint(bindAddr, port));
                return null;
            }
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "address already in use"
                : $"bind failed: {ex.SocketErrorCode}";
        }
        catch (Exception ex)
        {
            return $"bind failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true if any result is an Error (blocks Apply).
    /// </summary>
    public static bool HasErrors(IReadOnlyList<ConflictResult> results)
        => results.Any(r => r.Severity == ConflictSeverity.Error);
}
