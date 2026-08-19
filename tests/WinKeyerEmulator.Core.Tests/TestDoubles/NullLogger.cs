/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using WinKeyerEmulator.Core;

namespace WinKeyerEmulator.Core.Tests.TestDoubles;

/// <summary>
/// A no-op logger for use in tests where logging output is not relevant.
/// </summary>
public class NullLogger : ILogger
{
    public List<(string Message, LogSeverity Severity, string? Source)> Entries { get; } = new();

    public void Log(string message, LogSeverity severity, string? source = null)
    {
        Entries.Add((message, severity, source));
    }
}
