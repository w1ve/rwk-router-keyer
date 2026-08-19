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
using RWK.Shared.Net;

namespace RWK.Station.Tests.TestDoubles;

/// <summary>
/// Minimal fake <see cref="ITailscaleNode"/> that can raise StateChanged events for F9 testing.
/// </summary>
public sealed class FakeTailscaleNode : ITailscaleNode
{
    public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    public TailscaleState State { get; set; } = TailscaleState.Connected;
    public string? PeerAddress { get; set; }
    public string? SelfAddress { get; set; }
    public string? SelfDnsName { get; set; }
    public PathType CurrentPath { get; set; } = PathType.Direct;
    public TimeSpan RoundTripTime { get; set; }
    public string? DerpRegion { get; set; }

    public Task StartAsync(string? authKey)
        => Task.CompletedTask;

    public Task StopAsync()
        => Task.CompletedTask;

    public Task<int> SendEdgeAsync(ReadOnlyMemory<byte> data)
        => Task.FromResult(data.Length);

    public event EventHandler<ReadOnlyMemory<byte>>? EdgeReceived;

    public Task<Stream> ConnectControlAsync(string peerAddress, int port)
        => Task.FromResult<Stream>(Stream.Null);

    /// <summary>Simulates a Tailscale path fault for F9 testing.</summary>
    public void SimulateFault(string? message = null)
    {
        State = TailscaleState.Fault;
        StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
            TailscaleState.Fault,
            PathType.None,
            TimeSpan.Zero,
            DerpRegion: null,
            Message: message ?? "path lost"));
    }

    /// <summary>Simulates recovery from fault.</summary>
    public void SimulateRecovery()
    {
        State = TailscaleState.Connected;
        StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
            TailscaleState.Connected,
            PathType.Direct,
            TimeSpan.FromMilliseconds(20)));
    }

    public void Dispose() { }
}
