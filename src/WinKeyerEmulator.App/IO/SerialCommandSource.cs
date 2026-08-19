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
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.IO;

/// <summary>
/// Implements ICommandSource and ICommandSink over a physical serial port.
/// Reads incoming WinKeyer commands from host software (e.g., N1MM, DXLab)
/// and sends response bytes back to the host.
/// </summary>
public sealed class SerialCommandSource : ICommandSource, ICommandSink
{
    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _running;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>
    /// Raised when the port is disconnected unexpectedly (e.g., USB unplug).
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Starts listening on the specified serial port at 1200 baud, 8N1.
    /// </summary>
    /// <param name="portName">COM port name (e.g., "COM3").</param>
    public void Start(string portName)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialCommandSource));
        if (_running) throw new InvalidOperationException("Source is already running.");

        _port = new SerialPort(portName, 1200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };

        _port.Open();
        _running = true;

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "SerialCommandSource_ReadLoop"
        };
        _readThread.Start();
    }

    /// <inheritdoc/>
    public void Start()
    {
        throw new InvalidOperationException("Use Start(string portName) to specify the port.");
    }

    /// <inheritdoc/>
    public void SendResponse(byte[] data)
    {
        if (!_running || _port is null || !_port.IsOpen) return;

        try
        {
            _port.Write(data, 0, data.Length);
        }
        catch (IOException)
        {
            // Port disconnected during write; stop will be triggered by read loop
        }
        catch (InvalidOperationException)
        {
            // Port already closed
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _running = false;

        try
        {
            _port?.Close();
        }
        catch
        {
            // Best effort close
        }

        _readThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _readThread = null;
        _port?.Dispose();
        _port = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[256];

        while (_running)
        {
            try
            {
                if (_port is null || !_port.IsOpen) break;

                int bytesRead = _port.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);
                    DataReceived?.Invoke(this, data);
                }
            }
            catch (TimeoutException)
            {
                // Normal timeout on read, continue loop
            }
            catch (IOException)
            {
                // Port disconnected
                if (_running)
                {
                    _running = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
                break;
            }
            catch (OperationCanceledException)
            {
                // Port was closed during read — normal shutdown
                break;
            }
            catch (InvalidOperationException)
            {
                // Port was closed externally
                if (_running)
                {
                    _running = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
                break;
            }
        }
    }
}
