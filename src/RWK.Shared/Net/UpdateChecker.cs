/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Diagnostics;
using System.Text.Json;

namespace RWK.Shared.Net;

/// <summary>
/// The result of a successful update check: a newer published build is available.
/// </summary>
/// <param name="Version">The full published version (e.g. 1.0.5.24501).</param>
/// <param name="InstallerUrl">Direct download URL for the RWK-Setup.exe asset.</param>
public sealed record UpdateInfo(Version Version, string InstallerUrl);

/// <summary>
/// Checks the project's GitHub "latest release" for a newer build than the running one.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is build-precise. The release must include a <c>version.txt</c> asset
/// whose contents are the full four-part version string (e.g. <c>1.0.5.24501</c>). The tag
/// itself (e.g. <c>v1.0.5</c>) is not build-precise, so <c>version.txt</c> is what lets us
/// flag a "later build of the same minor version".
/// </para>
/// <para>
/// All network operations fail silently (return null / false): a missing internet
/// connection, a rate-limited API, or a malformed release must never disrupt the app.
/// </para>
/// </remarks>
public static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/w1ve/rwk-router-keyer/releases/latest";

    private const string VersionAssetName = "version.txt";
    private const string InstallerAssetName = "RWK-Setup.exe";

    // GitHub requires a User-Agent header on all API requests.
    private const string UserAgent = "RWK-UpdateChecker";

    /// <summary>
    /// Checks whether a newer build than <paramref name="currentVersion"/> is published.
    /// Returns the update info if a strictly newer build exists, otherwise null. Never throws.
    /// </summary>
    /// <param name="currentVersion">The running application's version.</param>
    /// <param name="cancellationToken">Cancels the network calls.</param>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            string json = await http.GetStringAsync(LatestReleaseApi, cancellationToken)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? versionUrl = null;
            string? installerUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null) continue;

                if (name.Equals(VersionAssetName, StringComparison.OrdinalIgnoreCase))
                    versionUrl = url;
                else if (name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                    installerUrl = url;
            }

            if (versionUrl is null || installerUrl is null)
                return null;

            string versionText = (await http.GetStringAsync(versionUrl, cancellationToken)
                .ConfigureAwait(false)).Trim();

            if (!TryParseVersion(versionText, out Version? latest) || latest is null)
                return null;

            // Only flag a strictly newer build.
            return latest > currentVersion ? new UpdateInfo(latest, installerUrl) : null;
        }
        catch
        {
            // Offline, rate-limited, malformed release, etc. — silently report "no update".
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer to a temp file and launches it. Returns the launched process
    /// path on success, or null on failure. The caller should exit the app after a successful
    /// launch so the installer can replace the running executables.
    /// </summary>
    /// <param name="installerUrl">The RWK-Setup.exe download URL from <see cref="UpdateInfo"/>.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public static async Task<string?> DownloadAndLaunchInstallerAsync(
        string installerUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"RWK-Setup-{DateTime.UtcNow:yyyyMMddHHmmss}.exe");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                byte[] bytes = await http.GetByteArrayAsync(installerUrl, cancellationToken)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            }

            // Launch with UseShellExecute so the installer's UAC manifest triggers elevation.
            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            };
            Process.Start(psi);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a version string that may be prefixed with 'v' (e.g. "v1.0.5.24501" or
    /// "1.0.5.24501"). Returns false if it cannot be parsed.
    /// </summary>
    private static bool TryParseVersion(string text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed.Substring(1);

        // Keep only the leading version token (ignore any trailing metadata after whitespace).
        int space = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '+', '-' });
        if (space > 0)
            trimmed = trimmed.Substring(0, space);

        return Version.TryParse(trimmed, out version);
    }
}
