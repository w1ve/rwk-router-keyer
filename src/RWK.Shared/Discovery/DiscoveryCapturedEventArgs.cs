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
/// One discovery datagram captured on the Station's local network.
/// </summary>
/// <param name="Radio">Identity and advertised endpoint parsed out of the payload.</param>
/// <param name="RawPayload">
/// The captured datagram, verbatim and unmodified. The Client — not the Station —
/// performs the endpoint rewrite, so the bytes must travel intact (15.2, 15.4).
/// </param>
/// <param name="CapturedUtc">When the Station received the datagram.</param>
/// <remarks>
/// <paramref name="RawPayload"/> is a read-only view so handlers cannot mutate the
/// captured bytes. Its contents are opaque here: nothing outside the codec interprets
/// them.
/// <para>
/// _Requirements: 15.1, 15.2_
/// </para>
/// </remarks>
public record DiscoveryCapturedEventArgs(
    DiscoveredRadio Radio,
    ReadOnlyMemory<byte> RawPayload,
    DateTime CapturedUtc);
