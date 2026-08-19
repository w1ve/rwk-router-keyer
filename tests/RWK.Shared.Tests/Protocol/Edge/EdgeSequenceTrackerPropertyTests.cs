/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared;
using RWK.Shared.Protocol.Edge;
using Xunit;

namespace RWK.Shared.Tests.Protocol.Edge;

/// <summary>
/// Property-based tests for edge validation (<see cref="EdgeSequenceTracker"/>):
/// epoch mismatch discard, duplicate discard, and timestamp monotonicity, plus the
/// "never invent keying" safety invariant.
/// </summary>
/// <remarks>
/// Example-based coverage lives in <see cref="EdgeSequenceTrackerTests"/>; this file adds
/// the universal statements over generated input and does not repeat those examples.
/// </remarks>
public class EdgeSequenceTrackerPropertyTests
{
    private const ushort SessionEpoch = 7;

    // --- generators --------------------------------------------------------------------

    /// <summary>
    /// Epochs biased towards the ushort boundaries, because epoch is a 2-byte field that
    /// rolls over on reconnect (Requirement 6.2) and equality across the rollover is the
    /// interesting case.
    /// </summary>
    private static Gen<ushort> EpochGen =>
        from pick in Gen.Choose(0, 9)
        from raw in Gen.Choose(0, ushort.MaxValue)
        select pick switch
        {
            0 => (ushort)0,
            1 => (ushort)1,
            2 => ushort.MaxValue,
            3 => (ushort)(ushort.MaxValue - 1),
            _ => (ushort)raw,
        };

    /// <summary>
    /// A session epoch paired with a frame epoch that is guaranteed to differ. The offset
    /// spans the whole nonzero range and is added with wraparound, so pairs straddling the
    /// ushort rollover (65535 → 0) are generated too.
    /// </summary>
    private static Gen<(ushort Session, ushort Frame)> MismatchedEpochPairGen =>
        from session in EpochGen
        from offset in Gen.Choose(1, ushort.MaxValue)
        select (session, (ushort)(session + offset));

    /// <summary>An unconstrained edge: any sequence, any timestamp, either key state.</summary>
    private static Gen<EdgeEntry> ArbitraryEdgeGen =>
        from sequence in Gen.Choose(0, 500)
        from timestampMs in Gen.Choose(0, 5_000)
        from keyDown in Gen.Choose(0, 1)
        select new EdgeEntry(
            (uint)sequence,
            (uint)timestampMs,
            keyDown == 1 ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp);

    /// <summary>1..4 arbitrary edges, the legal payload of one frame (Requirement 6.4).</summary>
    private static Gen<EdgeEntry[]> FrameEdgesGen =>
        from count in Gen.Choose(RwkPaddleFrame.MinEdgeCount, RwkPaddleFrame.MaxEdgeCount)
        from e0 in ArbitraryEdgeGen
        from e1 in ArbitraryEdgeGen
        from e2 in ArbitraryEdgeGen
        from e3 in ArbitraryEdgeGen
        select new[] { e0, e1, e2, e3 }.Take(count).ToArray();

    /// <summary>
    /// A well-formed Client edge stream: sequence ascending by one, timestamp non-decreasing
    /// (a zero delta is legal — monotonicity is non-strict), key state alternating.
    /// Paired with a per-frame loss flag so redundant-copy healing is exercised.
    /// </summary>
    private static Gen<(EdgeEntry[] Stream, bool[] Delivered)> RedundantStreamGen =>
        from startSequence in Gen.Choose(0, 100_000)
        from startTimestamp in Gen.Choose(0, 10_000)
        from steps in Gen.NonEmptyListOf(
            from delta in Gen.Choose(0, 60)
            from lossRoll in Gen.Choose(0, 9)
            select (Delta: delta, Delivered: lossRoll != 0))
        select BuildStream((uint)startSequence, (uint)startTimestamp, steps.ToArray());

    /// <summary>One arriving edge, mostly in-session and occasionally from a foreign epoch.</summary>
    private static Gen<(ushort Epoch, EdgeEntry Edge)> ArrivalGen =>
        from foreignRoll in Gen.Choose(0, 5)
        from edge in ArbitraryEdgeGen
        select (foreignRoll == 0 ? (ushort)(SessionEpoch + 1) : SessionEpoch, edge);

