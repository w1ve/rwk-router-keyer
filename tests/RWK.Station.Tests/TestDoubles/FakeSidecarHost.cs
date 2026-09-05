/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Net;
using System.Net.Http;
using RWK.Shared;
using RWK.Shared.Net;

namespace RWK.Station.Tests.TestDoubles;

/// <summary>
/// Minimal fake <see cref="ITsnetSidecarHost"/> for exercising
/// <see cref="RWK.Station.Controllers.ControlForwardRegistrar"/>. Its
/// <see cref="CreateInboundForwardAsync"/> throws for the first
/// <see cref="FailFirstAttempts"/> calls and then succeeds, and it records how many times it was
/// invoked so tests can assert idempotency (no duplicate forwards).
/// </summary>
public sealed class FakeSidecarHost : ITsnetSidecarHost
{
    /// <summary>Number of initial <see cref="CreateInboundForwardAsync"/> calls that throw.</summary>
    public int FailFirstAttempts { get; set; }

    /// <summary>When true, every <see cref="CreateInboundForwardAsync"/> call throws.</summary>
    public bool AlwaysFail { get; set; }

    /// <summary>Total number of times <see cref="CreateInboundForwardAsync"/> was invoked.</summary>
    public int CreateInboundForwardCallCount { get; private set; }

    /// <summary>The exception thrown on a failing attempt.</summary>
    public Func<Exception> ExceptionFactory { get; set; } =
        () => new HttpRequestException("simulated transient sidecar failure");

    public Task CreateInboundForwardAsync(
        int tailnetPort, int localPort, string? targetAddress = null, CancellationToken cancellationToken = default)
    {
        CreateInboundForwardCallCount++;
        cancellationToken.ThrowIfCancellationRequested();

        if (AlwaysFail || CreateInboundForwardCallCount <= FailFirstAttempts)
            return Task.FromException(ExceptionFactory());

        return Task.CompletedTask;
    }

    // ── Remaining ITsnetSidecarHost members: not needed by the registrar ──
    public string ApiBaseAddress => "http://127.0.0.1:0";
    public string Token => string.Empty;
    public IPEndPoint EdgeLocalEndpoint => new(IPAddress.Loopback, 0);
    public string EdgeTransport => "udp";
    public string JitterProfile => "PathAdaptive";
    public TailscaleState State { get; set; } = TailscaleState.Connected;
    public string? PeerAddress => null;
    public string? SelfAddress => null;
    public string? SelfDnsName => null;
    public PathType CurrentPath => PathType.Direct;
    public double RoundTripMs => -1;
    public string? DerpRegion => null;
    public string? AuthUrl => null;

    public event EventHandler<string>? AuthUrlAvailable;
    public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    public Task StartAsync(string? authKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IPEndPoint> CreateOutboundForwardAsync(string peerAddress, int port, CancellationToken cancellationToken = default)
        => Task.FromResult<IPEndPoint>(new(IPAddress.Loopback, port));
    public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RegisterEdgeCallbackAsync(string callbackAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetPeerAsync(string peerAddress, int edgePort = 0, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearPeerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
        _ = AuthUrlAvailable;
        _ = StateChanged;
    }
}
