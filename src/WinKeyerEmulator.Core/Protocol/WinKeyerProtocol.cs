namespace WinKeyerEmulator.Core.Protocol;

/// <summary>
/// WinKeyer protocol state machine. Processes incoming bytes one at a time,
/// maintaining protocol state and returning response bytes when appropriate.
/// </summary>
public class WinKeyerProtocol
{
    private readonly ILogger _logger;
    private readonly ProtocolState _state;
    private PendingCommand _pending;
    private int _pendingBytesRemaining; // for multi-byte commands like LoadDefaults

    /// <summary>
    /// Represents a multi-byte command in progress.
    /// </summary>
    private enum PendingCommand
    {
        None,
        AdminSubCommand,
        SpeedByte,
        SidetoneByte,         // 0x01: 1 byte
        WeightingByte,        // 0x03: 1 byte
        PttLeadTailByte1,     // 0x04: 2 bytes (lead + tail)
        PttLeadTailByte2,
        SpeedPotByte1,        // 0x05: 3 bytes
        SpeedPotByte2,
        SpeedPotByte3,
        PauseByte,            // 0x06: 1 byte
        PinConfigByte,        // 0x09: 1 byte
        KeyImmediateByte,     // 0x0B: 1 byte
        HscwSpeedByte,        // 0x0C: 1 byte
        FarnsworthByte,       // 0x0D: 1 byte
        Wk2ModeByte,          // 0x0E: 1 byte
        LoadDefaultsByte1,    // 0x0F: 15 bytes
        LoadDefaultsRemaining,
        FirstExtByte,         // 0x10: 1 byte
        KeyCompByte,          // 0x11: 1 byte
        PaddleSwitchByte,     // 0x12: 1 byte
        SoftPaddleByte,       // 0x14: 1 byte
        PointerByte,          // 0x16: 1 byte
        DitDahRatioByte,      // 0x17: 1 byte
        PttControlByte,       // 0x18: 1 byte
        BuffSpeedByte,        // 0x1A: 1 byte
        HscwCodeByte,         // 0x1B: 1 byte
        FreeFormByte,         // 0x1C: 1 byte
        AdminEchoByte,        // Admin 0x04: 1 byte to echo
        AdminDataByte,        // Admin commands that expect 1 follow-on data byte (0x10, 0x11, etc.)
    }

    /// <summary>
    /// Raised when text characters are queued in the buffer.
    /// </summary>
    public event EventHandler<char>? TextReceived;

    /// <summary>
    /// Raised when the buffer is cleared (abort/clear command received).
    /// </summary>
    public event EventHandler? BufferCleared;

    /// <summary>
    /// Raised when a Key Immediate command is received.
    /// True = key down, False = key up.
    /// </summary>
    public event EventHandler<bool>? KeyImmediate;

    /// <summary>
    /// Raised when the keying speed changes.
    /// The int is the new WPM value.
    /// </summary>
    public event EventHandler<int>? SpeedChanged;

    /// <summary>
    /// Gets the current protocol state for inspection.
    /// </summary>
    public ProtocolState State => _state;

    /// <summary>
    /// Creates a new WinKeyer protocol handler.
    /// </summary>
    /// <param name="logger">Logger for recording warnings and events.</param>
    public WinKeyerProtocol(ILogger logger)
    {
        _logger = logger;
        _state = new ProtocolState();
        _pending = PendingCommand.None;
    }

    /// <summary>
    /// Processes a single incoming byte through the protocol state machine.
    /// </summary>
    /// <param name="b">The byte received from the host.</param>
    /// <returns>Response bytes to send back, or null if no response is needed.</returns>
    public byte[]? ProcessByte(byte b)
    {
        // If we're waiting for the second byte of a multi-byte command
        if (_pending != PendingCommand.None)
        {
            return ProcessPendingByte(b);
        }

        // Not in host mode: only Admin Open is accepted
        if (!_state.HostMode)
        {
            if (b == CommandDefinitions.AdminCmd)
            {
                _pending = PendingCommand.AdminSubCommand;
                return null;
            }

            // Ignore everything else outside host mode
            return null;
        }

        // In host mode: process command bytes
        return ProcessHostModeByte(b);
    }

    /// <summary>
    /// Generates the current status byte based on protocol state.
    /// WinKeyer status bytes have bits 7:6 = 11 (0xC0 prefix) to distinguish
    /// them from echoed characters.
    /// Format: 1 1 X X B B S I
    ///   I = Idle (0 = idle, 1 = busy)  
    ///   S = Sending (buffer active)
    ///   BB = Breakin (paddle interrupt)
    ///   XX = reserved
    /// </summary>
    /// <returns>A status byte with 0xC0 prefix and appropriate bits set.</returns>
    public byte GetStatusByte()
    {
        byte status = 0xC0; // Bits 7:6 always set for status identification

        if (_state.BufferState == BufferState.Sending)
        {
            status |= 0x04; // bit 2 = buffer sending/busy
        }

        // Bit 0 = buffer has data waiting
        if (_state.TextBuffer.Count > 0)
        {
            status |= 0x01;
        }

        return status;
    }

