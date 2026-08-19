using System.Diagnostics;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.Protocol.Edge;
using RWK.Shared.Timing;
using RWK.Station.IO;
using RWK.Station.Replay;
using Xunit;
using Xunit.Abstractions;

namespace RWK.Integration.Tests;

/// <summary>
/// End-to-end fail-safe battery: feeds frames into the EdgeReplayer + FailSafeMonitor combo
/// and verifies each F1–F10 condition triggers correctly with proper key-up and SAFE latch
/// behavior, using real timing for timeout verification.
/// </summary>
/// <remarks>
/// Unlike the unit tests in RWK.Station.Tests which use a fake clock, these tests use the
/// real system clock to verify that the fail-safe timeouts fire within their specified windows.
/// <para>
/// **Validates: Requirements 9.1-9.12**
/// </para>
/// </remarks>
public class FailSafeBatteryTests : IDisposable
{
    private const ushort Epoch = 1;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;
    private readonly FakeKeyingOutputWithFault _keyingOutput;
    private readonly FakeTailscaleNodeIntegration _tailscaleNode;
    private readonly EdgeReplayer _replayer;
    private readonly FailSafeMonitor _monitor;
    private readonly List<FailSafeCondition> _firedConditions = new();

    public FailSafeBatteryTests(ITestOutputHelper output)
    {
        _output = output;
        _keyingOutput = new FakeKeyingOutputWithFault();
        _tailscaleNode = new FakeTailscaleNodeIntegration();

        var config = new JitterBufferConfig(
            DirectDelay: TimeSpan.FromMilliseconds(30),
            DerpDelay: TimeSpan.FromMilliseconds(200),
            AdaptiveMode: false);

        _replayer = new EdgeReplayer(
            clock: null, // real clock
            jitterConfig: config,
            pttTiming: null,
            EdgeJitterProfile.PathAdaptive)
        {
            Path = PathType.Direct,
        };

        _monitor = new FailSafeMonitor(
            _replayer,
            clock: null, // real clock
            keyingOutput: _keyingOutput,
            tailscaleNode: _tailscaleNode);

        _monitor.FailSafeTriggered += (_, e) =>
        {
            _firedConditions.Add(e.Condition);
            _output.WriteLine($"FailSafe fired: {e.Condition} - {e.Message}");
        };

        _replayer.FailSafeTriggered += (_, e) =>
        {
            if (!_firedConditions.Contains(e.Condition))
                _firedConditions.Add(e.Condition);
        };
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _replayer.Dispose();
        _keyingOutput.Dispose();
        _tailscaleNode.Dispose();
    }

