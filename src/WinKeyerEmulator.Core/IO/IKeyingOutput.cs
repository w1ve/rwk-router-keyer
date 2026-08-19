/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace WinKeyerEmulator.Core.IO;

/// <summary>
/// Abstraction for physical keying output via a serial port control line.
/// </summary>
public interface IKeyingOutput : IDisposable
{
    /// <summary>
    /// Opens the specified serial port and configures it for keying on the given control line.
    /// </summary>
    void Open(string portName, KeyingLine line);

    /// <summary>
    /// Closes the keying port and releases resources.
    /// </summary>
    void Close();

    /// <summary>
    /// Asserts the configured control line (key down).
    /// </summary>
    void KeyDown();

    /// <summary>
    /// De-asserts the configured control line (key up).
    /// </summary>
    void KeyUp();

    /// <summary>
    /// Gets whether the keying port is currently open.
    /// </summary>
    bool IsOpen { get; }
}
