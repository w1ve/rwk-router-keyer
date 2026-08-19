using RWK.Shared;
using RWK.Shared.Config;
using RWK.Station.Replay;
using RWK.Station.Tests.TestDoubles;
using Xunit;

namespace RWK.Station.Tests.Replay;

/// <summary>
/// Unit tests for <see cref="FailSafeMonitor"/> covering each fail-safe condition in isolation.
/// Uses a single <see cref="FakeClock"/> shared between the replayer and monitor so that all
/// timestamp comparisons are coherent and fully deterministic.
/// </summary>
/// <remarks>
/// The replayer thread is NOT started. Instead, the replayer is used in its no-thread fallback path
/// (BeginSession/ProcessHeartbeat apply immediately when no thread is running) and
/// <see cref="EdgeReplayer.ForceKeyDownForTest"/> provides key-down state.
/// <para>
/// _Validates: Requirements 9.1, 9.2, 9.3, 9.6, 9.9, 9.11, 9.12_
/// </para>
/// </remarks>
public class FailSafeMonitorTests : IDisposable
{
    private const ushort Epoch = 1;

    private readonly FakeClock _clock;
    private readonly FakeKeyingOutput _keyingOutput;
    private readonly FakeTailscaleNode _tailscaleNode;
    private readonly EdgeReplayer _replayer;
    private readonly FailSafeMonitor _monitor;
    private readonly List<(FailSafeCondition Condition, string Message)> _firedConditions = new();

    public FailSafeMonitorTests()
    {
        // Single clock shared by replayer and monitor ensures timestamp coherence.
        // Frequency 10 MHz matches typical Windows QPC; no auto-advance so time is manual.
        _clock = new FakeClock(10_000_000, autoAdvanceStep: 0) { Frequency = 10_000_000L };
        _keyingOutput = new FakeKeyingOutput();
        _tailscaleNode = new FakeTailscaleNode();

        _replayer = new EdgeReplayer(
            clock: _clock,
            jitterConfig: new JitterBufferConfig(
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(200),
                AdaptiveMode: false));

        _monitor = new FailSafeMonitor(
            _replayer,
            clock: _clock,
            keyingOutput: _keyingOutput,
            tailscaleNode: _tailscaleNode);

        _monitor.FailSafeTriggered += (_, e) =>
            _firedConditions.Add((e.Condition, e.Message));
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _replayer.Dispose();
        _keyingOutput.Dispose();
        _tailscaleNode.Dispose();
    }

    // ─── F1: 750ms no heartbeat while key-down ───────────────────────────────────

    [Fact]
    public void F1_TriggersWhenKeyDownAndNoTrafficFor750ms()
    {
        StartSessionWithKeyDown();

        // Advance clock past 750ms threshold
        _clock.AdvanceMs(800);

        _monitor.CheckConditions();

        Assert.Contains(_firedConditions, f => f.Condition == FailSafeCondition.F1);
    }

    [Fact]
    public void F1_DoesNotTriggerBeforeThreshold()
    {
        StartSessionWithKeyDown();

        // Advance only 500ms — under the 750ms threshold
        _clock.AdvanceMs(500);

        _monitor.CheckConditions();

        Assert.DoesNotContain(_firedConditions, f => f.Condition == FailSafeCondition.F1);
    }

    [Fact]
    public void F1_DoesNotLatch_AutoClearsWhenEdgesResume()
    {
        StartSessionWithKeyDown();
        _clock.AdvanceMs(800);
        _monitor.CheckConditions();

        // F1 fired but should NOT have latched SAFE
        Assert.False(_replayer.IsSafeLatched,
            "F1 should degrade session, not latch (9.12).");
    }

    // ─── F2: 3s no heartbeat while idle ──────────────────────────────────────────

    [Fact]
    public void F2_TriggersWhenIdleAndNoHeartbeatFor3s()
    {
        StartSession();

        // Key is up (idle), advance past 3s
        _clock.AdvanceMs(3100);

        _monitor.CheckConditions();

        Assert.Contains(_firedConditions, f => f.Condition == FailSafeCondition.F2);
    }

    [Fact]
    public void F2_DoesNotTriggerBeforeThreshold()
    {
        StartSession();

        _clock.AdvanceMs(2000);

        _monitor.CheckConditions();

        Assert.DoesNotContain(_firedConditions, f => f.Condition == FailSafeCondition.F2);
    }

