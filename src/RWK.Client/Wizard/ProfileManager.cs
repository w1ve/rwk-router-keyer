/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RWK.Client.Wizard;

/// <summary>
/// Manages saving and loading of Wizard profiles (.rwkprofile.json) and the
/// generated setup guides (-readme.txt).
/// </summary>
public static class ProfileManager
{
    private const int MaxSupportedVersion = 1;
    private const string ProfileExtension = ".rwkprofile.json";
    private const string ReadmeExtension = "-readme.txt";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Returns the profiles directory: %LOCALAPPDATA%\RWK Router Keyer\profiles\
    /// </summary>
    public static string GetProfilesDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "RWK Router Keyer", "profiles");
    }

    /// <summary>
    /// Sanitizes a profile name into a safe filename component.
    /// Strips non-ASCII, collapses whitespace to dashes, trims to 64 chars.
    /// </summary>
    public static string SanitizeFileName(string profileName)
    {
        // Remove everything outside [A-Za-z0-9._\- ] and common punctuation
        string clean = Regex.Replace(profileName, @"[^A-Za-z0-9._\- ]", "");
        // Collapse whitespace runs to a single dash
        clean = Regex.Replace(clean.Trim(), @"\s+", "-");
        // Trim to 64 characters
        if (clean.Length > 64)
            clean = clean[..64];
        return clean;
    }

    /// <summary>
    /// Saves a profile to the profiles directory.
    /// Returns the full path of the saved file.
    /// </summary>
    public static string SaveProfile(WizardProfile profile)
    {
        string dir = GetProfilesDirectory();
        Directory.CreateDirectory(dir);

        string baseName = SanitizeFileName(profile.Profile.Name);
        if (string.IsNullOrEmpty(baseName))
            baseName = "profile";

        string filePath = Path.Combine(dir, baseName + ProfileExtension);
        string json = JsonSerializer.Serialize(profile, WriteOptions);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Saves a profile to a user-specified path (Save As).
    /// </summary>
    public static void SaveProfileAs(WizardProfile profile, string filePath)
    {
        string dir = Path.GetDirectoryName(filePath) ?? GetProfilesDirectory();
        Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(profile, WriteOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Loads a profile from a file. Returns null if the file is corrupt or
    /// has a version higher than supported.
    /// </summary>
    /// <param name="filePath">Full path to the .rwkprofile.json file.</param>
    /// <param name="error">Error message if loading fails.</param>
    public static WizardProfile? LoadProfile(string filePath, out string? error)
    {
        error = null;

        if (!File.Exists(filePath))
        {
            error = $"File not found: {filePath}";
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var profile = JsonSerializer.Deserialize<WizardProfile>(json, ReadOptions);

            if (profile is null)
            {
                error = "Failed to parse profile (null result).";
                return null;
            }

            // Version gate (§4.3): reject versions higher than we understand.
            if (profile.RwkProfileVersion > MaxSupportedVersion)
            {
                error = $"Profile version {profile.RwkProfileVersion} is not supported by this version of RWK. " +
                        $"Maximum supported version is {MaxSupportedVersion}.";
                return null;
            }

            return profile;
        }
        catch (JsonException ex)
        {
            error = $"Invalid profile JSON: {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"Error reading profile: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Returns the readme file path for a given profile name.
    /// </summary>
    public static string GetReadmePath(string profileName)
    {
        string dir = GetProfilesDirectory();
        string baseName = SanitizeFileName(profileName);
        if (string.IsNullOrEmpty(baseName))
            baseName = "profile";
        return Path.Combine(dir, baseName + ReadmeExtension);
    }

    /// <summary>
    /// Lists all saved profiles in the profiles directory.
    /// </summary>
    public static IReadOnlyList<string> ListProfiles()
    {
        string dir = GetProfilesDirectory();
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        return Directory.GetFiles(dir, "*" + ProfileExtension)
            .OrderBy(f => f)
            .ToArray();
    }

    /// <summary>
    /// Builds a <see cref="WizardProfile"/> from a catalog entry and user inputs.
    /// </summary>
    /// <param name="entry">The selected catalog entry.</param>
    /// <param name="profileName">Operator-chosen name (e.g. "Malawi -- IC-7300MK2 via RS-BA1").</param>
    /// <param name="stationTarget">The station target IP/hostname the operator entered.</param>
    /// <param name="enableRules">Whether rules should be created enabled.</param>
    /// <param name="extras">Additional service entries selected in Step 4.</param>
    public static WizardProfile BuildProfile(
        CatalogEntry entry,
        string profileName,
        string stationTarget,
        bool enableRules,
        IReadOnlyList<CatalogEntry>? extras = null)
    {
        var profile = new WizardProfile
        {
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            Profile = new ProfileInfo
            {
                Name = profileName,
                CatalogId = entry.Id,
                Confidence = entry.Confidence
            },
            SetupNotes = new SetupNotes
            {
                Client = new List<string>(entry.ClientNotes),
                Station = new List<string>(entry.StationNotes),
                Radio = new List<string>(entry.RadioNotes)
            }
        };

        string bindAddr = entry.BindAddress ?? "127.0.0.1";

        // Add forwards from the main entry.
        foreach (var fwd in entry.Forwards)
        {
            profile.Forwards.Add(new ProfileForwardRule
            {
                Name = fwd.Name,
                Protocol = fwd.Proto,
                Enabled = enableRules,
                BindAddress = bindAddr,
                ClientPort = fwd.Port,
                StationTarget = stationTarget,
                StationPort = fwd.Port,
                PortIdentity = fwd.PortIdentity,
                Role = fwd.Role,
                Direction = fwd.Direction,
                Notes = fwd.Notes
            });
        }

        // Add extras (ancillary services from Step 4).
        if (extras is not null)
        {
            foreach (var extra in extras)
            {
                string extraTarget = extra.EndpointLocation == "station-pc" ? "127.0.0.1" : stationTarget;
                string extraBind = extra.BindAddress ?? "127.0.0.1";

                foreach (var fwd in extra.Forwards)
                {
                    profile.Forwards.Add(new ProfileForwardRule
                    {
                        Name = fwd.Name,
                        Protocol = fwd.Proto,
                        Enabled = enableRules,
                        BindAddress = extraBind,
                        ClientPort = fwd.Port,
                        StationTarget = extraTarget,
                        StationPort = fwd.Port,
                        PortIdentity = fwd.PortIdentity,
                        Role = fwd.Role,
                        Direction = fwd.Direction,
                        Notes = fwd.Notes
                    });
                }

                // Merge notes.
                profile.SetupNotes.Client.AddRange(extra.ClientNotes);
                profile.SetupNotes.Station.AddRange(extra.StationNotes);
            }
        }

        return profile;
    }
}
