/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Runtime;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.Interop;
using RWK.Shared.IO;
using RWK.Shared.Protocol.Edge;
using RWK.Shared.Timing;
using RWK.Station.IO;

namespace RWK.Station.Replay;

/// <summary>
/// The Station's edge replayer: parses RWK-PADDLE datagrams, validates them against the session,
/// buffers them for jitter, and keys the radio at absolute QPC deadlines from a
/// THREAD_PRIORITY_TIME_CRITICAL thread (design Component 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Division of labour.</b> This class is the plumbing between four pieces that are each testable
/// on their own: <see cref="RwkPaddleFrame"/> parses, <see cref="EdgeSequenceTracker"/> classifies
/// (epoch, duplicates, timestamp monotonicity, gaps), <see cref="JitterBuffer"/> chooses the delay,
/// and <see cref="ReplayAnchor"/> converts a session-relative timestamp to an absolute deadline.
/// Nothing those types decide is re-derived here.
/// </para>
/// <para>
/// <b>Thread layout.</b> Datagrams arrive on the network thread, which parses and timestamps them
/// and hands them to a bounded, allocation-free queue. One replay thread drains the queue,
/// validates, schedules, and keys. The sequence tracker and the anchor are touched only by that
/// thread, which is what their single-thread ownership rule requires.
/// </para>
/// <para>
/// <b>Zero allocation while keying.</b> The steady-state path allocates nothing: frames and
/// scheduled edges are value types in preallocated ring buffers, validation results live in a
/// <c>stackalloc</c> span, the wait-abort delegate is created once, and fail-safe messages are
/// constants. That is what makes <c>GCLatencyMode.SustainedLowLatency</c> meaningful rather than
/// decorative (14.7).
/// </para>
/// <para>
/// <b>PTT lead.</b> A key-down whose PTT is not yet asserted is handed to
/// <see cref="PttSequencer"/> one lead time <i>before</i> its deadline, so PTT rises early and the
/// key still lands on the deadline (8.4). While PTT is already asserted the edge is handed over on
/// the deadline itself, because the lead has been served and pre-firing would key early.
/// </para>
/// <para>
/// <b>Fail-safes are only wired, not implemented.</b> Any condition this class detects forces key
/// and PTT up, raises <see cref="FailSafeTriggered"/>, and is handed to
/// <see cref="FailSafeSink"/>. The latch <i>state</i> lives here because Component 7 exposes it;
/// the policy of which conditions latch and how a latch clears is the fail-safe monitor's
/// (tasks 12.1 - 12.6). The one policy decision taken here is conservative and deliberate: an
/// uninferable sequence gap or a timestamp regression latches SAFE even with no monitor installed,
/// because leaving 9.5 unenforced would be a live safety hole rather than an unimplemented feature.
/// </para>
/// <para>
/// _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5, 14.5, 14.7_
/// </para>
/// </remarks>
public sealed class EdgeReplayer : IEdgeReplayer
{
    /// <summary>Longest the replay loop sleeps when it has nothing scheduled.</summary>
    /// <remarks>
    /// Short enough that heartbeat and watchdog checks layered on later (12.1, 14.8) see a
    /// responsive loop, long enough that an idle Station is not spinning.
    /// </remarks>
    public static readonly TimeSpan MaxIdleWait = TimeSpan.FromMilliseconds(10);

    /// <summary>Inbound datagram queue capacity, in frames.</summary>
    public const int InboundCapacity = 256;

    /// <summary>Pending scheduled edge capacity, in edges.</summary>
    public const int PendingCapacity = 512;

    private const string EpochMismatchMessage =
        "Edge frame epoch does not match the current session; frame discarded and key forced up (F4).";

    private const string SequenceGapMessage =
        "Sequence gap whose key state cannot be inferred; key forced up and SAFE latched (F5).";

    private const string TimestampRegressionMessage =
        "Edge timestamp moved backwards within the session; key forced up and SAFE latched (F5).";

