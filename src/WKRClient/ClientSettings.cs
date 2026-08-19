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

namespace WKRClient;

/// <summary>
/// WinKeyer key mode (paddle behavior).
/// </summary>
public enum KeyMode
{
    IambicB = 0,
    IambicA = 1,
    Ultimatic = 2,
    Bug = 3,
}

public class ClientSettings
{
    public string? WinKeyerPort { get; set; }
    public string Transport { get; set; } = "UDP";
    public string ServerAddress { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7388;
    public string RelayUrl { get; set; } = "wss://wrs.w1ve.com/ws";
    public string? PairingToken { get; set; }

    // WinKeyer paddle settings (written to mode register 0x0E)
    public KeyMode KeyMode { get; set; } = KeyMode.IambicB;
    public bool PaddleSwap { get; set; } = false;
    public bool Autospace { get; set; } = false;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WKRClient", "settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Builds the WinKeyer mode register byte (0x0E) from settings.
    /// Bit 7: Disable paddle watchdog (0)
    /// Bit 6: Paddle echoback (1 = always on for RWK)
    /// Bit 5-4: Key mode (00=IambicB, 01=IambicA, 10=Ultimatic, 11=Bug)
    /// Bit 3: Paddle swap
    /// Bit 2: Serial echoback (0 = off, we don't want host CW echoed back)
    /// Bit 1: Autospace
    /// Bit 0: CT spacing (0)
    /// </summary>
    public byte BuildModeRegister()
    {
        byte mode = 0x40; // Bit 6 = paddle echoback always on
        mode |= (byte)((int)KeyMode << 4);
        if (PaddleSwap) mode |= 0x08;
        if (Autospace) mode |= 0x02;
        return mode;
    }
}
