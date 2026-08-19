/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RWK.Shared;
using RWK.Shared.Interop;
using RWK.Shared.IO;

namespace RWK.Client.IO;

/// <summary>
/// Reports that the paddle port failed while polling, for example because a USB serial
/// adapter was unplugged.
/// </summary>
/// <param name="Message">Human-readable description suitable for the Client UI.</param>
/// <param name="Win32ErrorCode">
/// Native error code from the failing call, or 0 when the fault did not come from Win32.
/// </param>
public record PaddleFaultEventArgs(string Message, int Win32ErrorCode);

/// <summary>
/// Polls a serial port's modem status register at 1 ms intervals on a dedicated
/// high-priority thread and reports debounced, QPC-timestamped paddle contact transitions.
/// </summary>
/// <remarks>
/// Pin mapping is CTS to dit, DSR to dah, DCD (RLSD) to the straight key (1.2). DTR is
/// asserted while the port is open because DTR is the voltage source feeding the paddle
/// contacts — without it the contacts read nothing (1.6).
/// <para>
/// The polling thread runs at <c>THREAD_PRIORITY_HIGHEST</c> (1.1, 14.6) and the process
/// holds 1 ms system timer resolution for the duration of polling (1.7). The resolution is
/// a system-wide reference-counted resource, so it is released on <see cref="Stop"/> and on
/// disposal.
/// </para>
/// <para>
/// Debounce is delegated to <see cref="ContactDebouncer"/>, a pure state machine, so this
/// class contains no timing rules of its own (1.4).
/// </para>
/// <para>
/// If the port disappears mid-session the polling thread does not throw out: it raises
/// <see cref="Fault"/>, stops cleanly, and releases the port and timer resolution.
/// </para>
/// <para>
/// <see cref="StateChanged"/> and <see cref="Fault"/> are raised on the polling thread.
/// Handlers must return promptly; the thread owes the keyer a 1 ms cadence.
/// </para>
/// _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 14.1_
/// </remarks>
public sealed class PaddleInputPoller : IPaddleInputPoller
{
    /// <summary>Nominal poll interval required by 1.1.</summary>
    private const int PollIntervalMs = 1;

    /// <summary>Timer resolution requested for the duration of polling (1.7).</summary>
    private const uint TimerPeriodMs = 1;

    private readonly ContactDebouncer _debouncer;
    private readonly object _lifecycleLock = new();

    private SafeFileHandle? _handle;
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    /// <summary>1 when the 1 ms timer period is currently held. Interlocked so that the
    /// polling thread's fault path and <see cref="Stop"/> cannot both release it.</summary>
    private int _timerPeriodRaised;

    private bool _disposed;

    // Written by the polling thread, read by the UI/keyer threads.
    private volatile bool _ditPressed;
    private volatile bool _dahPressed;
    private volatile bool _straightKeyPressed;

    /// <summary>
    /// Creates a poller with the default 5 ms debounce window (1.4).
    /// </summary>
    public PaddleInputPoller()
        : this(ContactDebouncer.DefaultDebounceTime)
    {
    }

    /// <summary>
    /// Creates a poller with an explicit debounce window.
    /// </summary>
    /// <param name="debounceTime">Per-contact debounce window (1.4).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="debounceTime"/> is negative.</exception>
    public PaddleInputPoller(TimeSpan debounceTime)
    {
        _debouncer = new ContactDebouncer(debounceTime);
    }

    /// <inheritdoc />
    public event EventHandler<PaddleStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when polling stops because the port failed — most commonly the serial adapter
    /// being unplugged. The poller has already released the port when this fires.
    /// </summary>
    public event EventHandler<PaddleFaultEventArgs>? Fault;

    /// <inheritdoc />
    public bool DitPressed => _ditPressed;

    /// <inheritdoc />
    public bool DahPressed => _dahPressed;

    /// <inheritdoc />
    public bool StraightKeyPressed => _straightKeyPressed;

    /// <inheritdoc />
    public TimeSpan DebounceTime
    {
        get => _debouncer.DebounceTime;
        set => _debouncer.DebounceTime = value;
    }

    /// <summary>Gets a value indicating whether the polling thread is running.</summary>
    public bool IsRunning => _pollThread is { IsAlive: true };

