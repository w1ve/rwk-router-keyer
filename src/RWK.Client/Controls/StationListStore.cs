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
using RWK.Shared.Config;

namespace RWK.Client.Controls;

/// <summary>
/// Persists the list of known Station entries to a JSON file under the user's roaming
/// application data (<c>%AppData%\RWK Client\stations.json</c>).
/// </summary>
/// <remarks>
/// Prior to v1.0.6 this was stored next to the executable (<see cref="AppContext.BaseDirectory"/>).
/// After the install location moved to Program Files (v1.0.5), that directory is not writable
/// for standard users, so imports silently failed to persist. The store now writes under
/// <c>%AppData%</c> (always user-writable) and migrates any legacy file on first load.
/// </remarks>
public static class StationListStore
{
    // Matches ConfigStore.ClientFolderName so the station list lives beside config.json.
    private const string AppFolderName = "RWK Client";
    private const string FileName = "stations.json";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        FileName);

    // Legacy location used before v1.0.6 (next to the executable).
    private static readonly string LegacyFilePath = Path.Combine(AppContext.BaseDirectory, FileName);

    public static List<StationEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<StationEntry>>(json) ?? new();
            }

            // One-time migration: if the new file doesn't exist but a legacy one does,
            // read it and re-save to the new location.
            if (File.Exists(LegacyFilePath))
            {
                string legacyJson = File.ReadAllText(LegacyFilePath);
                var migrated = JsonSerializer.Deserialize<List<StationEntry>>(legacyJson) ?? new();
                Save(migrated); // writes to the new %AppData% path
                return migrated;
            }

            return new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(List<StationEntry> entries)
    {
        try
        {
            // Ensure the target directory exists (created lazily on first save).
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            // Surface the failure to the debug log rather than swallowing it entirely, so a
            // future permission problem is diagnosable instead of silently losing imports.
            System.Diagnostics.Debug.WriteLine($"StationListStore.Save failed: {ex.Message}");
        }
    }
}
