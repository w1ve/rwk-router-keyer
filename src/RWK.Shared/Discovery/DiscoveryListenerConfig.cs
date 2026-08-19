/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Discovery;

/// <summary>
/// Runtime settings handed to <see cref="IDiscoveryListener.Start"/>.
/// </summary>
/// <param name="ListenPort">
/// UDP port the discovery socket binds. Supplied by the caller from the Station profile —
/// this type holds no default, because the correct value is a property of the FlexRadio
/// discovery protocol and is provisional until the captured fixture confirms it. The
/// profile field that carries it is marked <c>[VERIFY]</c>.
/// </param>
/// <param name="BindAddress">
/// Local address to bind, or <c>null</c> for any local interface on the Station LAN.
/// </param>
/// <param name="ReuseAddress">
/// Whether to set <c>SO_REUSEADDR</c> so a SmartSDR instance running at the Station keeps
/// receiving the same broadcasts. Defaults to <c>true</c>; brokering should not take a
/// working local SmartSDR away from the station owner.
/// </param>
/// <remarks>
/// Runtime configuration rather than persisted configuration: the listener is handed these
/// values each time it starts, which is only while the Station-side capture control is on
/// (15.6, 15.7).
/// <para>
/// _Requirements: 15.1, 15.6, 15.7_
/// </para>
/// </remarks>
public record DiscoveryListenerConfig(
    int ListenPort,
    string? BindAddress,
    bool ReuseAddress = true);