    [Fact]
    public void F2_LatchesSafe_RequiresManualRearm()
    {
        StartSession();
        _clock.AdvanceMs(3100);

        _monitor.CheckConditions();

        Assert.True(_replayer.IsSafeLatched,
            "F2 must latch SAFE requiring manual Re-Arm (9.11).");
    }

    // ─── F3: continuous key-down > 10s ───────────────────────────────────────────

    [Fact]
    public void F3_TriggersWhenKeyDownContinuouslyFor10s()
    {
        StartSessionWithKeyDown();

        // First check marks the start of key-down (_keyDownStartQpc = now).
        _monitor.CheckConditions();

        // Advance past 10s. To prevent F1 from firing first, refresh heartbeat at intervals
        // below the 750ms F1 threshold.
        AdvanceWithHeartbeats(10_100);

        _monitor.CheckConditions();

        Assert.Contains(_firedConditions, f => f.Condition == FailSafeCondition.F3);
    }

    [Fact]
    public void F3_DoesNotLatch()
    {
        StartSessionWithKeyDown();
        _monitor.CheckConditions();
        AdvanceWithHeartbeats(10_100);
        _monitor.CheckConditions();

        // F3 does NOT latch (9.3)
        Assert.False(_replayer.IsSafeLatched,
            "F3 must NOT latch; it is for TUNE scenarios (9.3).");
    }

    [Fact]
    public void F3_DoesNotTriggerBeforeThreshold()
    {
        StartSessionWithKeyDown();
        _monitor.CheckConditions();
        AdvanceWithHeartbeats(8_000);
        _monitor.CheckConditions();

        Assert.DoesNotContain(_firedConditions, f => f.Condition == FailSafeCondition.F3);
    }

    // ─── F6: serial port fault ───────────────────────────────────────────────────

    [Fact]
    public void F6_TriggersOnKeyingFaultEvent()
    {
        StartSession();

        // Act: simulate a serial port fault
        _keyingOutput.SimulateFault("KeyDown", "device removed");

        // Assert
        Assert.Contains(_firedConditions, f => f.Condition == FailSafeCondition.F6);
    }

    [Fact]
    public void F6_LatchesSafe()
    {
        StartSession();
        _keyingOutput.SimulateFault();

        Assert.True(_replayer.IsSafeLatched,
            "F6 must latch SAFE requiring manual Re-Arm (9.11).");
    }

    // ─── F9: Tailscale path lost ─────────────────────────────────────────────────

    [Fact]
    public void F9_TriggersOnTailscaleFault()
    {
        StartSession();

        // Simulate path loss
        _tailscaleNode.SimulateFault("path lost");

        // The monitor picks this up on its next check
        _monitor.CheckConditions();

        Assert.Contains(_firedConditions, f => f.Condition == FailSafeCondition.F9);
    }

    [Fact]
    public void F9_DoesNotLatch_AutoClears()
    {
        StartSession();
        _tailscaleNode.SimulateFault();
        _monitor.CheckConditions();

        Assert.False(_replayer.IsSafeLatched,
            "F9 should degrade session, not latch (9.12).");
    }

    // ─── F5: sequence gap (latch policy via OnFailSafe) ──────────────────────────

    [Fact]
    public void F5_LatchesSafe_ViaOnFailSafe()
    {
        StartSession();

        _monitor.OnFailSafe(FailSafeCondition.F5, "Sequence gap");

        Assert.True(_replayer.IsSafeLatched,
            "F5 must latch SAFE requiring manual Re-Arm (9.11).");
    }

    // ─── F7: unhandled exception (latch policy via OnFailSafe) ───────────────────

    [Fact]
    public void F7_LatchesSafe_ViaOnFailSafe()
    {
        StartSession();

        _monitor.OnFailSafe(FailSafeCondition.F7, "Unhandled exception");

        Assert.True(_replayer.IsSafeLatched,
            "F7 must latch SAFE requiring manual Re-Arm (9.11).");
    }

    // ─── F10: scheduler overrun (latch policy via OnFailSafe) ────────────────────

