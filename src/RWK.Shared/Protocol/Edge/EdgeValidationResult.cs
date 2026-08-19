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
/// The outcome of validating one received edge, plus everything the Edge_Replayer needs
/// to act on it without re-deriving the rules (Requirements 6.5, 6.6, 6.7).
/// </summary>
/// <remarks>
/// A readonly struct with no reference fields: classifying an edge on the keying path
/// allocates nothing. Malformed or hostile input is reported here, never thrown.
/// </remarks>
public readonly struct EdgeValidationResult : IEquatable<EdgeValidationResult>
{
    private EdgeValidationResult(
        EdgeValidationOutcome outcome,
        EdgeEntry edge,
        bool applied,
        bool canInferState,
        uint missedEdgeCount)
    {
        Outcome = outcome;
        Edge = edge;
        Applied = applied;
        CanInferState = canInferState;
        MissedEdgeCount = missedEdgeCount;
    }

    /// <summary>How the edge was classified.</summary>
    public EdgeValidationOutcome Outcome { get; }

    /// <summary>
    /// The edge that was validated. Default for <see cref="EdgeValidationOutcome.EpochMismatch"/>,
    /// which is decided from the frame header before any edge is examined.
    /// </summary>
    public EdgeEntry Edge { get; }

    /// <summary>
    /// True when the tracker advanced its state to this edge, so the replayer should
    /// schedule it. True for <see cref="EdgeValidationOutcome.Accepted"/> and for a
    /// <see cref="EdgeValidationOutcome.SequenceGap"/> whose state could be inferred.
    /// </summary>
    public bool Applied { get; }

    /// <summary>
    /// For <see cref="EdgeValidationOutcome.SequenceGap"/>: whether the key state across
    /// the gap is known to be safe. False forces <see cref="FailSafeCondition.F5"/>
    /// (Requirement 9.5). Always false for the other outcomes, which describe no gap.
    /// </summary>
    public bool CanInferState { get; }

    /// <summary>
    /// Number of edges that never arrived, for a <see cref="EdgeValidationOutcome.SequenceGap"/>.
    /// Zero for every other outcome.
    /// </summary>
    public uint MissedEdgeCount { get; }

    /// <summary>
    /// The fail-safe this outcome triggers, or null when none is needed. The replayer
    /// performs the response (force key-up, SAFE latch); this property only names it.
    /// </summary>
    public FailSafeCondition? FailSafe => Outcome switch
    {
        EdgeValidationOutcome.EpochMismatch => FailSafeCondition.F4,
        EdgeValidationOutcome.SequenceGap when !CanInferState => FailSafeCondition.F5,
        EdgeValidationOutcome.TimestampRegression => FailSafeCondition.F5,
        _ => null,
    };

    /// <summary>True when this outcome requires a fail-safe response.</summary>
    public bool RequiresFailSafe => FailSafe is not null;

    /// <summary>An in-order edge the tracker applied.</summary>
    public static EdgeValidationResult Accepted(in EdgeEntry edge)
        => new(EdgeValidationOutcome.Accepted, edge, applied: true, canInferState: false, missedEdgeCount: 0);

    /// <summary>An already-seen edge, discarded quietly (Requirement 6.6).</summary>
    public static EdgeValidationResult Duplicate(in EdgeEntry edge)
        => new(EdgeValidationOutcome.DuplicateDiscarded, edge, applied: false, canInferState: false, missedEdgeCount: 0);

    /// <summary>A frame whose epoch is not the current session's (Requirement 6.5).</summary>
    public static EdgeValidationResult EpochMismatch()
        => new(EdgeValidationOutcome.EpochMismatch, default, applied: false, canInferState: false, missedEdgeCount: 0);

    /// <summary>
    /// A gap in the sequence. <paramref name="canInferState"/> decides whether the edge is
    /// applied or escalates to <see cref="FailSafeCondition.F5"/>.
    /// </summary>
    public static EdgeValidationResult SequenceGap(in EdgeEntry edge, bool canInferState, uint missedEdgeCount)
        => new(EdgeValidationOutcome.SequenceGap, edge, applied: canInferState, canInferState, missedEdgeCount);

    /// <summary>An edge whose timestamp goes backwards within the session (Requirement 6.7).</summary>
    public static EdgeValidationResult TimestampRegression(in EdgeEntry edge)
        => new(EdgeValidationOutcome.TimestampRegression, edge, applied: false, canInferState: false, missedEdgeCount: 0);

    /// <inheritdoc />
    public bool Equals(EdgeValidationResult other)
        => Outcome == other.Outcome
        && Edge == other.Edge
        && Applied == other.Applied
        && CanInferState == other.CanInferState
        && MissedEdgeCount == other.MissedEdgeCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EdgeValidationResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Outcome, Edge, Applied, CanInferState, MissedEdgeCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EdgeValidationResult left, EdgeValidationResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EdgeValidationResult left, EdgeValidationResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Outcome switch
    {
        EdgeValidationOutcome.EpochMismatch => "EpochMismatch (F4)",
        EdgeValidationOutcome.SequenceGap =>
            $"SequenceGap(missed={MissedEdgeCount}, canInfer={CanInferState}) {Edge}",
        _ => $"{Outcome} {Edge}",
    };
}
