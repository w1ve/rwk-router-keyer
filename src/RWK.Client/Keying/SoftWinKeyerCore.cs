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
using RWK.Shared.Interop;
using RWK.Shared.Keying;
using RWK.Shared.Timing;

namespace RWK.Client.Keying;

/// <summary>
/// The Client's keyer: a dedicated high-priority timing thread driving a
/// <see cref="KeyerElementPump"/> (design Component 3).
/// </summary>
/// <remarks>
/// Everything that decides <em>what</em> to key lives in the pump, in RWK.Shared. This type
/// owns only <em>when</em> it runs: the thread, its priority, the GC latency mode, and the
/// shutdown discipline. That split is deliberate — the RWK v1 <c>SoftKeyer</c> fused the two,
/// and its behavior could only be tested by sleeping and hoping.
/// <para>
/// The thread runs at <see cref="NativeMethods.THREAD_PRIORITY_HIGHEST"/> with
/// <see cref="GCLatencyMode.SustainedLowLatency"/> (14.6). The managed
/// <see cref="ThreadPriority.Highest"/> is set as well, but the native call is what
/// guarantees the requirement's priority value.
/// </para>
/// <para>
/// The key is released on every exit path: normal stop, cancellation, and an unhandled
/// exception on the timing thread. An exception is captured in <see cref="Fault"/> and the
/// loop exits rather than being allowed to tear down the process — an operator whose keyer
/// thread has died is better served by a stopped keyer with the key up than by a crash.
/// </para>
/// _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 3.10, 14.2, 14.6_
/// </remarks>
public sealed class SoftWinKeyerCore : ISoftWinKeyerCore
{
    /// <summary>Idle wait between empty pump calls; keeps paddle response inside 1ms (14.2).</summary>
    private const int IdleWaitMs = 1;

    private readonly KeyerElementPump _pump;
    private readonly object _lifecycleLock = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<EdgeEvent>? EdgeGenerated;

    /// <inheritdoc/>
    public event EventHandler<char>? CharacterCompleted;

    /// <summary>
    /// Creates a keyer core driven by the system high-resolution clock.
    /// </summary>
    public SoftWinKeyerCore()
        : this(new StopwatchClock(), null)
    {
    }

    /// <summary>
    /// Creates a keyer core driven by the given clock and wait strategy.
    /// </summary>
    /// <param name="clock">Timing source.</param>
    /// <param name="wait">
    /// Wait strategy, or <see langword="null"/> for the production hybrid sleep/spin waiter.
    /// </param>
    public SoftWinKeyerCore(ISystemClock clock, KeyerWait? wait = null)
    {
        _pump = new KeyerElementPump(clock, wait);
        _pump.EdgeGenerated += (_, edge) => EdgeGenerated?.Invoke(this, edge);
        _pump.CharacterCompleted += (_, c) => CharacterCompleted?.Invoke(this, c);
    }

    /// <summary>
    /// Gets the exception that ended the timing thread, or <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// A placeholder for the Client-side fail-safe wiring: it records the fault so the UI
    /// can surface it, and the key is already up by the time it is set.
    /// </remarks>
    public Exception? Fault { get; private set; }

    /// <summary>
    /// Gets the element decision and scheduling pump, for inspection.
    /// </summary>
    public KeyerElementPump Pump => _pump;

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    public int SpeedWpm
    {
        get => _pump.SpeedWpm;
        set => _pump.SpeedWpm = value;
    }

    /// <inheritdoc/>
    public int Weight
    {
        get => _pump.Weight;
        set => _pump.Weight = value;
    }

    /// <inheritdoc/>
    public bool PaddleReverse
    {
        get => _pump.PaddleReverse;
        set => _pump.PaddleReverse = value;
    }

    /// <inheritdoc/>
    public KeyerMode Mode
    {
        get => _pump.Mode;
        set => _pump.Mode = value;
    }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
                return;

            Fault = null;
            _cts = new CancellationTokenSource();
            _running = true;

            _thread = new Thread(KeyerLoop)
            {
                Name = "RWK-SoftWinKeyerCore",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            _thread.Start();
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Thread? thread;

        lock (_lifecycleLock)
        {
            if (!_running)
                return;

            _running = false;
            _cts?.Cancel();
            thread = _thread;
            _thread = null;
        }

        // Joined outside the lock: the timing thread must never wait on a caller that is
        // itself waiting for the timing thread.
        if (thread is not null && !thread.Join(TimeSpan.FromMilliseconds(500)))
        {
            // Stuck in a wait or spin. It is a background thread and checks cancellation on
            // its next iteration, so it cannot outlive the process or keep keying.
        }

        lock (_lifecycleLock)
        {
            _cts?.Dispose();
            _cts = null;
        }

        // Defensive: the loop releases the key itself, but a thread abandoned by the join
        // timeout above may not have got there yet.
        _pump.ForceKeyUp();
    }

    /// <inheritdoc/>
    public void SetPaddleState(bool dit, bool dah, bool straight, long qpcTimestamp) =>
        _pump.SetPaddleState(dit, dah, straight, qpcTimestamp);

    /// <inheritdoc/>
    public void EnqueueText(string text) => _pump.EnqueueText(text);

    /// <inheritdoc/>
    public void SetKeyImmediate(bool down) => _pump.SetKeyImmediate(down);

    /// <inheritdoc/>
    public void AbortAndClear() => _pump.AbortAndClear();

    /// <summary>
    /// The timing thread: pump until cancelled, idling a millisecond when there is nothing
    /// to send.
    /// </summary>
    private void KeyerLoop()
    {
        GCLatencyMode previousLatencyMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // The managed enum tops out below the requirement's value, so set it natively (14.6).
        NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_PRIORITY_HIGHEST);

        // 1ms timer resolution so Thread.Sleep(1) in the HybridWaiter actually sleeps ~1ms
        // instead of the default 15.6ms. Without this, dit timing at 25+ WPM is destroyed.
        bool timerRaised = NativeMethods.TimeBeginPeriod(1) == NativeMethods.TIMERR_NOERROR;

        CancellationToken token = _cts!.Token;
        Func<bool> stop = () => token.IsCancellationRequested;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_pump.Pump(stop) == PumpAction.Idle)
                {
                    // Waits on the token handle rather than sleeping, so cancellation is
                    // observed immediately instead of up to a millisecond later.
                    token.WaitHandle.WaitOne(IdleWaitMs);
                }
            }
        }
        catch (Exception ex)
        {
            Fault = ex;
            _running = false;
        }
        finally
        {
            _pump.ForceKeyUp();
            if (timerRaised)
                NativeMethods.TimeEndPeriod(1);
            GCSettings.LatencyMode = previousLatencyMode;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();

        lock (_lifecycleLock)
        {
            _disposed = true;
        }
    }
}