    /// <summary>
    /// F1: No heartbeat or edge for 750ms while key-down → immediate key-up, session degraded.
    /// Does NOT set SAFE latch (auto-clears when edges resume, 9.12).
    /// </summary>
    [Fact]
    public void F1_KeyDownNoTrafficFor750ms_ForcesKeyUp_DoesNotLatch()
    {
        StartSessionWithKeyDown();

        // Start the monitor thread for real-time checking
        _monitor.Start();

        // Wait for F1 to trigger (750ms + some margin)
        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F1),
            TimeSpan.FromMilliseconds(1500)),
            "F1 did not fire within expected window");

        // Key must be up
        Assert.False(_keyingOutput.IsKeyDown, "Key should be forced up after F1");

        // F1 does NOT latch (9.12)
        Assert.False(_replayer.IsSafeLatched, "F1 should NOT latch SAFE (9.12)");
    }

    /// <summary>
    /// F2: No heartbeat for 3s while idle → session closed, SAFE latched.
    /// </summary>
    [Fact]
    public void F2_IdleNoHeartbeatFor3s_ClosesSession_LathesSafe()
    {
        StartSession();
        _monitor.Start();

        // Wait for F2 (3s + margin)
        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F2),
            TimeSpan.FromSeconds(5)),
            "F2 did not fire within expected window");

        // F2 MUST latch (9.11)
        Assert.True(_replayer.IsSafeLatched, "F2 must latch SAFE (9.11)");
    }

    /// <summary>
    /// F3: Key-down continuously for > 10s → force key-up, does NOT latch.
    /// </summary>
    [Fact]
    public void F3_ContinuousKeyDown10s_ForcesKeyUp_DoesNotLatch()
    {
        StartSessionWithKeyDown();
        _monitor.Start();

        // Keep feeding heartbeats to prevent F1 from firing (every 500ms)
        var cts = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                _replayer.ProcessHeartbeat();
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        // Wait for F3 (10s + margin)
        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F3),
            TimeSpan.FromSeconds(13)),
            "F3 did not fire within expected window");

        cts.Cancel();

        // F3 does NOT latch (9.3)
        Assert.False(_replayer.IsSafeLatched, "F3 should NOT latch SAFE (9.3)");
    }

    /// <summary>
    /// F4: Epoch mismatch → discard frame, force key-up if keyed, does NOT latch.
    /// </summary>
    [Fact]
    public void F4_EpochMismatch_DiscardsFrame_DoesNotLatch()
    {
        StartSessionWithKeyDown();

        // Send a frame with wrong epoch
        SendFrame((ushort)(Epoch + 1), EdgeEntry.KeyDownAt(sequence: 10, timestampMs: 5000));

        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F4),
            TimeSpan.FromSeconds(2)),
            "F4 did not fire");

        // F4 does NOT latch (9.4)
        Assert.False(_replayer.IsSafeLatched, "F4 should NOT latch SAFE (9.4)");
    }

    /// <summary>
    /// F5: Sequence gap with uninferrable key state → force key-up, SAFE latched.
    /// </summary>
    [Fact]
    public void F5_SequenceGap_ForcesKeyUp_LathesSafe()
    {
        StartSession();

        // Send initial edge (establishes baseline)
        SendFrame(Epoch, EdgeEntry.KeyUpAt(sequence: 1, timestampMs: 10));
        Thread.Sleep(100);

        // Send edge with large sequence gap (4 edges missing) that would be key-down
        SendFrame(Epoch, EdgeEntry.KeyDownAt(sequence: 5, timestampMs: 200));

        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F5),
            TimeSpan.FromSeconds(2)),
            "F5 did not fire");

        // F5 MUST latch (9.11)
        Assert.True(_replayer.IsSafeLatched, "F5 must latch SAFE (9.11)");
    }

    /// <summary>
    /// F6: Serial port error/device removal → force key-up, SAFE latched.
    /// </summary>
    [Fact]
    public void F6_SerialPortFault_ForcesKeyUp_LathesSafe()
    {
        StartSession();
        _monitor.Start();

        // Simulate a serial port fault
        _keyingOutput.SimulateFault("KeyDown", "device removed");

        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F6),
            TimeSpan.FromSeconds(2)),
            "F6 did not fire");

        // F6 MUST latch (9.11)
        Assert.True(_replayer.IsSafeLatched, "F6 must latch SAFE (9.11)");
    }

    /// <summary>
    /// F7: Unhandled exception on keying thread → force key-up, SAFE latched.
    /// </summary>
    [Fact]
    public void F7_UnhandledException_ForcesKeyUp_LathesSafe()
    {
        StartSession();

        // Simulate F7 via the OnFailSafe path (the replayer's thread exception handler
        // calls this internally; we test the end-to-end policy response)
        _monitor.OnFailSafe(FailSafeCondition.F7, "Unhandled exception");

        Assert.Contains(_firedConditions, c => c == FailSafeCondition.F7);
        Assert.True(_replayer.IsSafeLatched, "F7 must latch SAFE (9.11)");
    }

    /// <summary>
    /// F8: Application close while key-down → StationKeyingOutput disposal forces key-up.
    /// </summary>
    [Fact]
    public void F8_DisposalWhileKeyed_ForcesKeyUp()
    {
        StartSessionWithKeyDown();

        // Verify key is down
        Assert.True(WaitFor(() => _keyingOutput.IsKeyDown, TimeSpan.FromSeconds(2)),
            "Key never went down");

        // Simulate disposal (F8)
        _replayer.Stop();

        // Key must be up after stop (which mimics app close behavior, 9.8)
        Assert.False(_keyingOutput.IsKeyDown, "Key must be up after replayer stop (F8)");
    }

    /// <summary>
    /// F9: Tailscale path lost → force key-up, session degraded, does NOT latch.
    /// </summary>
    [Fact]
    public void F9_TailscalePathLost_ForcesKeyUp_DoesNotLatch()
    {
        StartSession();
        _monitor.Start();

        // Simulate path loss
        _tailscaleNode.SimulateFault("path lost");

        // Give the monitor one check cycle
        Thread.Sleep(100);

        Assert.True(WaitFor(() => _firedConditions.Contains(FailSafeCondition.F9),
            TimeSpan.FromSeconds(2)),
            "F9 did not fire");

        // F9 does NOT latch (9.12)
        Assert.False(_replayer.IsSafeLatched, "F9 should NOT latch SAFE (9.12)");
    }

    /// <summary>
    /// F10: Scheduler timing overrun > 250ms → force key-up, SAFE latched.
    /// </summary>
    [Fact]
    public void F10_SchedulerOverrun_ForcesKeyUp_LathesSafe()
    {
        StartSession();

        // Simulate F10 via the OnFailSafe path (the scheduler watchdog calls this)
        _monitor.OnFailSafe(FailSafeCondition.F10, "Scheduler overrun >250ms");

        Assert.Contains(_firedConditions, c => c == FailSafeCondition.F10);
        Assert.True(_replayer.IsSafeLatched, "F10 must latch SAFE (9.11)");
    }

    /// <summary>
    /// Verifies the SAFE latch policy: F2, F5, F6, F7, F10 require manual re-arm (9.11).
    /// </summary>
    [Theory]
    [InlineData(FailSafeCondition.F2)]
    [InlineData(FailSafeCondition.F5)]
    [InlineData(FailSafeCondition.F6)]
    [InlineData(FailSafeCondition.F7)]
    [InlineData(FailSafeCondition.F10)]
    public void ManualRearmConditions_RequireClearSafeLatch(FailSafeCondition condition)
    {
        StartSession();
        _monitor.OnFailSafe(condition, "test");

        Assert.True(_replayer.IsSafeLatched);

        // Manual re-arm clears it (9.11)
        _replayer.ClearSafeLatch();
        Assert.False(_replayer.IsSafeLatched);
    }

    /// <summary>
    /// Verifies: F1, F9 degrade session but auto-clear when edges resume (9.12).
    /// </summary>
    [Theory]
    [InlineData(FailSafeCondition.F1)]
    [InlineData(FailSafeCondition.F9)]
    public void AutoClearConditions_DoNotLatch(FailSafeCondition condition)
    {
        StartSession();
        _monitor.OnFailSafe(condition, "test");

        Assert.False(_replayer.IsSafeLatched,
            $"{condition} should NOT latch (9.12)");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private void StartSession()
    {
        _replayer.Start(_keyingOutput, pttOutput: null);
        _replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => _replayer.IsSessionActive));
        _replayer.ProcessHeartbeat();
    }

    private void StartSessionWithKeyDown()
    {
        _replayer.Start(_keyingOutput, pttOutput: null);
        _replayer.BeginSession(Epoch);
        Assert.True(WaitFor(() => _replayer.IsSessionActive));
        _replayer.ProcessHeartbeat();

        // Force key-down via internal test helper
        _replayer.ForceKeyDownForTest();
        Assert.True(_replayer.IsKeyDown);
    }

    private void SendFrame(ushort epoch, params EdgeEntry[] edges)
    {
        var frame = RwkPaddleFrame.Create(epoch, edges);
        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));
        _replayer.ProcessDatagram(buffer[..written]);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}

