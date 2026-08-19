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

namespace RWK.Client.IO;

/// <summary>
/// Drives a physical K1EL WinKeyer2/3 chip over serial, implementing <see cref="IWinKeyerProtocolHost"/>.
/// </summary>
/// <remarks>
/// In this mode the RWK Client acts as a HOST talking TO the hardware WinKeyer.
/// The flow is the reverse of <see cref="WinKeyerProtocolHost"/>:
/// <list type="bullet">
///   <item>Opens the serial port at 1200 baud, 8-N-2 (same as the K1EL spec).</item>
///   <item>Sends Admin Open (0x00 0x02) and waits for the version + status response.</item>
///   <item>Sets speed via the Speed command (0x02 WPM).</item>
///   <item>Sends buffered text as raw ASCII bytes (0x20-0x7E) that the chip keys.</item>
///   <item>Reads status bytes (bits 7:6 = 0xC0) and character echoes from the chip.</item>
///   <item>Surfaces the same events as the Logger App mode so the controller wiring is unchanged.</item>
/// </list>
/// <para>
/// The hardware WinKeyer handles all timing (dit/dah generation, iambic logic, weighting).
/// RWK just feeds it text and relays the resulting status/echoes to the UI and edge generation.
/// </para>
/// </remarks>
public sealed class HardwareWinKeyerHost : IWinKeyerProtocolHost
{
    private SerialPort? _port;
    private Thread? _readerThread;
    private volatile bool _running;
    private readonly object _writeLock = new();
    private int _chipVersion;
    private int _currentWpm = 25;
    private readonly ProtocolState _state = new();

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

    /// <summary>
    /// Raised when the hardware WinKeyer chip has been successfully opened and identified.
    /// The int argument is the chip version number.
    /// </summary>
    public event EventHandler<int>? ChipOpened;

    /// <inheritdoc/>
    public ProtocolState State => _state;

    /// <summary>
    /// Gets the WinKeyer chip version reported during Admin Open, or 0 if not opened.
    /// </summary>
    public int ChipVersion => _chipVersion;

    /// <inheritdoc/>
    public void Start(string portName)
    {
        if (_running)
            return;

        LogHw($"START: Opening {portName} at 1200 8-N-2 (hardware WinKeyer mode)");

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
                ReadTimeout = 2000,
                WriteTimeout = 500
            };

            _port.Open();

            // Send Admin Open to initialize the WinKeyer chip.
            if (!SendAdminOpen())
            {
                _port.Close();
                _port.Dispose();
                _port = null;
                throw new InvalidOperationException(
                    "WinKeyer chip did not respond to Admin Open. Check that a K1EL WinKeyer is connected.");
            }

            _state.HostMode = true;
            _running = true;

            _readerThread = new Thread(ReaderLoop)
            {
                Name = "HardwareWinKeyer-Reader",
                IsBackground = true
            };
            _readerThread.Start();

            LogHw($"START: Chip version {_chipVersion}, reader thread started");
            ChipOpened?.Invoke(this, _chipVersion);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            LogHw($"START FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (!_running)
            return;

        _running = false;

        // Send Admin Close to return the chip to standalone mode.
        SendAdminClose();

        try { _port?.Close(); } catch { }

        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread = null;

        try { _port?.Dispose(); } catch { }
        _port = null;

        _state.HostMode = false;
        LogHw("STOP: Port closed");
    }

    /// <inheritdoc/>
    public void SendStatus(byte status)
    {
        // In hardware mode, we don't send status bytes TO the chip.
        // The chip sends status bytes to US. This is a no-op.
    }

    /// <inheritdoc/>
    public void SendCharacterEcho(char c)
    {
        // In hardware mode, the chip echoes characters to us.
        // We don't echo back. This is a no-op.
    }

    /// <inheritdoc/>
    public void ReportSpeedToHost(int wpm)
    {
        // In hardware mode, send a Speed command to the chip so it
        // adjusts its internal timing.
        SetSpeed(wpm);
    }

    /// <summary>
    /// Sends buffered text to the hardware WinKeyer for keying.
    /// Each character is sent as a raw ASCII byte (0x20-0x7E).
    /// The chip handles all timing and reports echoes when each character completes.
    /// </summary>
    /// <param name="text">The text to key.</param>
    public void SendText(string text)
    {
        if (!_running || _port is null) return;

        foreach (char c in text)
        {
            byte b = (byte)c;
            if (b >= CommandDefinitions.PrintableAsciiStart && b <= CommandDefinitions.PrintableAsciiEnd)
            {
                WriteBytes(new[] { b });
            }
        }
    }

