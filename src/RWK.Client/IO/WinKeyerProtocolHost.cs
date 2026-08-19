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
using RWK.Shared.IO;
using RWK.Shared.Protocol;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Protocol;
using SharedProtocolState = RWK.Shared.Protocol.ProtocolState;
using V1ProtocolState = WinKeyerEmulator.Core.Protocol.ProtocolState;

namespace RWK.Client.IO;

/// <summary>
/// Wraps the existing <see cref="WinKeyerProtocol"/> state machine behind a physical
/// (or virtual) serial port at 1200 baud 8-N-2, implementing <see cref="IWinKeyerProtocolHost"/>.
/// </summary>
/// <remarks>
/// This is design Component 2. The class:
/// <list type="bullet">
///   <item>Opens the port at 1200 baud, 8 data bits, no parity, 2 stop bits (2.1).</item>
///   <item>Runs a background reader thread feeding bytes to the protocol state machine (2.2).</item>
///   <item>Forwards text characters to the keyer core via <see cref="TextReceived"/> (2.3).</item>
///   <item>Handles immediate key commands via <see cref="KeyImmediate"/> (2.4).</item>
///   <item>Echoes characters per WK2 spec via <see cref="SendCharacterEcho"/> (2.5).</item>
///   <item>Two-way speed sync via <see cref="SpeedChanged"/> and <see cref="ReportSpeedToHost"/> (2.6, 2.7).</item>
/// </list>
/// <para>
/// NOTE: This references <c>WinKeyerEmulator.Core</c> temporarily. Once the protocol engine
/// is ported to <c>RWK.Shared</c> (task 20.1), this reference will be removed.
/// </para>
/// _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7_
/// </remarks>
public sealed class WinKeyerProtocolHost : IWinKeyerProtocolHost
{
    private readonly WinKeyerProtocol _protocol;
    private readonly IProtocolLogger _logger;
    private readonly SharedProtocolState _sharedState = new();
    private SerialPort? _port;
    private Thread? _readerThread;
    private volatile bool _running;
    private readonly object _writeLock = new();

    /// <inheritdoc/>
    public event EventHandler<char>? TextReceived;

    /// <inheritdoc/>
    public event EventHandler<int>? SpeedChanged;

    /// <inheritdoc/>
    public event EventHandler<bool>? KeyImmediate;

    /// <inheritdoc/>
    public event EventHandler? BufferCleared;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? ResponseReady;

    /// <inheritdoc/>
    public SharedProtocolState State
    {
        get
        {
            // Sync the shared-namespace state from the v1 protocol engine's internal state.
            var v1State = _protocol.State;
            _sharedState.HostMode = v1State.HostMode;
            _sharedState.CurrentWpm = v1State.CurrentWpm;
            _sharedState.BufferState = v1State.BufferState switch
            {
                WinKeyerEmulator.Core.Protocol.BufferState.Sending => RWK.Shared.Protocol.BufferState.Sending,
                _ => RWK.Shared.Protocol.BufferState.Idle
            };
            return _sharedState;
        }
    }

