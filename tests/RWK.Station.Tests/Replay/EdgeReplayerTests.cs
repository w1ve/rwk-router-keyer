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
using RWK.Shared.Config;
using RWK.Shared.Protocol.Edge;
using RWK.Station.Replay;
using RWK.Station.Tests.TestDoubles;
using Xunit;

namespace RWK.Station.Tests.Replay;

/// <summary>
/// End-to-end behavior of the replayer: datagram in, keyed edge out, with the jitter delay, the
/// anchor, and the sequence rules in force (7.1 - 7.5).
/// </summary>
/// <remarks>
/// These exercise the real replay thread against a recording output, so timing assertions are
/// bounded generously; the +/-1ms accuracy target of 7.5 is measured by
/// <see cref="EdgeReplayerTelemetry.MaxReplayErrorMs"/> rather than asserted here, where a loaded
/// build agent would make it flaky.
/// </remarks>
public class EdgeReplayerTests
{
    private const ushort Epoch = 7;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static JitterBufferConfig DirectFixed => new(
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(200),
        AdaptiveMode: false);

    private static EdgeReplayer CreateReplayer()
        => new(clock: null, jitterConfig: DirectFixed, pttTiming: null, EdgeJitterProfile.PathAdaptive)
        {
            Path = PathType.Direct,
        };