    private byte[]? ProcessHostModeByte(byte b)
    {
        // Command byte range (0x00-0x1F)
        if (b <= CommandDefinitions.LastImmediateCmd)
        {
            return ProcessCommandByte(b);
        }

        // Printable ASCII text characters (0x20-0x7E)
        if (b >= CommandDefinitions.PrintableAsciiStart && b <= CommandDefinitions.PrintableAsciiEnd)
        {
            return ProcessTextByte(b);
        }

        // Unrecognized byte - log warning and discard
        _logger.Log($"Unrecognized byte 0x{b:X2} in host mode, discarding", LogSeverity.Warning, "Protocol");
        return null;
    }

    private byte[]? ProcessCommandByte(byte b)
    {
        switch (b)
        {
            case CommandDefinitions.AdminCmd:
                _pending = PendingCommand.AdminSubCommand;
                return null;

            case CommandDefinitions.SidetoneCmd:
                _pending = PendingCommand.SidetoneByte;
                return null;

            case CommandDefinitions.SpeedCmd:
                _pending = PendingCommand.SpeedByte;
                return null;

            case CommandDefinitions.WeightingCmd:
                _pending = PendingCommand.WeightingByte;
                return null;

            case CommandDefinitions.PttLeadTailCmd:
                _pending = PendingCommand.PttLeadTailByte1;
                return null;

            case CommandDefinitions.SpeedPotCmd:
                _pending = PendingCommand.SpeedPotByte1;
                return null;

            case CommandDefinitions.PauseCmd:
                _pending = PendingCommand.PauseByte;
                return null;

            case CommandDefinitions.GetSpeedPotCmd:
                // In WinKeyer2 host mode, this is a no-op since we don't have a physical speed pot.
                // Do NOT send a response - N1MM doesn't expect one during init sequences.
                _logger.Log("Get Speed Pot (no physical pot, no response)", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.BackspaceCmd:
                // Remove last character from buffer if any
                // WinKeyer spec: remove last queued character
                _logger.Log("Backspace command", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.PinConfigCmd:
                _pending = PendingCommand.PinConfigByte;
                return null;

            case CommandDefinitions.ClearBufferCmd:
                ClearBuffer();
                return null;

            case CommandDefinitions.KeyImmediateCmd:
                _pending = PendingCommand.KeyImmediateByte;
                return null;

            case CommandDefinitions.HscwSpeedCmd:
                _pending = PendingCommand.HscwSpeedByte;
                return null;

            case CommandDefinitions.FarnsworthCmd:
                _pending = PendingCommand.FarnsworthByte;
                return null;

            case CommandDefinitions.Wk2ModeCmd:
                _pending = PendingCommand.Wk2ModeByte;
                return null;

            case CommandDefinitions.LoadDefaultsCmd:
                _pending = PendingCommand.LoadDefaultsRemaining;
                _pendingBytesRemaining = 15;
                return null;

            case CommandDefinitions.FirstExtCmd:
                _pending = PendingCommand.FirstExtByte;
                return null;

            case CommandDefinitions.KeyCompCmd:
                _pending = PendingCommand.KeyCompByte;
                return null;

            case CommandDefinitions.PaddleSwitchCmd:
                _pending = PendingCommand.PaddleSwitchByte;
                return null;

            case CommandDefinitions.NullCmd:
                // No-op
                return null;

            case CommandDefinitions.SoftPaddleCmd:
                _pending = PendingCommand.SoftPaddleByte;
                return null;

            case CommandDefinitions.ReqStatusCmd:
                // Return current status byte
                return new[] { GetStatusByte() };

            case CommandDefinitions.PointerCmd:
                _pending = PendingCommand.PointerByte;
                return null;

            case CommandDefinitions.DitDahRatioCmd:
                _pending = PendingCommand.DitDahRatioByte;
                return null;

            case CommandDefinitions.PttControlCmd:
                _pending = PendingCommand.PttControlByte;
                return null;

            case CommandDefinitions.TimCharSpaceCmd:
                // No additional bytes, acknowledged
                _logger.Log("Timing char space command", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.BuffSpeedCmd:
                _pending = PendingCommand.BuffSpeedByte;
                return null;

            case CommandDefinitions.HscwCodeCmd:
                _pending = PendingCommand.HscwCodeByte;
                return null;

            case CommandDefinitions.FreeFormCmd:
                _pending = PendingCommand.FreeFormByte;
                return null;

            default:
                // Unknown command in 0x00-0x1F range
                _logger.Log($"Unknown command 0x{b:X2}, discarding", LogSeverity.Warning, "Protocol");
                return null;
        }
    }

    private byte[]? ProcessPendingByte(byte b)
    {
        var pending = _pending;

        // Handle multi-byte commands (LoadDefaults consumes 15 bytes, SpeedPot consumes 3)
        switch (pending)
        {
            case PendingCommand.LoadDefaultsRemaining:
                _pendingBytesRemaining--;
                if (_pendingBytesRemaining <= 0)
                {
                    _pending = PendingCommand.None;
                    _logger.Log("Load Defaults received (15 bytes consumed)", LogSeverity.Info, "Protocol");
                }
                return null;

            case PendingCommand.SpeedPotByte1:
                _pending = PendingCommand.SpeedPotByte2;
                return null;

            case PendingCommand.SpeedPotByte2:
                _pending = PendingCommand.SpeedPotByte3;
                return null;

            case PendingCommand.SpeedPotByte3:
                _pending = PendingCommand.None;
                _logger.Log("Speed Pot Setup received (3 bytes consumed)", LogSeverity.Info, "Protocol");
                return null;

            case PendingCommand.PttLeadTailByte1:
                _pending = PendingCommand.PttLeadTailByte2;
                return null;

            case PendingCommand.PttLeadTailByte2:
                _pending = PendingCommand.None;
                _logger.Log("PTT Lead/Tail received (2 bytes consumed)", LogSeverity.Info, "Protocol");
                return null;

            case PendingCommand.AdminDataByte:
                _pending = PendingCommand.None;
                _logger.Log($"Admin data byte 0x{b:X2} consumed", LogSeverity.Info, "Protocol");
                return null;
        }

        // Single follow-on byte commands
        _pending = PendingCommand.None;

        return pending switch
        {
            PendingCommand.AdminSubCommand => ProcessAdminSubCommand(b),
            PendingCommand.SpeedByte => ProcessSpeedByte(b),
            PendingCommand.AdminEchoByte => new[] { b }, // Echo the byte back
            PendingCommand.SidetoneByte => LogAndIgnore(b, "Sidetone"),
            PendingCommand.WeightingByte => LogAndIgnore(b, "Weighting"),
            PendingCommand.PauseByte => LogAndIgnore(b, "Pause"),
            PendingCommand.PinConfigByte => LogAndIgnore(b, "Pin Config"),
            PendingCommand.KeyImmediateByte => ProcessKeyImmediate(b),
            PendingCommand.HscwSpeedByte => LogAndIgnore(b, "HSCW Speed"),
            PendingCommand.FarnsworthByte => LogAndIgnore(b, "Farnsworth"),
            PendingCommand.Wk2ModeByte => LogAndIgnore(b, "WK2 Mode"),
            PendingCommand.FirstExtByte => LogAndIgnore(b, "First Extension"),
            PendingCommand.KeyCompByte => LogAndIgnore(b, "Key Compensation"),
            PendingCommand.PaddleSwitchByte => LogAndIgnore(b, "Paddle Switchpoint"),
            PendingCommand.SoftPaddleByte => LogAndIgnore(b, "Software Paddle"),
            PendingCommand.PointerByte => LogAndIgnore(b, "Pointer"),
            PendingCommand.DitDahRatioByte => LogAndIgnore(b, "Dit/Dah Ratio"),
            PendingCommand.PttControlByte => LogAndIgnore(b, "PTT Control"),
            PendingCommand.BuffSpeedByte => ProcessBuffSpeed(b),
            PendingCommand.HscwCodeByte => LogAndIgnore(b, "HSCW Code"),
            PendingCommand.FreeFormByte => LogAndIgnore(b, "Free Form"),
            _ => null
        };
    }

    private byte[]? LogAndIgnore(byte b, string commandName)
    {
        _logger.Log($"{commandName} set to 0x{b:X2} (acknowledged)", LogSeverity.Info, "Protocol");
        return null;
    }

    private byte[]? ProcessKeyImmediate(byte b)
    {
        if (b == 0x01)
        {
            _logger.Log("Key Immediate: key down", LogSeverity.Info, "Protocol");
            // Signal key down via event - actual keying handled by KeyerCore
            KeyImmediate?.Invoke(this, true);
        }
        else
        {
            _logger.Log("Key Immediate: key up", LogSeverity.Info, "Protocol");
            KeyImmediate?.Invoke(this, false);
        }
        return null;
    }

    private byte[]? ProcessBuffSpeed(byte b)
    {
        // Buffer speed: next text characters use this speed until another speed command
        int wpm = b;
        if (wpm >= CommandDefinitions.MinWpm && wpm <= CommandDefinitions.MaxWpm)
        {
            _state.CurrentWpm = wpm;
            _logger.Log($"Buffer speed set to {wpm} WPM", LogSeverity.Info, "Protocol");
        }
        return null;
    }

    private byte[]? ProcessAdminSubCommand(byte subCmd)
    {
        switch (subCmd)
        {
            case CommandDefinitions.AdminCalibrate:
                _logger.Log("Admin Calibrate (no-op)", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.AdminReset:
                _state.Reset();
                _logger.Log("Admin Reset", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.AdminOpen:
                _state.HostMode = true;
                _logger.Log("Host mode opened", LogSeverity.Info, "Protocol");
                // Return version byte followed by idle status byte
                // N1MM expects the status byte to confirm keyer is ready
                return new byte[] { CommandDefinitions.WinKeyerVersion, 0xC0 };

            case CommandDefinitions.AdminClose:
                _state.HostMode = false;
                ClearBuffer();
                _state.CurrentWpm = CommandDefinitions.DefaultWpm;
                _logger.Log("Host mode closed", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.AdminEcho:
                // Next byte will be echoed back
                _pending = PendingCommand.AdminEchoByte;
                return null;

            case CommandDefinitions.AdminPaddleA2D:
                // Return a dummy paddle A2D value (128 = center)
                _logger.Log("Admin Paddle A2D (returning 128)", LogSeverity.Info, "Protocol");
                return new byte[] { 128 };

            case CommandDefinitions.AdminSpeedA2D:
                // Return a dummy speed pot A2D value
                _logger.Log("Admin Speed A2D (returning 128)", LogSeverity.Info, "Protocol");
                return new byte[] { 128 };

            case CommandDefinitions.AdminGetValues:
                // Return 15 bytes of current settings (all zeros for unimplemented)
                _logger.Log("Admin Get Values (returning defaults)", LogSeverity.Info, "Protocol");
                return new byte[15];

            case CommandDefinitions.AdminGetCalibrate:
                // Return calibration value (0)
                _logger.Log("Admin Get Calibrate (returning 0)", LogSeverity.Info, "Protocol");
                return new byte[] { 0 };

            case CommandDefinitions.AdminWk1Mode:
                _logger.Log("Admin WK1 Mode set", LogSeverity.Info, "Protocol");
                return null;

            case CommandDefinitions.AdminWk2Mode:
                _logger.Log("Admin WK2 Mode set", LogSeverity.Info, "Protocol");
                return null;

            default:
                // Unknown admin sub-command: many WK3 admin sub-commands expect a data byte.
                // Sub-commands >= 0x10 typically expect 1 follow-on byte.
                if (subCmd >= 0x10)
                {
                    _pending = PendingCommand.AdminDataByte;
                    _logger.Log($"Admin sub-command 0x{subCmd:X2} (consuming 1 data byte)", LogSeverity.Info, "Protocol");
                }
                else
                {
                    _logger.Log($"Admin sub-command 0x{subCmd:X2} (acknowledged)", LogSeverity.Info, "Protocol");
                }
                return null;
        }
    }

    private byte[]? ProcessSpeedByte(byte speedByte)
    {
        int wpm = speedByte;

        if (wpm < CommandDefinitions.MinWpm || wpm > CommandDefinitions.MaxWpm)
        {
            _logger.Log(
                $"Speed value {wpm} WPM is outside valid range ({CommandDefinitions.MinWpm}-{CommandDefinitions.MaxWpm}), ignoring",
                LogSeverity.Warning,
                "Protocol");
            return null;
        }

        _state.CurrentWpm = wpm;
        _logger.Log($"Speed set to {wpm} WPM", LogSeverity.Info, "Protocol");
        SpeedChanged?.Invoke(this, wpm);
        return null;
    }

    private byte[]? ProcessTextByte(byte b)
    {
        char c = (char)b;
        _state.TextBuffer.Enqueue(c);

        if (_state.BufferState == BufferState.Idle)
        {
            _state.BufferState = BufferState.Sending;
        }

        TextReceived?.Invoke(this, c);

        // No immediate response - character echo comes when the character is actually sent
        return null;
    }

    private void ClearBuffer()
    {
        _state.TextBuffer.Clear();
        _state.BufferState = BufferState.Idle;
        _logger.Log("Buffer cleared", LogSeverity.Info, "Protocol");
        BufferCleared?.Invoke(this, EventArgs.Empty);
    }
}
