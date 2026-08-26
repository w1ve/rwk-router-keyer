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
/// Persists the list of known Station entries to a JSON file alongside the executable.
/// </summary>
public static class StationListStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "stations.json");

    public static List<StationEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<StationEntry>>(json) ?? new();
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
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
