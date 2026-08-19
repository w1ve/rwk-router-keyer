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
using System.Net.Sockets;

namespace RWK.Shared.Net;

/// <summary>
/// Implements <see cref="ITailscaleNode"/> as a façade over an <see cref="ITsnetSidecarHost"/>.
/// </summary>
/// <remarks>
/// This class does not own the sidecar process lifecycle — it delegates start/stop and status
/// to the injected host. Its own responsibilities are:
/// <list type="bullet">
///   <item><see cref="SendEdgeAsync"/>: UDP datagram send to the sidecar's edge local socket (5.6)</item>
///   <item><see cref="EdgeReceived"/>: UDP datagram receive on a local callback socket (5.6)</item>
///   <item><see cref="ConnectControlAsync"/>: TCP connection via an outbound forward (5.7)</item>
///   <item>Property projection and <see cref="StateChanged"/> relay from the host (5.8)</item>
/// </list>
/// <para>
/// Task 14.7 implements <see cref="ITsnetSidecarHost"/>. Task 14.9 adds resilience behavior.
/// </para>
/// <para>
/// _Requirements: 5.6, 5.7, 5.8_
/// </para>
/// </remarks>
public sealed class TailscaleNode : ITailscaleNode
{
    private readonly ITsnetSidecarHost _host;
    private UdpClient? _edgeSendClient;
    private UdpClient? _edgeReceiveClient;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="TailscaleNode"/> that delegates to the given sidecar host.
    /// </summary>
    /// <param name="host">
    /// The sidecar host abstraction that owns the child process, handshake, and status polling.
    /// </param>
    public TailscaleNode(ITsnetSidecarHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _host.StateChanged += OnHostStateChanged;
    }

    /// <inheritdoc/>
    public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<ReadOnlyMemory<byte>>? EdgeReceived;

    /// <inheritdoc/>
    public TailscaleState State => _host.State;

    /// <inheritdoc/>
    public string? PeerAddress => _host.PeerAddress;

    /// <inheritdoc/>
    public string? SelfAddress => _host.SelfAddress;

    /// <inheritdoc/>
    public string? SelfDnsName => _host.SelfDnsName;

    /// <inheritdoc/>
    public PathType CurrentPath => _host.CurrentPath;

    /// <inheritdoc/>
    public TimeSpan RoundTripTime =>
        _host.RoundTripMs < 0
            ? TimeSpan.FromMilliseconds(-1)
            : TimeSpan.FromMilliseconds(_host.RoundTripMs);

    /// <inheritdoc/>
    public string? DerpRegion => _host.DerpRegion;

    /// <summary>
    /// The edge transport declared by the sidecar: "udp" or "tcp". Consumers (such as
    /// the jitter buffer) should read this to select their profile rather than assuming UDP.
    /// </summary>
    public string EdgeTransport => _host.EdgeTransport;

    /// <summary>
    /// The jitter profile declared by the sidecar's status document. Maps to
    /// "PathAdaptive" or "DerpClassOnly". The Station's JitterBuffer reads this to
    /// decide its delay band.
    /// </summary>
    public string JitterProfile => _host.JitterProfile;

    /// <inheritdoc/>
    public async Task StartAsync(string? authKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Delegate tailnet join to the host.
        await _host.StartAsync(authKey).ConfigureAwait(false);

        // Set up the UDP sockets for edge communication.
        InitializeEdgeSockets();
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopEdgeReceiveLoop();
        CloseEdgeSockets();

        await _host.StopAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sends a UDP datagram to the sidecar's edge local address. The sidecar relays it
    /// over the tailnet to the peer. Datagram boundaries are preserved end-to-end when
    /// <see cref="EdgeTransport"/> is "udp" (5.6).
    /// </remarks>
    public async Task<int> SendEdgeAsync(ReadOnlyMemory<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_edgeSendClient is null)
            throw new InvalidOperationException("Edge socket not initialized. Call StartAsync first.");

        return await _edgeSendClient.SendAsync(data).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Establishes a TCP connection to the peer by requesting an outbound forward from the
    /// sidecar (POST /v1/forwards with kind:"out"), then connecting a TcpClient to the
    /// returned loopback listen address (5.7).
    /// </remarks>
    public async Task<Stream> ConnectControlAsync(string peerAddress, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(peerAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        // Ask the sidecar to create an outbound forward: it listens on loopback and
        // dials the peer over the tailnet.
        IPEndPoint listenEndpoint = await _host.CreateOutboundForwardAsync(peerAddress, port)
            .ConfigureAwait(false);

        // Connect a TcpClient to the sidecar's loopback listener.
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(listenEndpoint).ConfigureAwait(false);
            return tcp.GetStream();
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host.StateChanged -= OnHostStateChanged;
        StopEdgeReceiveLoop();
        CloseEdgeSockets();

        // Do not dispose the host — it is injected and may outlive this wrapper.
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private void InitializeEdgeSockets()
    {
        IPEndPoint edgeTarget = _host.EdgeLocalEndpoint;

        // Send socket: connected to the sidecar's edge UDP address so SendAsync works
        // without specifying a remote each time.
        _edgeSendClient = new UdpClient();
        _edgeSendClient.Connect(edgeTarget);

        // Receive socket: bind to a free loopback port. The sidecar delivers inbound
        // edge datagrams to this address once we register it via POST /v1/edge/callback.
        _edgeReceiveClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        // Register the callback address with the sidecar so it knows where to send
        // inbound edge datagrams from the peer.
        var callbackEp = (IPEndPoint)_edgeReceiveClient.Client.LocalEndPoint!;
        _ = RegisterEdgeCallbackAsync(callbackEp);

        // Start the background receive loop.
        _receiveCts = new CancellationTokenSource();
        _receiveLoop = RunEdgeReceiveLoopAsync(_receiveCts.Token);
    }

    private async Task RegisterEdgeCallbackAsync(IPEndPoint callbackEndpoint)
    {
        try
        {
            await _host.RegisterEdgeCallbackAsync(callbackEndpoint.ToString())
                .ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: edge receive won't work but the app stays up.
        }
    }

    /// <summary>
    /// Returns the local endpoint of the edge receive (callback) socket, or null if not started.
    /// Callers (e.g. the sidecar host) use this to register the callback address with the sidecar.
    /// </summary>
    public IPEndPoint? EdgeCallbackEndpoint =>
        _edgeReceiveClient?.Client.LocalEndPoint as IPEndPoint;

    private async Task RunEdgeReceiveLoopAsync(CancellationToken ct)
    {
        if (_edgeReceiveClient is null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _edgeReceiveClient.ReceiveAsync(ct)
                    .ConfigureAwait(false);

                EdgeReceived?.Invoke(this, result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Socket closed during shutdown.
        }
    }

    private void StopEdgeReceiveLoop()
    {
        _receiveCts?.Cancel();

        // Close the receive socket to unblock ReceiveAsync if it's waiting.
        _edgeReceiveClient?.Close();

        try
        {
            _receiveLoop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or ObjectDisposedException)) { }
    }

    private void CloseEdgeSockets()
    {
        _edgeSendClient?.Dispose();
        _edgeSendClient = null;

        _edgeReceiveClient?.Dispose();
        _edgeReceiveClient = null;

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveLoop = null;
    }

    private void OnHostStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        // Relay the event from the host to subscribers of this node's StateChanged.
        StateChanged?.Invoke(this, e);
    }
}
