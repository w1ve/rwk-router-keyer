/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;
using RWK.Shared.Interop;

namespace RWK.Station.IO;

/// <summary>
/// Pure mapping from a logical key/PTT state plus a polarity setting to the
/// <c>EscapeCommFunction</c> code that drives the chosen control line.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="StationKeyingOutput"/> and free of any handle or state so that
/// line selection (8.1, 8.2) and polarity inversion (8.3) can be verified exhaustively without a
/// serial port. Property 24 (Output Polarity Inversion) tests <see cref="ToPhysical"/> and
/// <see cref="EscapeCode"/> directly.
/// <para>
/// _Requirements: 8.1, 8.2, 8.3_
/// </para>
/// </remarks>
public static class KeyingLineCommands
{
    /// <summary>
    /// Maps a logical state to the physical line state: <c>physical = logical XOR invert</c> (8.3).
    /// </summary>
    /// <param name="logicalAsserted">
    /// <see langword="true"/> for key-down or PTT-on, <see langword="false"/> for key-up or PTT-off.
    /// </param>
    /// <param name="invert">Whether this line's polarity is inverted.</param>
    /// <returns><see langword="true"/> when the control line must be electrically asserted.</returns>
    public static bool ToPhysical(bool logicalAsserted, bool invert) => logicalAsserted ^ invert;

    /// <summary>
    /// Returns the <c>EscapeCommFunction</c> code that puts <paramref name="line"/> into the state
    /// implied by <paramref name="logicalAsserted"/> and <paramref name="invert"/>.
    /// </summary>
    /// <returns>
    /// The function code, or <see langword="null"/> when <paramref name="line"/> is
    /// <see cref="KeyingLine.None"/> — an unassigned PTT line is simply not driven (8.2).
    /// </returns>
    public static uint? EscapeCode(KeyingLine line, bool logicalAsserted, bool invert)
    {
        if (line == KeyingLine.None)
        {
            return null;
        }

        bool physical = ToPhysical(logicalAsserted, invert);

        return line switch
        {
            KeyingLine.DTR => physical ? NativeMethods.SETDTR : NativeMethods.CLRDTR,
            KeyingLine.RTS => physical ? NativeMethods.SETRTS : NativeMethods.CLRRTS,
            _ => null
        };
    }

    /// <summary>
    /// Gets whether <paramref name="line"/> is a usable key output line. Only RTS and DTR are
    /// (8.1); <see cref="KeyingLine.None"/> is not, because there would be nothing to key.
    /// </summary>
    public static bool IsValidKeyLine(KeyingLine line)
        => line is KeyingLine.RTS or KeyingLine.DTR;

    /// <summary>
    /// Gets whether <paramref name="line"/> is a usable PTT output line. RTS, DTR, and
    /// <see cref="KeyingLine.None"/> all are (8.2).
    /// </summary>
    public static bool IsValidPttLine(KeyingLine line)
        => line is KeyingLine.RTS or KeyingLine.DTR or KeyingLine.None;
}