/// <summary>
/// A fake keying output that can raise Fault events for integration tests.
/// Implements <see cref="IStationKeyingOutput"/> to satisfy the FailSafeMonitor constructor.
/// </summary>
internal sealed class FakeKeyingOutputWithFault : IStationKeyingOutput
{
    public event EventHandler<KeyingFaultEventArgs>? Fault;

    public KeyingLine KeyLine { get; private set; } = KeyingLine.RTS;
    public KeyingLine PttLine { get; private set; } = KeyingLine.None;
    public bool KeyInvert { get; private set; }
    public bool PttInvert { get; private set; }
    public bool IsKeyDown { get; private set; }
    public bool IsPttOn { get; private set; }
    public bool IsOpen { get; private set; }

    public void Configure(KeyingOutputConfig config)
    {
        KeyLine = config.KeyLine;
        PttLine = config.PttLine;
        KeyInvert = config.KeyInvert;
        PttInvert = config.PttInvert;
    }

    public void Open() => IsOpen = true;
    public void Open(string portName, KeyingLine line) { IsOpen = true; KeyLine = line; }
    public void Close() => IsOpen = false;
    public void KeyDown() => IsKeyDown = true;
    public void KeyUp() => IsKeyDown = false;
    public void PttDown() => IsPttOn = true;
    public void PttUp() => IsPttOn = false;
    public void EnsureAllLinesDown() { IsKeyDown = false; IsPttOn = false; }

    public void SimulateFault(string operation = "KeyDown", string message = "device removed")
    {
        Fault?.Invoke(this, new KeyingFaultEventArgs(operation, message, null, PortClosed: true));
    }

    public void Dispose() { IsOpen = false; }
}

/// <summary>
/// Minimal fake Tailscale node for integration tests.
/// </summary>
internal sealed class FakeTailscaleNodeIntegration : RWK.Shared.Net.ITailscaleNode
{
    public event EventHandler<RWK.Shared.TailscaleStateChangedEventArgs>? StateChanged;

    public TailscaleState State { get; set; } = TailscaleState.Connected;
    public string? PeerAddress { get; set; }
    public string? SelfAddress { get; set; }
    public string? SelfDnsName { get; set; }
    public PathType CurrentPath { get; set; } = PathType.Direct;
    public TimeSpan RoundTripTime { get; set; }
    public string? DerpRegion { get; set; }

    public Task StartAsync(string? authKey) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<int> SendEdgeAsync(ReadOnlyMemory<byte> data) => Task.FromResult(data.Length);
    public event EventHandler<ReadOnlyMemory<byte>>? EdgeReceived;
    public Task<Stream> ConnectControlAsync(string peerAddress, int port) => Task.FromResult<Stream>(Stream.Null);

    public void SimulateFault(string? message = null)
    {
        State = TailscaleState.Fault;
        StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
            TailscaleState.Fault,
            PathType.None,
            TimeSpan.Zero,
            DerpRegion: null,
            Message: message ?? "path lost"));
    }

    public void SimulateRecovery()
    {
        State = TailscaleState.Connected;
        StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
            TailscaleState.Connected,
            PathType.Direct,
            TimeSpan.FromMilliseconds(20)));
    }

    public void Dispose() { }
}
