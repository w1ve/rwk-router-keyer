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

namespace RWK.Shared.Interop;

/// <summary>
/// Shared P/Invoke surface for the native Windows APIs used by the keying paths on both
/// the Client and the Station.
/// </summary>
/// <remarks>
/// Carried forward from the v1 layer (<c>WinKeyerEmulator.App.IO.NativeMethods</c>) so that
/// existing serial code can be extended rather than rewritten, with the additions needed by
/// the v2 paddle poller and Station keying output:
/// <list type="bullet">
///   <item><description><see cref="GetCommModemStatus"/> plus the <c>MS_*</c> modem status
///   bits, for polling paddle contacts (1.1, 1.2).</description></item>
///   <item><description><see cref="TimeBeginPeriod"/> / <see cref="TimeEndPeriod"/>, for the
///   1 ms system timer resolution the 1 ms poll interval depends on (1.7).</description></item>
///   <item><description><see cref="SetThreadPriority"/> plus the <c>THREAD_PRIORITY_*</c>
///   values, for the elevated timing threads (14.6, 14.7).</description></item>
/// </list>
/// <para>
/// This type is deliberately general rather than paddle-specific: the Station keying output
/// consumes the same serial declarations. Members are public because callers live in other
/// assemblies (RWK.Client, RWK.Station).
/// </para>
/// <para>
/// Every entry point here is Windows-only. RWK ships Windows-only, so calls are not guarded
/// by platform checks.
/// </para>
/// _Requirements: 1.1, 1.7, 14.6, 14.7_
/// </remarks>
public static partial class NativeMethods
{
    // ─── Timer Resolution (winmm) ────────────────────────────────────────────