    /// <summary>
    /// A chaotic inbound frame stream: arbitrary sequences (so shuffles, duplicates, and
    /// gaps all occur), epochs drawn from a small pool so matches and mismatches are both
    /// frequent, and occasional session restarts.
    /// </summary>
    private static Gen<(bool Restart, ushort Epoch, EdgeEntry[] Edges)[]> ChaoticFrameStreamGen =>
        Gen.NonEmptyListOf(
            from restartRoll in Gen.Choose(0, 11)
            from epochPick in Gen.Choose(0, 2)
            from edges in FrameEdgesGen
            select (
                Restart: restartRoll == 0,
                Epoch: epochPick switch
                {
                    0 => SessionEpoch,
                    1 => (ushort)(SessionEpoch + 1),
                    _ => ushort.MaxValue,
                },
                Edges: edges))
        .Select(frames => frames.ToArray());

    private static (EdgeEntry[] Stream, bool[] Delivered) BuildStream(
        uint startSequence,
        uint startTimestamp,
        (int Delta, bool Delivered)[] steps)
    {
        EdgeEntry[] stream = new EdgeEntry[steps.Length];
        bool[] delivered = new bool[steps.Length];
        uint timestamp = startTimestamp;

        for (int i = 0; i < steps.Length; i++)
        {
            timestamp += (uint)steps[i].Delta;
            stream[i] = new EdgeEntry(
                startSequence + (uint)i,
                timestamp,
                (byte)(i % 2 == 0 ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp));
            delivered[i] = steps[i].Delivered;
        }

        return (stream, delivered);
    }

    /// <summary>
    /// The frame the Client would send for <paramref name="index"/>: the current edge first,
    /// then up to three previous edges (Requirement 6.4).
    /// </summary>
    private static RwkPaddleFrame FrameFor(ushort epoch, EdgeEntry[] stream, int index)
    {
        int oldest = Math.Max(0, index - (RwkPaddleFrame.MaxEdgeCount - 1));
        EdgeEntry[] wireOrder = new EdgeEntry[index - oldest + 1];
        for (int i = 0; i < wireOrder.Length; i++)
        {
            wireOrder[i] = stream[index - i];
        }

        return RwkPaddleFrame.Create(epoch, wireOrder);
    }

    private static (bool HasApplied, uint Sequence, uint TimestampMs, bool KeyDown, ushort Epoch) Snapshot(
        EdgeSequenceTracker tracker)
        => (tracker.HasApplied, tracker.LastSequence, tracker.LastTimestampMs, tracker.LastKeyDown, tracker.Epoch);

    // --- Property 18: Epoch Mismatch Discard ------------------------------------------

