using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.Interop;

namespace RWK.Station.IO;

/// <summary>
/// Keys the radio through a serial port's DTR and RTS control lines using
/// <c>CreateFile</c> + <c>EscapeCommFunction</c>, with independent key and PTT line selection
/// and per-line polarity inversion.
/// </summary>
/// <remarks>
/// Design Component 8. Carries forward the RWK v1 <c>SerialKeyingOutput</c> mechanics unchanged —
/// a cached <see cref="SafeFileHandle"/> opened on the <c>\\.\COMx</c> device path, a DCB with
/// <c>DTR_CONTROL_DISABLE</c> and <c>RTS_CONTROL_DISABLE</c> so the driver never asserts a keying
/// line on its own, and <c>EscapeCommFunction</c> for minimum-latency line toggling — and adds:
/// <list type="bullet">
///   <item><description>Independent key line (RTS or DTR, 8.1) and PTT line (RTS, DTR, or None, 8.2).</description></item>
///   <item><description>Per-line polarity inversion, physical = logical XOR invert (8.3).</description></item>
///   <item><description><see cref="EnsureAllLinesDown"/>, which generalizes v1's <c>EnsureKeyUp</c>
///   to every configured line (8.7).</description></item>
/// </list>
/// <para>
/// <b>Fail-safe behavior.</b> This class physically keys a transmitter, so every failure path
/// drops the transmitter rather than reporting and continuing:
/// </para>
/// <list type="bullet">
///   <item><description>Every <c>EscapeCommFunction</c> return value is checked. A failure — or any
///   exception on an assertion path — forces all configured lines inactive before the call returns,
///   so an exception can never leave a line asserted (8.7, 9.6).</description></item>
///   <item><description>If a line cannot be driven inactive, the port handle is closed. Closing the
///   handle makes the driver drop DTR and RTS (9.8).</description></item>
///   <item><description><see cref="Close"/> and <see cref="Dispose"/> drive all lines inactive
///   before releasing the handle, so closing the application while keyed keys up (9.8, F8).</description></item>
///   <item><description>Faults are surfaced on <see cref="Fault"/> so the Edge Replayer can latch
///   SAFE (F6, 9.6). The SAFE latch itself lives in the replayer; this class only obeys.</description></item>
/// </list>
/// <para>
/// <b>Inversion and the handle-closure fail-safe.</b> "Inactive" here means the logical key-up /
/// PTT-off state with the configured polarity applied, which is the only reading that actually
/// stops the transmitter. For a non-inverted line that is also the electrically de-asserted state,
/// so dropped lines mean key-up and handle closure is itself a fail-safe as 9.8 intends. Inverting
/// a line reverses that: a dropped line then reads as active at the interface, so an inverted line
/// forfeits the handle-closure fail-safe and relies on this class driving the line instead. Prefer
/// wiring the interface so no inversion is needed.
/// </para>
/// <para>
/// _Requirements: 8.1, 8.2, 8.3, 8.7, 9.6, 9.8_
/// </para>
/// </remarks>
public sealed class StationKeyingOutput : IStationKeyingOutput
{
    private readonly object _gate = new();

