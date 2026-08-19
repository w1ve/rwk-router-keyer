/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared;

/// <summary>
/// A single key-state transition produced by the SoftKeyer core, timestamped with
/// QueryPerformanceCounter at the moment the transition was decided (3.8).
/// </summary>
/// <remarks>
/// This is the in-process keyer event, consumed by the sidetone engine and by the
/// edge frame builder. It is deliberately distinct from the on-the-wire edge entry
/// defined by the RWK-PADDLE frame format (Requirement 6): the wire form uses a
/// sequence number and a session-relative millisecond timestamp, whereas this type
/// carries a raw local QPC tick count and never leaves the Client process.
/// <para>
/// _Requirements: 3.8_
/// </para>
/// </remarks>
/// <param name="QpcTimestamp">
/// Raw QueryPerformanceCounter tick count at the transition. Ticks, not milliseconds:
/// scale by the QPC frequency to convert.
/// </param>
/// <param name="KeyDown"><see langword="true"/> for key-down, <see langword="false"/> for key-up.</param>
/// <param name="Source">The input path that produced this edge.</param>
public record EdgeEvent(
    long QpcTimestamp,
    bool KeyDown,
    EdgeSource Source
);
