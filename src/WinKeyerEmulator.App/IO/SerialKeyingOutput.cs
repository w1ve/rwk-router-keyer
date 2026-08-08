using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.IO;

/// <summary>
/// Implements IKeyingOutput using native CreateFile and EscapeCommFunction
/// for minimal-latency DTR/RTS control line toggling.
/// </summary>
public sealed class SerialKeyingOutput : IKeyingOutput
{
    private SafeFileHandle? _handle;
    private KeyingLine _line;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsOpen => _handle is not null && !_handle.IsInvalid && !_handle.IsClosed;

    /// <inheritdoc/>
    public void Open(string portName, KeyingLine line)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialKeyingOutput));
        if (IsOpen) throw new InvalidOperationException("Port is already open.");

        _line = line;

        // Open the COM port using the \\.\COMx device path
        string devicePath = $"\\\\.\\{portName}";
        _handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            0, // No sharing
            nint.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            nint.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Failed to open serial port '{portName}' (error {error}).");
        }

        ConfigureDcb(_handle);
    }

    /// <inheritdoc/>
    public void KeyDown()
    {
        if (!IsOpen) return;

        uint func = _line == KeyingLine.DTR ? NativeMethods.SETDTR : NativeMethods.SETRTS;
        NativeMethods.EscapeCommFunction(_handle!, func);
    }

    /// <inheritdoc/>
    public void KeyUp()
    {
        if (!IsOpen) return;

        uint func = _line == KeyingLine.DTR ? NativeMethods.CLRDTR : NativeMethods.CLRRTS;
        NativeMethods.EscapeCommFunction(_handle!, func);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_handle is not null && !_handle.IsInvalid && !_handle.IsClosed)
        {
            // Ensure lines are de-asserted before closing
            try
            {
                KeyUp();
            }
            catch
            {
                // Best effort
            }

            _handle.Close();
            _handle = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Close();
        }
    }

    /// <summary>
    /// Configures the DCB to disable automatic DTR/RTS control,
    /// giving us manual control via EscapeCommFunction.
    /// </summary>
    private static void ConfigureDcb(SafeFileHandle handle)
    {
        var dcb = new NativeMethods.DCB();
        dcb.DCBlength = (uint)Marshal.SizeOf<NativeMethods.DCB>();

        if (!NativeMethods.GetCommState(handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetCommState failed (error {error}).");
        }

        // Configure for manual DTR/RTS control
        dcb.SetBinary(true);
        dcb.SetDtrControl(NativeMethods.DTR_CONTROL_DISABLE);
        dcb.SetRtsControl(NativeMethods.RTS_CONTROL_DISABLE);
        dcb.BaudRate = 9600; // Baud rate doesn't matter for keying, but must be set
        dcb.ByteSize = 8;
        dcb.Parity = 0; // None
        dcb.StopBits = 0; // One

        if (!NativeMethods.SetCommState(handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"SetCommState failed (error {error}).");
        }
    }
}