    private const string PendingOverflowMessage =
        "Replay schedule overflowed; the replay thread could not keep up, key forced up (F10 candidate).";

    private readonly ISystemClock _clock;
    private readonly long _frequency;
    private readonly PttTimingConfig _pttTiming;
    private readonly JitterBuffer _jitter;
    private readonly ReplayAnchor _anchor;
    private readonly ReplayRingBuffer<InboundFrame> _inbound = new(InboundCapacity);
    private readonly ReplayRingBuffer<ScheduledEdge> _pending = new(PendingCapacity);
    private readonly Func<bool> _shouldAbortWait;
    private readonly object _controlGate = new();
    private readonly long _maxIdleWaitTicks;

    // Replay-thread state.
    private EdgeSequenceTracker? _tracker;
    private PttSequencer? _sequencer;
    private long _lastScheduledDeadline = long.MinValue;
    private long _pttLeadTicks;
    private long _waitSignalSnapshot;

    // Cross-thread state.
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _safeLatched;
    private volatile bool _sessionActive;
    private volatile bool _hasPendingControl;
    private volatile bool _disposed;
    private long _wakeSignal;
    private long _lastHeartbeatQpc;
    private long _lastInboundQpc;
    private EdgeReplayerState _state = EdgeReplayerState.Stopped;
    private FailSafeCondition? _lastCondition;

    // Pending control requests, guarded by _controlGate.
    private bool _pendingBegin;
    private bool _pendingEnd;
    private ushort _pendingEpoch;

    // Telemetry counters. Written by the network thread where marked, otherwise by the replay
    // thread; always read through Volatile/Interlocked so a UI snapshot cannot tear.
    private long _framesReceived;   // network thread
    private long _framesDropped;    // network thread and replay thread
    private long _edgesApplied;
    private long _edgesReplayed;
    private long _duplicateEdges;
    private long _lateEdges;
    private long _pendingOverflows;
    private long _maxLatenessTicks;
    private long _maxReplayErrorTicks;

    /// <summary>Creates a replayer.</summary>
    /// <param name="clock">Timestamp source; defaults to <see cref="StopwatchClock"/>.</param>
    /// <param name="jitterConfig">Jitter buffer delays; defaults to <see cref="JitterBufferConfig.Default"/>.</param>
    /// <param name="pttTiming">PTT lead and tail timing; defaults to 15ms / 500ms.</param>
    /// <param name="jitterProfile">
    /// The sidecar's declared jitter profile (Component 13). Defaults to
    /// <see cref="EdgeJitterProfile.DerpClassOnly"/> so that the conservative band applies until the
    /// declaration has actually been read.
    /// </param>
    public EdgeReplayer(
        ISystemClock? clock = null,
        JitterBufferConfig? jitterConfig = null,
        PttTimingConfig? pttTiming = null,
        EdgeJitterProfile jitterProfile = EdgeJitterProfile.DerpClassOnly)
    {
        _clock = clock ?? new StopwatchClock();
        _frequency = _clock.Frequency > 0 ? _clock.Frequency : 1;
        _pttTiming = pttTiming ?? new PttTimingConfig();
        _jitter = new JitterBuffer(jitterConfig, jitterProfile);
        _anchor = new ReplayAnchor(_frequency);
        _maxIdleWaitTicks = ReplayAnchor.TicksForMilliseconds((long)MaxIdleWait.TotalMilliseconds, _frequency);

        // Created once: the wait loop must not allocate a delegate per iteration.
        _shouldAbortWait = () => _stopRequested || Volatile.Read(ref _wakeSignal) != _waitSignalSnapshot;
    }

    /// <inheritdoc/>
    public event EventHandler<EdgeReplayerStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<FailSafeTriggeredEventArgs>? FailSafeTriggered;

    /// <summary>The jitter buffer, exposed so path type and RTT samples can be fed to it (7.6).</summary>
    public JitterBuffer JitterBuffer => _jitter;

    /// <inheritdoc/>
    public JitterBufferConfig JitterConfig
    {
        get => _jitter.Config;
        set => _jitter.Config = value;
    }

