using RWK.Shared;
using RWK.Shared.Net;
using RWK.Shared.Timing;
using RWK.Station.IO;

namespace RWK.Station.Replay;

/// <summary>
/// Watchdog-style component that monitors the Edge Replayer's timing state and triggers
/// fail-safe conditions the replayer cannot detect itself: heartbeat timeouts (F1, F2),
/// continuous key-down (F3), serial port faults (F6), Tailscale path loss (F9).
/// </summary>
/// <remarks>
/// <para>
/// Runs a 50ms check thread per requirement 14.8, reading <see cref="EdgeReplayer.LastHeartbeatQpc"/>,
/// <see cref="EdgeReplayer.LastInboundQpc"/>, and <see cref="EdgeReplayer.IsKeyDown"/> to detect
/// timing conditions.
/// </para>
/// <para>
/// Also implements <see cref="IFailSafeSink"/> so the replayer can report conditions it does detect
/// (F4, F5, F7, F10) and the monitor applies the correct latch policy.
/// </para>
/// <para>
/// Latch policy (9.11, 9.12):
/// <list type="bullet">
///   <item><description>Manual Re-Arm required: F2, F5, F6, F7, F10</description></item>
///   <item><description>Auto-clear when valid edges resume: F1, F9</description></item>
///   <item><description>No latch: F3 (key-up only), F4 (frame discarded), F8 (shutdown)</description></item>
/// </list>
/// </para>
/// <para>
/// _Requirements: 9.1, 9.2, 9.3, 9.6, 9.9, 9.11, 9.12, 14.8_
/// </para>
/// </remarks>
public sealed class FailSafeMonitor : IFailSafeSink, IDisposable
{
    /// <summary>Check interval for the monitoring thread (14.8).</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>F1 threshold: 750ms no heartbeat/edge while key-down (9.1).</summary>
    public static readonly TimeSpan F1Timeout = TimeSpan.FromMilliseconds(750);

    /// <summary>F2 threshold: 3s no heartbeat while idle (9.2).</summary>
    public static readonly TimeSpan F2Timeout = TimeSpan.FromSeconds(3);

    /// <summary>F3 threshold: continuous key-down 10s (9.3).</summary>
    public static readonly TimeSpan F3MaxDown = TimeSpan.FromSeconds(10);

    private const string F1Message = "No heartbeat or edge for 750ms while key-down; key forced up, session degraded (F1).";
    private const string F2Message = "No heartbeat for 3 seconds while idle; session closed, SAFE latched (F2).";
    private const string F3Message = "Key has been down continuously for >10 seconds; key forced up (F3).";
    private const string F6Message = "Serial port error or device removal; SAFE latched (F6).";
    private const string F9Message = "Tailscale path lost; key forced up, session degraded (F9).";

    private readonly EdgeReplayer _replayer;
    private readonly ISystemClock _clock;
    private readonly long _frequency;
    private readonly long _f1Ticks;
    private readonly long _f2Ticks;
    private readonly long _f3Ticks;
    private readonly long _checkIntervalMs;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _disposed;

    // F3 tracking: when did continuous key-down start?
    private long _keyDownStartQpc;
    private bool _wasKeyDown;

    // F9 tracking
    private volatile bool _tailscaleFaulted;

    /// <summary>Creates a fail-safe monitor for the given replayer.</summary>
    /// <param name="replayer">The edge replayer to monitor.</param>
    /// <param name="clock">Clock for timestamp comparisons; defaults to <see cref="StopwatchClock"/>.</param>
    /// <param name="keyingOutput">Optional keying output to subscribe to Fault events (F6).</param>
    /// <param name="tailscaleNode">Optional Tailscale node to subscribe to StateChanged events (F9).</param>
    public FailSafeMonitor(
        EdgeReplayer replayer,
        ISystemClock? clock = null,
        IStationKeyingOutput? keyingOutput = null,
        ITailscaleNode? tailscaleNode = null)
    {
        _replayer = replayer ?? throw new ArgumentNullException(nameof(replayer));
        _clock = clock ?? new StopwatchClock();
        _frequency = _clock.Frequency > 0 ? _clock.Frequency : 1;

        _f1Ticks = TicksForMs(750);
        _f2Ticks = TicksForMs(3000);
        _f3Ticks = TicksForMs(10_000);
        _checkIntervalMs = 50;

        // Wire ourselves as the replayer's fail-safe sink so we receive F4/F5/F7/F10.
        _replayer.FailSafeSink = this;

        if (keyingOutput is not null)
        {
            keyingOutput.Fault += OnKeyingFault;
        }

        if (tailscaleNode is not null)
        {
            tailscaleNode.StateChanged += OnTailscaleStateChanged;
        }
    }

    /// <summary>Raised when any fail-safe condition fires, for UI display.</summary>
    public event EventHandler<FailSafeTriggeredEventArgs>? FailSafeTriggered;

    /// <summary>Whether the monitor thread is running.</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Starts the 50ms check thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning) return;

        _stopRequested = false;
        _wasKeyDown = false;
        _keyDownStartQpc = 0;
        _tailscaleFaulted = false;

        _thread = new Thread(MonitorLoop)
        {
            Name = "RWK-FailSafeMonitor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Stops the monitor thread.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromMilliseconds(200));
        _thread = null;
    }

