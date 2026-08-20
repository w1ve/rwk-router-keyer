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

namespace RWK.Shared.Config;

/// <summary>
/// The persisted Station profile (12.5).
/// </summary>
/// <remarks>
/// Every property has a usable default so that a missing or corrupt profile can be
/// replaced by <c>new StationConfig()</c> and saved back (12.6). Secrets inside
/// <see cref="Tailscale"/> are DPAPI-protected by the configuration store (12.2).
/// <para>
/// _Requirements: 12.5, 15.6, 15.14_
/// </para>
/// </remarks>
public record StationConfig
{
    /// <summary>Serial port used to key the radio, or <see langword="null"/> if unset (8.1).</summary>
    public string? KeyingPortName { get; init; }

    /// <summary>Control line asserted for key-down. Default RTS (8.1).</summary>
    public KeyingLine KeyLine { get; init; } = KeyingLine.RTS;

    /// <summary>
    /// Control line asserted for PTT. Default DTR (8.2). Set explicitly rather than
    /// relying on <c>default(KeyingLine)</c>, which is DTR for v1 compatibility.
    /// </summary>
    public KeyingLine PttLine { get; init; } = KeyingLine.DTR;

    /// <summary>Inverts the key line's polarity (8.3).</summary>
    public bool KeyInvert { get; init; }

    /// <summary>Inverts the PTT line's polarity (8.3).</summary>
    public bool PttInvert { get; init; }

    /// <summary>Jitter buffer delays and adaptive mode (7.1).</summary>
    public JitterBufferConfig JitterBuffer { get; init; } = JitterBufferConfig.Default;

    /// <summary>PTT lead and tail timing (8.4, 8.5).</summary>
    public PttTimingConfig PttTiming { get; init; } = new();

    /// <summary>Tailscale node and pairing settings (5.2, 11.1).</summary>
    public TailscaleConfig Tailscale { get; init; } = new();

    /// <summary>
    /// Station owner decisions about Client-pushed forward rules: allow/deny and target
    /// host overrides (10.9, 10.10).
    /// </summary>
    public ImmutableList<ForwardRuleOverride> ForwardOverrides { get; init; } =
        ImmutableList<ForwardRuleOverride>.Empty;

    // ---- FlexRadio discovery capture (Requirement 15) ----

    /// <summary>
    /// Enables capture of FlexRadio discovery broadcasts on the Station's local network.
    /// Defaults to <see langword="false"/>: discovery is disabled by default on both ends
    /// (15.6). While off, the listener stays stopped and its socket released (15.7).
    /// </summary>
    public bool DiscoveryCaptureEnabled { get; init; }

    // ---- Logger WinKeyer Input ----

    /// <summary>
    /// Enables a secondary WK2 protocol host on <see cref="LoggerPortName"/> that accepts
    /// CW macros from logging software running on the Station PC (e.g. N1MM+ via RDP).
    /// When active, logger CW takes priority over remote paddle edges.
    /// </summary>
    public bool LoggerInputEnabled { get; init; }

    /// <summary>
    /// Serial port (real or virtual) on which the logger WK2 host listens.
    /// Must differ from <see cref="KeyingPortName"/>.
    /// </summary>
    public string? LoggerPortName { get; init; }

    /// <summary>
    /// UDP port the discovery listener binds to on the Station's local network.
    /// <para>
    /// <c>[VERIFY]</c> — provisional. The FlexRadio discovery broadcast port is not established
    /// fact and is confirmed against the datagram captured from physical hardware
    /// (task 27.1, 15.20).
    /// </para>
    /// </summary>
    public int DiscoveryListenPort { get; init; } = 4992;

    /// <summary>
    /// Local address the discovery listener binds to. <see langword="null"/> means any local
    /// interface on the Station LAN.
    /// </summary>
    public string? DiscoveryBindAddress { get; init; }

    /// <summary>
    /// Radio expiry window used by the discovery tracking table. Default 10 seconds (15.14);
    /// mirrors <c>ClientConfig.DiscoveryExpiryInterval</c> so each end can be configured
    /// independently.
    /// </summary>
    public TimeSpan DiscoveryExpiryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Builds the keying output configuration for the Station's serial keyer from the
    /// port and line settings above. Returns <see langword="null"/> while no keying port
    /// has been chosen.
    /// </summary>
    public KeyingOutputConfig? ToKeyingOutputConfig()
        => string.IsNullOrWhiteSpace(KeyingPortName)
            ? null
            : new KeyingOutputConfig(KeyingPortName, KeyLine, PttLine, KeyInvert, PttInvert);
}
