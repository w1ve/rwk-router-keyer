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
/// Reports a change in the embedded Tailscale node's connection state, carrying the
/// path metrics that the UI displays and that the jitter buffer depends on.
/// </summary>
/// <remarks>
/// The design names this type on <c>ITailscaleNode.StateChanged</c> without giving its
/// members; the members here mirror the node's observable properties (5.3, 5.4, 5.5) so
/// a subscriber never has to read the node back to learn what changed. A
/// <see cref="TailscaleState.Fault"/> value is what 5.8 requires on path loss, and it is
/// what drives fail-safe F9 on the Station (9.9).
/// <para>
/// _Requirements: 5.3, 5.4, 5.5, 5.8, 13.1_
/// </para>
/// </remarks>
/// <param name="State">The new connection state.</param>
/// <param name="Path">
/// Current path type. <see cref="PathType.None"/> whenever no path exists, including in
/// the <see cref="TailscaleState.Fault"/> case.
/// </param>
/// <param name="RoundTripTime">
/// Most recent measured round-trip time, or <see cref="TimeSpan.Zero"/> when unmeasured.
/// </param>
/// <param name="DerpRegion">
/// DERP region identifier when <paramref name="Path"/> is <see cref="PathType.Derp"/>,
/// otherwise <see langword="null"/> (5.5).
/// </param>
/// <param name="Message">
/// Optional human-readable detail; used to name the cause of a
/// <see cref="TailscaleState.Fault"/>.
/// </param>
public record TailscaleStateChangedEventArgs(
    TailscaleState State,
    PathType Path,
    TimeSpan RoundTripTime,
    string? DerpRegion = null,
    string? Message = null
);
