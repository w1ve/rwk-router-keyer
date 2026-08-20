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
/// Root document of the radios.json catalog file.
/// </summary>
public sealed class RadioCatalog
{
    [JsonPropertyName("catalogVersion")]
    public int CatalogVersion { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("entries")]
    public List<CatalogEntry> Entries { get; set; } = new();
}

/// <summary>
/// One radio/service entry in the catalog. Describes a specific control path
/// (e.g. "RS-BA1 direct to radio LAN port") with its port forwards, prompts,
/// and checklist notes.
/// </summary>
public sealed class CatalogEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();

    [JsonPropertyName("software")]
    public string? Software { get; set; }

    /// <summary>
    /// Where the endpoint lives relative to the Station.
    /// Values: "lan", "station-pc".
    /// </summary>
    [JsonPropertyName("endpointLocation")]
    public string EndpointLocation { get; set; } = "lan";

    /// <summary>
    /// Confidence level: "verified", "community", "unverified".
    /// </summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "unverified";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// If true, the FlexRadio discovery relay must be enabled on both sides.
    /// </summary>
    [JsonPropertyName("requiresDiscoveryRelay")]
    public bool RequiresDiscoveryRelay { get; set; }

    /// <summary>
    /// If true, this entry is an ancillary service (Step 4 extra), not a radio.
    /// </summary>
    [JsonPropertyName("isService")]
    public bool IsService { get; set; }

    /// <summary>
    /// If true, this is the generic RS-232 serial bridge entry with sub-flow prompts.
    /// </summary>
    [JsonPropertyName("isGenericSerial")]
    public bool IsGenericSerial { get; set; }

    /// <summary>
    /// If true, this is a generic TCP/UDP service (fully user-configured).
    /// </summary>
    [JsonPropertyName("isGenericService")]
    public bool IsGenericService { get; set; }

    /// <summary>
    /// Override bind address for all rules in this entry (e.g. "0.0.0.0" for RemoteRig).
    /// Null means default 127.0.0.1.
    /// </summary>
    [JsonPropertyName("bindAddress")]
    public string? BindAddress { get; set; }

    /// <summary>
    /// Port forward definitions for this entry.
    /// </summary>
    [JsonPropertyName("forwards")]
    public List<ForwardDef> Forwards { get; set; } = new();

    /// <summary>
    /// Prompts keyed by input name (e.g. "stationTarget", "basePort").
    /// </summary>
    [JsonPropertyName("prompts")]
    public Dictionary<string, PromptDef> Prompts { get; set; } = new();

    [JsonPropertyName("clientNotes")]
    public List<string> ClientNotes { get; set; } = new();

    [JsonPropertyName("stationNotes")]
    public List<string> StationNotes { get; set; } = new();

    [JsonPropertyName("radioNotes")]
    public List<string> RadioNotes { get; set; } = new();

    /// <summary>Display string for list controls: "Vendor — DisplayName".</summary>
    public override string ToString() => $"{Vendor} — {DisplayName}";
}

/// <summary>
/// A single port forward definition within a catalog entry.
/// </summary>
public sealed class ForwardDef
{
    [JsonPropertyName("proto")]
    public string Proto { get; set; } = "TCP";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "generic";

    /// <summary>
    /// Port identity constraint: "required", "floating", "unknown".
    /// </summary>
    [JsonPropertyName("portIdentity")]
    public string PortIdentity { get; set; } = "unknown";

    /// <summary>
    /// If true, the port cannot be changed at the device (stronger than portIdentity: required).
    /// </summary>
    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Explanatory prompt definition for a user input (§2.4 of the spec).
/// </summary>
public sealed class PromptDef
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>What this value does and where it ends up.</summary>
    [JsonPropertyName("why")]
    public string Why { get; set; } = "";

    /// <summary>Where to find the value (model-specific menu paths, etc.).</summary>
    [JsonPropertyName("howToFind")]
    public string HowToFind { get; set; } = "";

    /// <summary>The symptom of getting this value wrong.</summary>
    [JsonPropertyName("ifWrong")]
    public string IfWrong { get; set; } = "";
}
