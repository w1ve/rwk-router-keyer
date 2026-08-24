/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.IO.Ports;
using System.Runtime;
using RWK.Shared;
using RWK.Shared.Interop;
using RWK.Shared.IO;
using RWK.Shared.Keying;
using RWK.Shared.Protocol;
using RWK.Shared.Timing;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Protocol;
using SharedProtocolState = RWK.Shared.Protocol.ProtocolState;

namespace RWK.Station.IO;

/// <summary>
/// Station-side Logger WinKeyer Input: accepts WK2 protocol from logging software
/// (N1MM+, DXLog, etc.) on a virtual or real COM port and drives the Station's keying
/// output directly with locally-generated CW timing.
/// </summary>
/// <remarks>
/// <para>
/// This component runs entirely on the Station PC and is independent of the Tailscale
/// link. When a logger sends CW macros, this host generates edges locally and keys the
/// radio through the same <see cref="IKeyingOutput"/> that the remote <c>EdgeReplayer</c>
/// uses. An interlock ensures logger CW has priority over remote paddle edges.
/// </para>
/// <para>
/// Architecture: WinKeyer protocol state machine → text events → KeyerElementPump
/// (timing thread) → edge events → direct keying output calls.
/// </para>
/// </remarks>
public sealed class StationLoggerHost : IDisposable
{
    private const int IdleWaitMs = 1;

    private readonly IProtocolLogger _protocolLogger;
    private readonly KeyerElementPump _pump;
    private readonly ISystemClock _clock;

    private WinKeyerProtocol? _protocol;
    private SerialPort? _port;
    private Thread? _readerThread;
    private Thread? _keyerThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private volatile bool _disposed;

    private IKeyingOutput? _keyingOutput;
    private IPttOutput? _pttOutput;
    private volatile bool _keying; // true while key is asserted by logger
    private volatile bool _sending; // true while logger has buffered text in flight
    private long _lastEdgeTimestamp;
    private System.Threading.Timer? _idleTimer;
    private const int IdleTimeoutMs = 2000; // 2s after last edge → consider idle

    /// <summary>
    /// Raised when the logger starts sending (first character queued).
    /// The Station controller uses this to suppress remote edges.
    /// </summary>
    public event EventHandler? SendingStarted;

    /// <summary>
    /// Raised when the logger finishes sending (buffer empty, key up, idle timeout).
    /// The Station controller uses this to resume remote edges.
    /// </summary>
    public event EventHandler? SendingCompleted;

    /// <summary>
    /// Raised when the logger's speed changes (from WK2 speed command).
    /// </summary>
    public event EventHandler<int>? SpeedChanged;

    /// <summary>Whether the logger keyer is currently sending CW (buffer not empty or key held).</summary>
    public bool IsSending => _sending;

    /// <summary>Whether the host is running and listening on a COM port.</summary>
    public bool IsRunning => _running;

    /// <summary>Current keyer speed in WPM.</summary>
    public int SpeedWpm
    {
        get => _pump.SpeedWpm;
        set => _pump.SpeedWpm = value;
    }

    /// <summary>
    /// Creates a new Station Logger Host.
    /// </summary>
    /// <param name="protocolLogger">Logger for protocol state machine diagnostics.</param>
    /// <param name="clock">Optional clock for testing; defaults to <see cref="StopwatchClock"/>.</param>
    public StationLoggerHost(IProtocolLogger? protocolLogger = null, ISystemClock? clock = null)
    {
        _protocolLogger = protocolLogger ?? NullProtocolLogger.Instance;
        _clock = clock ?? new StopwatchClock();
        _pump = new KeyerElementPump(_clock, null);
        _pump.EdgeGenerated += OnEdgeGenerated;
        _pump.CharacterCompleted += OnCharacterCompleted;
    }

