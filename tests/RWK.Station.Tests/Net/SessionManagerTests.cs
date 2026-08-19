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
using System.Security.Cryptography;
using RWK.Shared;
using RWK.Shared.Net;
using RWK.Station.Net;
using Xunit;

namespace RWK.Station.Tests.Net;

/// <summary>
/// Unit tests for <see cref="SessionManager"/>: validates HMAC challenge/response auth,
/// timeout rejection, single-session enforcement, and epoch incrementing.
/// </summary>
/// <remarks>
/// _Requirements: 11.1–11.8_
/// </remarks>
public class SessionManagerTests : IDisposable
{
    private static readonly byte[] TestSecret = "test-pairing-secret-32bytes!!"u8.ToArray();
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    private readonly SessionManager _manager;
    private readonly int _port;

    public SessionManagerTests()
    {
        _port = GetFreePort();
        _manager = new SessionManager(TestSecret, ShortTimeout);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    /// <summary>
    /// A client that provides a valid HMAC response is accepted and a session is established (11.4).
    /// </summary>
    [Fact]
    public async Task ValidAuthEstablishesSession()
    {
        SessionEventArgs? startedEvent = null;
        _manager.SessionStarted += (_, e) => startedEvent = e;
        _manager.Start(_port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        NetworkStream stream = client.GetStream();

        // Read the 32-byte nonce (11.2).
        byte[] nonce = await ReadExactAsync(stream, SessionManager.NonceLength);

        // Compute and send the correct HMAC (11.3).
        byte[] hmac = HMACSHA256.HashData(TestSecret, nonce);
        await stream.WriteAsync(hmac);
        await stream.FlushAsync();

        // Read the OK confirmation.
        byte[] response = await ReadExactAsync(stream, 2);
        Assert.Equal("OK"u8.ToArray(), response);

        // Give the event a moment to fire.
        await WaitForAsync(() => _manager.CurrentSession is not null);

        Assert.NotNull(_manager.CurrentSession);
        Assert.Equal(SessionState.Active, _manager.CurrentSession!.State);
        Assert.NotNull(startedEvent);
        Assert.Equal(SessionState.Active, startedEvent!.State);
    }

    /// <summary>
    /// A client that provides an incorrect HMAC response is rejected and no session is established (11.5).
    /// </summary>
    [Fact]
    public async Task WrongSecretIsRejected()
    {
        _manager.Start(_port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        NetworkStream stream = client.GetStream();

        // Read the nonce.
        byte[] nonce = await ReadExactAsync(stream, SessionManager.NonceLength);

        // Send garbage HMAC.
        byte[] badHmac = new byte[SessionManager.HmacResponseLength];
        Array.Fill(badHmac, (byte)0xAA);
        await stream.WriteAsync(badHmac);
        await stream.FlushAsync();

        // Should get FAIL response.
        byte[] response = await ReadExactAsync(stream, 4);
        Assert.Equal("FAIL"u8.ToArray(), response);

        // No session should be established.
        await Task.Delay(100);
        Assert.Null(_manager.CurrentSession);
    }

    /// <summary>
    /// A client that does not respond within the timeout is rejected (11.3, 11.5).
    /// </summary>
    [Fact]
    public async Task TimeoutRejectsConnection()
    {
        // Use a very short timeout for test speed.
        using var manager = new SessionManager(TestSecret, TimeSpan.FromMilliseconds(500));
        int port = GetFreePort();
        manager.Start(port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();

        // Read the nonce but don't send a response.
        _ = await ReadExactAsync(stream, SessionManager.NonceLength);

        // Wait for FAIL to arrive due to timeout.
        byte[] response = await ReadExactAsync(stream, 4, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal("FAIL"u8.ToArray(), response);

        Assert.Null(manager.CurrentSession);
    }

    /// <summary>
    /// While a session is active, a second connection receives BUSY and is rejected (11.6).
    /// </summary>
    [Fact]
    public async Task SecondConnectionGetsBusyWhileSessionActive()
    {
        _manager.Start(_port);

        // Establish first session.
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, _port);
        await AuthenticateAsync(client1.GetStream());
        await WaitForAsync(() => _manager.CurrentSession is not null);

        // Attempt a second connection.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, _port);
        NetworkStream stream2 = client2.GetStream();

        // Should immediately get BUSY.
        byte[] response = await ReadExactAsync(stream2, 4, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal("BUSY"u8.ToArray(), response);

        // Original session is still active.
        Assert.NotNull(_manager.CurrentSession);
    }

    /// <summary>
    /// The epoch counter increments with each successfully authenticated session (F4 tie-in).
    /// </summary>
    [Fact]
    public async Task EpochIncrementsPerSession()
    {
        _manager.Start(_port);
        ushort initialEpoch = _manager.CurrentEpoch;

        // First session.
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, _port);
        await AuthenticateAsync(client1.GetStream());
        await WaitForAsync(() => _manager.CurrentSession is not null);

        ushort epochAfterFirst = _manager.CurrentEpoch;
        Assert.Equal((ushort)(initialEpoch + 1), epochAfterFirst);

        // Disconnect first session.
        _manager.DisconnectSession();
        await WaitForAsync(() => _manager.CurrentSession is null);

        // Second session.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, _port);
        await AuthenticateAsync(client2.GetStream());
        await WaitForAsync(() => _manager.CurrentSession is not null);

        ushort epochAfterSecond = _manager.CurrentEpoch;
        Assert.Equal((ushort)(initialEpoch + 2), epochAfterSecond);
    }

    /// <summary>
    /// DisconnectSession ends the current session and raises SessionEnded (11.7).
    /// </summary>
    [Fact]
    public async Task DisconnectSessionEndsActiveSession()
    {
        SessionEventArgs? endedEvent = null;
        _manager.SessionEnded += (_, e) => endedEvent = e;
        _manager.Start(_port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await AuthenticateAsync(client.GetStream());
        await WaitForAsync(() => _manager.CurrentSession is not null);

        _manager.DisconnectSession();
        await WaitForAsync(() => _manager.CurrentSession is null);

        Assert.Null(_manager.CurrentSession);
        Assert.NotNull(endedEvent);
        Assert.Equal(SessionState.Closed, endedEvent!.State);
        Assert.Contains("Owner forced disconnect", endedEvent.Reason!);
    }

    // ----- Helpers -----

    private async Task AuthenticateAsync(NetworkStream stream)
    {
        byte[] nonce = await ReadExactAsync(stream, SessionManager.NonceLength);
        byte[] hmac = HMACSHA256.HashData(TestSecret, nonce);
        await stream.WriteAsync(hmac);
        await stream.FlushAsync();
        // Read the OK.
        _ = await ReadExactAsync(stream, 2);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, TimeSpan? timeout = null)
    {
        byte[] buffer = new byte[count];
        int total = 0;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cts.Token);
            if (read == 0)
                throw new EndOfStreamException($"Stream closed after {total} bytes, expected {count}");
            total += read;
        }

        return buffer;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met within timeout.");
    }

    private static int GetFreePort()
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)sock.LocalEndPoint!).Port;
    }
}