    private SafeFileHandle? _handle;
    private KeyingOutputConfig? _config;
    private bool _keyDown;
    private bool _pttOn;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<KeyingFaultEventArgs>? Fault;

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return IsOpenUnlocked;
            }
        }
    }

    /// <summary>Serial port this output is configured for, or <see langword="null"/> if unconfigured.</summary>
    public string? PortName => _config?.PortName;

    /// <inheritdoc/>
    public KeyingLine KeyLine => _config?.KeyLine ?? KeyingLine.RTS;

    /// <inheritdoc/>
    public KeyingLine PttLine => _config?.PttLine ?? KeyingLine.None;

    /// <inheritdoc/>
    public bool KeyInvert => _config?.KeyInvert ?? false;

    /// <inheritdoc/>
    public bool PttInvert => _config?.PttInvert ?? false;

    /// <inheritdoc/>
    public bool IsKeyDown { get { lock (_gate) { return _keyDown; } } }

    /// <inheritdoc/>
    public bool IsPttOn { get { lock (_gate) { return _pttOn; } } }

    private bool IsOpenUnlocked => _handle is not null && !_handle.IsInvalid && !_handle.IsClosed;

    /// <summary>
    /// Validates <paramref name="config"/> against the line rules of 8.1 and 8.2.
    /// </summary>
    /// <exception cref="ArgumentException">The configuration cannot key a radio safely.</exception>
    public static void Validate(KeyingOutputConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.PortName))
        {
            throw new ArgumentException("A keying port name is required.", nameof(config));
        }

        // None is valid for PTT only (8.2). A key line of None would leave nothing to key, so it
        // is a configuration error rather than a "keying disabled" mode.
        if (!KeyingLineCommands.IsValidKeyLine(config.KeyLine))
        {
            throw new ArgumentException(
                $"Key line must be RTS or DTR; got {config.KeyLine}. 'None' is valid for the PTT line only.",
                nameof(config));
        }

        if (!KeyingLineCommands.IsValidPttLine(config.PttLine))
        {
            throw new ArgumentException(
                $"PTT line must be RTS, DTR, or None; got {config.PttLine}.",
                nameof(config));
        }

        // One line cannot carry both signals: PTT would key the radio and key-up would drop PTT.
        if (config.PttLine != KeyingLine.None && config.PttLine == config.KeyLine)
        {
            throw new ArgumentException(
                $"Key and PTT cannot share the {config.KeyLine} line.",
                nameof(config));
        }
    }

    /// <inheritdoc/>
    public void Configure(KeyingOutputConfig config)
    {
        Validate(config);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsOpenUnlocked)
            {
                throw new InvalidOperationException(
                    "Close the keying port before reconfiguring lines or polarity.");
            }

            _config = config;
        }
    }

    /// <inheritdoc/>
    public void Open()
    {
        KeyingOutputConfig config;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            config = _config
                ?? throw new InvalidOperationException("Configure must be called before Open.");
        }

        OpenCore(config);
    }

    /// <summary>
    /// Opens <paramref name="portName"/> keying on <paramref name="line"/> with no PTT line and no
    /// inversion. Provided for <see cref="RWK.Shared.IO.IKeyingOutput"/> compatibility; Station code
    /// should use <see cref="Configure"/> plus <see cref="Open()"/> so PTT and polarity are set.
    /// </summary>
    public void Open(string portName, KeyingLine line)
        => OpenCore(new KeyingOutputConfig(portName, line, KeyingLine.None, false, false));

    private void OpenCore(KeyingOutputConfig config)
    {
        Validate(config);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsOpenUnlocked)
            {
                throw new InvalidOperationException("Keying port is already open.");
            }

            _config = config;
            _keyDown = false;
            _pttOn = false;

            // \\.\COMx form so ports numbered COM10 and above open correctly.
            string devicePath = $@"\\.\{config.PortName}";
            SafeFileHandle handle = NativeMethods.CreateFile(
                devicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                0, // no sharing: nothing else may drive the keying lines
                nint.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                nint.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    $"Failed to open keying port '{config.PortName}' (error {error}).");
            }

            _handle = handle;

            try
            {
                ConfigureDcb(handle);

                // Establish a known-inactive state rather than inheriting whatever the driver left
                // behind. With an inverted line this asserts that line at open, which is correct:
                // asserted-when-inverted is the key-up / PTT-off state at the interface.
                WriteUnlocked(config.KeyLine, false, config.KeyInvert, "Open/KeyUp");
                WriteUnlocked(config.PttLine, false, config.PttInvert, "Open/PttUp");
            }
            catch
            {
                // Never leave a half-configured port open: the DCB may still have the driver
                // auto-asserting a line. Closing drops DTR and RTS.
                CloseHandleUnlocked();
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public void KeyDown()
        => Guarded("KeyDown", rethrow: true, action: () =>
        {
            WriteUnlocked(KeyLine, true, KeyInvert, "KeyDown");
            _keyDown = true;
        });

    /// <inheritdoc/>
    public void KeyUp()
        => Guarded("KeyUp", rethrow: false, action: () =>
        {
            WriteUnlocked(KeyLine, false, KeyInvert, "KeyUp");
            _keyDown = false;
        });

    /// <inheritdoc/>
    public void PttDown()
        => Guarded("PttDown", rethrow: true, action: () =>
        {
            WriteUnlocked(PttLine, true, PttInvert, "PttDown");
            _pttOn = PttLine != KeyingLine.None;
        });

    /// <inheritdoc/>
    public void PttUp()
        => Guarded("PttUp", rethrow: false, action: () =>
        {
            WriteUnlocked(PttLine, false, PttInvert, "PttUp");
            _pttOn = false;
        });

    /// <inheritdoc/>
    public void EnsureAllLinesDown()
    {
        KeyingFaultEventArgs? fault;

        lock (_gate)
        {
            if (!IsOpenUnlocked)
            {
                _keyDown = false;
                _pttOn = false;
                return;
            }

            fault = DropAllLinesUnlocked("EnsureAllLinesDown", cause: null);
        }

        RaiseFault(fault);
    }

    /// <inheritdoc/>
    public void Close()
    {
        KeyingFaultEventArgs? fault = null;

        lock (_gate)
        {
            if (IsOpenUnlocked)
            {
                // Drop the lines before the handle goes away so the transmitter is released by an
                // explicit write, not merely as a side effect of the driver dropping DTR/RTS (9.8).
                fault = DropAllLinesUnlocked("Close", cause: null);
            }

            CloseHandleUnlocked();
        }

        RaiseFault(fault);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Close() de-asserts every line first, satisfying F8: closing the application while keyed
        // forces key-up during disposal (8.7, 9.8).
        try
        {
            Close();
        }
        catch
        {
            // Disposal must not throw. The handle is closed either way, which drops the lines.
        }
    }

    /// <summary>
    /// Runs a line operation under the lock, forcing every line inactive if it fails so that no
    /// failure path can leave a line asserted (8.7).
    /// </summary>
    private void Guarded(string operation, bool rethrow, Action action)
    {
        KeyingFaultEventArgs? fault = null;
        Exception? cause = null;

        lock (_gate)
        {
            if (!IsOpenUnlocked)
            {
                // Matches v1: line operations on a closed port are no-ops. A closed port cannot key.
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                cause = ex;
                fault = DropAllLinesUnlocked(operation, ex);
            }
        }

        // Raised outside the lock: a handler may call back into this instance (for example to close
        // the port while latching SAFE).
        RaiseFault(fault);

        if (cause is null)
        {
            return;
        }

        if (rethrow)
        {
            throw cause as KeyingException
                  ?? new KeyingException($"{operation} failed: {cause.Message}", cause);
        }

        // De-assert paths never throw — the caller is already trying to stop transmitting, and the
        // Fault event has told the replayer to latch SAFE.
        System.Diagnostics.Debug.WriteLine($"{operation} warning: {cause.Message}");
    }

    /// <summary>
    /// Best-effort drive of every configured line to its inactive state. If a line cannot be
    /// driven, the handle is closed so the driver drops DTR and RTS.
    /// </summary>
    /// <returns>A fault to raise, or <see langword="null"/> when everything succeeded.</returns>
    private KeyingFaultEventArgs? DropAllLinesUnlocked(string operation, Exception? cause)
    {
        Exception? keyFailure = null;
        Exception? pttFailure = null;

        // Key first, then PTT: release the transmitter's keying line before removing transmit enable.
        try
        {
            WriteUnlocked(KeyLine, false, KeyInvert, "KeyUp");
            _keyDown = false;
        }
        catch (Exception ex)
        {
            keyFailure = ex;
        }

        try
        {
            WriteUnlocked(PttLine, false, PttInvert, "PttUp");
            _pttOn = false;
        }
        catch (Exception ex)
        {
            pttFailure = ex;
        }

        bool dropFailed = keyFailure is not null || pttFailure is not null;

        if (dropFailed)
        {
            // Last resort: closing the handle makes the driver drop both lines (9.8).
            CloseHandleUnlocked();
            _keyDown = false;
            _pttOn = false;
        }

        if (cause is null && !dropFailed)
        {
            return null;
        }

        Exception? reported = cause ?? keyFailure ?? pttFailure;
        string detail = dropFailed
            ? $" Could not drive lines inactive ({(keyFailure ?? pttFailure)!.Message}); keying port closed to drop DTR and RTS."
            : string.Empty;

        return new KeyingFaultEventArgs(
            operation,
            $"Keying fault during {operation}: {reported?.Message ?? "unknown error"}.{detail}",
            reported,
            PortClosed: dropFailed);
    }

    /// <summary>
    /// Issues one <c>EscapeCommFunction</c> call for <paramref name="line"/>. A
    /// <see cref="KeyingLine.None"/> line is not driven (8.2).
    /// </summary>
    private void WriteUnlocked(KeyingLine line, bool logicalAsserted, bool invert, string operation)
    {
        uint? code = KeyingLineCommands.EscapeCode(line, logicalAsserted, invert);
        if (code is null)
        {
            return;
        }

        SafeFileHandle? handle = _handle;
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            throw new KeyingException($"{operation} failed: keying port is not open.");
        }

        if (!NativeMethods.EscapeCommFunction(handle, code.Value))
        {
            int error = Marshal.GetLastWin32Error();
            throw new KeyingException(
                $"{operation} failed: EscapeCommFunction on {line} returned false (error {error}).");
        }
    }

    private void CloseHandleUnlocked()
    {
        SafeFileHandle? handle = _handle;
        _handle = null;

        if (handle is null)
        {
            return;
        }

        try
        {
            handle.Dispose();
        }
        catch
        {
            // Nothing useful remains to do; the lines drop when the driver releases the port.
        }
    }

    private void RaiseFault(KeyingFaultEventArgs? fault)
    {
        if (fault is null)
        {
            return;
        }

        try
        {
            Fault?.Invoke(this, fault);
        }
        catch
        {
            // A misbehaving subscriber must not mask the fail-safe that already ran.
        }
    }

    /// <summary>
    /// Configures the DCB for manual DTR/RTS control so the driver never asserts a keying line
    /// by itself. Unchanged from RWK v1.
    /// </summary>
    private static void ConfigureDcb(SafeFileHandle handle)
    {
        var dcb = new NativeMethods.DCB
        {
            DCBlength = (uint)Marshal.SizeOf<NativeMethods.DCB>()
        };

        if (!NativeMethods.GetCommState(handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetCommState failed (error {error}).");
        }

        dcb.SetBinary(true);
        dcb.SetDtrControl(NativeMethods.DTR_CONTROL_DISABLE);
        dcb.SetRtsControl(NativeMethods.RTS_CONTROL_DISABLE);
        dcb.BaudRate = 9600; // irrelevant to control-line keying, but must be valid
        dcb.ByteSize = 8;
        dcb.Parity = 0;      // none
        dcb.StopBits = 0;    // one

        if (!NativeMethods.SetCommState(handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"SetCommState failed (error {error}).");
        }
    }
}
