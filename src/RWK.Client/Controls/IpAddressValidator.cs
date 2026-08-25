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

namespace RWK.Client.Controls;

/// <summary>
/// Specifies which address families the validator accepts.
/// </summary>
public enum IpAddressMode
{
    /// <summary>Accept both IPv4 and IPv6 addresses.</summary>
    Both,
    /// <summary>Accept only IPv4 addresses.</summary>
    IPv4Only,
    /// <summary>Accept only IPv6 addresses.</summary>
    IPv6Only
}

/// <summary>
/// Result of validating an IP address string.
/// </summary>
public readonly record struct IpValidationResult(bool IsValid, IPAddress? Address, string? ErrorMessage)
{
    public static IpValidationResult Valid(IPAddress address) => new(true, address, null);
    public static IpValidationResult Error(string message) => new(false, null, message);
}

/// <summary>
/// Pure validation logic for IP address input. Extracted from the UI control so it can be
/// unit-tested independently of WinForms. Follows the same pattern as
/// <see cref="RWK.Shared.Net.BindAddressResolver"/> — pure function, no side effects.
/// </summary>
public static class IpAddressValidator
{
    /// <summary>
    /// Validates an IP address string against the specified mode.
    /// </summary>
    /// <param name="input">The address string to validate (may include zone ID for link-local IPv6).</param>
    /// <param name="mode">Which address families to accept.</param>
    /// <returns>A validation result indicating success (with parsed address) or failure (with error message).</returns>
    public static IpValidationResult Validate(string? input, IpAddressMode mode = IpAddressMode.Both)
    {
        if (string.IsNullOrWhiteSpace(input))
            return IpValidationResult.Error("Address cannot be empty");

        string trimmed = input.Trim();

        // Strip brackets if present (user might type [::1])
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];

        // Handle zone ID for link-local (e.g. fe80::1%eth0)
        // IPAddress.TryParse handles zone IDs on .NET 5+
        if (!IPAddress.TryParse(trimmed, out IPAddress? address))
            return IpValidationResult.Error($"'{input}' is not a valid IP address");

        // Check address family against mode
        switch (mode)
        {
            case IpAddressMode.IPv4Only:
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    return IpValidationResult.Error("IPv4 address required");
                break;
            case IpAddressMode.IPv6Only:
                if (address.AddressFamily != AddressFamily.InterNetworkV6)
                    return IpValidationResult.Error("IPv6 address required");
                break;
            case IpAddressMode.Both:
            default:
                break;
        }

        return IpValidationResult.Valid(address);
    }

    /// <summary>
    /// Returns a user-friendly description of the address (e.g. "IPv4 loopback", "IPv6 global unicast").
    /// </summary>
    public static string Describe(IPAddress address)
    {
        string family = address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

        if (IPAddress.IsLoopback(address))
            return $"{family} loopback";
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return $"{family} any (all interfaces)";
        if (address.IsIPv6LinkLocal)
            return $"{family} link-local";

        return family;
    }
}
