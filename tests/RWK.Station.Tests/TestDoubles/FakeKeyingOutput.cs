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
using RWK.Station.IO;

namespace RWK.Station.Tests.TestDoubles;

/// <summary>
/// Fake <see cref="IStationKeyingOutput"/> that tracks state and can raise Fault events for F6 testing.
/// </summary>
public sealed class FakeKeyingOutput : IStationKeyingOutput
{
    public event EventHandler<KeyingFaultEventArgs>? Fault;

    public KeyingLine KeyLine { get; private set; } = KeyingLine.RTS;
    public KeyingLine PttLine { get; private set; } = KeyingLine.None;
    public bool KeyInvert { get; private set; }
    public bool PttInvert { get; private set; }
    public bool IsKeyDown { get; private set; }
    public bool IsPttOn { get; private set; }
    public bool IsOpen { get; private set; }

    public void Configure(KeyingOutputConfig config)
    {
        KeyLine = config.KeyLine;
        PttLine = config.PttLine;
        KeyInvert = config.KeyInvert;
        PttInvert = config.PttInvert;
    }

    public void Open() => IsOpen = true;
    public void Open(string portName, KeyingLine line) { IsOpen = true; KeyLine = line; }
    public void Close() => IsOpen = false;

    public void KeyDown() => IsKeyDown = true;
    public void KeyUp() => IsKeyDown = false;
    public void PttDown() => IsPttOn = true;
    public void PttUp() => IsPttOn = false;
    public void EnsureAllLinesDown() { IsKeyDown = false; IsPttOn = false; }

    /// <summary>Simulates a serial port fault for F6 testing.</summary>
    public void SimulateFault(string operation = "KeyDown", string message = "device removed")
    {
        Fault?.Invoke(this, new KeyingFaultEventArgs(operation, message, null, PortClosed: true));
    }

    public void Dispose() { IsOpen = false; }
}
