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

namespace WinKeyerEmulator.App.Settings;

public class AppSettings
{
    public string? KeyingPortName { get; set; }
    public string KeyingLine { get; set; } = "DTR";
    public string? CommandPortName { get; set; }
    public string Transport { get; set; } = "UDP";
    public string UdpAddress { get; set; } = "127.0.0.1";
    public int UdpPort { get; set; } = 7388;
    public string RelayUrl { get; set; } = "wss://wrs.w1ve.com/ws";
    public string? PairingToken { get; set; }
    public bool LogRawData { get; set; }
    
    // Sidetone settings
    public bool SidetoneEnabled { get; set; }
    public string? SidetoneDeviceId { get; set; }
    public int SidetoneFrequency { get; set; } = 700;
    
    // CW Timing settings
    public int Weight { get; set; } = 50;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WKRServer", "settings.json");

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
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
