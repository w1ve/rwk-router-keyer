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
/// One radio the broker knows about, as extracted from a discovery payload by
/// <see cref="IDiscoveryPayloadCodec.TryParse"/>.
/// </summary>
/// <param name="Serial">
/// The radio's serial number, carried in the discovery payload. This is the stable
/// identity key for the per-radio tracking table (15.16).
/// </param>
/// <param name="Model">Model string as advertised by the radio, for UI display (13.18).</param>
/// <param name="StationAddress">The address the radio advertised on the Station's local network.</param>
/// <param name="StationCommandPort">The command port the radio advertised.</param>
/// <param name="LastSeenUtc">When the most recent report for this radio was observed.</param>
/// <param name="AdvertisedLocalEndpoint">
/// The Client-side endpoint substituted into the payload by the rewrite, or <c>null</c>
/// while the radio is not being advertised.
/// </param>
/// <remarks>
/// This record is the whole vocabulary the rest of the system has for a discovery payload:
/// identity, model, advertised endpoint. It carries no field offsets, encodings, or
/// ordering — all layout knowledge lives inside the single
/// <see cref="IDiscoveryPayloadCodec"/> implementation.
/// <para>
/// <see cref="StationAddress"/> and <see cref="StationCommandPort"/> are the values the
/// radio itself announced. They are unreachable from the Client, which is exactly why the
/// emitter must rewrite them before broadcasting (15.5).
/// </para>
/// _Requirements: 15.16, 15.17_
/// </remarks>
public record DiscoveredRadio(
    string Serial,
    string Model,
    IPAddress StationAddress,
    int StationCommandPort,
    DateTime LastSeenUtc,
    IPEndPoint? AdvertisedLocalEndpoint);
