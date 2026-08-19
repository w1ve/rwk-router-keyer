/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.IO;

/// <summary>
/// Specifies the operating mode of the WinKeyer COM port.
/// </summary>
public enum WinKeyerMode
{
    /// <summary>
    /// Logger App mode: RWK Client emulates a WinKeyer device on the serial port.
    /// A logging application (N1MM+, DXLog, etc.) connects to the port and sends
    /// WK2 protocol commands. RWK receives those commands and drives its internal
    /// keyer engine.
    /// </summary>
    LoggerApp = 0,

    /// <summary>
    /// Hardware WinKey mode: RWK Client acts as a host talking TO a physical WinKeyer
    /// chip (K1EL WinKeyer2/3) over serial. RWK sends Admin Open, speed, and buffered
    /// text commands; the hardware handles timing and reports status/echoes back.
    /// </summary>
    HardwareWinKey = 1
}