    [Fact]
    public void F10_LatchesSafe_ViaOnFailSafe()
    {
        StartSession();

        _monitor.OnFailSafe(FailSafeCondition.F10, "Scheduler overrun");

        Assert.True(_replayer.IsSafeLatched,
            "F10 must latch SAFE requiring manual Re-Arm (9.11).");
    }

    // ─── F4: epoch mismatch (no latch) ───────────────────────────────────────────

    [Fact]
    public void F4_DoesNotLatch_ViaOnFailSafe()
    {
        StartSession();

        _monitor.OnFailSafe(FailSafeCondition.F4, "Epoch mismatch");

        Assert.False(_replayer.IsSafeLatched,
            "F4 must NOT latch (discard only, 9.4).");
    }

    // ─── SAFE latch policy summary ───────────────────────────────────────────────

    [Theory]
    [InlineData(FailSafeCondition.F2)]
    [InlineData(FailSafeCondition.F5)]
    [InlineData(FailSafeCondition.F6)]
    [InlineData(FailSafeCondition.F7)]
    [InlineData(FailSafeCondition.F10)]
    public void ManualRearmConditions_LatchSafe(FailSafeCondition condition)
    {
        StartSession();
        _monitor.OnFailSafe(condition, "test");
        Assert.True(_replayer.IsSafeLatched);
    }

    [Theory]
    [InlineData(FailSafeCondition.F1)]
    [InlineData(FailSafeCondition.F9)]
    public void AutoClearConditions_DoNotLatch(FailSafeCondition condition)
    {
        StartSession();
        _monitor.OnFailSafe(condition, "test");
        Assert.False(_replayer.IsSafeLatched);
    }

    [Theory]
    [InlineData(FailSafeCondition.F3)]
    [InlineData(FailSafeCondition.F4)]
    [InlineData(FailSafeCondition.F8)]
    public void NoLatchConditions_DoNotLatch(FailSafeCondition condition)
    {
        StartSession();
        _monitor.OnFailSafe(condition, "test");
        Assert.False(_replayer.IsSafeLatched);
    }

    // ─── Monitor does not fire when no session active ────────────────────────────

    [Fact]
    public void DoesNotFireWhenNoSessionActive()
    {
        // No session started, advance past all thresholds
        _clock.AdvanceMs(10_000);
        _monitor.CheckConditions();

        Assert.Empty(_firedConditions);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a session on the replayer. The replay thread is running with a frozen fake clock
    /// (all sleeps are real 1ms Thread.Sleep but the clock doesn't advance, keeping the thread
    /// idle). Control commands are picked up via the wake signal.
    /// </summary>
    private void StartSession()
    {
        _replayer.Start(_keyingOutput, pttOutput: null);
        _replayer.BeginSession(Epoch);

        // Wait for the replay thread to apply the BeginSession control command.
        Assert.True(
            SpinWait.SpinUntil(() => _replayer.IsSessionActive, TimeSpan.FromSeconds(2)),
            "Replay thread did not apply BeginSession in time.");

        // Stamp the heartbeat so the monitor has a baseline.
        _replayer.ProcessHeartbeat();
    }

    /// <summary>
    /// Starts a session and forces the key into the down state via the internal test helper.
    /// </summary>
    private void StartSessionWithKeyDown()
    {
        _replayer.Start(_keyingOutput, pttOutput: null);
        _replayer.BeginSession(Epoch);

        Assert.True(
            SpinWait.SpinUntil(() => _replayer.IsSessionActive, TimeSpan.FromSeconds(2)),
            "Replay thread did not apply BeginSession in time.");

        _replayer.ProcessHeartbeat();

        // Force key-down via internal test helper.
        _replayer.ForceKeyDownForTest();
        Assert.True(_replayer.IsKeyDown, "Key should be down after ForceKeyDownForTest.");
    }

    /// <summary>
    /// Advances the clock by <paramref name="totalMs"/> while sending heartbeats every 500ms
    /// to keep the F1 timer from firing. Used by F3 tests that need >10s of key-down
    /// without F1 interference.
    /// </summary>
    private void AdvanceWithHeartbeats(long totalMs)
    {
        const long heartbeatInterval = 500; // < 750ms F1 threshold
        long remaining = totalMs;

        while (remaining > 0)
        {
            long step = Math.Min(remaining, heartbeatInterval);
            _clock.AdvanceMs(step);
            _replayer.ProcessHeartbeat();
            remaining -= step;
        }
    }
}
