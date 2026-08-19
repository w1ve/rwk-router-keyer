/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Protocol.Edge;

/// <summary>
/// Classification of a received edge by <see cref="EdgeSequenceTracker"/>
/// (Requirements 6.5, 6.6, 6.7).
/// </summary>
/// <remarks>
/// The tracker only classifies. Mapping an outcome onto a key-up, a SAFE latch, or a
/// discard is the Edge_Replayer's job, so that no component re-derives the rules.
/// The mapping is fixed and is stated on each member below.
/// </remarks>
public enum EdgeValidationOutcome
{
    /// <summary>
    /// The edge is in order: it is the first edge of the session, or its sequence is
    /// exactly one past the last applied edge, and its timestamp does not go backwards.
    /// The tracker has applied it. The replayer schedules it; no fail-safe.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// The edge has already been seen: its sequence is at or below the last applied
    /// sequence (Requirement 6.6). The tracker did not apply it.
    /// <para>
    /// This is the common case, not an error. Requirement 6.4 puts the current edge plus
    /// up to three previous edges in every frame, so most arriving edges are redundant
    /// copies. The replayer discards them quietly: no log spam, no fail-safe.
    /// </para>
    /// </summary>
    DuplicateDiscarded = 1,

    /// <summary>
    /// The frame's epoch does not match the current session epoch (Requirement 6.5).
    /// The tracker applied nothing and none of the frame's edges were examined.
    /// Maps to fail-safe <see cref="FailSafeCondition.F4"/>: discard the whole frame and
    /// force key-up if currently keyed (Requirement 9.4). No SAFE latch.
    /// </summary>
    EpochMismatch = 2,

    /// <summary>
    /// The edge's sequence is more than one past the last applied sequence, so at least
    /// one edge never arrived. Inspect <see cref="EdgeValidationResult.CanInferState"/>:
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>true</c> — the resulting key state is known to be safe, so the tracker applied
    ///   the edge and the replayer schedules it normally. No fail-safe.
    ///   </description></item>
    ///   <item><description>
    ///   <c>false</c> — the tracker did not apply the edge. Maps to fail-safe
    ///   <see cref="FailSafeCondition.F5"/>: force key-up and set the SAFE latch
    ///   (Requirement 9.5).
    ///   </description></item>
    /// </list>
    /// </summary>
    SequenceGap = 3,

    /// <summary>
    /// The edge carries a new sequence but a timestamp earlier than the last applied
    /// edge's, breaking the monotonicity Requirement 6.7 guarantees within a session.
    /// The tracker did not apply it.
    /// <para>
    /// Requirement 9 enumerates no fail-safe for this case, because within one epoch the
    /// Client cannot produce it: the timestamp and the sequence advance together. Seeing
    /// it means the stream is corrupt or foreign, so the replayer treats it exactly like
    /// an uninferable <see cref="SequenceGap"/> — <see cref="FailSafeCondition.F5"/>,
    /// force key-up and latch. That is the conservative reading of "key-up on any
    /// failure"; scheduling an edge from a stream whose ordering is untrustworthy is not.
    /// </para>
    /// </summary>
    TimestampRegression = 4
}
