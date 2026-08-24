/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text;

namespace RWK.Shared.Discovery;

/// <summary>
/// Parses and rewrites N1MM+ inter-station discovery packets.
/// </summary>
/// <remarks>
/// N1MM+ broadcasts discovery/inter-station packets on UDP port 2237.
/// The packet format is %-delimited ASCII:
/// <c>COMPUTER%LAN_IP%PORT%VERSION%CALLSIGN%%</c>
/// <para>
/// Example: <c>STATION-PC%192.168.1.100%2238%1.0.9248.0%W1VE%%</c>
/// </para>
/// <para>
/// The codec extracts the LAN_IP field, rewrites it with the replacement address
/// (typically the Station's Tailscale IP), and reconstructs the packet.
/// </para>
/// </remarks>
public static class N1mmDiscoveryCodec
{
    /// <summary>
    /// The UDP port N1MM+ uses for inter-station discovery broadcasts.
    /// </summary>
    public const int DiscoveryPort = 2237;

    /// <summary>
    /// The UDP port N1MM+ uses for inter-station data exchange.
    /// </summary>
    public const int DataPort = 2238;

    /// <summary>
    /// Attempts to parse an N1MM+ discovery packet.
    /// </summary>
    /// <param name="data">Raw UTF-8 packet bytes.</param>
    /// <param name="result">Parsed result if successful.</param>
    /// <returns>True if the packet was successfully parsed as an N1MM+ discovery packet.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out N1mmDiscoveryPacket result)
    {
        result = default;

        if (data.Length < 5 || data.Length > 1024)
            return false;

        // N1MM+ packets are ASCII text, %-delimited, ending with %%
        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch
        {
            return false;
        }

        // Must end with %% (or at least contain % delimiters)
        if (!text.Contains('%'))
            return false;

        // Split on %: expect at least 5 fields (COMPUTER, IP, PORT, VERSION, CALLSIGN)
        // The trailing %% produces empty elements at the end.
        string[] parts = text.Split('%');
        if (parts.Length < 5)
            return false;

        // Filter out empty trailing parts from %%
        var fields = parts.Where(p => p.Length > 0).ToArray();
        if (fields.Length < 5)
            return false;

        result = new N1mmDiscoveryPacket
        {
            ComputerName = fields[0],
            LanIp = fields[1],
            Port = fields[2],
            Version = fields[3],
            Callsign = fields[4],
            RawText = text
        };

        return true;
    }

    /// <summary>
    /// Rewrites the LAN_IP field in an N1MM+ discovery packet with a new address.
    /// </summary>
    /// <param name="originalData">The original packet bytes.</param>
    /// <param name="newIp">The IP address to substitute for the LAN_IP field.</param>
    /// <returns>Rewritten packet bytes, or null if the packet could not be parsed.</returns>
    public static byte[]? RewriteIp(ReadOnlySpan<byte> originalData, string newIp)
    {
        if (!TryParse(originalData, out var parsed))
            return null;

        // Reconstruct with the new IP, preserving all other fields.
        string rewritten = $"{parsed.ComputerName}%{newIp}%{parsed.Port}%{parsed.Version}%{parsed.Callsign}%%";
        return Encoding.UTF8.GetBytes(rewritten);
    }

    /// <summary>
    /// Rewrites both the LAN_IP and PORT fields in an N1MM+ discovery packet.
    /// </summary>
    /// <param name="originalData">The original packet bytes.</param>
    /// <param name="newIp">The IP address to substitute for the LAN_IP field.</param>
    /// <param name="newPort">The port to substitute for the PORT field.</param>
    /// <returns>Rewritten packet bytes, or null if the packet could not be parsed.</returns>
    public static byte[]? RewriteEndpoint(ReadOnlySpan<byte> originalData, string newIp, int newPort)
    {
        if (!TryParse(originalData, out var parsed))
            return null;

        string rewritten = $"{parsed.ComputerName}%{newIp}%{newPort}%{parsed.Version}%{parsed.Callsign}%%";
        return Encoding.UTF8.GetBytes(rewritten);
    }
}

/// <summary>
/// Parsed N1MM+ discovery packet fields.
/// </summary>
/// <remarks>
/// This is a mutable struct for parsing convenience. Fields may be null/default
/// if the packet did not contain the expected number of %-delimited fields (though
/// <see cref="N1mmDiscoveryCodec.TryParse"/> guards against this).
/// </remarks>
public struct N1mmDiscoveryPacket
{
    /// <summary>Computer/station name (first field).</summary>
    public string ComputerName { get; set; }

    /// <summary>LAN IP address of the sending station (second field).</summary>
    public string LanIp { get; set; }

    /// <summary>Port number as string (third field, typically "2238").</summary>
    public string Port { get; set; }

    /// <summary>N1MM+ version string (fourth field).</summary>
    public string Version { get; set; }

    /// <summary>Callsign of the sending station (fifth field).</summary>
    public string Callsign { get; set; }

    /// <summary>The full raw text of the original packet.</summary>
    public string RawText { get; set; }

    /// <summary>Display string for diagnostics.</summary>
    public override readonly string ToString()
        => $"{Callsign}@{ComputerName} ({LanIp}:{Port}) v{Version}";
}
