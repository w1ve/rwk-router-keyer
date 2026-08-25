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

namespace RWK.Shared.Config;

/// <summary>
/// Classifies the network exposure level of a bind or target address. Used by the UI
/// to determine the severity of exposure warnings (10.14, 10.28).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Loopback"/> — 127.x.x.x or ::1. Reachable only from the local host.</item>
/// <item><see cref="PrivateOrLinkLocal"/> — RFC1918 (10/8, 172.16/12, 192.168/16), IPv6 ULA (fc00::/7),
///     or IPv6 link-local (fe80::/10). Reachable only from the LAN.</item>
/// <item><see cref="GlobalUnicast"/> — Everything else. For IPv6 this is typically directly routable
///     from the public internet with no NAT in front of it.</item>
/// <item><see cref="Invalid"/> — The address string could not be parsed.</item>
/// </list>
/// _Requirements: 10.14, 10.28_
/// </remarks>
public enum AddressExposure
{
    /// <summary>Loopback (127.x.x.x, ::1). Only reachable from this host.</summary>
    Loopback,

    /// <summary>Private/LAN-only (RFC1918 IPv4, ULA IPv6 fc00::/7, link-local fe80::/10).</summary>
    PrivateOrLinkLocal,

    /// <summary>Globally routable. For IPv6 this means no NAT — potentially internet-reachable.</summary>
    GlobalUnicast,

    /// <summary>The address string could not be parsed as a valid IP address.</summary>
    Invalid
}

/// <summary>
/// Extension methods for classifying IP addresses into <see cref="AddressExposure"/> levels.
/// </summary>
public static class AddressExposureClassifier
{
    /// <summary>
    /// Classifies an IP address string into its exposure level.
    /// </summary>
    public static AddressExposure Classify(string? addressString)
    {
        if (string.IsNullOrWhiteSpace(addressString))
            return AddressExposure.Invalid;

        if (!IPAddress.TryParse(addressString, out IPAddress? address))
            return AddressExposure.Invalid;

        return Classify(address);
    }

    /// <summary>
    /// Classifies a parsed IP address into its exposure level.
    /// </summary>
    public static AddressExposure Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Loopback: 127.x.x.x or ::1
        if (IPAddress.IsLoopback(address))
            return AddressExposure.Loopback;

        // Any-address (0.0.0.0 or ::) binds all interfaces — treat as LAN exposure
        // since it exposes to the local network at minimum.
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return AddressExposure.PrivateOrLinkLocal;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4 private ranges (RFC1918):
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 10)
                return AddressExposure.PrivateOrLinkLocal;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return AddressExposure.PrivateOrLinkLocal;
            if (bytes[0] == 192 && bytes[1] == 168)
                return AddressExposure.PrivateOrLinkLocal;
            // 169.254.0.0/16 link-local (APIPA)
            if (bytes[0] == 169 && bytes[1] == 254)
                return AddressExposure.PrivateOrLinkLocal;
            // Tailscale CGNAT range: 100.64.0.0/10
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return AddressExposure.PrivateOrLinkLocal;

            return AddressExposure.GlobalUnicast;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();

            // IPv6 link-local: fe80::/10
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                return AddressExposure.PrivateOrLinkLocal;

            // IPv6 ULA (Unique Local Address): fc00::/7 (includes fd00::/8)
            // This covers Tailscale's fd7a:115c:a1e0::/48 range.
            if ((bytes[0] & 0xfe) == 0xfc)
                return AddressExposure.PrivateOrLinkLocal;

            // IPv4-mapped IPv6 (::ffff:x.x.x.x) — classify the embedded IPv4
            if (address.IsIPv4MappedToIPv6)
                return Classify(address.MapToIPv4());

            return AddressExposure.GlobalUnicast;
        }

        return AddressExposure.Invalid;
    }
}
