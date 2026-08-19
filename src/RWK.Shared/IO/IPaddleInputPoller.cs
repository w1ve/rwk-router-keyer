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
/// High-frequency poller that turns serial modem status pin transitions into debounced,
/// QPC-timestamped paddle state changes.
/// </summary>
/// <remarks>
/// Contract as declared by design Component 1. Implementations poll at 1 ms intervals on a
/// dedicated high-priority thread, map CTS to dit, DSR to dah, and DCD to the straight key,
/// assert DTR as the paddle contact voltage source, and apply software debounce before
/// raising <see cref="StateChanged"/>.
/// <para>
/// <see cref="StateChanged"/> is raised on the polling thread. Handlers must return quickly
/// and must not block: the thread runs above normal priority and owes the keyer a 1 ms poll
/// cadence.
/// </para>
/// _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7_
/// </remarks>
public interface IPaddleInputPoller : IDisposable
{
    /// <summary>
    /// Raised when a debounced contact transition is accepted, carrying the QPC timestamp
    /// taken at the moment of detection and all three contact states (1.3, 1.5).
    /// </summary>
    event EventHandler<PaddleStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Opens the paddle port and begins polling.
    /// </summary>
    /// <param name="portName">Serial port name, for example <c>COM3</c>.</param>
    void Start(string portName);

    /// <summary>
    /// Stops polling and releases the port. Safe to call when not started.
    /// </summary>
    void Stop();

    /// <summary>Gets the debounced dit contact state (CTS).</summary>
    bool DitPressed { get; }

    /// <summary>Gets the debounced dah contact state (DSR).</summary>
    bool DahPressed { get; }

    /// <summary>Gets the debounced straight key contact state (DCD).</summary>
    bool StraightKeyPressed { get; }

    /// <summary>
    /// Gets or sets the software debounce window applied per contact (1.4). Default 5 ms.
    /// May be changed while polling.
    /// </summary>
    TimeSpan DebounceTime { get; set; }
}