    /// <summary>
    /// Starts the logger host: opens the COM port for WK2 protocol, starts the keyer
    /// timing thread, and begins accepting commands from the logger.
    /// </summary>
    /// <param name="portName">COM port to listen on (e.g. "COM5").</param>
    /// <param name="keyingOutput">The Station's keying output to drive.</param>
    /// <param name="pttOutput">Optional PTT output; null if PTT is not configured.</param>
    public void Start(string portName, IKeyingOutput keyingOutput, IPttOutput? pttOutput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(portName);
        ArgumentNullException.ThrowIfNull(keyingOutput);

        if (_running)
            throw new InvalidOperationException("Logger host is already running.");

        _keyingOutput = keyingOutput;
        _pttOutput = pttOutput;
        _keying = false;
        _sending = false;

        // Create protocol state machine.
        var legacyLogger = new ProtocolLoggerBridge(_protocolLogger);
        _protocol = new WinKeyerProtocol(legacyLogger);
        _protocol.TextReceived += OnProtocolTextReceived;
        _protocol.SpeedChanged += OnProtocolSpeedChanged;
        _protocol.KeyImmediate += OnProtocolKeyImmediate;
        _protocol.BufferCleared += OnProtocolBufferCleared;

        // Open serial port at 1200 8-N-2 (WK2 standard).
        _port = new SerialPort(portName)
        {
            BaudRate = 1200,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.Two,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 500,
            WriteTimeout = 500
        };

        _port.Open();
        _running = true;
        _cts = new CancellationTokenSource();

        LogStation($"START: opened {portName} at 1200 8-N-2 for Logger Input");

        // Start keyer timing thread.
        _keyerThread = new Thread(KeyerLoop)
        {
            Name = "RWK-StationLoggerKeyer",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        _keyerThread.Start();

        // Start serial reader thread.
        _readerThread = new Thread(ReaderLoop)
        {
            Name = "RWK-StationLoggerReader",
            IsBackground = true
        };
        _readerThread.Start();

        _protocolLogger.Log($"Station Logger Host started on {portName}", ProtocolLogSeverity.Info, "LoggerHost");
    }

    /// <summary>
    /// Stops the logger host: releases the key, closes the COM port, stops threads.
    /// </summary>
    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _cts?.Cancel();

        // Force key up immediately.
        ForceKeyUp();

        // Close port to unblock reader.
        try { _port?.Close(); } catch { /* best effort */ }

        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _keyerThread?.Join(TimeSpan.FromSeconds(1));
        _readerThread = null;
        _keyerThread = null;

        try { _port?.Dispose(); } catch { /* best effort */ }
        _port = null;

        if (_protocol is not null)
        {
            _protocol.TextReceived -= OnProtocolTextReceived;
            _protocol.SpeedChanged -= OnProtocolSpeedChanged;
            _protocol.KeyImmediate -= OnProtocolKeyImmediate;
            _protocol.BufferCleared -= OnProtocolBufferCleared;
            _protocol = null;
        }

        _cts?.Dispose();
        _cts = null;

        _idleTimer?.Dispose();
        _idleTimer = null;

        if (_sending)
        {
            _sending = false;
            SendingCompleted?.Invoke(this, EventArgs.Empty);
        }

        _protocolLogger.Log("Station Logger Host stopped", ProtocolLogSeverity.Info, "LoggerHost");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Protocol events
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnProtocolTextReceived(object? sender, char c)
    {
        if (!_running) return;

        LogStation($"TEXT: '{c}' — echoing + enqueueing");

        // Echo immediately — WK2 protocol requires echo when character starts,
        // not when it finishes. N1MM uses echoes for flow control.
        WriteToPort(new[] { (byte)c });

        // Send "busy/sending" status so N1MM knows we're working.
        // Status 0xC4 = bits 7:6 set (status marker) + bit 2 (buffer sending).
        WriteToPort(new[] { (byte)0xC4 });

        // Signal sending started on first character.
        if (!_sending)
        {
            _sending = true;
            SendingStarted?.Invoke(this, EventArgs.Empty);
        }

        // Reset idle timer.
        ResetIdleTimer();

        _pump.EnqueueText(c.ToString());
    }

    private void OnProtocolSpeedChanged(object? sender, int wpm)
    {
        if (!_running) return;

        _pump.SpeedWpm = wpm;
        SpeedChanged?.Invoke(this, wpm);
    }

    private void OnProtocolKeyImmediate(object? sender, bool down)
    {
        if (!_running) return;

        if (!_sending && down)
        {
            _sending = true;
            SendingStarted?.Invoke(this, EventArgs.Empty);
        }

        ResetIdleTimer();
        _pump.SetKeyImmediate(down);
    }

    private void OnProtocolBufferCleared(object? sender, EventArgs e)
    {
        if (!_running) return;

        _pump.AbortAndClear();
        ForceKeyUp();

        // Buffer cleared = sending complete.
        if (_sending)
        {
            _sending = false;
            SendingCompleted?.Invoke(this, EventArgs.Empty);
        }

        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Edge events from keyer pump → drive physical keying output
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnEdgeGenerated(object? sender, EdgeEvent edge)
    {
        if (!_running || _keyingOutput is null) return;

        _lastEdgeTimestamp = _clock.GetTimestamp();
        ResetIdleTimer();

        if (edge.KeyDown)
        {
            if (!_keying)
            {
                // Assert PTT before first key-down.
                _pttOutput?.PttDown();
            }
            _keyingOutput.KeyDown();
            _keying = true;
        }
        else
        {
            _keyingOutput.KeyUp();
            _keying = false;
        }
    }

    private void OnCharacterCompleted(object? sender, char c)
    {
        if (!_running) return;

        // Character finished keying. Send "idle" status so N1MM knows the buffer
        // has space (this is what allows the next character to be sent).
        WriteToPort(new[] { (byte)0xC0 });
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Idle detection → sending completed
    // ──────────────────────────────────────────────────────────────────────────────

    private void ResetIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = new System.Threading.Timer(OnIdleTimeout, null, IdleTimeoutMs, Timeout.Infinite);
    }

    private void OnIdleTimeout(object? state)
    {
        if (!_running) return;

        // If we're not actively keying and the pump has nothing queued, we're done.
        if (!_keying && _sending)
        {
            // Drop PTT after tail time.
            _pttOutput?.PttUp();

            _sending = false;
            SendingCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Keyer timing thread
    // ──────────────────────────────────────────────────────────────────────────────

    private void KeyerLoop()
    {
        GCLatencyMode previousLatencyMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_PRIORITY_HIGHEST);
        bool timerRaised = NativeMethods.TimeBeginPeriod(1) == NativeMethods.TIMERR_NOERROR;

        CancellationToken token = _cts!.Token;
        Func<bool> stop = () => token.IsCancellationRequested;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_pump.Pump(stop) == PumpAction.Idle)
                {
                    token.WaitHandle.WaitOne(IdleWaitMs);
                }
            }
        }
        catch
        {
            // Keyer thread died — ensure key is up.
        }
        finally
        {
            _pump.ForceKeyUp();
            if (timerRaised)
                NativeMethods.TimeEndPeriod(1);
            GCSettings.LatencyMode = previousLatencyMode;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Serial reader thread
    // ──────────────────────────────────────────────────────────────────────────────

    private void ReaderLoop()
    {
        while (_running)
        {
            try
            {
                var port = _port;
                if (port is null || !port.IsOpen)
                    break;

                int b = port.ReadByte();
                if (b < 0)
                    continue;

                _protocolLogger.Log($"RX: 0x{b:X2}", ProtocolLogSeverity.Info, "LoggerHost");

                // Feed byte to protocol state machine.
                byte[]? response = _protocol?.ProcessByte((byte)b);

                // Write response back to the logger.
                if (response is { Length: > 0 })
                {
                    LogStation($"TX: [{string.Join(" ", response.Select(x => $"0x{x:X2}"))}]");
                    WriteToPort(response);
                }
            }
            catch (TimeoutException)
            {
                // Normal — ReadByte timed out, loop again.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                if (_running)
                {
                    _protocolLogger.Log("Logger port I/O error",
                        ProtocolLogSeverity.Error, "LoggerHost");
                }
                break;
            }
            catch (InvalidOperationException)
            {
                // Port closed externally.
                break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private void ForceKeyUp()
    {
        _pump.ForceKeyUp();
        if (_keying)
        {
            _keyingOutput?.KeyUp();
            _keying = false;
        }
        _pttOutput?.PttUp();
    }

    private void WriteToPort(byte[] data)
    {
        try
        {
            var port = _port;
            if (port is not null && port.IsOpen)
            {
                port.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            // Best effort — don't let a write failure crash the host.
            LogStation($"Write error: {ex.Message}");
        }
    }

    private static void LogStation(string msg)
    {
        try
        {
            RWK.Shared.IO.RotatingFileLog.Append("station-logger.log", msg);
        }
        catch { }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// Bridges <see cref="IProtocolLogger"/> to the legacy <see cref="WinKeyerEmulator.Core.ILogger"/>
/// interface expected by <see cref="WinKeyerProtocol"/>.
/// </summary>
internal sealed class ProtocolLoggerBridge : WinKeyerEmulator.Core.ILogger
{
    private readonly IProtocolLogger _inner;

    public ProtocolLoggerBridge(IProtocolLogger inner) => _inner = inner;

    public void Log(string message, LogSeverity severity, string? source = null)
    {
        var mapped = severity switch
        {
            LogSeverity.Warning => ProtocolLogSeverity.Warning,
            LogSeverity.Error => ProtocolLogSeverity.Error,
            _ => ProtocolLogSeverity.Info
        };
        _inner.Log(message, mapped, source);
    }
}
