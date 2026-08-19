/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Net;

namespace RWK.Shared.Discovery;

/// <summary>
/// One entry of the Client emitter's per-radio table: a tracked radio plus its current
/// advertisability decision.
/// </summary>
/// <param name="Radio">The radio identity and the endpoint it advertised at the Station.</param>
/// <param name="State">Whether the radio is being broadcast, and if not, why (15.11, 15.14, 15.17).</param>
/// <param name="AdvertisedLocalEndpoint">
/// The Client-side endpoint SmartSDR is told to connect to — the bind address and Client
/// port of the enabled command-channel forward rule. <c>null</c> whenever
/// <paramref name="State"/> is not <see cref="RadioAdvertiseState.Advertising"/>.
/// </param>
/// <param name="LastBroadcastUtc">When this radio's rewritten payload was last broadcast.</param>
/// <param name="WithheldReason">
/// UI text naming what is missing: the absent command-channel rule (15.11), the rewrite
/// failure reason (15.17), or the advisory that data and stream rules are absent or
/// disabled so the radio is unusable (15.12). <c>null</c> when nothing needs saying.
/// </param>
/// <remarks>
/// The advisory in 15.12 does not withhold the radio, so a
/// <see cref="RadioAdvertiseState.Advertising"/> entry can still carry a
/// <paramref name="WithheldReason"/>: the radio is listed in SmartSDR but is not usable
/// until its data and stream rules are enabled.
/// <para>
/// _Requirements: 13.18, 13.20, 15.11, 15.12, 15.14_
/// </para>
/// </remarks>
public record AdvertisedRadio(
    DiscoveredRadio Radio,
    RadioAdvertiseState State,
    IPEndPoint? AdvertisedLocalEndpoint,
    DateTime LastBroadcastUtc,
    string? WithheldReason);