    /// <summary>
    /// Creates a new WinKeyerProtocolHost.
    /// </summary>
    /// <param name="logger">Logger for protocol state machine diagnostics.</param>
    public WinKeyerProtocolHost(IProtocolLogger logger)
    {
        _logger = logger;

        // Wrap the shared IProtocolLogger into the legacy ILogger expected by WinKeyerProtocol.
        var legacyLogger = new ProtocolLoggerBridge(logger);
        _protocol = new WinKeyerProtocol(legacyLogger);

        // Wire protocol events to our public surface.
        _protocol.TextReceived += (_, c) => TextReceived?.Invoke(this, c);
        _protocol.SpeedChanged += (_, wpm) => SpeedChanged?.Invoke(this, wpm);
        _protocol.KeyImmediate += (_, down) => KeyImmediate?.Invoke(this, down);
        _protocol.BufferCleared += (_, _) => BufferCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Start(string portName)
    {
        if (_running)
            return;

        LogWk($"START: Opening {portName} at 1200 8-N-2");

        try
        {
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

            _readerThread = new Thread(ReaderLoop)
            {
                Name = "WinKeyerHost-Reader",
                IsBackground = true
            };
            _readerThread.Start();

            LogWk($"START: Port opened successfully, reader thread started");
            _logger.Log("WinKeyerProtocolHost started", ProtocolLogSeverity.Info, "WKHost");
        }
        catch (Exception ex)
        {
            LogWk($"START FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (!_running)
            return;

        _running = false;

        // Close the port to unblock any blocking read.
        try { _port?.Close(); } catch { /* best effort */ }

        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread = null;

        try { _port?.Dispose(); } catch { /* best effort */ }
        _port = null;

        _logger.Log("WinKeyerProtocolHost stopped", ProtocolLogSeverity.Info, "WKHost");
    }

    /// <inheritdoc/>
    public void SendStatus(byte status)
    {
        WriteBytes(new[] { status });
    }

    /// <inheritdoc/>
    public void SendCharacterEcho(char c)
    {
        // WK2 spec: echoed characters are sent as raw ASCII bytes (< 0x80).
        // The host differentiates them from status bytes by checking bits 7:6.
        WriteBytes(new[] { (byte)c });
    }

    /// <inheritdoc/>
    public void ReportSpeedToHost(int wpm)
    {
        // WK2 spec: when speed changes from the paddle/pot side, send a status byte
        // with the speed-pot-change flag. However, the actual K1EL hardware sends a
        // speed-pot status byte (0xC0 | flags). For N1MM+ compatibility, we send the
        // updated status byte so the logger polls for the new value.
        // The protocol state machine tracks the canonical speed; update it here.
        _protocol.State.CurrentWpm = wpm;

        // Send a status byte indicating the change, so polling loggers pick it up.
        var statusByte = _protocol.GetStatusByte();
        WriteBytes(new[] { statusByte });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// Injects a byte directly into the protocol state machine as if it were received
    /// from the serial port. Used for loopback testing without physical hardware.
    /// </summary>
    /// <param name="b">The protocol byte to process.</param>
    /// <returns>Response bytes produced by the state machine, or null.</returns>
    public byte[]? InjectByte(byte b)
    {
        LogWk($"INJECT: 0x{b:X2}");

        byte[]? response = _protocol.ProcessByte(b);

        if (response is { Length: > 0 })
        {
            LogWk($"INJECT-TX: [{string.Join(" ", response.Select(x => $"0x{x:X2}"))}]");
            ResponseReady?.Invoke(this, response);
        }

        return response;
    }

    /// <summary>
    /// Injects a sequence of bytes into the protocol state machine.
    /// Convenience wrapper over <see cref="InjectByte"/>.
    /// </summary>
    /// <param name="data">The protocol bytes to process.</param>
    public void InjectBytes(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            InjectByte(b);
    }

    /// <summary>
    /// Background reader loop: reads bytes one at a time from the serial port and
    /// feeds them to the protocol state machine.
    /// </summary>
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

                LogWk($"RX: 0x{b:X2}");

                // Feed the byte to the protocol state machine.
                byte[]? response = _protocol.ProcessByte((byte)b);

                // If the state machine produces response bytes, write them back and
                // notify listeners.
                if (response is { Length: > 0 })
                {
                    LogWk($"TX: [{string.Join(" ", response.Select(x => $"0x{x:X2}"))}]");
                    WriteBytes(response);
                    ResponseReady?.Invoke(this, response);
                }
            }
            catch (TimeoutException)
            {
                // ReadByte timed out — this is normal, loop and try again.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Port closed or device removed.
                if (_running)
                {
                    _logger.Log("Serial port I/O error on WinKeyer port",
                        ProtocolLogSeverity.Error, "WKHost");
                }
                break;
            }
            catch (InvalidOperationException)
            {
                // Port was closed externally.
                break;
            }
        }
    }

    private static void LogWk(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "winkeyer.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    /// <summary>
    /// Writes bytes to the serial port in a thread-safe manner.
    /// </summary>
    private void WriteBytes(byte[] data)
    {
        lock (_writeLock)
        {
            try
            {
                var port = _port;
                if (port is not null && port.IsOpen)
                {
                    port.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                _logger.Log($"Failed to write to WinKeyer port: {ex.Message}",
                    ProtocolLogSeverity.Warning, "WKHost");
            }
        }
    }

    /// <summary>
    /// Bridges the <see cref="IProtocolLogger"/> interface (RWK.Shared) to the
    /// <see cref="WinKeyerEmulator.Core.ILogger"/> interface expected by the legacy
    /// <see cref="WinKeyerProtocol"/> class.
    /// </summary>
    private sealed class ProtocolLoggerBridge : WinKeyerEmulator.Core.ILogger
    {
        private readonly IProtocolLogger _inner;

        public ProtocolLoggerBridge(IProtocolLogger inner)
        {
            _inner = inner;
        }

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
}