    /// <summary>
    /// The jitter profile declared by the sidecar (<c>edge.jitterProfile</c>). Setting
    /// <see cref="EdgeJitterProfile.DerpClassOnly"/> widens the buffer immediately, even on a direct
    /// path.
    /// </summary>
    public EdgeJitterProfile JitterProfile
    {
        get => _jitter.Profile;
        set => _jitter.Profile = value;
    }

    /// <summary>The current network path type, which selects the delay band (7.1).</summary>
    public PathType Path
    {
        get => _jitter.Path;
        set => _jitter.Path = value;
    }

    /// <inheritdoc/>
    public EdgeReplayerState State => _state;

    /// <inheritdoc/>
    public bool IsSafeLatched => _safeLatched;

    /// <inheritdoc/>
    public IFailSafeSink? FailSafeSink { get; set; }

    /// <summary>Whether a session epoch is bound and edges are being accepted.</summary>
    public bool IsSessionActive => _sessionActive;

    /// <summary>Whether the key line is currently asserted.</summary>
    public bool IsKeyDown => _sequencer?.IsKeyAsserted ?? false;

    /// <summary>Whether the replay thread is running.</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// Clock timestamp of the last heartbeat received, or 0 if none. Input to F1 and F2 (9.1, 9.2).
    /// </summary>
    public long LastHeartbeatQpc => Volatile.Read(ref _lastHeartbeatQpc);

    /// <summary>
    /// Clock timestamp of the last accepted datagram, or 0 if none. Input to F1 together with
    /// <see cref="LastHeartbeatQpc"/> (9.1).
    /// </summary>
    public long LastInboundQpc => Volatile.Read(ref _lastInboundQpc);

    /// <inheritdoc/>
    public EdgeReplayerTelemetry Telemetry => new(
        Volatile.Read(ref _framesReceived),
        Volatile.Read(ref _framesDropped),
        Volatile.Read(ref _edgesApplied),
        Volatile.Read(ref _edgesReplayed),
        Volatile.Read(ref _duplicateEdges),
        _anchor.AnchorCount,
        Volatile.Read(ref _lateEdges),
        ReplayAnchor.MillisecondsForTicks(Volatile.Read(ref _maxLatenessTicks), _frequency),
        ReplayAnchor.MillisecondsForTicks(Volatile.Read(ref _maxReplayErrorTicks), _frequency),
        Volatile.Read(ref _pendingOverflows),
        _jitter.CurrentDelay,
        _jitter.RttEwmaMs,
        _jitter.JitterEwmaMs);

    /// <inheritdoc/>
    public void Start(IKeyingOutput keyingOutput, IPttOutput? pttOutput)
    {
        ArgumentNullException.ThrowIfNull(keyingOutput);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new InvalidOperationException("The edge replayer is already started.");
        }

        _sequencer = new PttSequencer(keyingOutput, pttOutput, _pttTiming, _clock);
        _pttLeadTicks = pttOutput is null
            ? 0
            : ReplayAnchor.TicksForMilliseconds((long)_pttTiming.LeadTime.TotalMilliseconds, _frequency);

        _stopRequested = false;
        _lastScheduledDeadline = long.MinValue;
        _anchor.Reset();
        _inbound.Clear();
        _pending.Clear();

        _thread = new Thread(ReplayLoop)
        {
            Name = "RWK-EdgeReplayer",
            IsBackground = true,

            // Managed priority tops out below the native TIME_CRITICAL value; the loop raises
            // itself with SetThreadPriority once running (14.7). Highest is the floor, not the goal.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();

        SetState(EdgeReplayerState.Idle, null, "Edge replayer started.");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Thread? thread = _thread;
        if (thread is null)
        {
            return;
        }

        _stopRequested = true;
        Interlocked.Increment(ref _wakeSignal); // break the loop out of its wait

        if (!thread.Join(TimeSpan.FromMilliseconds(500)))
        {
            // Background thread: it exits on its next abort check, and the finally block below
            // has already dropped the lines either way.
        }

        _thread = null;
        _sessionActive = false;

        // Belt and braces: the loop's finally block drops the lines, but Stop must not depend on
        // the thread having got there.
        _sequencer?.ForceAllUp();
        _sequencer = null;

        SetState(EdgeReplayerState.Stopped, null, "Edge replayer stopped.");
    }

