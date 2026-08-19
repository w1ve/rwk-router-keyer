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
/// Abstraction for sending WinKeyer protocol response bytes back to the host.
/// </summary>
public interface ICommandSink
{
    /// <summary>
    /// Sends response data back to the connected host.
    /// </summary>
    void SendResponse(byte[] data);
}
