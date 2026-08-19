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
/// Specifies which serial port control line is used for keying output.
/// </summary>
public enum KeyingLine
{
    DTR,
    RTS
}
