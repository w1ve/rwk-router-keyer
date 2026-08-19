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
/// Operating state of the Station edge replayer.
/// </summary>
/// <remarks>
/// The design names this type on <c>IEdgeReplayer.State</c> but does not enumerate its
/// members; these members are derived from the fail-safe behavior of Requirement 9.
/// <see cref="SafeLatched"/> and <see cref="Degraded"/> map onto the two latch classes:
/// a latch requiring manual Re-Arm (9.11) versus a degraded session that clears itself
/// when valid edges resume (9.12).
/// <para>
/// _Requirements: 9.11, 9.12, 13.5, 13.6, 13.7_
/// </para>
/// </remarks>
public enum EdgeReplayerState
{
    /// <summary>Not started, or stopped. No keying thread running.</summary>
    Stopped = 0,

    /// <summary>Started and armed, with no edge traffic being scheduled.</summary>
    Idle = 1,

    /// <summary>Scheduling and replaying edges normally.</summary>
    Active = 2,

    /// <summary>
    /// Running but impaired by F1 or F9. Key is forced up; normal operation resumes
    /// automatically when valid edges return (9.1, 9.9, 9.12).
    /// </summary>
    Degraded = 3,

    /// <summary>
    /// SAFE latch set by F2, F5, F6, F7, or F10. Key output stays locked until the
    /// Station owner performs a manual Re-Arm (9.11, 13.6, 13.8).
    /// </summary>
    SafeLatched = 4
}
