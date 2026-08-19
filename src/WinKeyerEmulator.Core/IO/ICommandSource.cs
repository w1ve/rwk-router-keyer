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
/// Abstraction for a source of incoming WinKeyer protocol command bytes.
/// </summary>
public interface ICommandSource : IDisposable
{
    /// <summary>
    /// Raised when one or more command bytes are received from the source.
    /// </summary>
    event EventHandler<byte[]> DataReceived;

    /// <summary>
    /// Begins listening for incoming command data.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops listening and releases transport resources.
    /// </summary>
    void Stop();
}
