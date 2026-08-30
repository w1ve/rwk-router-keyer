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

namespace MorseTest;

/// <summary>
/// Application settings with JSON persistence.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MorseTest",
        "settings.json");

    public string? SelectedComPort { get; set; }
    public string? SelectedAudioDevice { get; set; }
    public int ToneFrequency { get; set; } = 750;
    public int WordsPerMinute { get; set; } = 25;
    public double Volume { get; set; } = 0.5;
    public PinMonitorMode PinMode { get; set; } = PinMonitorMode.CTS;
    public bool InvertPin { get; set; } = false;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // If loading fails, return defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Silently fail if we can't save settings
        }
    }
}

/// <summary>
/// Which pin to monitor for key state.
/// </summary>
public enum PinMonitorMode
{
    /// <summary>Monitor CTS pin (typically looped back from RTS)</summary>
    CTS,
    /// <summary>Monitor DSR pin (typically looped back from DTR)</summary>
    DSR,
    /// <summary>Monitor DCD pin</summary>
    DCD
}