    /// <summary>
    /// Sets the minimum system timer resolution. Calling with <paramref name="uPeriod"/> = 1
    /// requests 1 ms resolution (1.7).
    /// </summary>
    /// <remarks>
    /// The resolution is a system-wide, reference-counted resource: every successful call must
    /// be paired with a <see cref="TimeEndPeriod"/> call using the same period.
    /// </remarks>
    /// <returns><see cref="TIMERR_NOERROR"/> on success.</returns>
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint uPeriod);

    /// <summary>
    /// Releases a timer resolution previously requested by <see cref="TimeBeginPeriod"/>.
    /// Must be called with the same period value.
    /// </summary>
    /// <returns><see cref="TIMERR_NOERROR"/> on success.</returns>
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint uPeriod);

    /// <summary>Success return value for <see cref="TimeBeginPeriod"/> / <see cref="TimeEndPeriod"/>.</summary>
    public const uint TIMERR_NOERROR = 0;

    // ─── Thread Priority (kernel32) ──────────────────────────────────────────

    /// <summary>
    /// Returns a pseudo-handle for the calling thread. The handle needs no cleanup.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    /// <summary>
    /// Sets the priority of a thread to one of the <c>THREAD_PRIORITY_*</c> values.
    /// </summary>
    /// <remarks>
    /// Used to place the Client keyer/poller thread at <see cref="THREAD_PRIORITY_HIGHEST"/>
    /// (14.6) and the Station replay thread at <see cref="THREAD_PRIORITY_TIME_CRITICAL"/>
    /// (14.7). The managed <see cref="System.Threading.ThreadPriority"/> enum has no
    /// time-critical value, which is why the native call is needed.
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadPriority(nint hThread, int nPriority);

    /// <summary>Base priority 15 within a normal-priority process (Station replay thread, 14.7).</summary>
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    /// <summary>Two points above normal (Client keyer/paddle thread, 14.6).</summary>
    public const int THREAD_PRIORITY_HIGHEST = 2;

    /// <summary>One point above normal.</summary>
    public const int THREAD_PRIORITY_ABOVE_NORMAL = 1;

    /// <summary>Normal priority. Discovery listener/emitter threads run here (15.18).</summary>
    public const int THREAD_PRIORITY_NORMAL = 0;

    /// <summary>One point below normal.</summary>
    public const int THREAD_PRIORITY_BELOW_NORMAL = -1;

    /// <summary>Two points below normal.</summary>
    public const int THREAD_PRIORITY_LOWEST = -2;

    /// <summary>Base priority 1 within a normal-priority process.</summary>
    public const int THREAD_PRIORITY_IDLE = -15;

    // ─── Serial Port (kernel32) ──────────────────────────────────────────────

    /// <summary>
    /// Opens a file or device. Used with the <c>\\.\COMx</c> device path form so that ports
    /// numbered COM10 and above open correctly.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Reads the modem status register, yielding the current state of the input control lines.
    /// </summary>
    /// <remarks>
    /// The paddle poller calls this at 1 ms intervals and maps CTS to dit, DSR to dah, and
    /// DCD (RLSD) to the straight key (1.1, 1.2).
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommModemStatus(SafeFileHandle hFile, out uint lpModemStat);

    /// <summary>
    /// Directs a serial port to perform an extended function, for example asserting or
    /// clearing DTR or RTS.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EscapeCommFunction(SafeFileHandle hFile, uint dwFunc);

    /// <summary>
    /// Configures a communications device according to the specified <see cref="DCB"/>.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    /// <summary>
    /// Retrieves the current control settings for a communications device.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    /// <summary>
    /// Closes an open object handle.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    // ─── Serial Port Constants ───────────────────────────────────────────────

    /// <summary>Read access for <see cref="CreateFile"/>.</summary>
    public const uint GENERIC_READ = 0x80000000;

    /// <summary>Write access for <see cref="CreateFile"/>.</summary>
    public const uint GENERIC_WRITE = 0x40000000;

    /// <summary>Open only an existing device; required for COM ports.</summary>
    public const uint OPEN_EXISTING = 3;

    /// <summary>Normal file attributes for <see cref="CreateFile"/>.</summary>
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // EscapeCommFunction function codes.

    /// <summary>Assert RTS.</summary>
    public const uint SETRTS = 3;

    /// <summary>Clear RTS.</summary>
    public const uint CLRRTS = 4;

    /// <summary>Assert DTR. The paddle port asserts DTR as the contact voltage source (1.6).</summary>
    public const uint SETDTR = 5;

    /// <summary>Clear DTR.</summary>
    public const uint CLRDTR = 6;

    // Modem status register bits returned by GetCommModemStatus.

    /// <summary>Clear To Send is asserted. Mapped to the dit contact (1.2).</summary>
    public const uint MS_CTS_ON = 0x0010;

    /// <summary>Data Set Ready is asserted. Mapped to the dah contact (1.2).</summary>
    public const uint MS_DSR_ON = 0x0020;

    /// <summary>Ring Indicator is asserted. Unused by RWK; declared for completeness.</summary>
    public const uint MS_RING_ON = 0x0040;

    /// <summary>
    /// Receive Line Signal Detect (carrier detect, DCD) is asserted. Mapped to the straight
    /// key contact (1.2).
    /// </summary>
    public const uint MS_RLSD_ON = 0x0080;

    // DCB control flags for DTR/RTS handling.

    /// <summary>Leave DTR under manual control via <see cref="EscapeCommFunction"/>.</summary>
    public const uint DTR_CONTROL_DISABLE = 0x00;

    /// <summary>Have the driver assert DTR when the port opens.</summary>
    public const uint DTR_CONTROL_ENABLE = 0x01;

    /// <summary>Leave RTS under manual control via <see cref="EscapeCommFunction"/>.</summary>
    public const uint RTS_CONTROL_DISABLE = 0x00;

    /// <summary>Have the driver assert RTS when the port opens.</summary>
    public const uint RTS_CONTROL_ENABLE = 0x01;

    // ─── DCB Structure ───────────────────────────────────────────────────────

    /// <summary>
    /// Win32 device control block. Only the fields RWK sets are given helpers; the layout must
    /// match the native structure exactly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DCB
    {
        /// <summary>Size of this structure in bytes; must be set before use.</summary>
        public uint DCBlength;

        /// <summary>Baud rate. Irrelevant to control-line keying but must be valid.</summary>
        public uint BaudRate;

        /// <summary>Packed bit fields (fBinary, fDtrControl, fRtsControl, and others).</summary>
        public uint Flags;

        /// <summary>Reserved; must be zero.</summary>
        public ushort wReserved;

        /// <summary>XON threshold.</summary>
        public ushort XonLim;

        /// <summary>XOFF threshold.</summary>
        public ushort XoffLim;

        /// <summary>Bits per byte.</summary>
        public byte ByteSize;

        /// <summary>Parity scheme (0 = none).</summary>
        public byte Parity;

        /// <summary>Stop bits (0 = one, 1 = 1.5, 2 = two).</summary>
        public byte StopBits;

        /// <summary>XON character.</summary>
        public byte XonChar;

        /// <summary>XOFF character.</summary>
        public byte XoffChar;

        /// <summary>Character replacing bytes received with a parity error.</summary>
        public byte ErrorChar;

        /// <summary>Character signalling end of data.</summary>
        public byte EofChar;

        /// <summary>Character used to signal an event.</summary>
        public byte EvtChar;

        /// <summary>Reserved; must be zero.</summary>
        public ushort wReserved1;

        /// <summary>
        /// Sets the fDtrControl bit field (bits 4-5 of <see cref="Flags"/>).
        /// </summary>
        public void SetDtrControl(uint value)
        {
            Flags = (Flags & ~(3u << 4)) | ((value & 3u) << 4);
        }

        /// <summary>
        /// Sets the fRtsControl bit field (bits 12-13 of <see cref="Flags"/>).
        /// </summary>
        public void SetRtsControl(uint value)
        {
            Flags = (Flags & ~(3u << 12)) | ((value & 3u) << 12);
        }

        /// <summary>
        /// Sets the fBinary bit (bit 0 of <see cref="Flags"/>). Windows requires binary mode.
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
