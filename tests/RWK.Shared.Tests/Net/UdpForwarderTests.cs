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
using RWK.Shared.Config;
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Net;

/// <summary>
/// Unit tests for <see cref="UdpForwarder"/>: datagram boundary preservation, session
/// creation on first packet, reply routing to correct original sender, and idle timeout
/// eviction.
/// </summary>
/// <remarks>
/// All tests use loopback UdpClient pairs to avoid network dependencies.
/// _Requirements: 10.5, 10.6_
/// </remarks>
public sealed class UdpForwarderTests : IDisposable
{
    private readonly PortForwardManager _manager;

    public UdpForwarderTests()
    {
        _manager = new PortForwardManager();
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    /// <summary>
    /// Sends a datagram of N bytes and verifies the destination receives exactly N bytes
    /// (datagram boundaries preserved, no coalescing or splitting).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1400)]
    public async Task DatagramBoundaryPreservation_SendNBytes_ReceiveExactlyNBytes(int size)
    {
        // Arrange: set up the "destination" server that the forwarder sends to.
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        // The listener socket (what clients send to).
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        // Register a rule so the byte counters work.
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager);
        _ = forwarder.RunAsync(cts.Token);

        // Give the receive loop a moment to start.
        await Task.Delay(50);

        // Act: send a datagram of exactly `size` bytes from a separate client.
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] payload = new byte[size];
        Random.Shared.NextBytes(payload);
        await sender.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, listenerPort));

        // Assert: the destination receives exactly the same bytes.
        destination.Client.ReceiveTimeout = 3000;
        IPEndPoint? remote = null;
        byte[] received = destination.Receive(ref remote);

        Assert.Equal(size, received.Length);
        Assert.Equal(payload, received);
    }

    /// <summary>
    /// The first datagram from a new sender endpoint creates a session.
    /// </summary>
    [Fact]
    public async Task SessionCreation_FirstPacketCreatesSession()
    {
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager);
        _ = forwarder.RunAsync(cts.Token);

        // No sessions yet.
        Assert.Equal(0, forwarder.SessionCount);

        // Act: send a datagram.
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await sender.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Loopback, listenerPort));

        // Wait for the forwarder to process.
        await Task.Delay(100);

        // Assert: one session created.
        Assert.Equal(1, forwarder.SessionCount);
    }

    /// <summary>
    /// Replies from the destination are routed back to the correct original sender.
    /// </summary>
    [Fact]
    public async Task ReplyRouting_RepliesToCorrectSender()
    {
        // The "destination" echo server: receives a datagram and sends a reply back.
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager);
        _ = forwarder.RunAsync(cts.Token);
        await Task.Delay(50);

        // Sender sends a request.
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] request = new byte[] { 0xCA, 0xFE };
        await sender.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, listenerPort));

        // Destination receives the forwarded datagram.
        destination.Client.ReceiveTimeout = 3000;
        IPEndPoint? forwarderEndpoint = null;
        byte[] forwarded = destination.Receive(ref forwarderEndpoint);
        Assert.Equal(request, forwarded);

        // Destination sends a reply back to the forwarder's session socket.
        byte[] reply = new byte[] { 0xDE, 0xAD };
        await destination.SendAsync(reply, reply.Length, forwarderEndpoint!);

        // Sender receives the reply routed through the forwarder.
        sender.Client.ReceiveTimeout = 3000;
        IPEndPoint? replySource = null;
        byte[] receivedReply = sender.Receive(ref replySource);

        Assert.Equal(reply, receivedReply);
        // The reply should appear to come from the forwarder's listener port.
        Assert.Equal(listenerPort, replySource!.Port);
    }

    /// <summary>
    /// Two different senders each get their replies routed correctly (no cross-talk).
    /// </summary>
    [Fact]
    public async Task ReplyRouting_MultipleSenders_NoCrosstalk()
    {
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager);
        _ = forwarder.RunAsync(cts.Token);
        await Task.Delay(50);

        // Two different senders.
        using var sender1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] msg1 = new byte[] { 0x01 };
        byte[] msg2 = new byte[] { 0x02 };

        await sender1.SendAsync(msg1, msg1.Length, new IPEndPoint(IPAddress.Loopback, listenerPort));
        await Task.Delay(50);
        await sender2.SendAsync(msg2, msg2.Length, new IPEndPoint(IPAddress.Loopback, listenerPort));
        await Task.Delay(50);

        Assert.Equal(2, forwarder.SessionCount);

        // Destination receives both, sends different replies to each.
        destination.Client.ReceiveTimeout = 3000;
        IPEndPoint? ep1 = null;
        byte[] recv1 = destination.Receive(ref ep1);
        IPEndPoint? ep2 = null;
        byte[] recv2 = destination.Receive(ref ep2);

        // Reply to each via their respective session sockets.
        byte[] reply1 = new byte[] { 0xA1 };
        byte[] reply2 = new byte[] { 0xA2 };
        await destination.SendAsync(reply1, reply1.Length, ep1!);
        await destination.SendAsync(reply2, reply2.Length, ep2!);

        // Each sender should get their own reply.
        sender1.Client.ReceiveTimeout = 3000;
        sender2.Client.ReceiveTimeout = 3000;
        IPEndPoint? replyEp1 = null;
        byte[] senderRecv1 = sender1.Receive(ref replyEp1);
        IPEndPoint? replyEp2 = null;
        byte[] senderRecv2 = sender2.Receive(ref replyEp2);

        Assert.Equal(reply1, senderRecv1);
        Assert.Equal(reply2, senderRecv2);
    }

    /// <summary>
    /// Sessions are evicted after the idle timeout elapses with no traffic.
    /// Uses a short idle timeout to avoid long test durations.
    /// </summary>
    [Fact]
    public async Task IdleTimeout_SessionEvictedAfterTimeout()
    {
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        // Use a very short idle timeout for testing (1 second).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager,
            idleTimeout: TimeSpan.FromSeconds(1));
        _ = forwarder.RunAsync(cts.Token);
        await Task.Delay(50);

        // Send a datagram to create a session.
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await sender.SendAsync(new byte[] { 1 }, 1, new IPEndPoint(IPAddress.Loopback, listenerPort));
        await Task.Delay(100);

        Assert.Equal(1, forwarder.SessionCount);

        // Wait for the idle timeout plus the scavenge interval (15s is too long).
        // The scavenge timer runs every 15 seconds by default, which is too long for tests.
        // We'll wait a bit more than the idle timeout and trigger scavenging by checking.
        // Actually, the timer runs on 15s intervals. For a 1s timeout, we need to wait
        // for the scavenge timer to fire. Let's wait up to 20 seconds.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (forwarder.SessionCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        Assert.Equal(0, forwarder.SessionCount);
    }

    /// <summary>
    /// Byte counters are incremented correctly.
    /// </summary>
    [Fact]
    public async Task ByteCounters_IncrementedOnForward()
    {
        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destPort = ((IPEndPoint)destination.Client.LocalEndPoint!).Port;

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var ruleId = Guid.NewGuid();
        var rule = new ForwardRule(ruleId, "test", ForwardProtocol.Udp, listenerPort, destPort, true);
        _manager.AddRule(rule);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var forwarder = new UdpForwarder(
            listener,
            new IPEndPoint(IPAddress.Loopback, destPort),
            ruleId,
            _manager);
        _ = forwarder.RunAsync(cts.Token);
        await Task.Delay(50);

        // Send 10 bytes.
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] payload = new byte[10];
        await sender.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, listenerPort));

        // Wait for forwarding.
        await Task.Delay(100);

        // BytesOut should be 10 (Client→Station direction).
        var (bytesIn, bytesOut) = _manager.GetByteCounters(ruleId);
        Assert.Equal(10, bytesOut);

        // Now simulate a reply from destination.
        destination.Client.ReceiveTimeout = 3000;
        IPEndPoint? fwdEp = null;
        destination.Receive(ref fwdEp);

        byte[] reply = new byte[5];
        await destination.SendAsync(reply, reply.Length, fwdEp!);
        await Task.Delay(100);

        // BytesIn should be 5 (Station→Client direction).
        (bytesIn, bytesOut) = _manager.GetByteCounters(ruleId);
        Assert.Equal(5, bytesIn);
        Assert.Equal(10, bytesOut);
    }
}
