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
using RWK.Shared.Timing;

namespace RWK.Station.Replay;

/// <summary>
/// A minimal, allocation-free watchdog thread that monitors the replay thread's health by
/// comparing the replayer's last edge fire time against a 250ms overrun threshold (F10).
/// </summary>
/// <remarks>
/// <para>
/// The design specifies this as a separate component (<c>RWK.Station/Replay/SchedulerWatchdog.cs</c>)
/// that runs its own thread with zero allocations in steady state, per the safety requirements.
/// </para>
/// <para>
/// On each 50ms check (14.8), if the replayer has pending edges and the time since the last
/// fire exceeds 250ms, the watchdog forces key-up and reports F10 to the fail-safe monitor.
/// </para>
/// <para>
/// _Requirements: 9.10, 14.8_
/// </para>
/// </remarks>
public sealed class SchedulerWatchdog : IDisposable
{
    /// <summary>Check interval for the watchdog thread (14.8).</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Overrun threshold: 250ms (9.10).</summary>
    public static readonly TimeSpan OverrunThreshold = TimeSpan.FromMilliseconds(250);

    private const string OverrunMessage =
        "Scheduler timing overrun > 250ms; replay thread stalled, key forced up and SAFE latched (F10).";

    private readonly EdgeReplayer _replayer;
    private readonly IFailSafeSink? _sink;
    private readonly ISystemClock _clock;
    private readonly long _frequency;
    private readonly long _overrunTicks;
    private readonly int _checkIntervalMs;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _disposed;

    // Last timestamp when an edge was successfully fired. Written by the external caller
    // (the replayer's FireDueEdges) via ReportEdgeFired, read by the watchdog thread.
    private long _lastEdgeFiredQpc;

    // Whether the replayer currently has pending scheduled edges.
    private volatile bool _hasPendingEdges;

    /// <summary>Creates a scheduler watchdog.</summary>
    /// <param name="replayer">The edge replayer to monitor and force key-up on if stalled.</param>
    /// <param name="sink">Optional fail-safe sink to report F10 to (typically the FailSafeMonitor).</param>
    /// <param name="clock">Clock for timestamp comparisons; defaults to <see cref="StopwatchClock"/>.</param>
    public SchedulerWatchdog(
        EdgeReplayer replayer,
        IFailSafeSink? sink = null,
        ISystemClock? clock = null)
    {
        _replayer = replayer ?? throw new ArgumentNullException(nameof(replayer));
        _sink = sink;
        _clock = clock ?? new StopwatchClock();
        _frequency = _clock.Frequency > 0 ? _clock.Frequency : 1;
        _overrunTicks = (_frequency * 250) / 1000;
        _checkIntervalMs = 50;
    }

    /// <summary>Whether the watchdog thread is running.</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>The last QPC at which an edge was fired, for test inspection.</summary>
    public long LastEdgeFiredQpc => Volatile.Read(ref _lastEdgeFiredQpc);

    /// <summary>Starts the watchdog thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        _stopRequested = false;
        Volatile.Write(ref _lastEdgeFiredQpc, _clock.GetTimestamp());

        _thread = new Thread(WatchdogLoop)
        {
            Name = "RWK-SchedulerWatchdog",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Stops the watchdog thread.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromMilliseconds(200));
        _thread = null;
    }

    /// <summary>
    /// Called by the replay thread each time an edge is fired. This is the heartbeat the
    /// watchdog uses to know the replay thread is alive and on time. Zero allocations.
    /// </summary>
    /// <param name="qpc">The clock timestamp at which the edge fired.</param>
    public void ReportEdgeFired(long qpc)
    {
        Volatile.Write(ref _lastEdgeFiredQpc, qpc);
    }

    /// <summary>
    /// Called to indicate whether the replayer currently has pending edges to fire.
    /// When there are no pending edges, the watchdog does not flag an overrun because
    /// the replay thread is legitimately idle.
    /// </summary>
    public void SetHasPendingEdges(bool hasPending)
    {
        _hasPendingEdges = hasPending;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ─── Watchdog loop ───────────────────────────────────────────────────────────

    private void WatchdogLoop()
    {
        while (!_stopRequested)
        {
            CheckOverrun();
            Thread.Sleep(_checkIntervalMs);
        }
    }

    private void CheckOverrun()
    {
        // Only check when the replayer has edges pending — if idle, no overrun is possible.
        if (!_hasPendingEdges)
        {
            return;
        }

        if (!_replayer.IsSessionActive || _replayer.IsSafeLatched)
        {
            return;
        }

        long now = _clock.GetTimestamp();
        long lastFired = Volatile.Read(ref _lastEdgeFiredQpc);

        if (lastFired <= 0)
        {
            return;
        }

        long elapsed = now - lastFired;
        if (elapsed >= _overrunTicks)
        {
            // F10: scheduler stall. Force key-up and report.
            _replayer.ForceKeyUp();
            _sink?.OnFailSafe(FailSafeCondition.F10, OverrunMessage);
        }
    }
}
