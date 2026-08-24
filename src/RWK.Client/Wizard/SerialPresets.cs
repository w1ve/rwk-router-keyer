/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Client.Wizard;

/// <summary>
/// Serial port parameter presets for common ham radio CAT protocols.
/// Used by the serial bridge sub-flow to pre-fill baud/bits/parity/stop/DTR/RTS
/// based on the radio type the operator selects.
/// </summary>
public sealed class SerialPreset
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public int StopBits { get; init; } = 1;
    public string DtrControl { get; init; } = "Off";
    public string RtsControl { get; init; } = "Off";
    public string Notes { get; init; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// The built-in presets for serial bridge configuration.
/// </summary>
public static class SerialPresets
{
    public static IReadOnlyList<SerialPreset> All { get; } = new List<SerialPreset>
    {
        new()
        {
            Name = "Icom CI-V (modern — IC-7300, IC-7610, IC-9700)",
            Description = "19200 baud, 8N1, no handshake",
            BaudRate = 19200,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "CI-V is open-collector, no flow control. DTR/RTS should be Off."
        },
        new()
        {
            Name = "Icom CI-V (older — IC-746, IC-756, IC-7000)",
            Description = "9600 baud, 8N1, no handshake",
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "Older Icom radios default to 9600. Check your radio's CI-V baud setting."
        },
        new()
        {
            Name = "Kenwood (TS-890S, TS-990S)",
            Description = "115200 baud, 8N1, no handshake",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "TS-890/990 use 115200 by default on their rear COM port."
        },
        new()
        {
            Name = "Kenwood (TS-590S/SG, TS-480)",
            Description = "9600 baud, 8N1, no handshake",
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "Older Kenwood radios use 9600. Verify in the radio's MENU."
        },
        new()
        {
            Name = "Yaesu CAT (FTDX101, FT-991A, FT-710, FT-DX10)",
            Description = "38400 baud, 8N1, no handshake",
            BaudRate = 38400,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "Most modern Yaesu use 38400. Some older models (FT-857, FT-897) use 4800 or 9600."
        },
        new()
        {
            Name = "Yaesu CAT (older — FT-857, FT-897, FT-817)",
            Description = "4800 baud, 8N2, no handshake",
            BaudRate = 4800,
            DataBits = 8,
            Parity = "None",
            StopBits = 2,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "FT-857/897/817 use 4800 8N2. Note: 2 stop bits."
        },
        new()
        {
            Name = "Elecraft K-line (K3, K3S, KX3, KX2)",
            Description = "38400 baud, 8N1, no handshake",
            BaudRate = 38400,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "Elecraft K-line protocol at 38400."
        },
        new()
        {
            Name = "Elecraft K4 (RS-232 CAT port)",
            Description = "115200 baud, 8N1, no handshake",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "K4 rear-panel RS-232 runs at 115200."
        },
        new()
        {
            Name = "Generic (custom settings)",
            Description = "9600 baud, 8N1 — edit all fields below",
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = 1,
            DtrControl = "Off",
            RtsControl = "Off",
            Notes = "Set the parameters to match your device."
        }
    };

    /// <summary>Standard baud rates for the dropdown.</summary>
    public static readonly int[] BaudRates = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

    /// <summary>Parity options.</summary>
    public static readonly string[] ParityOptions = { "None", "Even", "Odd", "Mark", "Space" };

    /// <summary>DTR/RTS control options.</summary>
    public static readonly string[] HandshakeOptions = { "Off", "On", "Handshake" };
}
