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
using System.Text;

namespace RWK.Shared.Discovery;

/// <summary>
/// Parses and rewrites FlexRadio VITA-49 discovery packets (SmartSDR v1.1.3+).
/// </summary>
/// <remarks>
/// Layout (from the FlexRadio community documentation):
/// <list type="bullet">
///   <item>28-byte VITA-49 preamble (7 × 32-bit words): header, stream ID (0x00000800),
///         class ID high (OUI 0x001C2D53), class ID low (0x4CFFFF00), 3 timestamp words (zeros)</item>
///   <item>ASCII payload: space-separated key=value pairs, padded to 4-byte boundary</item>
///   <item>Keys include: discovery_protocol_version, model, serial, version, nickname,
///         callsign, ip, port, status, inuse_ip, inuse_host, max_licensed_version, etc.</item>
/// </list>
/// <para>
/// The class ID identifies this as a Flex discovery packet: OUI 1C-2D-53 (Flex's IEEE OUI).
/// Stream ID 0x800 distinguishes discovery from other VITA-49 streams.
/// </para>
/// <para>
/// This is the ONLY file in the system that knows the payload layout. All layout constants
/// are defined here. Nothing else may encode offsets, field names, or parsing assumptions.
/// </para>
/// _Requirements: 15.4, 15.5, 15.17, 15.20_
/// </remarks>
public sealed class FlexVitaDiscoveryCodec : IDiscoveryPayloadCodec
{
    /// <summary>VITA-49 preamble size in bytes (7 words × 4 bytes).</summary>
    private const int PreambleSize = 28;

    /// <summary>Stream ID for Flex discovery packets.</summary>
    private const uint DiscoveryStreamId = 0x00000800;

    /// <summary>Class ID high word (Flex OUI 0x001C2D + 0x53).</summary>
    private const uint ClassIdHigh = 0x001C2D53;

    /// <summary>Class ID low word.</summary>
    private const uint ClassIdLow = 0x4CFFFF00;

    /// <summary>UDP port for discovery broadcasts.</summary>
    public const int DiscoveryPort = 4992;

    /// <inheritdoc/>
    public bool TryParse(ReadOnlySpan<byte> payload, out DiscoveredRadio radio, out string? failureReason)
    {
        radio = default!;
        failureReason = null;

        if (payload.Length < PreambleSize + 4)
        {
            failureReason = $"Payload too short ({payload.Length} bytes, need at least {PreambleSize + 4}).";
            return false;
        }

        // Verify stream ID (word 1, offset 4)
        uint streamId = ReadUInt32BE(payload, 4);
        if (streamId != DiscoveryStreamId)
        {
            failureReason = $"Stream ID mismatch: expected 0x{DiscoveryStreamId:X8}, got 0x{streamId:X8}.";
            return false;
        }

        // Verify class ID (words 2-3, offsets 8 and 12)
        uint classHigh = ReadUInt32BE(payload, 8);
        uint classLow = ReadUInt32BE(payload, 12);
        if (classHigh != ClassIdHigh || classLow != ClassIdLow)
        {
            failureReason = $"Class ID mismatch: expected {ClassIdHigh:X8}:{ClassIdLow:X8}, got {classHigh:X8}:{classLow:X8}.";
            return false;
        }

        // Extract ASCII payload after the 28-byte preamble
        string ascii = Encoding.ASCII.GetString(payload[PreambleSize..]).TrimEnd('\0', ' ');

        // Parse key=value pairs (space-separated)
        var fields = ParseKeyValuePairs(ascii);

        if (!fields.TryGetValue("serial", out string? serial) || string.IsNullOrEmpty(serial))
        {
            failureReason = "Missing or empty 'serial' field.";
            return false;
        }

        if (!fields.TryGetValue("ip", out string? ipStr) || !IPAddress.TryParse(ipStr, out IPAddress? ip))
        {
            failureReason = $"Missing or invalid 'ip' field: '{ipStr ?? "(null)"}.'";
            return false;
        }

        if (!fields.TryGetValue("port", out string? portStr) || !int.TryParse(portStr, out int port))
        {
            failureReason = $"Missing or invalid 'port' field: '{portStr ?? "(null)"}.'";
            return false;
        }

        string model = fields.GetValueOrDefault("model", "Unknown");

        radio = new DiscoveredRadio(
            Serial: serial,
            Model: model,
            StationAddress: ip,
            StationCommandPort: port,
            LastSeenUtc: DateTime.UtcNow,
            AdvertisedLocalEndpoint: null);

        return true;
    }

    /// <inheritdoc/>
    public bool TryRewriteEndpoint(
        ReadOnlySpan<byte> payload,
        IPEndPoint localEndpoint,
        out byte[] rewritten,
        out string? failureReason)
    {
        rewritten = Array.Empty<byte>();
        failureReason = null;

        // First verify the payload is parseable
        if (!TryParse(payload, out _, out failureReason))
            return false;

        // Extract the ASCII part
        string ascii = Encoding.ASCII.GetString(payload[PreambleSize..]).TrimEnd('\0', ' ');

        // Replace ip=... and port=... values
        string newIp = localEndpoint.Address.ToString();
        string newPort = localEndpoint.Port.ToString();

        string rewrittenAscii = ReplaceField(ascii, "ip", newIp);
        rewrittenAscii = ReplaceField(rewrittenAscii, "port", newPort);

        // Rebuild the packet: preamble + new ASCII payload (padded to 4-byte boundary)
        byte[] asciiBytes = Encoding.ASCII.GetBytes(rewrittenAscii);
        int paddedLen = (asciiBytes.Length + 3) & ~3; // Round up to 4-byte boundary

        byte[] result = new byte[PreambleSize + paddedLen];

        // Copy original preamble unchanged
        payload[..PreambleSize].CopyTo(result);

        // Copy rewritten ASCII payload
        asciiBytes.CopyTo(result, PreambleSize);
        // Remaining bytes are already zero (padding)

        // Update the packet length in the VITA-49 header (word 0, bits 15:0 = word count)
        int wordCount = result.Length / 4;
        result[2] = (byte)(wordCount >> 8);
        result[3] = (byte)(wordCount & 0xFF);

        // Verify the rewrite by re-parsing
        if (!TryParse(result, out var verification, out _))
        {
            failureReason = "Rewritten packet failed verification parse.";
            rewritten = Array.Empty<byte>();
            return false;
        }

        if (!verification.StationAddress.Equals(localEndpoint.Address) ||
            verification.StationCommandPort != localEndpoint.Port)
        {
            failureReason = "Rewritten packet has wrong endpoint after re-parse.";
            rewritten = Array.Empty<byte>();
            return false;
        }

        rewritten = result;
        return true;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseKeyValuePairs(string ascii)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string token in ascii.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq > 0 && eq < token.Length - 1)
            {
                string key = token[..eq];
                string value = token[(eq + 1)..];
                result[key] = value;
            }
        }

        return result;
    }

    private static string ReplaceField(string ascii, string key, string newValue)
    {
        // Find key= in the string and replace the value up to the next space or end
        string prefix = key + "=";
        int start = ascii.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return ascii;

        int valueStart = start + prefix.Length;
        int valueEnd = ascii.IndexOf(' ', valueStart);
        if (valueEnd < 0) valueEnd = ascii.Length;

        return string.Concat(ascii.AsSpan(0, valueStart), newValue, ascii.AsSpan(valueEnd));
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }
}