    /// <inheritdoc/>
    public void BeginSession(ushort epoch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_controlGate)
        {
            _pendingEpoch = epoch;
            _pendingBegin = true;
            _pendingEnd = false;
        }

        _hasPendingControl = true;
        Interlocked.Increment(ref _wakeSignal);

        if (!IsRunning)
        {
            // No replay thread to apply it, so apply here. Safe precisely because there is no
            // thread to race with.
            ApplyPendingControl();
        }
    }

    /// <inheritdoc/>
    public void EndSession()
    {
        lock (_controlGate)
        {
            _pendingBegin = false;
            _pendingEnd = true;
        }

        _hasPendingControl = true;
        _sessionActive = false;
        Interlocked.Increment(ref _wakeSignal);

        ForceKeyUp();

        if (!IsRunning)
        {
            ApplyPendingControl();
        }
    }

    /// <inheritdoc/>
    public void ProcessDatagram(ReadOnlySpan<byte> data)
    {
        if (_disposed || !_sessionActive || _safeLatched)
        {
            // Nothing to schedule against: no session, or key output is locked.
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        if (!RwkPaddleFrame.TryRead(data, out RwkPaddleFrame frame, out _))
        {
            // Malformed or truncated. Never throws by contract; a bad datagram is simply not a frame.
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        long arrival = _clock.GetTimestamp();

        if (!_inbound.TryEnqueue(new InboundFrame(frame, arrival)))
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        Interlocked.Increment(ref _framesReceived);
        Volatile.Write(ref _lastInboundQpc, arrival);
        Interlocked.Increment(ref _wakeSignal);
    }

    /// <inheritdoc/>
    public void ProcessHeartbeat()
    {
        long now = _clock.GetTimestamp();
        Volatile.Write(ref _lastHeartbeatQpc, now);
        Volatile.Write(ref _lastInboundQpc, now);
    }

    /// <inheritdoc/>
    public void LatchSafe(FailSafeCondition condition, string message)
    {
        _safeLatched = true;
        ForceKeyUp();
        SetState(EdgeReplayerState.SafeLatched, condition, message);
    }

    /// <inheritdoc/>
    public void ClearSafeLatch()
    {
        if (!_safeLatched)
        {
            return;
        }

        _safeLatched = false;

        // Timing state from before the latch is meaningless: re-anchor on the next edge. The
        // sequence baseline is deliberately left alone — only a genuine session change may reset
        // that, or a key-down sitting behind a gap would be applied as a fresh baseline instead of
        // raising F5 (9.5).
        _anchor.Reset();
        _lastScheduledDeadline = long.MinValue;

        SetState(IsRunning ? EdgeReplayerState.Idle : EdgeReplayerState.Stopped, null, "SAFE latch cleared.");
    }

    /// <inheritdoc/>
    public void ForceKeyUp()
    {
        _sequencer?.ForceAllUp();
        _pending.Clear();
        _anchor.Reset();
        _lastScheduledDeadline = long.MinValue;
    }

    /// <summary>
    /// Forces the key line into the down state for testing purposes only. This bypasses the normal
    /// edge scheduling path and directly asserts the key line via the PttSequencer.
    /// </summary>
    /// <remarks>Internal visibility: accessible to the test project via InternalsVisibleTo.</remarks>
    internal void ForceKeyDownForTest()
    {
        _sequencer?.KeyDown();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch
        {
            // Disposal must not throw; the keying output performs its own fail-safe on disposal.
        }
    }

    // ─── Replay thread ───────────────────────────────────────────────────────

    private void ReplayLoop()
    {
        GCLatencyMode previousMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // THREAD_PRIORITY_TIME_CRITICAL (14.7). The managed enum has no such value, hence the
        // native call. A failure is not fatal: the loop still runs, just at Highest.
        _ = NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_PRIORITY_TIME_CRITICAL);

        // 1ms timer resolution so the coarse phase of the hybrid wait is actually 1ms granular.
        bool timerRaised = NativeMethods.TimeBeginPeriod(1) == NativeMethods.TIMERR_NOERROR;

        try
        {
            while (!_stopRequested)
            {
                _waitSignalSnapshot = Volatile.Read(ref _wakeSignal);

                if (_hasPendingControl)
                {
                    ApplyPendingControl();
                }

                DrainInbound();

                long now = _clock.GetTimestamp();
                long wake = ComputeWake(now);
                if (wake > now)
                {
                    HybridWaiter.WaitUntil(wake, _clock, _shouldAbortWait);
                }

                FireDueEdges(_clock.GetTimestamp());
                _sequencer?.Tick();
            }
        }
        catch (Exception ex)
        {
            // F7: an unhandled exception on the keying thread. Force key-up first, then report —
            // the monitor decides the latch (9.7, task 12.4).
            ForceKeyUp();
            ReportFailSafe(FailSafeCondition.F7, $"Unhandled exception on the replay thread: {ex.Message}");
        }
        finally
        {
            _sequencer?.ForceAllUp();

            if (timerRaised)
            {
                _ = NativeMethods.TimeEndPeriod(1);
            }

            GCSettings.LatencyMode = previousMode;
        }
    }

    private void ApplyPendingControl()
    {
        bool begin;
        bool end;
        ushort epoch;

        lock (_controlGate)
        {
            begin = _pendingBegin;
            end = _pendingEnd;
            epoch = _pendingEpoch;
            _pendingBegin = false;
            _pendingEnd = false;
        }

        _hasPendingControl = false;

        if (end)
        {
            _sessionActive = false;
            _tracker = null;
            _inbound.Clear();
            ForceKeyUp();
            _jitter.ResetSamples();
            SetState(IsRunning ? EdgeReplayerState.Idle : EdgeReplayerState.Stopped, null, "Session ended.");
        }

        if (!begin)
        {
            return;
        }

        // The one place BeginSession is legitimate: a genuine session establishment or reconnect.
        // Nothing on the anchor-reset or fail-safe paths may call it, because it discards the
        // verified sequence baseline (see EdgeSequenceTracker.BeginSession remarks, 9.5).
        if (_tracker is null)
        {
            _tracker = new EdgeSequenceTracker(epoch);
        }
        else
        {
            _tracker.BeginSession(epoch);
        }

        _inbound.Clear();
        _pending.Clear();
        _anchor.Reset();
        _lastScheduledDeadline = long.MinValue;
        _jitter.ResetSamples();
        _sessionActive = true;
        _safeLatched = false;

        long now = _clock.GetTimestamp();
        Volatile.Write(ref _lastHeartbeatQpc, now);
        Volatile.Write(ref _lastInboundQpc, now);

        SetState(EdgeReplayerState.Idle, null, "Session established.");
    }

    private void DrainInbound()
    {
        EdgeSequenceTracker? tracker = _tracker;
        if (tracker is null)
        {
            _inbound.Clear();
            return;
        }

        // One stack buffer for the whole drain: no allocation per frame.
        Span<EdgeValidationResult> results = stackalloc EdgeValidationResult[RwkPaddleFrame.MaxEdgeCount];

        while (_inbound.TryDequeue(out InboundFrame inbound))
        {
            if (_safeLatched)
            {
                Interlocked.Increment(ref _framesDropped);
                LogReplay($"DRAIN: safe latched, dropping frame");
                continue;
            }

            if (!tracker.TryValidateFrame(inbound.Frame, results, out int count))
            {
                Interlocked.Increment(ref _framesDropped);
                LogReplay($"DRAIN: TryValidateFrame returned false, frame epoch={inbound.Frame.Epoch}, tracker epoch={tracker.Epoch}, edges={inbound.Frame.EdgeCount}");
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                LogReplay($"DRAIN: result[{i}]={results[i].Outcome}, edge seq={results[i].Edge.Sequence}, keyDown={results[i].Edge.KeyDown}");
                HandleValidationResult(results[i], inbound.ArrivalQpc);

                if (_safeLatched)
                {
                    break;
                }
            }
        }
    }

    private void HandleValidationResult(in EdgeValidationResult result, long arrivalQpc)
    {
        switch (result.Outcome)
        {
            case EdgeValidationOutcome.Accepted:
                ScheduleEdge(result.Edge, arrivalQpc);
                return;

            case EdgeValidationOutcome.DuplicateDiscarded:
                // The common case: 6.4 redundancy means most arriving edges are already applied.
                _duplicateEdges++;
                return;

            case EdgeValidationOutcome.SequenceGap when result.Applied:
                // A key-up across a gap is safe to apply: the transmitter ends up unkeyed. Timing
                // was lost, keying safety was not.
                ScheduleEdge(result.Edge, arrivalQpc);
                return;

            case EdgeValidationOutcome.EpochMismatch:
                // F4: discard the frame and force key-up if keyed (9.4). No latch.
                ForceKeyUp();
                ReportFailSafe(FailSafeCondition.F4, EpochMismatchMessage);
                return;

            case EdgeValidationOutcome.SequenceGap:
                // F5: an unhealed gap ending in a key-down. Never guess a key-down (9.5).
                LatchSafe(FailSafeCondition.F5, SequenceGapMessage);
                ReportFailSafe(FailSafeCondition.F5, SequenceGapMessage);
                return;

            case EdgeValidationOutcome.TimestampRegression:
                LatchSafe(FailSafeCondition.F5, TimestampRegressionMessage);
                ReportFailSafe(FailSafeCondition.F5, TimestampRegressionMessage);
                return;

            default:
                Interlocked.Increment(ref _framesDropped);
                return;
        }
    }

    private void ScheduleEdge(in EdgeEntry edge, long arrivalQpc)
    {
        long delayTicks = _jitter.CurrentDelayIn(_frequency);
        long deadline = _anchor.Schedule(arrivalQpc, edge, delayTicks, out bool reanchored);

        LogReplay($"SCHEDULE: seq={edge.Sequence} keyDown={edge.KeyDown} tsMs={edge.TimestampMs} delay={delayTicks} deadline-now={(deadline - _clock.GetTimestamp())} reanchored={reanchored}");

        if (reanchored)
        {
            _jitter.ApplyPendingPathChange();
            _lastScheduledDeadline = long.MinValue;
        }

        // Function 2's loop invariant: scheduled edges keep monotonic order. Within a session the
        // tracker has already guaranteed non-decreasing timestamps, so this only matters across a
        // re-anchor, and clamping is safer than reordering the key stream.
        if (deadline < _lastScheduledDeadline)
        {
            deadline = _lastScheduledDeadline;
        }

        _lastScheduledDeadline = deadline;

        long now = _clock.GetTimestamp();
        if (deadline < now)
        {
            // Late: the buffer delay was smaller than this datagram's excess latency. Replay it at
            // once — dropping it could strand the key down — and let telemetry show the lateness
            // rather than hiding a mistimed edge.
            long lateness = now - deadline;
            _lateEdges++;
            if (lateness > _maxLatenessTicks)
            {
                Volatile.Write(ref _maxLatenessTicks, lateness);
            }

            // Feed the late-edge storm detector; it may auto-bump D for the next burst.
            _jitter.ReportLateEdge();
        }

        if (!_pending.TryEnqueue(new ScheduledEdge(deadline, edge.KeyDown)))
        {
            // The replay thread is starved by hundreds of edges. Anything still queued is stale, so
            // the safe response is key-up, not best-effort catch-up. F10 territory; the watchdog
            // (12.5) is what names it.
            _pendingOverflows++;
            ForceKeyUp();
            ReportFailSafe(FailSafeCondition.F10, PendingOverflowMessage);
            return;
        }

        _edgesApplied++;

        if (_state == EdgeReplayerState.Idle)
        {
            SetState(EdgeReplayerState.Active, null, null);
        }
    }

    private void FireDueEdges(long now)
    {
        PttSequencer? sequencer = _sequencer;
        if (sequencer is null)
        {
            return;
        }

        while (_pending.TryPeek(out ScheduledEdge edge))
        {
            long fireAt = EffectiveFireQpc(edge, sequencer);
            if (now < fireAt)
            {
                return;
            }

            _ = _pending.TryDequeue(out _);

            if (edge.KeyDown)
            {
                _ = sequencer.KeyDown();
                LogReplay($"FIRE: keyDown=True, output.IsKeyDown={_sequencer?.IsKeyAsserted}, output.IsPttOn={_sequencer?.IsPttAsserted}");
            }
            else
            {
                sequencer.KeyUp();
                LogReplay($"FIRE: keyDown=False");
            }

            _edgesReplayed++;

            long error = now - fireAt;
            if (error > _maxReplayErrorTicks)
            {
                Volatile.Write(ref _maxReplayErrorTicks, error);
            }
        }
    }

    /// <summary>
    /// When the sequencer must be called for <paramref name="edge"/>: its deadline, less one PTT
    /// lead time for a key-down that still needs PTT raised (8.4).
    /// </summary>
    private long EffectiveFireQpc(in ScheduledEdge edge, PttSequencer sequencer)
        => edge.KeyDown && _pttLeadTicks > 0 && !sequencer.IsPttAsserted
            ? edge.DeadlineQpc - _pttLeadTicks
            : edge.DeadlineQpc;

    private long ComputeWake(long now)
    {
        long wake = now + _maxIdleWaitTicks;

        PttSequencer? sequencer = _sequencer;
        if (sequencer is not null && _pending.TryPeek(out ScheduledEdge edge))
        {
            long fireAt = EffectiveFireQpc(edge, sequencer);
            if (fireAt < wake)
            {
                wake = fireAt;
            }
        }

        // The sequencer publishes its own next deadline — a pending key-down or a tail expiry — so
        // PTT timing folds into this one absolute-deadline wait instead of needing a timer.
        if (sequencer?.NextDeadline is long pttDeadline && pttDeadline < wake)
        {
            wake = pttDeadline;
        }

        return wake;
    }

    private void ReportFailSafe(FailSafeCondition condition, string message)
    {
        try
        {
            FailSafeTriggered?.Invoke(this, new FailSafeTriggeredEventArgs(condition, message));
        }
        catch
        {
            // A misbehaving subscriber must not undo the key-up that already happened.
        }

        try
        {
            FailSafeSink?.OnFailSafe(condition, message);
        }
        catch
        {
            // Same reasoning: the monitor is downstream of the safety action, not part of it.
        }
    }

    private void SetState(EdgeReplayerState state, FailSafeCondition? condition, string? message)
    {
        bool changed = _state != state || _lastCondition != condition;
        _state = state;
        _lastCondition = condition;

        if (!changed)
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(
                this,
                new EdgeReplayerStateChangedEventArgs(state, _safeLatched, condition, message));
        }
        catch
        {
            // UI subscriber faults must not reach the replay thread's loop.
        }
    }

    /// <summary>One parsed datagram waiting to be validated, with the arrival time it was stamped with.</summary>
    private readonly record struct InboundFrame(RwkPaddleFrame Frame, long ArrivalQpc);

    /// <summary>One validated edge waiting for its deadline.</summary>
    private readonly record struct ScheduledEdge(long DeadlineQpc, bool KeyDown);

    private static void LogReplay(string msg)
    {
        try { RWK.Shared.IO.RotatingFileLog.Append("replayer.log", msg); } catch { }
    }
}
