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

namespace RWK.Client.Wizard;

/// <summary>
/// Loads the radio catalog from the radios.json file shipped alongside the executable.
/// </summary>
public static class CatalogLoader
{
    private const string CatalogFileName = "radios.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Loads the catalog from the standard location (Wizard subfolder next to the exe,
    /// or same directory as the exe for single-file publish).
    /// </summary>
    /// <returns>The loaded catalog, or an empty catalog if the file is missing or corrupt.</returns>
    public static RadioCatalog Load()
    {
        string baseDir = AppContext.BaseDirectory;

        // Try Wizard subfolder first (development layout), then exe directory (single-file publish).
        string path = Path.Combine(baseDir, "Wizard", CatalogFileName);
        if (!File.Exists(path))
            path = Path.Combine(baseDir, CatalogFileName);

        if (!File.Exists(path))
            return new RadioCatalog();

        return LoadFromFile(path);
    }

    /// <summary>
    /// Loads the catalog from a specific file path.
    /// </summary>
    public static RadioCatalog LoadFromFile(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<RadioCatalog>(json, JsonOptions) ?? new RadioCatalog();
        }
        catch
        {
            return new RadioCatalog();
        }
    }

    /// <summary>
    /// Returns only the radio entries (not services), grouped by vendor, sorted alphabetically.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> GetRadioEntries(RadioCatalog catalog)
    {
        return catalog.Entries
            .Where(e => !e.IsService && !e.IsGenericSerial && !e.IsGenericService)
            .OrderBy(e => e.Vendor)
            .ThenBy(e => e.DisplayName)
            .ToList();
    }

    /// <summary>
    /// Returns only the ancillary service entries (Step 4 extras).
    /// </summary>
    public static IReadOnlyList<CatalogEntry> GetServiceEntries(RadioCatalog catalog)
    {
        return catalog.Entries
            .Where(e => e.IsService)
            .OrderBy(e => e.DisplayName)
            .ToList();
    }

    /// <summary>
    /// Returns the generic entries (serial bridge + TCP/UDP service).
    /// </summary>
    public static IReadOnlyList<CatalogEntry> GetGenericEntries(RadioCatalog catalog)
    {
        return catalog.Entries
            .Where(e => e.IsGenericSerial || e.IsGenericService)
            .ToList();
    }
}