    private static void Send(EdgeReplayer replayer, ushort epoch, params EdgeEntry[] edges)
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(epoch, edges);
        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));
        replayer.ProcessDatagram(buffer[..written]);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(2);
        }

        return condition();
    }

    [Fact]
    public void ReplaysKeyDownAndKeyUpPreservingClientSpacing()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        output.Restart();
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 1_000));
        Send(replayer, Epoch, EdgeEntry.KeyUpAt(sequence: 2, timestampMs: 1_050));

        Assert.True(WaitFor(() => output.KeyTransitions.Count >= 2), "key never cycled");
        replayer.Stop();

        IReadOnlyList<LineTransition> keys = output.KeyTransitions;
        Assert.True(keys[0].Asserted);
        Assert.False(keys[1].Asserted);

        // Buffered by the 60ms direct delay before the key goes down (7.1, 7.2).
        Assert.InRange(keys[0].ElapsedMs, 40, 250);

        // The Client's 50ms element survives the trip (7.3).
        Assert.InRange(keys[1].ElapsedMs - keys[0].ElapsedMs, 25, 120);
    }

    [Fact]
    public void RedundantCopiesOfAnEdgeKeyOnlyOnce()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        EdgeEntry down = EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 100);
        EdgeEntry up = EdgeEntry.KeyUpAt(sequence: 2, timestampMs: 160);

        // Every frame carries the current edge plus its predecessors (6.4).
        Send(replayer, Epoch, down);
        Send(replayer, Epoch, up, down);
        Send(replayer, Epoch, up, down);

        Assert.True(WaitFor(() => output.KeyTransitions.Count >= 2));
        Assert.True(WaitFor(() => replayer.Telemetry.DuplicateEdges >= 3));
        replayer.Stop();

        Assert.Equal(2, output.KeyTransitions.Count);
        Assert.Equal(2, replayer.Telemetry.EdgesApplied);
    }

    [Fact]
    public void EpochMismatchRaisesF4WithoutKeying()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();
        FailSafeCondition? reported = null;
        replayer.FailSafeTriggered += (_, e) => reported = e.Condition;

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        Send(replayer, (ushort)(Epoch + 1), EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 0));

        Assert.True(WaitFor(() => reported is not null), "F4 was never reported");
        replayer.Stop();

        Assert.Equal(FailSafeCondition.F4, reported);
        Assert.False(replayer.IsSafeLatched); // F4 does not latch (9.4)
        Assert.Empty(output.KeyTransitions);
    }

    [Fact]
    public void UninferableSequenceGapLatchesSafeAndBlocksFurtherEdges()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();
        FailSafeCondition? reported = null;
        replayer.FailSafeTriggered += (_, e) => reported = e.Condition;

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        // Baseline, then a key-down four sequences later: the missing edges cannot be healed and a
        // key-down must never be guessed (9.5).
        Send(replayer, Epoch, EdgeEntry.KeyUpAt(sequence: 1, timestampMs: 10));
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 5, timestampMs: 200));

        Assert.True(WaitFor(() => replayer.IsSafeLatched), "SAFE was never latched");
        Assert.True(WaitFor(() => reported is not null), "F5 event was never reported");
        Assert.Equal(FailSafeCondition.F5, reported);
        Assert.Equal(EdgeReplayerState.SafeLatched, replayer.State);

        long droppedBefore = replayer.Telemetry.FramesDropped;
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 6, timestampMs: 260));
        Assert.True(replayer.Telemetry.FramesDropped > droppedBefore);

        replayer.ClearSafeLatch();
        Assert.False(replayer.IsSafeLatched);

        replayer.Stop();
        Assert.DoesNotContain(output.KeyTransitions, t => t.Asserted);
    }

    [Fact]
    public void DatagramsBeforeSessionEstablishmentAreDropped()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();

        replayer.Start(output, pttOutput: null);
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 0));

        Thread.Sleep(100);
        replayer.Stop();

        Assert.Equal(0, replayer.Telemetry.FramesReceived);
        Assert.True(replayer.Telemetry.FramesDropped >= 1);
        Assert.Empty(output.KeyTransitions);
    }

    [Fact]
    public void PttRisesBeforeTheKeyWhenAPttLineIsConfigured()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = new EdgeReplayer(
            clock: null,
            jitterConfig: DirectFixed,
            pttTiming: new PttTimingConfig
            {
                LeadTime = TimeSpan.FromMilliseconds(15),
                TailTime = TimeSpan.FromMilliseconds(60),
            },
            EdgeJitterProfile.PathAdaptive)
        {
            Path = PathType.Direct,
        };

        replayer.Start(output, output);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        output.Restart();
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 500));
        Send(replayer, Epoch, EdgeEntry.KeyUpAt(sequence: 2, timestampMs: 560));

        Assert.True(WaitFor(() => output.KeyTransitions.Count >= 2), "key never cycled");
        Assert.True(WaitFor(() => output.PttTransitions.Count >= 1), "PTT never asserted");
        replayer.Stop();

        LineTransition pttOn = output.PttTransitions[0];
        LineTransition keyDown = output.KeyTransitions[0];

        Assert.True(pttOn.Asserted);
        Assert.True(
            pttOn.ElapsedMs <= keyDown.ElapsedMs,
            $"PTT asserted at {pttOn.ElapsedMs:F1}ms, after key-down at {keyDown.ElapsedMs:F1}ms (8.4)");
    }

    [Fact]
    public void AnchorIsReestablishedAfterTwoSecondsOfIdle()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 0));
        Send(replayer, Epoch, EdgeEntry.KeyUpAt(sequence: 2, timestampMs: 40));
        Assert.True(WaitFor(() => replayer.Telemetry.AnchorCount >= 1));

        Thread.Sleep(2_100);
        output.Restart();

        // Timestamps have moved on by more than two seconds along with the idle period; without a
        // re-anchor this edge would be scheduled seconds into the future (7.2).
        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 3, timestampMs: 2_500));
        Send(replayer, Epoch, EdgeEntry.KeyUpAt(sequence: 4, timestampMs: 2_540));

        Assert.True(WaitFor(() => output.KeyTransitions.Count >= 2), "key never cycled after idle");
        replayer.Stop();

        Assert.True(replayer.Telemetry.AnchorCount >= 2);
        Assert.InRange(output.KeyTransitions[0].ElapsedMs, 40, 250);
    }

    [Fact]
    public void StopForcesKeyUpWhileKeyed()
    {
        var output = new RecordingKeyingOutput();
        using var replayer = CreateReplayer();

        replayer.Start(output, pttOutput: null);
        replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => replayer.IsSessionActive));

        Send(replayer, Epoch, EdgeEntry.KeyDownAt(sequence: 1, timestampMs: 0));
        Assert.True(WaitFor(() => output.IsKeyDown), "key never went down");

        replayer.Stop();

        Assert.False(output.IsKeyDown);
        Assert.Equal(EdgeReplayerState.Stopped, replayer.State);
    }
}
