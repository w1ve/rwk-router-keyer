/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;

namespace RWK.Station.Replay;

/// <summary>
/// The seam through which the Edge Replayer hands a detected fail-safe condition to the fail-safe
/// monitor (tasks 12.1 - 12.6).
/// </summary>
/// <remarks>
/// The replayer's own responsibility on any condition is unconditional and already done by the time
/// this is called: key and PTT have been forced up. What remains is policy — whether the SAFE latch
/// is set, whether the session is merely degraded, whether it is closed, how the latch clears
/// (9.11, 9.12) — and that policy is the monitor's, not the replayer's.
/// <para>
/// A sink may act on the replayer through <see cref="IEdgeReplayer.LatchSafe"/> and
/// <see cref="IEdgeReplayer.ClearSafeLatch"/>. It must not block: the call arrives on the
/// TIME_CRITICAL replay thread, so anything slow belongs on the monitor's own thread.
/// </para>
/// <para>
/// _Requirements: 9.11, 9.12_
/// </para>
/// </remarks>
public interface IFailSafeSink
{
    /// <summary>
    /// Reports that <paramref name="condition"/> fired. Key output has already been forced up.
    /// </summary>
    /// <param name="condition">Which of the ten enumerated conditions fired.</param>
    /// <param name="message">Human-readable detail for the log and the Station UI.</param>
    void OnFailSafe(FailSafeCondition condition, string message);
}
