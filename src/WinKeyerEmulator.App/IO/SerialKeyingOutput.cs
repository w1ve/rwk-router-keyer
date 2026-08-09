using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.IO;

/// <summary>
/// Exception thrown when a keying operation fails.
/// </summary>
public sealed class KeyingException : Exception
{
    public KeyingException(string message) : base(message) { }
    public KeyingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Implements IKeyingOutput using native CreateFile and EscapeCommFunction
/// for minimal-latency DTR/RTS control line toggling.
/// </summary>
public sealed class SerialKeyingOutput : IKeyingOutput
{
    private SafeFileHandle? _handle;
    private KeyingLine _line;
    private bool _disposed;
    private bool _isKeyDown; // Track current key state

    /// <inheritdoc/>
    public bool IsOpen => _handle is not null && !_handle.IsInvalid && !_handle.IsClosed;

    /// <inheritdoc/>
    public void Open(string portName, KeyingLine line)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialKeyingOutput));
        if (IsOpen) throw new InvalidOperationException("Port is already open.");

        _line = line;
        _isKeyDown = false;

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
        if (!NativeMethods.EscapeCommFunction(_handle!, func))
        {
            int error = Marshal.GetLastWin32Error();
            throw new KeyingException($"KeyDown failed: EscapeCommFunction returned false (error {error})");
        }
        _isKeyDown = true;
    }

    /// <inheritdoc/>
    public void KeyUp()
    {
        if (!IsOpen) return;

        uint func = _line == KeyingLine.DTR ? NativeMethods.CLRDTR : NativeMethods.CLRRTS;
        if (!NativeMethods.EscapeCommFunction(_handle!, func))
        {
            int error = Marshal.GetLastWin32Error();
            // Log but don't throw on KeyUp - we always want to try to release the key
            // The line may already be released or the port may have disconnected
            System.Diagnostics.Debug.WriteLine($"KeyUp warning: EscapeCommFunction returned false (error {error})");
        }
        _isKeyDown = false;
    }

    /// <summary>
    /// Ensures the key is released. Call this in cleanup paths.
    /// </summary>
    public void EnsureKeyUp()
    {
        if (_isKeyDown)
        {
            try { KeyUp(); } catch { /* Best effort */ }
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_handle is not null && !_handle.IsInvalid && !_handle.IsClosed)
        {
            // Ensure lines are de-asserted before closing
            EnsureKeyUp();

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
