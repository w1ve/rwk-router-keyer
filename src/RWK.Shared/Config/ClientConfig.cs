/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Collections.Immutable;
using RWK.Shared.IO;

namespace RWK.Shared.Config;

/// <summary>
/// The persisted Client profile (12.4).
/// </summary>
/// <remarks>
/// Every property has a usable default so that a missing or corrupt profile can be
/// replaced by <c>new ClientConfig()</c> and saved back (12.6). Secrets inside
/// <see cref="Tailscale"/> are DPAPI-protected by the configuration store (12.2).
/// <para>
/// _Requirements: 12.4, 15.6, 15.14_
/// </para>
/// </remarks>
public record ClientConfig
{
    /// <summary>Serial port the paddle is wired to, or <see langword="null"/> if unset (1.1).</summary>
    public string? PaddlePortName { get; init; }

    /// <summary>Serial port the logging software opens as a WinKeyer, or <see langword="null"/> (2.1).</summary>
    public string? WinKeyerPortName { get; init; }

    /// <summary>
    /// WinKeyer operating mode: LoggerApp (RWK emulates WK2 for logging software) or
    /// HardwareWinKey (RWK drives a physical K1EL WinKeyer chip). Default LoggerApp.
    /// </summary>
    public WinKeyerMode WinKeyerMode { get; init; } = WinKeyerMode.LoggerApp;

    /// <summary>Keyer speed in words per minute. Default 25; supported range 5-60 (3.10).</summary>
    public int SpeedWpm { get; init; } = 25;

    /// <summary>Element weight as a percentage. Default 50; supported range 25-75 (3.9).</summary>
    public int Weight { get; init; } = 50;

    /// <summary>Swaps the dit and dah paddle contacts.</summary>
    public bool PaddleReverse { get; init; }

    /// <summary>Keyer mode. Default Iambic B (3.1).</summary>
    public KeyerMode KeyerMode { get; init; } = KeyerMode.IambicB;

    /// <summary>Paddle contact debounce window. Default 5ms (1.4).</summary>
    public TimeSpan DebounceTime { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Local sidetone settings (4.3, 4.5, 4.6).</summary>
    public SidetoneConfig Sidetone { get; init; } = new();

    /// <summary>Tailscale node and pairing settings (5.2, 11.3).</summary>
    public TailscaleConfig Tailscale { get; init; } = new();

    /// <summary>
    /// Port forwarding rules, restored on startup (10.7). Each rule carries its own
    /// bind address, which defaults to loopback (10.11, 10.12).
    /// </summary>
    public ImmutableList<ForwardRule> ForwardRules { get; init; } = ImmutableList<ForwardRule>.Empty;

    // ---- FlexRadio discovery re-emission (Requirement 15) ----

    /// <summary>
    /// Enables re-emission of Station-forwarded discovery payloads onto the Client's local
    /// network. Defaults to <see langword="false"/>: discovery is disabled by default on both
    /// ends (15.6), and a radio is advertised only while this control and the Station-side
    /// capture control are both on (15.9). While off, the emitter stays stopped and forwarded
    /// payloads are discarded (15.8).
    /// </summary>
    public bool DiscoveryEmitEnabled { get; init; }

    /// <summary>
    /// How long a radio may go without a report from the Station before the emitter stops
    /// broadcasting it and drops it from the advertised list. Default 10 seconds (15.14).
    /// </summary>
    public TimeSpan DiscoveryExpiryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// UDP port the rewritten discovery payload is broadcast to on the Client's local network;
    /// must match the port SmartSDR listens on.
    /// <para>
    /// <c>[VERIFY]</c> — provisional. The FlexRadio discovery port is not established fact and
    /// is confirmed against the datagram captured from physical hardware (task 27.1, 15.20).
    /// </para>
    /// </summary>
    public int DiscoveryBroadcastPort { get; init; } = 4992;

    /// <summary>
    /// Destination address for the re-broadcast: the limited broadcast address, or a
    /// subnet-directed broadcast address for the Client's local network.
    /// <para>
    /// <c>[VERIFY]</c> — provisional alongside <see cref="DiscoveryBroadcastPort"/> until the
    /// captured fixture from task 27.1 exists.
    /// </para>
    /// </summary>
    public string DiscoveryBroadcastAddress { get; init; } = "255.255.255.255";

    // ---- PTT Input (footswitch + hotkey) ----

    /// <summary>
    /// Serialized PTT hotkey combo (format: "KeyCode|Ctrl|Shift|Alt").
    /// Null or empty means no hotkey configured.
    /// </summary>
    public string? PttHotKey { get; init; }

    /// <summary>
    /// COM port name for the PTT footswitch input, or null/empty if not used.
    /// </summary>
    public string? PttInputPortName { get; init; }

    /// <summary>
    /// Which serial line the footswitch asserts: "DTR" (read as DSR) or "RTS" (read as CTS).
    /// Default "DTR".
    /// </summary>
    public string PttInputLine { get; init; } = "DTR";


}
