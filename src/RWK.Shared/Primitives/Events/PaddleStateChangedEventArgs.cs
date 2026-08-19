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
/// Reports an accepted (debounced) paddle contact transition together with the QPC
/// timestamp taken at the moment of detection (1.3, 1.5).
/// </summary>
/// <remarks>
/// All three contact states are reported on every transition, not just the one that
/// changed, so the keyer never has to reconstruct paddle state from a partial update.
/// <para>
/// Contact mapping is CTS to dit, DSR to dah, DCD to straight key (1.2).
/// </para>
/// _Requirements: 1.3, 1.5_
/// </remarks>
/// <param name="QpcTimestamp">
/// Raw QueryPerformanceCounter tick count captured when the transition was detected.
/// </param>
/// <param name="DitPressed">Dit contact closed (CTS asserted).</param>
/// <param name="DahPressed">Dah contact closed (DSR asserted).</param>
/// <param name="StraightKeyPressed">Straight key contact closed (DCD asserted).</param>
public record PaddleStateChangedEventArgs(
    long QpcTimestamp,
    bool DitPressed,
    bool DahPressed,
    bool StraightKeyPressed
);