    /// <summary>
    /// Property 18: for any frame whose epoch differs from the session epoch, the whole
    /// frame is discarded as a single <see cref="EdgeValidationOutcome.EpochMismatch"/>
    /// result mapping to <see cref="FailSafeCondition.F4"/>, no edge is applied, and the
    /// tracker's state is completely unchanged.
    ///
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Property]
    public Property Property18_MismatchedEpochFrame_IsDiscardedWithStateUnchanged()
    {
        var gen = from epochs in MismatchedEpochPairGen
                  from establishBaseline in Gen.Choose(0, 1)
                  from baseline in ArbitraryEdgeGen
                  from edges in FrameEdgesGen
                  select (epochs, establishBaseline: establishBaseline == 1, baseline, edges);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (epochs, establishBaseline, baseline, edges) = input;

            EdgeSequenceTracker tracker = new(epochs.Session);
            if (establishBaseline)
            {
                Assert.True(tracker.Validate(epochs.Session, baseline).Applied);
            }

            var before = Snapshot(tracker);

            RwkPaddleFrame frame = RwkPaddleFrame.Create(epochs.Frame, edges);
            EdgeValidationResult[] results = new EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];
            Assert.True(tracker.TryValidateFrame(frame, results, out int count));

            // The frame is rejected from its header: one result, no edge examined.
            Assert.Equal(1, count);
            Assert.Equal(EdgeValidationOutcome.EpochMismatch, results[0].Outcome);
            Assert.False(results[0].Applied);
            Assert.Equal(FailSafeCondition.F4, results[0].FailSafe);
            Assert.Equal(before, Snapshot(tracker));

            return true;
        });
    }

    /// <summary>
    /// Property 18, per-edge path: a single edge carrying a foreign epoch is rejected before
    /// any sequence or timestamp comparison, so it can neither establish a baseline on a
    /// fresh tracker nor move an established one.
    ///
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Property]
    public Property Property18_MismatchedEpochEdge_NeverAppliesAndNeverEstablishesBaseline()
    {
        var gen = from epochs in MismatchedEpochPairGen
                  from edges in FrameEdgesGen
                  select (epochs, edges);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (epochs, edges) = input;

            EdgeSequenceTracker tracker = new(epochs.Session);
            var before = Snapshot(tracker);

            foreach (EdgeEntry edge in edges)
            {
                EdgeValidationResult result = tracker.Validate(epochs.Frame, edge);

                Assert.Equal(EdgeValidationOutcome.EpochMismatch, result.Outcome);
                Assert.False(result.Applied);
                Assert.Equal(FailSafeCondition.F4, result.FailSafe);
                Assert.False(tracker.HasApplied);
                Assert.Equal(before, Snapshot(tracker));
            }

            return true;
        });
    }

    // --- Property 19: Duplicate Edge Discard ------------------------------------------

    /// <summary>
    /// Property 19: for any edge whose sequence is at or below the last applied sequence,
    /// the outcome is <see cref="EdgeValidationOutcome.DuplicateDiscarded"/>, nothing is
    /// applied, no fail-safe is raised, and the tracker state does not roll backwards.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property]
    public Property Property19_SequenceAtOrBelowLastApplied_IsDiscardedAsDuplicate()
    {
        var gen = from baseline in ArbitraryEdgeGen
                  from sequenceDrop in Gen.Choose(0, 500)
                  from timestampMs in Gen.Choose(0, 5_000)
                  from keyDown in Gen.Choose(0, 1)
                  select (baseline, sequenceDrop: (uint)sequenceDrop, timestampMs: (uint)timestampMs, keyDown: keyDown == 1);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (baseline, sequenceDrop, timestampMs, keyDown) = input;

            EdgeSequenceTracker tracker = new(SessionEpoch);
            Assert.True(tracker.Validate(SessionEpoch, baseline).Applied);
            var before = Snapshot(tracker);

            uint duplicateSequence = baseline.Sequence - Math.Min(sequenceDrop, baseline.Sequence);
            EdgeEntry duplicate = new(
                duplicateSequence,
                timestampMs,
                keyDown ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp);

            EdgeValidationResult result = tracker.Validate(SessionEpoch, duplicate);

            Assert.Equal(EdgeValidationOutcome.DuplicateDiscarded, result.Outcome);
            Assert.False(result.Applied);
            Assert.Null(result.FailSafe);
            Assert.Equal(0u, result.MissedEdgeCount);
            Assert.Equal(before, Snapshot(tracker));

            return true;
        });
    }

    /// <summary>
    /// Property 19 over a realistic redundant-copy stream: every frame repeats up to three
    /// already-sent edges (Requirement 6.4) and some datagrams are lost. Each edge is
    /// applied at most once; every repeat is discarded as a duplicate and leaves the
    /// tracker untouched, so applied sequences and timestamps only ever move forwards.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property]
    public Property Property19_RedundantCopies_AreDiscardedAndNeverRollStateBackwards()
    {
        return Prop.ForAll(RedundantStreamGen.ToArbitrary(), input =>
        {
            var (stream, delivered) = input;

            EdgeSequenceTracker tracker = new(SessionEpoch);
            HashSet<uint> appliedSequences = new();

            for (int i = 0; i < stream.Length; i++)
            {
                if (!delivered[i])
                {
                    continue;
                }

                RwkPaddleFrame frame = FrameFor(SessionEpoch, stream, i);

                // Ascending order is what TryValidateFrame imposes; walking it explicitly
                // lets each edge's before/after state be inspected.
                EdgeEntry[] ordered = new EdgeEntry[frame.EdgeCount];
                Assert.True(frame.TryCopyEdgesTo(ordered, out _));
                Array.Sort(ordered, (a, b) => a.Sequence.CompareTo(b.Sequence));

                foreach (EdgeEntry edge in ordered)
                {
                    var before = Snapshot(tracker);
                    EdgeValidationResult result = tracker.Validate(SessionEpoch, edge);

                    if (result.Outcome == EdgeValidationOutcome.DuplicateDiscarded)
                    {
                        Assert.False(result.Applied);
                        Assert.Null(result.FailSafe);
                        Assert.Equal(before, Snapshot(tracker));
                        continue;
                    }

                    // A well-formed stream never regresses in timestamp.
                    Assert.NotEqual(EdgeValidationOutcome.TimestampRegression, result.Outcome);

                    if (result.Applied)
                    {
                        Assert.True(appliedSequences.Add(edge.Sequence), "an edge was applied twice");
                        Assert.True(tracker.LastSequence > before.Sequence, "applied sequence must advance");
                        Assert.True(tracker.LastTimestampMs >= before.TimestampMs, "applied timestamp must not go back");
                    }
                    else
                    {
                        Assert.Equal(before, Snapshot(tracker));
                    }
                }
            }

            return true;
        });
    }

    // --- Property 20: Edge Timestamp Monotonicity -------------------------------------

    /// <summary>
    /// Property 20: across any arriving edge stream — arbitrary sequences, arbitrary
    /// timestamps, occasional foreign epochs — the timestamps of the edges the tracker
    /// applies are monotonically non-decreasing. Non-decreasing, not strictly increasing:
    /// two edges may legitimately share a millisecond.
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property]
    public Property Property20_AppliedEdgeTimestamps_AreMonotonicallyNonDecreasing()
    {
        return Prop.ForAll(Gen.NonEmptyListOf(ArrivalGen).ToArbitrary(), arrivals =>
        {
            EdgeSequenceTracker tracker = new(SessionEpoch);
            bool anyApplied = false;
            uint lastAppliedTimestamp = 0;

            foreach (var (epoch, edge) in arrivals)
            {
                EdgeValidationResult result = tracker.Validate(epoch, edge);

                if (!result.Applied)
                {
                    continue;
                }

                if (anyApplied)
                {
                    Assert.True(
                        edge.TimestampMs >= lastAppliedTimestamp,
                        $"applied timestamp {edge.TimestampMs} went back from {lastAppliedTimestamp}");
                }

                anyApplied = true;
                lastAppliedTimestamp = edge.TimestampMs;
                Assert.Equal(lastAppliedTimestamp, tracker.LastTimestampMs);
            }

            return true;
        });
    }

    /// <summary>
    /// Property 20 across the frame path and across session restarts: within one epoch the
    /// applied timestamps never go backwards, and a restart is the only thing that resets
    /// the baseline (sequence and timestamp are per-epoch state).
    ///
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Property]
    public Property Property20_AppliedTimestamps_AreNonDecreasingWithinEachEpoch()
    {
        return Prop.ForAll(ChaoticFrameStreamGen.ToArbitrary(), frames =>
        {
            EdgeSequenceTracker tracker = new(SessionEpoch);
            EdgeValidationResult[] results = new EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];
            bool anyApplied = false;
            uint lastAppliedTimestamp = 0;

            foreach (var (restart, epoch, edges) in frames)
            {
                if (restart)
                {
                    tracker.BeginSession(epoch);
                    anyApplied = false;
                    lastAppliedTimestamp = 0;
                    continue;
                }

                RwkPaddleFrame frame = RwkPaddleFrame.Create(epoch, edges);
                Assert.True(tracker.TryValidateFrame(frame, results, out int count));

                for (int i = 0; i < count; i++)
                {
                    if (!results[i].Applied)
                    {
                        continue;
                    }

                    if (anyApplied)
                    {
                        Assert.True(
                            results[i].Edge.TimestampMs >= lastAppliedTimestamp,
                            $"applied timestamp {results[i].Edge.TimestampMs} went back from {lastAppliedTimestamp}");
                    }

                    anyApplied = true;
                    lastAppliedTimestamp = results[i].Edge.TimestampMs;
                }
            }

            return true;
        });
    }

    // --- safety invariant: never invent keying ----------------------------------------

    /// <summary>
    /// Safety invariant, the prime directive in tracker terms: the tracker never applies a
    /// key-down edge across an unhealed sequence gap, so the Edge_Replayer is never handed a
    /// key-down whose position in the stream is unverified.
    /// <para>
    /// <b>What is guaranteed.</b> Across any inbound frame stream — shuffled wire order,
    /// duplicates, gaps, foreign epochs, mid-stream session restarts — every key-down edge
    /// the tracker applies is either the first edge applied in the session or carries
    /// exactly one more than the sequence of the previously applied edge. The
    /// <em>previously applied edge</em> is tracked here from the edges' own sequence numbers,
    /// deliberately not from <see cref="EdgeValidationResult.MissedEdgeCount"/> or
    /// <see cref="EdgeValidationResult.CanInferState"/>: those are the tracker's own account
    /// of whether a gap exists, and a property that trusts them cannot detect the tracker
    /// getting that account wrong. Every applied key-up must likewise advance the sequence,
    /// though it may jump — healing a gap with a key-up is the one legitimate jump, because
    /// the resulting key state is safe whatever the missing edges carried. Frames from which
    /// nothing is applied must leave the tracker bit-identical, which is what makes an
    /// epoch mismatch (6.5) and a duplicate (6.6) inert rather than merely unscheduled.
    /// </para>
    /// <para>
    /// <b>Why it is stated this way.</b> The earlier form of this property asserted that an
    /// applied key-down had <c>Outcome == Accepted</c> and <c>MissedEdgeCount == 0</c>. Both
    /// hold by construction — <see cref="EdgeValidationResult.SequenceGap"/> sets
    /// <c>Applied</c> from <c>CanInferState</c>, and
    /// <see cref="EdgeSequenceTracker.CanInferStateAcrossGap"/> is <c>!KeyDown</c> — so the
    /// assertion followed from two one-line factory methods and said nothing about the gap
    /// arithmetic or the frame walk that decide whether a gap was noticed at all. Weakening
    /// the gap test in <see cref="EdgeSequenceTracker"/> to <c>missed &gt; 1</c>, which lets a
    /// key-down one edge past a hole through as <c>Accepted</c> with <c>MissedEdgeCount == 0</c>,
    /// left that form passing at 50,000 cases; the contiguity statement below falsifies it
    /// within about 1,500.
    /// </para>
    /// <para>
    /// <b>What is not guaranteed.</b> The first edge after <see cref="EdgeSequenceTracker.BeginSession"/>
    /// is applied whatever its sequence and whatever its key state, key-down included — that
    /// is the deliberate "joining a stream already in progress must not latch SAFE" rule. So
    /// a restart resets the verified baseline, and the tracker cannot tell a genuine reconnect
    /// from a restart issued mid-stream. Nothing here bounds how long such a key-down keys the
    /// transmitter either; that is F1 (750 ms without traffic while key-down) and F3 (10 s
    /// continuously key-down), which live in the replayer. The tracker's contribution is
    /// narrower and is what this property pins down: within one session it never advances its
    /// key state to down over edges it never saw.
    /// </para>
    /// <para>
    /// Run at 1,000 cases rather than the default 100 because the interesting shapes — a
    /// key-down landing just past a hole, a key-up healing a gap and a key-down following it
    /// contiguously — need a frame stream several frames long to appear.
    /// </para>
    ///
    /// **Validates: Requirements 6.6, 6.7**
    /// </summary>
    [Property(MaxTest = 1_000)]
    public Property KeyDown_IsNeverAppliedAcrossAnUnhealedGap()
    {
        return Prop.ForAll(ChaoticFrameStreamGen.ToArbitrary(), frames =>
        {
            EdgeSequenceTracker tracker = new(SessionEpoch);
            EdgeValidationResult[] results = new EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];

            // The verified baseline, derived only from the sequence numbers of the edges the
            // tracker actually took. Independent of how the tracker describes the gap.
            bool anyApplied = false;
            uint lastAppliedSequence = 0;

            foreach (var (restart, epoch, edges) in frames)
            {
                if (restart)
                {
                    tracker.BeginSession(epoch);
                    anyApplied = false;
                    lastAppliedSequence = 0;
                    continue;
                }

                var beforeFrame = Snapshot(tracker);
                RwkPaddleFrame frame = RwkPaddleFrame.Create(epoch, edges);
                Assert.True(tracker.TryValidateFrame(frame, results, out int count));

                bool appliedThisFrame = false;

                for (int i = 0; i < count; i++)
                {
                    EdgeValidationResult result = results[i];

                    // 9.5 — a gap ending in a key-down must reach the replayer as F5.
                    if (result.Outcome == EdgeValidationOutcome.SequenceGap && result.Edge.KeyDown)
                    {
                        Assert.False(result.Applied, "a key-down was applied across a gap");
                        Assert.Equal(FailSafeCondition.F5, result.FailSafe);
                    }

                    if (!result.Applied)
                    {
                        continue;
                    }

                    EdgeEntry applied = result.Edge;

                    if (applied.KeyDown)
                    {
                        Assert.True(
                            !anyApplied || applied.Sequence == lastAppliedSequence + 1,
                            $"key-down seq={applied.Sequence} applied across a gap from seq={lastAppliedSequence}");
                    }
                    else
                    {
                        Assert.True(
                            !anyApplied || applied.Sequence > lastAppliedSequence,
                            $"key-up seq={applied.Sequence} applied at or below seq={lastAppliedSequence}");
                    }

                    appliedThisFrame = true;
                    anyApplied = true;
                    lastAppliedSequence = applied.Sequence;
                }

                // The tracker's own state must agree with the independently tracked baseline,
                // and a frame that applied nothing must not have moved it at all.
                Assert.Equal(anyApplied, tracker.HasApplied);

                if (appliedThisFrame)
                {
                    Assert.Equal(lastAppliedSequence, tracker.LastSequence);
                }
                else
                {
                    Assert.Equal(beforeFrame, Snapshot(tracker));
                }
            }

            return true;
        });
    }
}