    /// <summary>
    /// Sets the keying speed on the hardware WinKeyer.
    /// </summary>
    /// <param name="wpm">Speed in words per minute (5-60).</param>
    public void SetSpeed(int wpm)
    {
        wpm = Math.Clamp(wpm, 5, 60);
        _currentWpm = wpm;
        _state.CurrentWpm = wpm;

        if (_running)
        {
            WriteBytes(new[] { CommandDefinitions.SpeedCmd, (byte)wpm });
            LogHw($"SET SPEED: {wpm} WPM");
        }
    }

    /// <summary>
    /// Sends a Clear Buffer command to the hardware WinKeyer, aborting any text in progress.
    /// </summary>
    public void ClearBuffer()
    {
        if (_running)
        {
            WriteBytes(new[] { CommandDefinitions.ClearBufferCmd });
            LogHw("CLEAR BUFFER sent");
        }
    }

    /// <summary>
    /// Sends a Key Immediate command to the hardware WinKeyer.
    /// </summary>
    /// <param name="down">True for key down, false for key up.</param>
    public void SendKeyImmediate(bool down)
    {
        if (_running)
        {
            WriteBytes(new[] { CommandDefinitions.KeyImmediateCmd, (byte)(down ? 0x01 : 0x00) });
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — Admin Open / Close
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends Admin Open (0x00 0x02) and reads the 2-byte response (version + status).
    /// Returns true if the chip responded correctly.
    /// </summary>
    private bool SendAdminOpen()
    {
        if (_port is null || !_port.IsOpen) return false;

        // Send Admin Open
        _port.Write(new byte[] { CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen }, 0, 2);

        // Read response: version byte + status byte
        try
        {
            int version = _port.ReadByte();
            if (version < 0) return false;

            int status = _port.ReadByte();
            if (status < 0) return false;

            _chipVersion = version;
            LogHw($"ADMIN OPEN: version={version}, status=0x{status:X2}");
            return true;
        }
        catch (TimeoutException)
        {
            LogHw("ADMIN OPEN: timeout waiting for response");
            return false;
        }
    }

    /// <summary>
    /// Sends Admin Close (0x00 0x03) to return the chip to standalone mode.
    /// </summary>
    private void SendAdminClose()
    {
        try
        {
            if (_port is not null && _port.IsOpen)
            {
                _port.Write(new byte[] { CommandDefinitions.AdminCmd, CommandDefinitions.AdminClose }, 0, 2);
                LogHw("ADMIN CLOSE sent");
            }
        }
        catch
        {
            // Best effort on shutdown
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — Reader loop (reads status bytes and character echoes from the chip)
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
                if (b < 0) continue;

                // Classify the byte: status bytes have bits 7:6 = 0xC0
                if ((b & 0xC0) == 0xC0)
                {
                    // Status byte from the chip
                    ProcessStatusByte((byte)b);
                }
                else if (b >= CommandDefinitions.PrintableAsciiStart && b <= CommandDefinitions.PrintableAsciiEnd)
                {
                    // Character echo: the chip finished sending this character
                    char c = (char)b;
                    LogHw($"ECHO: '{c}'");
                    TextReceived?.Invoke(this, c);
                    ResponseReady?.Invoke(this, new[] { (byte)b });
                }
                else
                {
                    // Other bytes (speed pot change, paddle status, etc.)
                    LogHw($"RX: 0x{b:X2} (unhandled)");
                    ResponseReady?.Invoke(this, new[] { (byte)b });
                }
            }
            catch (TimeoutException)
            {
                // Normal — just loop and try again
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                if (_running)
                    LogHw("I/O error on hardware WinKeyer port");
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Processes a status byte received from the hardware WinKeyer.
    /// Status format: 1 1 X X B B S I (bits 7:6 always set)
    /// </summary>
    private void ProcessStatusByte(byte status)
    {
        bool isBusy = (status & 0x04) != 0;
        bool hasData = (status & 0x01) != 0;

        _state.BufferState = isBusy
            ? RWK.Shared.Protocol.BufferState.Sending
            : RWK.Shared.Protocol.BufferState.Idle;

        LogHw($"STATUS: 0x{status:X2} busy={isBusy} hasData={hasData}");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — helpers
    // ──────────────────────────────────────────────────────────────────────────────

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
                LogHw($"Write error: {ex.Message}");
            }
        }
    }

    private static void LogHw(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "winkeyer.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [HW] {msg}\n");
        }
        catch { }
    }
}
