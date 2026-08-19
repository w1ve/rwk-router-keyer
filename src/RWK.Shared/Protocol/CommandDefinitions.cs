/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Protocol;

/// <summary>
/// WinKeyer protocol command byte constants.
/// </summary>
/// <remarks>
/// Behavior-preserving port of <c>WinKeyerEmulator.Core.Protocol.CommandDefinitions</c> (RWK v1).
/// Values are reverse-engineered from real K1EL hardware and from the byte stream N1MM+ emits;
/// do not "tidy" them.
/// </remarks>
public static class CommandDefinitions
{
    // Primary command bytes (first byte of a command sequence)

    /// <summary>Admin command prefix (0x00). Followed by a sub-command byte.</summary>
    public const byte AdminCmd = 0x00;

    /// <summary>Sidetone command (0x01). Followed by frequency byte.</summary>
    public const byte SidetoneCmd = 0x01;

    /// <summary>Speed command (0x02). Followed by WPM byte.</summary>
    public const byte SpeedCmd = 0x02;

    /// <summary>Weighting command (0x03). Followed by weight byte.</summary>
    public const byte WeightingCmd = 0x03;

    /// <summary>PTT Lead/Tail command (0x04). Followed by TWO timing bytes (lead + tail).</summary>
    public const byte PttLeadTailCmd = 0x04;

    /// <summary>Speed Pot Setup command (0x05). Followed by 3 bytes.</summary>
    public const byte SpeedPotCmd = 0x05;

    /// <summary>Pause command (0x06). Followed by pause byte.</summary>
    public const byte PauseCmd = 0x06;

    /// <summary>Get Speed Pot command (0x07). No additional bytes, and no response.</summary>
    public const byte GetSpeedPotCmd = 0x07;

    /// <summary>Backspace command (0x08). No additional bytes.</summary>
    public const byte BackspaceCmd = 0x08;

    /// <summary>Pin Configuration command (0x09). Followed by config byte.</summary>
    public const byte PinConfigCmd = 0x09;

    /// <summary>Clear Buffer command (0x0A). No additional bytes.</summary>
    public const byte ClearBufferCmd = 0x0A;

    /// <summary>Key Immediate command (0x0B). Followed by key state byte.</summary>
    public const byte KeyImmediateCmd = 0x0B;

    /// <summary>HSCW Speed command (0x0C). Followed by speed byte.</summary>
    public const byte HscwSpeedCmd = 0x0C;

    /// <summary>Farnsworth command (0x0D). Followed by speed byte.</summary>
    public const byte FarnsworthCmd = 0x0D;

    /// <summary>WinKeyer2 Mode command (0x0E). Followed by mode byte.</summary>
    public const byte Wk2ModeCmd = 0x0E;

    /// <summary>Load Defaults command (0x0F). Followed by 15 bytes.</summary>
    public const byte LoadDefaultsCmd = 0x0F;

    /// <summary>First Extension command (0x10). Followed by extension byte.</summary>
    public const byte FirstExtCmd = 0x10;

    /// <summary>Key Compensation command (0x11). Followed by comp byte.</summary>
    public const byte KeyCompCmd = 0x11;

    /// <summary>Paddle Switchpoint command (0x12). Followed by switchpoint byte.</summary>
    public const byte PaddleSwitchCmd = 0x12;

    /// <summary>Null command (0x13). No operation.</summary>
    public const byte NullCmd = 0x13;

    /// <summary>Software Paddle command (0x14). Followed by paddle byte.</summary>
    public const byte SoftPaddleCmd = 0x14;

    /// <summary>Request WinKeyer Status command (0x15). No additional bytes.</summary>
    public const byte ReqStatusCmd = 0x15;

    /// <summary>Pointer command (0x16). Followed by pointer byte.</summary>
    public const byte PointerCmd = 0x16;

    /// <summary>Dit/Dah Ratio command (0x17). Followed by ratio byte.</summary>
    public const byte DitDahRatioCmd = 0x17;

    /// <summary>PTT Control command (0x18). Followed by control byte.</summary>
    public const byte PttControlCmd = 0x18;

    /// <summary>Timing Char Space command (0x19). No additional bytes.</summary>
    public const byte TimCharSpaceCmd = 0x19;

    /// <summary>Buffer Speed command (0x1A). Followed by speed byte.</summary>
    public const byte BuffSpeedCmd = 0x1A;

    /// <summary>HSCW code (0x1B). Followed by code byte.</summary>
    public const byte HscwCodeCmd = 0x1B;

    /// <summary>Free Form Message command (0x1C). Followed by message byte.</summary>
    public const byte FreeFormCmd = 0x1C;

    /// <summary>End of immediate commands range.</summary>
    public const byte LastImmediateCmd = 0x1F;

    // Admin sub-command bytes (second byte after AdminCmd)

    /// <summary>Admin Calibrate (0x00).</summary>
    public const byte AdminCalibrate = 0x00;

    /// <summary>Admin Reset (0x01).</summary>
    public const byte AdminReset = 0x01;

    /// <summary>Admin Open Host Mode (0x02). Responds with version byte.</summary>
    public const byte AdminOpen = 0x02;

    /// <summary>Admin Close Host Mode (0x03).</summary>
    public const byte AdminClose = 0x03;

    /// <summary>Admin Echo (0x04). Followed by a byte to echo back.</summary>
    public const byte AdminEcho = 0x04;

    /// <summary>Admin Paddle A2D (0x05).</summary>
    public const byte AdminPaddleA2D = 0x05;

    /// <summary>Admin Speed A2D (0x06).</summary>
    public const byte AdminSpeedA2D = 0x06;

    /// <summary>Admin Get Values (0x07).</summary>
    public const byte AdminGetValues = 0x07;

    /// <summary>Admin Get Calibrate (0x09).</summary>
    public const byte AdminGetCalibrate = 0x09;

    /// <summary>Admin WK1 Mode (0x0A).</summary>
    public const byte AdminWk1Mode = 0x0A;

    /// <summary>Admin WK2 Mode (0x0B).</summary>
    public const byte AdminWk2Mode = 0x0B;

    // Protocol constants

    /// <summary>WinKeyer version byte reported on Admin Open. Version 23 = WinKeyer 2.</summary>
    public const byte WinKeyerVersion = 23;

    /// <summary>Minimum allowed WPM speed.</summary>
    public const int MinWpm = 5;

    /// <summary>Maximum allowed WPM speed.</summary>
    public const int MaxWpm = 45;

    /// <summary>Default WPM speed.</summary>
    public const int DefaultWpm = 15;

    // Status byte bit definitions

    /// <summary>Status bit: keyer is idle/busy (bit 0).</summary>
    public const byte StatusBusyBit = 0x01;

    /// <summary>Status bit: currently sending (bit 1).</summary>
    public const byte StatusSendingBit = 0x02;

    /// <summary>Status bit: buffer space available (bit 2).</summary>
    public const byte StatusBufferSpaceBit = 0x04;

    /// <summary>
    /// Prefix applied to every status byte (bits 7:6 set). Distinguishes a status byte
    /// from an echoed character on the wire — N1MM+ relies on this.
    /// </summary>
    public const byte StatusPrefix = 0xC0;

    /// <summary>Maximum capacity of the text buffer.</summary>
    public const int MaxBufferCapacity = 128;

    // Printable ASCII range for text characters

    /// <summary>First printable ASCII character (space).</summary>
    public const byte PrintableAsciiStart = 0x20;

    /// <summary>Last printable ASCII character (tilde).</summary>
    public const byte PrintableAsciiEnd = 0x7E;
}
