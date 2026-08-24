/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text.Json.Serialization;

namespace RWK.Client.Wizard;

/// <summary>
/// The output JSON profile saved by the Wizard (§4 of the spec).
/// Portable artifact for backup, sharing, or reloading on another PC.
/// </summary>
public sealed class WizardProfile
{
    [JsonPropertyName("rwkProfileVersion")]
    public int RwkProfileVersion { get; set; } = 1;

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = "RWK Wizard 1.0";

    [JsonPropertyName("createdUtc")]
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("profile")]
    public ProfileInfo Profile { get; set; } = new();

    [JsonPropertyName("setupNotes")]
    public SetupNotes SetupNotes { get; set; } = new();

    [JsonPropertyName("forwards")]
    public List<ProfileForwardRule> Forwards { get; set; } = new();

    [JsonPropertyName("serialBridge")]
    public SerialBridgeInfo? SerialBridge { get; set; }
}

/// <summary>
/// Metadata about the profile (what radio/path it was generated for).
/// </summary>
public sealed class ProfileInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("catalogId")]
    public string CatalogId { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "unverified";
}

/// <summary>
/// Human-readable setup notes organized by where the operator needs to act.
/// </summary>
public sealed class SetupNotes
{
    [JsonPropertyName("client")]
    public List<string> Client { get; set; } = new();

    [JsonPropertyName("station")]
    public List<string> Station { get; set; } = new();

    [JsonPropertyName("radio")]
    public List<string> Radio { get; set; } = new();

    [JsonPropertyName("virtualSerial")]
    public List<string> VirtualSerial { get; set; } = new();
}

/// <summary>
/// Serial bridge configuration metadata, included in the profile when the
/// generic serial bridge entry is used.
/// </summary>
public sealed class SerialBridgeInfo
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("tcpPort")]
    public int TcpPort { get; set; }

    [JsonPropertyName("clientComPort")]
    public int ClientComPort { get; set; }

    [JsonPropertyName("stationComPort")]
    public string StationComPort { get; set; } = "";

    [JsonPropertyName("baudRate")]
    public int BaudRate { get; set; } = 9600;

    [JsonPropertyName("dataBits")]
    public int DataBits { get; set; } = 8;

    [JsonPropertyName("parity")]
    public string Parity { get; set; } = "None";

    [JsonPropertyName("stopBits")]
    public int StopBits { get; set; } = 1;

    [JsonPropertyName("dtrControl")]
    public string DtrControl { get; set; } = "Off";

    [JsonPropertyName("rtsControl")]
    public string RtsControl { get; set; } = "Off";

    [JsonPropertyName("presetName")]
    public string PresetName { get; set; } = "Generic";
}

/// <summary>
/// A single forward rule in the profile output (§4.1 field mapping).
/// </summary>
public sealed class ProfileForwardRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "TCP";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("bindAddress")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [JsonPropertyName("clientPort")]
    public int ClientPort { get; set; }

    [JsonPropertyName("stationTarget")]
    public string StationTarget { get; set; } = "127.0.0.1";

    [JsonPropertyName("stationPort")]
    public int StationPort { get; set; }

    [JsonPropertyName("portIdentity")]
    public string PortIdentity { get; set; } = "floating";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "generic";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "ClientToStation";

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
