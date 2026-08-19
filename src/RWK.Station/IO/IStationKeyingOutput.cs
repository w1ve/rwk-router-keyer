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
using RWK.Shared.Config;
using RWK.Shared.IO;

namespace RWK.Station.IO;

/// <summary>
/// The Station's serial keying output: an <see cref="IKeyingOutput"/> with an independently
/// configurable PTT line (<see cref="IPttOutput"/>) and per-line polarity inversion.
/// </summary>
/// <remarks>
/// Design Component 8. Extends the RWK v1 single-line <c>SerialKeyingOutput</c> shape with dual
/// line support (8.1, 8.2) and inversion (8.3). Lead and tail sequencing is deliberately absent:
/// it lives in <see cref="PttSequencer"/> (8.4, 8.5, 8.6) so that it can be tested against a fake
/// output and a fake clock.
/// <para>
/// _Requirements: 8.1, 8.2, 8.3, 8.7_
/// </para>
/// </remarks>
public interface IStationKeyingOutput : IKeyingOutput, IPttOutput
{
    /// <summary>
    /// Applies port, line, and polarity settings. Must be called before <see cref="Open()"/> and
    /// only while the port is closed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key line is <see cref="KeyingLine.None"/>, a line value is unrecognized, or the key and
    /// PTT lines are the same physical line.
    /// </exception>
    void Configure(KeyingOutputConfig config);

    /// <summary>
    /// Opens the port named by the configuration supplied to <see cref="Configure"/>.
    /// </summary>
    void Open();

    /// <summary>
    /// Drives every configured line to its inactive state (key-up, PTT-off), honoring each line's
    /// polarity. Safe to call when already inactive or when the port is closed (8.7).
    /// </summary>
    void EnsureAllLinesDown();

    /// <summary>
    /// Raised after a line operation failed and every configured line has been forced inactive.
    /// The Edge Replayer maps this to fail-safe F6 (9.6).
    /// </summary>
    event EventHandler<KeyingFaultEventArgs>? Fault;

    /// <summary>Control line asserted for key-down: RTS or DTR (8.1).</summary>
    KeyingLine KeyLine { get; }

    /// <summary>Control line asserted for PTT: RTS, DTR, or None (8.2).</summary>
    KeyingLine PttLine { get; }

    /// <summary>Whether the key line's polarity is inverted (8.3).</summary>
    bool KeyInvert { get; }

    /// <summary>Whether the PTT line's polarity is inverted (8.3).</summary>
    bool PttInvert { get; }

    /// <summary>Whether the key line is currently in its logical key-down state.</summary>
    bool IsKeyDown { get; }

    /// <summary>Whether the PTT line is currently in its logical PTT-on state.</summary>
    bool IsPttOn { get; }
}