    /// <summary>Gets the port currently being polled, or <see langword="null"/> when stopped.</summary>
    public string? PortName { get; private set; }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="portName"/> is null or blank.</exception>
    /// <exception cref="ObjectDisposedException">The poller has been disposed.</exception>
    /// <exception cref="Win32Exception">The port could not be opened or configured.</exception>
    public void Start(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("Port name is required.", nameof(portName));

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Restarting on a different port is a normal operation (user changes the setting).
            StopCore();

            SafeFileHandle handle = OpenPort(portName);
            try
            {
                ConfigurePort(handle);
                AssertDtr(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            _handle = handle;
            PortName = portName;

            // A stale debounce window must not suppress the first contact of a new session.
            _debouncer.Reset();
            PublishStates(ContactStates.None);

            // 1 ms timer resolution is what makes a 1 ms Sleep-based cadence achievable (1.7).
            if (NativeMethods.TimeBeginPeriod(TimerPeriodMs) == NativeMethods.TIMERR_NOERROR)
                Volatile.Write(ref _timerPeriodRaised, 1);

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _pollThread = new Thread(() => PollLoop(handle, token))
            {
                Name = "RWK Paddle Poller",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _pollThread.Start();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopCore();
        }

        StateChanged = null;
        Fault = null;
    }

    // ─── Polling ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The polling body. Never throws: a port failure is reported through
    /// <see cref="Fault"/> and ends the loop so the thread unwinds cleanly.
    /// </summary>
    private void PollLoop(SafeFileHandle handle, CancellationToken token)
    {
        // The managed Highest priority maps to THREAD_PRIORITY_HIGHEST already; setting it
        // natively as well keeps the requirement explicit and survives any future change to
        // how the thread is created (1.1, 14.6).
        NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_PRIORITY_HIGHEST);

        long ticksPerInterval = Math.Max(1, Stopwatch.Frequency / 1000 * PollIntervalMs);
        long nextDeadline = Stopwatch.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!NativeMethods.GetCommModemStatus(handle, out uint modemStatus))
                {
                    int error = Marshal.GetLastWin32Error();
                    ReportFault(
                        $"Paddle port {PortName} stopped responding (Win32 error {error}). " +
                        "The adapter may have been unplugged.",
                        error);
                    return;
                }

                // Timestamp taken at the moment of detection, before any dispatch work (1.3).
                long qpcTimestamp = Stopwatch.GetTimestamp();

                ContactStates raw = MapModemStatus(modemStatus);

                if (_debouncer.TryAccept(raw, qpcTimestamp, out ContactStates accepted))
                {
                    PublishStates(accepted);
                    RaiseStateChanged(qpcTimestamp, accepted);
                }

                nextDeadline = WaitForNextPoll(nextDeadline + ticksPerInterval, token);
            }
        }
        catch (ObjectDisposedException)
        {
            // Handle closed underneath us by a concurrent Stop/Dispose; nothing to report.
        }
        catch (Exception ex)
        {
            ReportFault($"Paddle polling stopped: {ex.Message}", 0);
        }
    }

    /// <summary>
    /// Waits until <paramref name="deadline"/> and returns the anchor for the next interval.
    /// </summary>
    /// <remarks>
    /// A 1 ms cancellation-aware wait does the bulk of the pacing (viable only because 1 ms
    /// timer resolution is held), with a short spin to absorb an early wake. If the thread
    /// has already fallen past the deadline — a scheduling hiccup, or a slow handler — the
    /// anchor is reset to now so drift cannot accumulate into a burst of catch-up polls.
    /// </remarks>
    private static long WaitForNextPoll(long deadline, CancellationToken token)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= deadline)
            return now;

        // Wakes immediately on Stop, so shutdown never waits out a full interval.
        token.WaitHandle.WaitOne(PollIntervalMs);

        while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            Thread.SpinWait(20);

        return deadline;
    }

    /// <summary>
    /// Maps modem status register bits to paddle contacts: CTS to dit, DSR to dah,
    /// DCD (RLSD) to the straight key (1.2).
    /// </summary>
    internal static ContactStates MapModemStatus(uint modemStatus) => new(
        DitPressed: (modemStatus & NativeMethods.MS_CTS_ON) != 0,
        DahPressed: (modemStatus & NativeMethods.MS_DSR_ON) != 0,
        StraightKeyPressed: (modemStatus & NativeMethods.MS_RLSD_ON) != 0);

    private void PublishStates(ContactStates states)
    {
        _ditPressed = states.DitPressed;
        _dahPressed = states.DahPressed;
        _straightKeyPressed = states.StraightKeyPressed;
    }

    private void RaiseStateChanged(long qpcTimestamp, ContactStates states)
    {
        StateChanged?.Invoke(this, new PaddleStateChangedEventArgs(
            qpcTimestamp,
            states.DitPressed,
            states.DahPressed,
            states.StraightKeyPressed));
    }

    /// <summary>
    /// Releases the port and timer resolution, then reports the fault. Ordering matters: the
    /// Client should see a fault only once the poller has actually let go of the port.
    /// </summary>
    private void ReportFault(string message, int win32Error)
    {
        // Cannot take _lifecycleLock here: Stop() holds it while joining this thread.
        ReleasePort();
        ReleaseTimerPeriod();
        PublishStates(ContactStates.None);

        try
        {
            Fault?.Invoke(this, new PaddleFaultEventArgs(message, win32Error));
        }
        catch
        {
            // A faulting handler must not take down the polling thread.
        }
    }

    // ─── Lifecycle helpers ───────────────────────────────────────────────────

    /// <summary>Stops polling. Caller must hold <see cref="_lifecycleLock"/>.</summary>
    private void StopCore()
    {
        CancellationTokenSource? cts = _cts;
        Thread? thread = _pollThread;

        _cts = null;
        _pollThread = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            // Generous relative to a 1 ms loop; guards against a wedged event handler.
            thread.Join(TimeSpan.FromSeconds(2));
        }

        cts?.Dispose();

        ReleasePort();
        ReleaseTimerPeriod();
        PublishStates(ContactStates.None);
        _debouncer.Reset();
        PortName = null;
    }

    private void ReleasePort()
    {
        SafeFileHandle? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
            return;

        try
        {
            if (!handle.IsInvalid && !handle.IsClosed)
                NativeMethods.EscapeCommFunction(handle, NativeMethods.CLRDTR);
        }
        catch
        {
            // Port already gone; closing the handle is all that matters.
        }

        handle.Dispose();
    }

    /// <summary>
    /// Drops the 1 ms timer resolution. Leaving it raised would keep the whole system on a
    /// fast tick after RWK stops polling.
    /// </summary>
    private void ReleaseTimerPeriod()
    {
        if (Interlocked.Exchange(ref _timerPeriodRaised, 0) == 0)
            return;

        NativeMethods.TimeEndPeriod(TimerPeriodMs);
    }

    // ─── Port setup ──────────────────────────────────────────────────────────

    private static SafeFileHandle OpenPort(string portName)
    {
        // \\.\COMx form is required for COM10 and above.
        string devicePath = portName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? portName
            : @"\\.\" + portName;

        SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            dwShareMode: 0,
            lpSecurityAttributes: nint.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            hTemplateFile: nint.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Failed to open paddle port {portName}.");
        }

        return handle;
    }

    /// <summary>
    /// Puts the port in binary mode with DTR driver-asserted and RTS under manual control.
    /// Baud rate is irrelevant to control-line sensing but the DCB must still be valid.
    /// </summary>
    private static void ConfigurePort(SafeFileHandle handle)
    {
        NativeMethods.DCB dcb = default;
        dcb.DCBlength = (uint)Marshal.SizeOf<NativeMethods.DCB>();

        if (!NativeMethods.GetCommState(handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to read paddle port state.");

        dcb.DCBlength = (uint)Marshal.SizeOf<NativeMethods.DCB>();
        dcb.SetBinary(true);
        dcb.SetDtrControl(NativeMethods.DTR_CONTROL_ENABLE);
        dcb.SetRtsControl(NativeMethods.RTS_CONTROL_DISABLE);

        if (!NativeMethods.SetCommState(handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure paddle port.");
    }

    /// <summary>
    /// Asserts DTR explicitly. DTR is the voltage source for the paddle contacts, so without
    /// it CTS/DSR/DCD never change (1.6). Belt and braces alongside DTR_CONTROL_ENABLE: some
    /// USB serial drivers honor only one of the two paths.
    /// </summary>
    private static void AssertDtr(SafeFileHandle handle)
    {
        if (!NativeMethods.EscapeCommFunction(handle, NativeMethods.SETDTR))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to assert DTR on paddle port.");
    }
}
