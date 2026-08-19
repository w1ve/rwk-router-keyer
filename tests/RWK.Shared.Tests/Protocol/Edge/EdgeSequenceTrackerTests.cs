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
using RWK.Shared.Protocol.Edge;
using Xunit;

namespace RWK.Shared.Tests.Protocol.Edge;

/// <summary>
/// Unit tests for edge validation: epoch match, sequence ordering and duplicate rejection,
/// and timestamp monotonicity (Requirements 6.5, 6.6, 6.7).
/// </summary>
public class EdgeSequenceTrackerTests
{
    private const ushort Epoch = 7;

    private static EdgeSequenceTracker TrackerAt(uint sequence, uint timestampMs, bool keyDown)
    {
        EdgeSequenceTracker tracker = new(Epoch);
        EdgeValidationResult first = tracker.Validate(
            Epoch,
            new EdgeEntry(sequence, timestampMs, keyDown ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp));

        Assert.Equal(EdgeValidationOutcome.Accepted, first.Outcome);
        return tracker;
    }

    [Fact]
    public void FirstEdge_EstablishesBaseline_WhateverItsSequence()
    {
        EdgeSequenceTracker tracker = new(Epoch);
        Assert.False(tracker.HasApplied);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyDownAt(sequence: 5_000, timestampMs: 900));

