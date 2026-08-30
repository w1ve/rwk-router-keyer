/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MorseTest;

/// <summary>
/// Reads the status of serial port control pins (DTR/RTS) using P/Invoke
/// for direct COM port access via file methods.
/// </summary>
public sealed class SerialPinReader : IDisposable
{
    // Win32 API Constants
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // Modem Status Register bits
    private const uint MS_CTS_ON = 0x0010;  // Clear To Send
    private const uint MS_DSR_ON = 0x0020;  // Data Set Ready
    private const uint MS_RING_ON = 0x0040; // Ring Indicator
    private const uint MS_RLSD_ON = 0x0080; // Receive Line Signal Detect (DCD/Carrier Detect)

    // DCB flags for EscapeCommFunction
    private const int SETDTR = 5;
    private const int CLRDTR = 6;
    private const int SETRTS = 3;
    private const int CLRRTS = 4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommModemStatus(SafeFileHandle hFile, out uint lpModemStat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle hFile, int dwFunc);

    private SafeFileHandle? _handle;
    private string _portName = string.Empty;
    private bool _disposed;

    public bool IsOpen => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;
    public string PortName => _portName;

    /// <summary>
    /// Opens the specified COM port for pin status reading.
    /// </summary>
    /// <param name="portName">Port name like "COM1", "COM3", etc.</param>
    public void Open(string portName)
    {
        Close();

        // For COM ports >= COM10, we need the \\.\COM10 format
        string devicePath = portName;
        if (!portName.StartsWith(@"\\.\"))
        {
            devicePath = @"\\.\" + portName;
        }

        _handle = CreateFile(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            0, // No sharing
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to open {portName}. Error code: {error}");
        }

        _portName = portName;
    }

    /// <summary>
    /// Closes the COM port.
    /// </summary>
    public void Close()
    {
        if (_handle != null && !_handle.IsInvalid)
        {
            _handle.Close();
            _handle = null;
        }
        _portName = string.Empty;
    }

    /// <summary>
    /// Reads the current modem status register and returns individual pin states.
    /// </summary>
    public ModemPinStatus GetPinStatus()
    {
        if (!IsOpen)
            throw new InvalidOperationException("Port is not open");

        if (!GetCommModemStatus(_handle!, out uint status))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to get modem status. Error code: {error}");
        }

        return new ModemPinStatus
        {
            CTS = (status & MS_CTS_ON) != 0,
            DSR = (status & MS_DSR_ON) != 0,
            Ring = (status & MS_RING_ON) != 0,
            DCD = (status & MS_RLSD_ON) != 0
        };
    }

    /// <summary>
    /// Sets the DTR (Data Terminal Ready) output pin state.
    /// </summary>
    public void SetDTR(bool state)
    {
        if (!IsOpen)
            throw new InvalidOperationException("Port is not open");

        if (!EscapeCommFunction(_handle!, state ? SETDTR : CLRDTR))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to set DTR. Error code: {error}");
        }
    }

    /// <summary>
    /// Sets the RTS (Request To Send) output pin state.
    /// </summary>
    public void SetRTS(bool state)
    {
        if (!IsOpen)
            throw new InvalidOperationException("Port is not open");

        if (!EscapeCommFunction(_handle!, state ? SETRTS : CLRRTS))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to set RTS. Error code: {error}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents the state of modem control input pins.
/// Note: DTR and RTS are OUTPUT pins on the DTE side, so we read CTS, DSR, DCD, and Ring.
/// Typically, a key connected to the serial port will toggle CTS or DSR via a loopback
/// from DTR or RTS.
/// </summary>
public struct ModemPinStatus
{
    /// <summary>Clear To Send - typically reflects RTS when looped back</summary>
    public bool CTS { get; set; }
    
    /// <summary>Data Set Ready - typically reflects DTR when looped back</summary>
    public bool DSR { get; set; }
    
    /// <summary>Ring Indicator</summary>
    public bool Ring { get; set; }
    
    /// <summary>Data Carrier Detect (Receive Line Signal Detect)</summary>
    public bool DCD { get; set; }
}
