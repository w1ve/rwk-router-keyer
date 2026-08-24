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

namespace RWK.Client.IO;

/// <summary>
/// Polls a serial port control line (DTR or RTS — read as DSR or CTS respectively on the
/// input side) to detect a footswitch PTT closure. Fires <see cref="PttStateChanged"/> on
/// transitions.
/// </summary>
/// <remarks>
/// A footswitch typically grounds a control line when pressed. The poller reads DSR (for DTR
/// wired setups) or CTS (for RTS wired setups) at 10ms intervals. The serial port is opened
/// with no data transfer — only the pin state is monitored.
/// </remarks>
public sealed class PttFootswitchPoller : IDisposable
{
    /// <summary>Which input line to monitor.</summary>
    public enum PttInputLine { DSR, CTS }

    private SerialPort? _port;
    private System.Threading.Timer? _pollTimer;
    private volatile bool _lastState;
    private bool _disposed;

    private readonly PttInputLine _line;

    /// <summary>Fired when the footswitch PTT state changes. True = pressed, false = released.</summary>
    public event EventHandler<bool>? PttStateChanged;

    /// <summary>Whether the footswitch is currently pressed.</summary>
    public bool IsPressed => _lastState;

    /// <summary>Whether the poller is actively monitoring.</summary>
    public bool IsRunning => _port is not null && _port.IsOpen;

    public PttFootswitchPoller(PttInputLine line = PttInputLine.DSR)
    {
        _line = line;
    }

    /// <summary>
    /// Opens the specified COM port and begins polling the configured input line.
    /// </summary>
    /// <param name="portName">COM port name (e.g. "COM3").</param>
    public void Start(string portName)
    {
        Stop();

        _port = new SerialPort(portName)
        {
            BaudRate = 9600,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 100,
            WriteTimeout = 100
        };
        _port.Open();

        _lastState = ReadPin();
        _pollTimer = new System.Threading.Timer(PollCallback, null, 10, 10);
    }

    /// <summary>
    /// Stops polling and closes the COM port.
    /// </summary>
    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (_port is not null)
        {
            try { _port.Close(); } catch { }
            _port.Dispose();
            _port = null;
        }

        if (_lastState)
        {
            _lastState = false;
            PttStateChanged?.Invoke(this, false);
        }
    }

    private void PollCallback(object? state)
    {
        try
        {
            if (_port is null || !_port.IsOpen) return;

            bool current = ReadPin();
            if (current != _lastState)
            {
                _lastState = current;
                PttStateChanged?.Invoke(this, current);
            }
        }
        catch
        {
            // Port may have been removed — stop gracefully
            Stop();
        }
    }

    private bool ReadPin()
    {
        if (_port is null || !_port.IsOpen) return false;
        return _line switch
        {
            PttInputLine.DSR => _port.DsrHolding,
            PttInputLine.CTS => _port.CtsHolding,
            _ => false
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
