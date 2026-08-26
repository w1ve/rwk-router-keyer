/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Config;

/// <summary>
/// A persisted Station entry: a named reference to a Station's Tailscale IP and pairing key.
/// Stored in the Client's station list (stations.json) so the operator can select from a
/// dropdown rather than manually entering IP and key each time.
/// </summary>
/// <param name="Name">Friendly name for this station (max 20 chars, e.g. "Home Station").</param>
/// <param name="TailscaleIp">The Station's Tailscale IP address (100.x.x.x or fd7a:...).</param>
/// <param name="PairingKey">The Station's 8-character pairing key (DPAPI-encrypted at rest).</param>
public record StationEntry(string Name, string TailscaleIp, string PairingKey)
{
    /// <summary>Maximum length for the station name.</summary>
    public const int MaxNameLength = 20;

    /// <summary>
    /// Parses a station info string copied from the Station's "Copy Station Info" menu.
    /// Format: "TailscaleIP|PairingKey"
    /// </summary>
    /// <param name="clipboardText">The pasted clipboard text.</param>
    /// <param name="ip">Parsed Tailscale IP.</param>
    /// <param name="key">Parsed pairing key.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseClipboard(string? clipboardText, out string ip, out string key)
    {
        ip = "";
        key = "";
        if (string.IsNullOrWhiteSpace(clipboardText)) return false;

        string trimmed = clipboardText.Trim();
        int sep = trimmed.LastIndexOf('|');
        if (sep <= 0 || sep >= trimmed.Length - 1) return false;

        ip = trimmed[..sep].Trim();
        key = trimmed[(sep + 1)..].Trim();

        // Validate IP
        if (!System.Net.IPAddress.TryParse(ip, out _)) return false;
        // Key should be non-empty
        if (key.Length < 4) return false;

        return true;
    }

    /// <summary>
    /// Formats this entry as the clipboard export string: "TailscaleIP|PairingKey"
    /// </summary>
    public string ToClipboardString() => $"{TailscaleIp}|{PairingKey}";

    public override string ToString() => Name;
}
