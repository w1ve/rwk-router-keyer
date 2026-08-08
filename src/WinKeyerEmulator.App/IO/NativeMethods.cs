using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinKeyerEmulator.App.IO;

/// <summary>
/// P/Invoke declarations for native Windows APIs used by the emulator.
/// </summary>
internal static partial class NativeMethods
{
    // ─── Timer Resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Sets the minimum timer resolution for the application.
    /// Calling with uPeriod=1 requests 1ms timer resolution.
    /// </summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint uPeriod);

    /// <summary>
    /// Clears a previously set minimum timer resolution.
    /// Must be called with the same uPeriod value as timeBeginPeriod.
    /// </summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint uPeriod);

    // ─── Serial Port P/Invokes ───────────────────────────────────────────────

    /// <summary>
    /// Opens a file or device (used to open COM ports).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Directs a serial port to perform an extended function (e.g., set/clear DTR/RTS).
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EscapeCommFunction(SafeFileHandle hFile, uint dwFunc);

    /// <summary>
    /// Configures a communications device according to the specified DCB structure.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    /// <summary>
    /// Retrieves the current control settings for a specified communications device.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    /// <summary>
    /// Closes an open object handle.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // ─── Serial Port Constants ───────────────────────────────────────────────

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // EscapeCommFunction function codes
    internal const uint SETDTR = 5;
    internal const uint CLRDTR = 6;
    internal const uint SETRTS = 3;
    internal const uint CLRRTS = 4;

    // DCB control flags for DTR/RTS
    internal const uint DTR_CONTROL_DISABLE = 0x00;
    internal const uint DTR_CONTROL_ENABLE = 0x01;
    internal const uint RTS_CONTROL_DISABLE = 0x00;
    internal const uint RTS_CONTROL_ENABLE = 0x01;

    // ─── DCB Structure ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags; // Packed bit fields
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;

        /// <summary>
        /// Sets fDtrControl bits (bits 4-5 in Flags).
        /// </summary>
        public void SetDtrControl(uint value)
        {
            Flags = (Flags & ~(3u << 4)) | ((value & 3u) << 4);
        }

        /// <summary>
        /// Sets fRtsControl bits (bits 12-13 in Flags).
        /// </summary>
        public void SetRtsControl(uint value)
        {
            Flags = (Flags & ~(3u << 12)) | ((value & 3u) << 12);
        }

        /// <summary>
        /// Sets fBinary bit (bit 0 in Flags).
        /// </summary>
        public void SetBinary(bool value)
        {
            if (value)
                Flags |= 1u;
            else
                Flags &= ~1u;
        }
    }
}
