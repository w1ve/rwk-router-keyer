/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Station.Replay;

/// <summary>
/// How much freedom the Station has when choosing its jitter buffer delay. Declared by the
/// Tailscale sidecar as <c>edge.jitterProfile</c> in its status document; the Station reads it
/// rather than assuming the edge path is true UDP.
/// </summary>
/// <remarks>
/// Design Component 13 and ADR 0001: the sidecar publishes <c>edge.transport</c> and
/// <c>edge.jitterProfile</c> so the buffer profile follows an observed fact instead of an
/// assumption. If a future sidecar change ever loses datagram fidelity, the declaration flips to
/// <c>tcp</c> / <c>DerpClassOnly</c> and this enum is the mechanism that keeps the Station honest.
/// <para>
/// _Requirements: 7.1_
/// </para>
/// </remarks>
public enum EdgeJitterProfile
{
    /// <summary>
    /// The Station may pick its delay band from the observed path type: the direct band on a
    /// direct path, the DERP band on a relayed one (7.1). Reported when the edge transport
    /// preserves datagram fidelity.
    /// </summary>
    PathAdaptive = 0,

    /// <summary>
    /// The Station must use the DERP-class band at all times, even on a direct path. Reported
    /// when the edge path is not true UDP, so datagram timing cannot be trusted to the tighter
    /// direct band.
    /// </summary>
    DerpClassOnly = 1
}

/// <summary>
/// Maps the sidecar's declared profile string onto <see cref="EdgeJitterProfile"/>.
/// </summary>
/// <remarks>
/// Task 14.2 supplies the status document; this parser exists so that the replayer takes the
/// declaration as an input from the start rather than hardcoding a profile.
/// </remarks>
public static class EdgeJitterProfiles
{
    /// <summary>The sidecar's string for <see cref="EdgeJitterProfile.PathAdaptive"/>.</summary>
    public const string PathAdaptiveDeclaration = "PathAdaptive";

    /// <summary>The sidecar's string for <see cref="EdgeJitterProfile.DerpClassOnly"/>.</summary>
    public const string DerpClassOnlyDeclaration = "DerpClassOnly";

    /// <summary>
    /// Parses a declared profile. An unrecognized, empty, or missing declaration resolves to
    /// <see cref="EdgeJitterProfile.DerpClassOnly"/>.
    /// </summary>
    /// <remarks>
    /// Unknown resolves to the conservative profile deliberately: the failure direction is a
    /// longer buffer, which costs latency, rather than a shorter one, which costs timing fidelity
    /// on a path whose behavior the Station has not been told about.
    /// </remarks>
    public static EdgeJitterProfile FromDeclaration(string? declaration)
        => declaration switch
        {
            PathAdaptiveDeclaration => EdgeJitterProfile.PathAdaptive,
            DerpClassOnlyDeclaration => EdgeJitterProfile.DerpClassOnly,
            _ => EdgeJitterProfile.DerpClassOnly,
        };
}
