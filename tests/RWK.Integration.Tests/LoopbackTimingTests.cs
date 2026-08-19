/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Diagnostics;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.Keying;
using RWK.Shared.Protocol.Edge;
using RWK.Shared.Timing;
using RWK.Station.Replay;
using Xunit;
using Xunit.Abstractions;

namespace RWK.Integration.Tests;

/// <summary>
/// Loopback timing integration test: generates edges at 35 WPM via
/// <see cref="KeyerElementPump"/>, builds RWK-PADDLE frames with redundancy,
/// feeds them into an <see cref="EdgeReplayer"/> with a <see cref="RecordingKeyingOutput"/>,
/// and asserts that all replayed intervals are within ±2ms of expected timing.
/// </summary>
/// <remarks>
/// <para>
/// This validates the full Client-edge-generation → frame → replayer pipeline without
/// any real network or serial hardware. The jitter buffer is set to a minimal direct delay
/// so the test completes quickly while still exercising the real replay scheduler.
/// </para>
/// <para>
/// **Validates: Requirements 7.5, 14.5**
/// </para>
/// </remarks>
public class LoopbackTimingTests : IDisposable
{
    private const int SpeedWpm = 35;
    private const ushort Epoch = 1;
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>At 35 WPM: dit = 1200/35 ≈ 34.3ms, dah = 102.9ms.</summary>
    private static readonly double DitMs = 1200.0 / SpeedWpm;
    private static readonly double DahMs = DitMs * 3.0;

    private readonly ITestOutputHelper _output;
    private readonly RecordingKeyingOutput _keyingOutput;
    private readonly EdgeReplayer _replayer;

    public LoopbackTimingTests(ITestOutputHelper output)
    {
        _output = output;
        _keyingOutput = new RecordingKeyingOutput();

        // Use a small fixed jitter delay (30ms) so edges fire quickly.
        var config = new JitterBufferConfig(
            DirectDelay: TimeSpan.FromMilliseconds(30),
            DerpDelay: TimeSpan.FromMilliseconds(200),
            AdaptiveMode: false);

        _replayer = new EdgeReplayer(
            clock: null,
            jitterConfig: config,
            pttTiming: null,
            EdgeJitterProfile.PathAdaptive)
        {
            Path = PathType.Direct,
        };
    }

    public void Dispose()
    {
        _replayer.Dispose();
        _keyingOutput.Dispose();
    }

    /// <summary>
    /// Generates a stream of Morse edges at 35 WPM using the KeyerElementPump,
    /// wraps them in RWK-PADDLE frames with redundancy, feeds them through the
    /// EdgeReplayer, and verifies all intervals are within ±2ms of expected.
    /// </summary>
    [Fact]
    public void EdgeReplayer_ReproducesTimingWithin2ms_At35Wpm()
    {
        // Generate a known Morse sequence: "PARIS " repeated (the WPM standard word).
        // We'll use host-text path to generate edges deterministically.
        var clock = new StopwatchClock();
        var pump = new KeyerElementPump(clock);
        pump.SpeedWpm = SpeedWpm;
        pump.Weight = 50;

        // Collect edges from the pump
        var edges = new List<EdgeEvent>();
        pump.EdgeGenerated += (_, e) => edges.Add(e);

        // Generate edges for "PARIS PARIS " (enough for meaningful timing validation)
        pump.EnqueueText("PARIS PARIS ");

        // Run the pump until all text is sent (pump in synchronous style)
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TestDuration)
        {
            var result = pump.Pump(() => false);
            if (result == PumpAction.Idle && !pump.HasPendingText)
                break;
        }

        Assert.True(edges.Count >= 4,
            $"Expected at least 4 edges from the pump, got {edges.Count}");

        _output.WriteLine($"Generated {edges.Count} edges from pump at {SpeedWpm} WPM");

        // Convert EdgeEvents to EdgeEntries with session-relative timestamps.
        long baseQpc = edges[0].QpcTimestamp;
        long frequency = clock.Frequency;
        var entries = new List<EdgeEntry>();
        uint seq = 1;

        foreach (var edge in edges)
        {
            uint timestampMs = (uint)(((edge.QpcTimestamp - baseQpc) * 1000) / frequency);
            entries.Add(new EdgeEntry(seq++, timestampMs,
                edge.KeyDown ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp));
        }

        // Start the replayer
        _replayer.Start(_keyingOutput, pttOutput: null);
        _replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => _replayer.IsSessionActive));

        _keyingOutput.Restart();

        // Feed entries as RWK-PADDLE frames with redundancy (up to 3 previous edges per frame)
        for (int i = 0; i < entries.Count; i++)
        {
            var frameEdges = new List<EdgeEntry>();
            // Current edge first
            frameEdges.Add(entries[i]);
            // Add up to 3 redundant previous edges
            for (int r = 1; r <= 3 && i - r >= 0; r++)
                frameEdges.Add(entries[i - r]);

            var frame = RwkPaddleFrame.Create(Epoch, frameEdges.ToArray());
            Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
            Assert.True(frame.TryWrite(buffer, out int written));
            _replayer.ProcessDatagram(buffer[..written]);

            // Small delay between frames to simulate real-time-ish delivery
            Thread.Sleep(1);
        }

        // Wait for all edges to be replayed
        int expectedTransitions = entries.Count;
        Assert.True(WaitFor(() => _keyingOutput.KeyTransitions.Count >= expectedTransitions,
            timeout: TimeSpan.FromSeconds(15)),
            $"Only {_keyingOutput.KeyTransitions.Count}/{expectedTransitions} transitions replayed");

        _replayer.Stop();

        // Verify timing accuracy
        var transitions = _keyingOutput.KeyTransitions;
        _output.WriteLine($"Replayed {transitions.Count} transitions");

        // Compare intervals between consecutive transitions against expected intervals
        double maxError = 0;
        int intervalsChecked = 0;

        for (int i = 1; i < Math.Min(transitions.Count, entries.Count); i++)
        {
            double actualIntervalMs = transitions[i].ElapsedMs - transitions[i - 1].ElapsedMs;
            double expectedIntervalMs = entries[i].TimestampMs - entries[i - 1].TimestampMs;

            // Skip intervals < 5ms (these can be noise from element spacing at high WPM)
            if (expectedIntervalMs < 5)
                continue;

            double error = Math.Abs(actualIntervalMs - expectedIntervalMs);
            maxError = Math.Max(maxError, error);
            intervalsChecked++;

            _output.WriteLine(
                $"  Interval {i}: expected={expectedIntervalMs:F1}ms, actual={actualIntervalMs:F1}ms, error={error:F2}ms");

            // ±2ms tolerance per the spec
            Assert.True(error <= 2.0,
                $"Interval {i}: expected {expectedIntervalMs:F1}ms, got {actualIntervalMs:F1}ms " +
                $"(error={error:F2}ms exceeds ±2ms tolerance). " +
                $"Validates: Requirements 7.5, 14.5");
        }

        Assert.True(intervalsChecked >= 5,
            $"Only {intervalsChecked} intervals checked; expected at least 5 meaningful intervals");

        _output.WriteLine($"Max timing error: {maxError:F2}ms across {intervalsChecked} intervals (target: ±2ms)");
        _output.WriteLine($"Telemetry: MaxReplayErrorMs={_replayer.Telemetry.MaxReplayErrorMs:F2}ms");
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(2);
        }
        return condition();
    }
}