        Assert.Equal(EdgeValidationOutcome.Accepted, result.Outcome);
        Assert.True(result.Applied);
        Assert.Null(result.FailSafe);
        Assert.True(tracker.HasApplied);
        Assert.Equal(5_000u, tracker.LastSequence);
        Assert.Equal(900u, tracker.LastTimestampMs);
        Assert.True(tracker.LastKeyDown);
    }

    [Fact]
    public void ConsecutiveSequence_IsAccepted_AndAdvancesState()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: true);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyUpAt(sequence: 11, timestampMs: 140));

        Assert.Equal(EdgeValidationOutcome.Accepted, result.Outcome);
        Assert.True(result.Applied);
        Assert.Equal(0u, result.MissedEdgeCount);
        Assert.Equal(11u, tracker.LastSequence);
        Assert.Equal(140u, tracker.LastTimestampMs);
        Assert.False(tracker.LastKeyDown);
    }

    [Fact]
    public void EqualTimestamps_AreAccepted_MonotonicityIsNonDecreasing()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyDownAt(sequence: 11, timestampMs: 100));

        Assert.Equal(EdgeValidationOutcome.Accepted, result.Outcome);
        Assert.True(result.Applied);
    }

    // --- 6.6 duplicates ---------------------------------------------------------------

    [Theory]
    [InlineData(10u)] // same edge again, the usual redundant retransmit
    [InlineData(9u)]  // older copy from the redundancy block
    [InlineData(0u)]
    public void SequenceAtOrBelowLast_IsDiscardedAsDuplicate(uint sequence)
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: true);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyUpAt(sequence, timestampMs: 100));

        Assert.Equal(EdgeValidationOutcome.DuplicateDiscarded, result.Outcome);
        Assert.False(result.Applied);
        Assert.Null(result.FailSafe);

        // State untouched: a duplicate must not roll the session backwards.
        Assert.Equal(10u, tracker.LastSequence);
        Assert.Equal(100u, tracker.LastTimestampMs);
        Assert.True(tracker.LastKeyDown);
    }

    // --- 6.5 / 9.4 epoch -------------------------------------------------------------

    [Fact]
    public void MismatchedEpoch_IsReportedAsF4_AndAppliesNothing()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        EdgeValidationResult result = tracker.Validate(
            (ushort)(Epoch + 1),
            EdgeEntry.KeyDownAt(sequence: 11, timestampMs: 140));

        Assert.Equal(EdgeValidationOutcome.EpochMismatch, result.Outcome);
        Assert.False(result.Applied);
        Assert.Equal(FailSafeCondition.F4, result.FailSafe);
        Assert.Equal(10u, tracker.LastSequence);
    }

    [Fact]
    public void MismatchedEpochFrame_IsDiscardedWithoutExaminingEdges()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            (ushort)(Epoch + 1),
            new[]
            {
                EdgeEntry.KeyDownAt(13, 180),
                EdgeEntry.KeyUpAt(12, 160),
                EdgeEntry.KeyDownAt(11, 140),
            });

        Span<EdgeValidationResult> results = stackalloc EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];
        Assert.True(tracker.TryValidateFrame(frame, results, out int count));

        Assert.Equal(1, count);
        Assert.Equal(EdgeValidationOutcome.EpochMismatch, results[0].Outcome);
        Assert.Equal(10u, tracker.LastSequence);
    }

    [Fact]
    public void BeginSession_RebindsEpochAndClearsState()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: true);

        tracker.BeginSession(Epoch + 1);

        Assert.Equal((ushort)(Epoch + 1), tracker.Epoch);
        Assert.False(tracker.HasApplied);
        Assert.Equal(0u, tracker.LastSequence);
        Assert.Equal(0u, tracker.LastTimestampMs);
        Assert.False(tracker.LastKeyDown);

        // Sequence and timestamp are per-epoch, so the old epoch's values impose nothing.
        EdgeValidationResult result = tracker.Validate(
            (ushort)(Epoch + 1),
            EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 5));
        Assert.Equal(EdgeValidationOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public void EpochRollover_PastUshortMaxValue_WrapsToZeroAndStillMatchesByEquality()
    {
        Assert.Equal((ushort)0, EdgeSequenceTracker.NextEpoch(ushort.MaxValue));

        EdgeSequenceTracker tracker = new(ushort.MaxValue);
        Assert.Equal(
            EdgeValidationOutcome.Accepted,
            tracker.Validate(ushort.MaxValue, EdgeEntry.KeyUpAt(1, 10)).Outcome);

        tracker.BeginSession(EdgeSequenceTracker.NextEpoch(ushort.MaxValue));
        Assert.Equal((ushort)0, tracker.Epoch);

        // The wrapped epoch is now the live one, and the pre-rollover epoch is stale.
        Assert.Equal(
            EdgeValidationOutcome.Accepted,
            tracker.Validate(0, EdgeEntry.KeyUpAt(1, 10)).Outcome);
        Assert.Equal(
            EdgeValidationOutcome.EpochMismatch,
            tracker.Validate(ushort.MaxValue, EdgeEntry.KeyUpAt(2, 20)).Outcome);
    }

    // --- 9.5 sequence gaps -----------------------------------------------------------

    [Fact]
    public void GapEndingInKeyUp_IsInferable_AndApplied()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: true);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyUpAt(sequence: 14, timestampMs: 220));

        Assert.Equal(EdgeValidationOutcome.SequenceGap, result.Outcome);
        Assert.True(result.CanInferState);
        Assert.True(result.Applied);
        Assert.Equal(3u, result.MissedEdgeCount);
        Assert.Null(result.FailSafe);
        Assert.Equal(14u, tracker.LastSequence);
        Assert.False(tracker.LastKeyDown);
    }

    [Fact]
    public void GapEndingInKeyDown_IsNotInferable_AndMapsToF5()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyDownAt(sequence: 12, timestampMs: 160));

        Assert.Equal(EdgeValidationOutcome.SequenceGap, result.Outcome);
        Assert.False(result.CanInferState);
        Assert.False(result.Applied);
        Assert.Equal(1u, result.MissedEdgeCount);
        Assert.Equal(FailSafeCondition.F5, result.FailSafe);

        // Never guess a key-down: the tracker did not advance to it.
        Assert.Equal(10u, tracker.LastSequence);
        Assert.False(tracker.LastKeyDown);
    }

    [Fact]
    public void CanInferStateAcrossGap_IsTrueOnlyForKeyUp()
    {
        Assert.True(EdgeSequenceTracker.CanInferStateAcrossGap(EdgeEntry.KeyUpAt(1, 0)));
        Assert.False(EdgeSequenceTracker.CanInferStateAcrossGap(EdgeEntry.KeyDownAt(1, 0)));
    }

    [Fact]
    public void RedundantCopiesInFrame_HealGap_SoNoFailSafeIsReported()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        // Datagram carrying edges 11-13 was lost; the next frame's redundancy block still
        // has them. Wire order is current-edge-first (6.4).
        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            Epoch,
            new[]
            {
                EdgeEntry.KeyUpAt(14, 220),
                EdgeEntry.KeyDownAt(13, 200),
                EdgeEntry.KeyUpAt(12, 160),
                EdgeEntry.KeyDownAt(11, 140),
            });

        Span<EdgeValidationResult> results = stackalloc EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];
        Assert.True(tracker.TryValidateFrame(frame, results, out int count));

        Assert.Equal(4, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(EdgeValidationOutcome.Accepted, results[i].Outcome);
            Assert.Null(results[i].FailSafe);
        }

        // Ascending order, oldest first, regardless of wire order.
        Assert.Equal(11u, results[0].Edge.Sequence);
        Assert.Equal(14u, results[3].Edge.Sequence);
        Assert.Equal(14u, tracker.LastSequence);
    }

    [Fact]
    public void FrameOfAlreadyAppliedEdges_ReportsOnlyDuplicates()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            Epoch,
            new[] { EdgeEntry.KeyUpAt(10, 100), EdgeEntry.KeyDownAt(9, 80) });

        Span<EdgeValidationResult> results = stackalloc EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];
        Assert.True(tracker.TryValidateFrame(frame, results, out int count));

        Assert.Equal(2, count);
        Assert.Equal(EdgeValidationOutcome.DuplicateDiscarded, results[0].Outcome);
        Assert.Equal(EdgeValidationOutcome.DuplicateDiscarded, results[1].Outcome);
        Assert.Equal(10u, tracker.LastSequence);
    }

    [Fact]
    public void TryValidateFrame_ReturnsFalseWhenResultBufferTooSmall()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 100, keyDown: false);

        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            Epoch,
            new[] { EdgeEntry.KeyDownAt(12, 160), EdgeEntry.KeyUpAt(11, 140) });

        Span<EdgeValidationResult> results = stackalloc EdgeValidationResult[1];
        Assert.False(tracker.TryValidateFrame(frame, results, out int count));

        Assert.Equal(0, count);
        Assert.Equal(10u, tracker.LastSequence);
    }

    // --- 6.7 timestamp monotonicity --------------------------------------------------

    [Fact]
    public void NewSequenceWithEarlierTimestamp_IsRejectedAsRegression()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: 10, timestampMs: 500, keyDown: false);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyDownAt(sequence: 11, timestampMs: 499));

        Assert.Equal(EdgeValidationOutcome.TimestampRegression, result.Outcome);
        Assert.False(result.Applied);
        Assert.Equal(FailSafeCondition.F5, result.FailSafe);
        Assert.Equal(10u, tracker.LastSequence);
        Assert.Equal(500u, tracker.LastTimestampMs);
    }

    [Fact]
    public void AcceptedTimestamps_AreMonotonicallyNonDecreasing()
    {
        EdgeSequenceTracker tracker = new(Epoch);
        uint[] timestamps = { 0, 40, 40, 90, 130 };
        uint previous = 0;

        for (uint i = 0; i < timestamps.Length; i++)
        {
            EdgeValidationResult result = tracker.Validate(
                Epoch,
                new EdgeEntry(i + 1, timestamps[i], (byte)(i % 2)));

            Assert.True(result.Applied);
            Assert.True(tracker.LastTimestampMs >= previous);
            previous = tracker.LastTimestampMs;
        }
    }

    // --- sequence space boundary -----------------------------------------------------

    [Fact]
    public void SequenceAtUintMaxValue_IsAccepted()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: uint.MaxValue - 1, timestampMs: 100, keyDown: false);

        EdgeValidationResult result = tracker.Validate(Epoch, EdgeEntry.KeyDownAt(uint.MaxValue, 140));

        Assert.Equal(EdgeValidationOutcome.Accepted, result.Outcome);
        Assert.Equal(uint.MaxValue, tracker.LastSequence);
    }

    [Fact]
    public void SequenceWrapPastUintMaxValue_IsDiscardedNotAccepted()
    {
        EdgeSequenceTracker tracker = TrackerAt(sequence: uint.MaxValue, timestampMs: 100, keyDown: false);

        // Sequence is compared as a plain unsigned value, so the tracker saturates rather
        // than wrapping. Discarding can drop keying; wrapping could accept a stale replay.
        Assert.Equal(
            EdgeValidationOutcome.DuplicateDiscarded,
            tracker.Validate(Epoch, EdgeEntry.KeyDownAt(0, 140)).Outcome);
        Assert.Equal(
            EdgeValidationOutcome.DuplicateDiscarded,
            tracker.Validate(Epoch, EdgeEntry.KeyDownAt(1, 180)).Outcome);

        Assert.Equal(uint.MaxValue, tracker.LastSequence);
        Assert.False(tracker.LastKeyDown);

        // A new epoch is how the Client escapes a saturated sequence space.
        tracker.BeginSession(EdgeSequenceTracker.NextEpoch(Epoch));
        Assert.Equal(
            EdgeValidationOutcome.Accepted,
            tracker.Validate(EdgeSequenceTracker.NextEpoch(Epoch), EdgeEntry.KeyDownAt(0, 0)).Outcome);
    }
}
