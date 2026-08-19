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

namespace WinKeyerEmulator.Integration.Tests;

/// <summary>
/// Test client that sends UDP command datagrams and receives responses with timeout.
/// </summary>
public sealed class UdpTestClient : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _serverEndpoint;
    private bool _disposed;

    /// <summary>
    /// Default timeout for receiving responses.
    /// </summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates a UDP test client that sends to the specified server port on loopback.
    /// </summary>
    /// <param name="serverPort">The port the UdpTestServer is listening on.</param>
    public UdpTestClient(int serverPort)
    {
        _serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverPort);
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    }

    /// <summary>
    /// Sends command bytes to the server without waiting for a response.
    /// </summary>
    public async Task SendAsync(params byte[] data)
    {
        await _client.SendAsync(data, data.Length, _serverEndpoint);
    }

    /// <summary>
    /// Sends command bytes and waits for a response within the configured timeout.
    /// Returns the response bytes, or null if no response was received.
    /// </summary>
    public async Task<byte[]?> SendAndReceiveAsync(params byte[] data)
    {
        await _client.SendAsync(data, data.Length, _serverEndpoint);
        return await ReceiveAsync();
    }

    /// <summary>
    /// Waits for a response from the server within the configured timeout.
    /// Returns the response bytes, or null if no response was received.
    /// </summary>
    public async Task<byte[]?> ReceiveAsync()
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        try
        {
            var result = await _client.ReceiveAsync(cts.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to receive a response, expecting none (validates no response within a short timeout).
    /// Returns true if no response was received (expected behavior), false if a response was received.
    /// </summary>
    public async Task<bool> ExpectNoResponseAsync(TimeSpan? timeout = null)
    {
        var waitTime = timeout ?? TimeSpan.FromMilliseconds(200);
        using var cts = new CancellationTokenSource(waitTime);
        try
        {
            await _client.ReceiveAsync(cts.Token);
            return false; // Received unexpected response
        }
        catch (OperationCanceledException)
        {
            return true; // No response received (expected)
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Close();
        _client.Dispose();
    }
}