    /// <inheritdoc/>
    public void OnFailSafe(FailSafeCondition condition, string message)
    {
        // Apply latch policy for conditions the replayer detected itself.
        ApplyLatchPolicy(condition, message);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ─── Monitor loop ────────────────────────────────────────────────────────────

    private void MonitorLoop()
    {
        while (!_stopRequested)
        {
            try
            {
                CheckConditions();
            }
            catch
            {
                // The monitor must not crash; absorb and continue.
            }

            Thread.Sleep((int)_checkIntervalMs);
        }
    }

    internal void CheckConditions()
    {
        if (!_replayer.IsSessionActive || _replayer.IsSafeLatched)
        {
            _wasKeyDown = false;
            _keyDownStartQpc = 0;
            return;
        }

        long now = _clock.GetTimestamp();
        bool isKeyDown = _replayer.IsKeyDown;
        long lastHeartbeat = _replayer.LastHeartbeatQpc;
        long lastInbound = _replayer.LastInboundQpc;

        // Use the more recent of heartbeat and inbound for F1 (any traffic resets the timer).
        long lastTraffic = lastHeartbeat > lastInbound ? lastHeartbeat : lastInbound;

        // F1: 750ms no traffic while key-down
        if (isKeyDown && lastTraffic > 0)
        {
            long elapsed = now - lastTraffic;
            if (elapsed >= _f1Ticks)
            {
                TriggerF1();
                return;
            }
        }

        // F2: 3s no heartbeat while idle (key-up)
        if (!isKeyDown && lastHeartbeat > 0)
        {
            long elapsed = now - lastHeartbeat;
            if (elapsed >= _f2Ticks)
            {
                TriggerF2();
                return;
            }
        }

        // F3: continuous key-down > 10s
        if (isKeyDown)
        {
            if (!_wasKeyDown)
            {
                // Key just went down — mark the start.
                _keyDownStartQpc = now;
                _wasKeyDown = true;
            }
            else if (_keyDownStartQpc > 0)
            {
                long downDuration = now - _keyDownStartQpc;
                if (downDuration >= _f3Ticks)
                {
                    TriggerF3();
                    _wasKeyDown = false;
                    _keyDownStartQpc = 0;
                    return;
                }
            }
        }
        else
        {
            _wasKeyDown = false;
            _keyDownStartQpc = 0;
        }

        // F9: Tailscale fault detected by event handler
        if (_tailscaleFaulted)
        {
            _tailscaleFaulted = false;
            TriggerF9();
        }
    }

    // ─── Condition triggers ──────────────────────────────────────────────────────

    private void TriggerF1()
    {
        _replayer.ForceKeyUp();
        ApplyLatchPolicy(FailSafeCondition.F1, F1Message);
    }

    private void TriggerF2()
    {
        _replayer.EndSession();
        ApplyLatchPolicy(FailSafeCondition.F2, F2Message);
    }

    private void TriggerF3()
    {
        _replayer.ForceKeyUp();
        // F3 does not latch (9.3): just force key-up.
        RaiseFailSafe(FailSafeCondition.F3, F3Message);
    }

    private void TriggerF9()
    {
        _replayer.ForceKeyUp();
        ApplyLatchPolicy(FailSafeCondition.F9, F9Message);
    }

    // ─── External event handlers ─────────────────────────────────────────────────

    private void OnKeyingFault(object? sender, KeyingFaultEventArgs e)
    {
        // F6: serial port error. Force key-up and latch.
        _replayer.ForceKeyUp();
        ApplyLatchPolicy(FailSafeCondition.F6, F6Message + " " + e.Message);
    }

    private void OnTailscaleStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        if (e.State == TailscaleState.Fault)
        {
            // Set the flag; the monitor loop will pick it up on its next check.
            _tailscaleFaulted = true;
        }
    }

    // ─── Latch policy ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the correct latch policy for a fail-safe condition per requirements 9.11 and 9.12.
    /// </summary>
    private void ApplyLatchPolicy(FailSafeCondition condition, string message)
    {
        switch (condition)
        {
            // Manual Re-Arm required (9.11)
            case FailSafeCondition.F2:
            case FailSafeCondition.F5:
            case FailSafeCondition.F6:
            case FailSafeCondition.F7:
            case FailSafeCondition.F10:
                _replayer.LatchSafe(condition, message);
                break;

            // Auto-clear when valid edges resume (9.12) — degrade but don't hard-latch
            case FailSafeCondition.F1:
            case FailSafeCondition.F9:
                // The replayer already forced key-up. Mark as degraded but allow edges to resume.
                // The "auto-clear" means we do NOT latch here. The replayer's normal edge
                // processing path will naturally resume when edges arrive.
                break;

            // No latch: F3 (key-up only), F4 (frame discarded), F8 (shutdown)
            case FailSafeCondition.F3:
            case FailSafeCondition.F4:
            case FailSafeCondition.F8:
                break;
        }

        RaiseFailSafe(condition, message);
    }

    private void RaiseFailSafe(FailSafeCondition condition, string message)
    {
        try
        {
            FailSafeTriggered?.Invoke(this, new FailSafeTriggeredEventArgs(condition, message));
        }
        catch
        {
            // UI subscriber faults must not break the monitor.
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private long TicksForMs(long ms) => (ms * _frequency) / 1000;
}
