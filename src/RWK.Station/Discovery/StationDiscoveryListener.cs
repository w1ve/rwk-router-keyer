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
using RWK.Shared.Discovery;

namespace RWK.Station.Discovery;

/// <summary>
/// Listens for FlexRadio VITA-49 discovery broadcasts on the Station's LAN (UDP port 4992),
/// parses them via <see cref="IDiscoveryPayloadCodec"/>, and raises events with the raw
/// payload for forwarding to the Client.
/// </summary>
/// <remarks>
/// Runs at normal thread priority. The socket uses SO_REUSEADDR so that SmartSDR at the
/// Station still receives its own discovery broadcasts. Only started when the Station's
/// capture enable control is on (15.6, 15.7).
/// _Requirements: 15.1, 15.6, 15.7, 15.17, 15.18_
/// </remarks>
public sealed class StationDiscoveryListener : IDisposable
{
    private readonly IDiscoveryPayloadCodec _codec;
    private readonly Action<string>? _diagnostics;
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>
    /// Raised when a valid discovery payload is received and parsed. Carries the raw
    /// payload bytes (for forwarding) and the parsed radio identity (for display).
    /// </summary>
    public event EventHandler<DiscoveryCapturedEventArgs>? DiscoveryCaptured;

    public StationDiscoveryListener(IDiscoveryPayloadCodec codec, Action<string>? diagnostics = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _diagnostics = diagnostics;
    }

    /// <summary>Whether the listener is currently running.</summary>
    public bool IsRunning => _receiveLoop is not null && !_receiveLoop.IsCompleted;

    /// <summary>
    /// Starts listening on UDP port 4992 for FlexRadio discovery broadcasts.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();

        _socket = new UdpClient();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, FlexVitaDiscoveryCodec.DiscoveryPort));

        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        _diagnostics?.Invoke($"Discovery listener started on UDP port {FlexVitaDiscoveryCodec.DiscoveryPort}.");
    }

    /// <summary>
    /// Stops listening and releases the socket.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _socket?.Close();

        try { _receiveLoop?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        _socket?.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _receiveLoop = null;

        _diagnostics?.Invoke("Discovery listener stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] payload = result.Buffer;

                if (_codec.TryParse(payload, out DiscoveredRadio radio, out string? failureReason))
                {
                    DiscoveryCaptured?.Invoke(this, new DiscoveryCapturedEventArgs(
                        radio, payload, DateTime.UtcNow));
                }
                else
                {
                    // Not a Flex discovery packet — ignore silently (could be other traffic on 4992)
                    // Only log at diagnostics level if it looked like it might be one
                    if (payload.Length > 28)
                        _diagnostics?.Invoke($"Discovery packet rejected: {failureReason}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex)
        {
            _diagnostics?.Invoke($"Discovery listener socket error: {ex.Message}");
        }
    }
}
