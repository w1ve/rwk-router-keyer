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
/// The keying mode used by the SoftKeyer core when translating paddle contacts
/// into Morse elements (3.1).
/// </summary>
/// <remarks>
/// Declared in the root <c>RWK.Shared</c> namespace so every sub-namespace
/// (<c>RWK.Shared.IO</c>, <c>RWK.Shared.Config</c>, <c>RWK.Shared.Protocol</c>, ...)
/// resolves it without a using directive.
/// <para>
/// Values are explicit and MUST NOT be renumbered: keyer mode is persisted in
/// configuration and carried in the WK2 mode register mapping.
/// </para>
/// _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_
/// </remarks>
public enum KeyerMode
{
    /// <summary>
    /// Iambic B: squeezing both paddles produces alternating dits and dahs; the
    /// current element completes before the alternate paddle is examined, so a
    /// release during an element still yields the queued opposite element (3.2).
    /// </summary>
    IambicB = 0,

    /// <summary>
    /// Iambic A: squeezing both paddles produces alternating elements, but
    /// alternation ceases when the paddles are released during an element (3.3).
    /// </summary>
    IambicA = 1,

    /// <summary>
    /// Ultimatic: in a squeeze, the element of the last paddle pressed repeats (3.4).
    /// </summary>
    Ultimatic = 2,

    /// <summary>
    /// Bug: dits are generated automatically, the dah paddle is passed straight
    /// through so the operator times dahs manually (3.5).
    /// </summary>
    Bug = 3,

    /// <summary>
    /// Straight key: the straight-key contact is passed directly to key output with
    /// no element generation (3.6).
    /// </summary>
    Straight = 4
}
