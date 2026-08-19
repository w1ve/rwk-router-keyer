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
/// How traffic currently reaches the peer (5.3).
/// </summary>
/// <remarks>
/// Path type selects the jitter buffer delay band: a direct path uses the shorter
/// band (default 60ms) and a DERP-relayed path the longer one (default 200ms), per
/// 7.1. It is also surfaced in the Client UI (13.1).
/// <para>
/// _Requirements: 5.3, 5.5, 7.1, 13.1_
/// </para>
/// </remarks>
public enum PathType
{
    /// <summary>No path established — nothing is reachable.</summary>
    None = 0,

    /// <summary>Direct peer-to-peer path.</summary>
    Direct = 1,

    /// <summary>
    /// Relayed via a DERP server. The region identifier is reported separately (5.5).
    /// </summary>
    Derp = 2
}
