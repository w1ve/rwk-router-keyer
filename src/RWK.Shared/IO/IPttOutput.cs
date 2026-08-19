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
/// Abstraction for push-to-talk (PTT) output on a serial port control line.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IKeyingOutput"/> because PTT is optional: a Station
/// may be configured with a PTT line of <c>None</c> (8.2). The PTT sequencer owns the
/// lead and tail timing (8.4, 8.5, 8.6); this interface only asserts and de-asserts
/// the line, so implementations perform no timing of their own.
/// <para>
/// Implementations MUST de-assert the line on any error so that a fault cannot leave
/// the transmitter enabled (8.7).
/// </para>
/// _Requirements: 8.2, 8.7_
/// </remarks>
public interface IPttOutput
{
    /// <summary>
    /// Asserts the configured PTT line (transmit enabled).
    /// </summary>
    void PttDown();

    /// <summary>
    /// De-asserts the configured PTT line (transmit disabled).
    /// </summary>
    void PttUp();
}
