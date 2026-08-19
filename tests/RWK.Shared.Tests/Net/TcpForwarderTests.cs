using System.Net;
using System.Net.Sockets;
using RWK.Shared.Config;
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Net;

/// <summary>
/// Unit tests for the TCP relay pump: bidirectional data flow, half-close propagation,
/// multiple simultaneous connections, byte counter tracking, and error handling.
/// </summary>
/// <remarks>
/// All tests use loopback TCP sockets to avoid network dependencies.
/// _Requirements: 10.2, 10.3, 10.4_
/// </remarks>
public sealed class TcpForwarderTests : IAsyncLifetime
{
    private TcpListener _stationListener = null!;
    private int _stationPort;

    public Task InitializeAsync()
    {
        // Simulate the Station-side target: a TCP listener on loopback.
        _stationListener = new TcpListener(IPAddress.Loopback, 0);
        _stationListener.Start();
        _stationPort = ((IPEndPoint)_stationListener.LocalEndpoint).Port;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _stationListener.Stop();
        _stationListener.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a PortForwardManager with a tunnel dial that connects to the local
    /// Station listener (simulating a Tailscale tunnel connection to 127.0.0.1:StationPort).
    /// </summary>
    private PortForwardManager CreateManagerWithLoopbackDial()
    {
        return new PortForwardManager(async (port, ct) =>
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            return tcp.GetStream();
        });
    }

    /// <summary>
    /// Helper: reads from a NetworkStream with a timeout.
    /// </summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await stream.ReadAsync(buffer, cts.Token);
    }

    /// <summary>
    /// Verifies that data sent from the client side reaches the Station side.
    /// </summary>
    [Fact]
    public async Task BidirectionalRelay_ClientToStation_DataFlows()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        // Act: connect as a client.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Send data client → Station.
        byte[] payload = "Hello, Station!"u8.ToArray();
        await clientStream.WriteAsync(payload);
        await clientStream.FlushAsync();

        // Read on Station side.
        byte[] buffer = new byte[1024];
        int bytesRead = await ReadWithTimeoutAsync(stationStream, buffer, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, buffer[..bytesRead]);
    }

    /// <summary>
    /// Verifies that data sent from the Station side reaches the client.
    /// </summary>
    [Fact]
    public async Task BidirectionalRelay_StationToClient_DataFlows()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: send data Station → Client.
        byte[] payload = "Hello, Client!"u8.ToArray();
        await stationStream.WriteAsync(payload);
        await stationStream.FlushAsync();

        // Read on Client side.
        byte[] buffer = new byte[1024];
        int bytesRead = await ReadWithTimeoutAsync(clientStream, buffer, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, buffer[..bytesRead]);
    }

    /// <summary>
    /// Verifies bidirectional data flow: both directions simultaneously.
    /// </summary>
    [Fact]
    public async Task BidirectionalRelay_BothDirectionsSimultaneously()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: send data both directions concurrently.
        byte[] clientPayload = "From Client"u8.ToArray();
        byte[] stationPayload = "From Station"u8.ToArray();

        await clientStream.WriteAsync(clientPayload);
        await clientStream.FlushAsync();
        await stationStream.WriteAsync(stationPayload);
        await stationStream.FlushAsync();

        // Read both sides.
        byte[] clientBuffer = new byte[1024];
        byte[] stationBuffer = new byte[1024];

        int stationRead = await ReadWithTimeoutAsync(stationStream, stationBuffer, TimeSpan.FromSeconds(5));
        int clientRead = await ReadWithTimeoutAsync(clientStream, clientBuffer, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(clientPayload, stationBuffer[..stationRead]);
        Assert.Equal(stationPayload, clientBuffer[..clientRead]);
    }

    /// <summary>
    /// Verifies that half-close propagation works: when the client shuts down its send side,
    /// the Station receives EOF on read, but data can still flow from Station to Client.
    /// </summary>
    [Fact]
    public async Task HalfClose_ClientShutdownSend_StationReceivesEof_ReverseStillOpen()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: client sends data then shuts down its send direction.
        byte[] payload = "Last message"u8.ToArray();
        await clientStream.WriteAsync(payload);
        await clientStream.FlushAsync();

        // Read the forwarded data on Station side first.
        byte[] buffer = new byte[1024];
        int bytesRead = await ReadWithTimeoutAsync(stationStream, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal(payload, buffer[..bytesRead]);

        // Shut down client's send side (FIN).
        client.Client.Shutdown(SocketShutdown.Send);

        // Station should now get EOF (read returns 0).
        int eofRead = await ReadWithTimeoutAsync(stationStream, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal(0, eofRead);

        // But the reverse direction should still work: Station → Client.
        byte[] reversePayload = "Still alive!"u8.ToArray();
        await stationStream.WriteAsync(reversePayload);
        await stationStream.FlushAsync();

        byte[] reverseBuffer = new byte[1024];
        int reverseRead = await ReadWithTimeoutAsync(clientStream, reverseBuffer, TimeSpan.FromSeconds(5));
        Assert.Equal(reversePayload, reverseBuffer[..reverseRead]);
    }

    /// <summary>
    /// Verifies that half-close propagation works in reverse: when the Station shuts down
    /// its send side, the Client receives EOF, but Client → Station still works.
    /// </summary>
    [Fact]
    public async Task HalfClose_StationShutdownSend_ClientReceivesEof_ReverseStillOpen()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: Station sends data then shuts down send.
        byte[] payload = "Final from Station"u8.ToArray();
        await stationStream.WriteAsync(payload);
        await stationStream.FlushAsync();

        // Read on client side.
        byte[] buffer = new byte[1024];
        int bytesRead = await ReadWithTimeoutAsync(clientStream, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal(payload, buffer[..bytesRead]);

        // Station shuts down send (FIN).
        stationClient.Client.Shutdown(SocketShutdown.Send);

        // Client should get EOF.
        int eofRead = await ReadWithTimeoutAsync(clientStream, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal(0, eofRead);

        // Reverse direction (Client → Station) should still work.
        byte[] reversePayload = "Client still sends"u8.ToArray();
        await clientStream.WriteAsync(reversePayload);
        await clientStream.FlushAsync();

        byte[] reverseBuffer = new byte[1024];
        int reverseRead = await ReadWithTimeoutAsync(stationStream, reverseBuffer, TimeSpan.FromSeconds(5));
        Assert.Equal(reversePayload, reverseBuffer[..reverseRead]);
    }

    /// <summary>
    /// Verifies that byte counters increment correctly for both directions.
    /// </summary>
    [Fact]
    public async Task ByteCounters_IncrementForBothDirections()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: send 100 bytes Client → Station.
        byte[] outPayload = new byte[100];
        Random.Shared.NextBytes(outPayload);
        await clientStream.WriteAsync(outPayload);
        await clientStream.FlushAsync();

        // Read on Station side to ensure the pump has processed.
        byte[] buf = new byte[1024];
        int read = await ReadWithTimeoutAsync(stationStream, buf, TimeSpan.FromSeconds(5));
        Assert.Equal(100, read);

        // Send 50 bytes Station → Client.
        byte[] inPayload = new byte[50];
        Random.Shared.NextBytes(inPayload);
        await stationStream.WriteAsync(inPayload);
        await stationStream.FlushAsync();

        // Read on client side.
        int clientRead = await ReadWithTimeoutAsync(clientStream, buf, TimeSpan.FromSeconds(5));
        Assert.Equal(50, clientRead);

        // Allow a moment for counters to propagate.
        await Task.Delay(50);

        // Assert byte counters.
        var (bytesIn, bytesOut) = manager.GetByteCounters(ruleId);
        Assert.Equal(50, bytesIn);   // Station → Client
        Assert.Equal(100, bytesOut); // Client → Station
    }

    /// <summary>
    /// Verifies multiple simultaneous connections per rule are supported.
    /// </summary>
    [Fact]
    public async Task MultipleConnections_IndependentRelays()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        // Accept two Station-side connections.
        var stationAcceptTask1 = _stationListener.AcceptTcpClientAsync();

        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream1 = client1.GetStream();
        using var stationClient1 = await stationAcceptTask1.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream1 = stationClient1.GetStream();

        var stationAcceptTask2 = _stationListener.AcceptTcpClientAsync();

        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream2 = client2.GetStream();
        using var stationClient2 = await stationAcceptTask2.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream2 = stationClient2.GetStream();

        // Act: send different data on each connection.
        byte[] payload1 = "Connection 1"u8.ToArray();
        byte[] payload2 = "Connection 2"u8.ToArray();

        await clientStream1.WriteAsync(payload1);
        await clientStream1.FlushAsync();
        await clientStream2.WriteAsync(payload2);
        await clientStream2.FlushAsync();

        // Assert: each Station connection receives its own data (no crosstalk).
        byte[] buf1 = new byte[1024];
        byte[] buf2 = new byte[1024];

        int read1 = await ReadWithTimeoutAsync(stationStream1, buf1, TimeSpan.FromSeconds(5));
        int read2 = await ReadWithTimeoutAsync(stationStream2, buf2, TimeSpan.FromSeconds(5));

        Assert.Equal(payload1, buf1[..read1]);
        Assert.Equal(payload2, buf2[..read2]);
    }

    /// <summary>
    /// Verifies that a socket error on one connection does not affect other connections
    /// on the same rule.
    /// </summary>
    [Fact]
    public async Task ErrorIsolation_OneConnectionError_OtherConnectionsSurvive()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        // Establish two connections.
        var stationAcceptTask1 = _stationListener.AcceptTcpClientAsync();
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream1 = client1.GetStream();
        using var stationClient1 = await stationAcceptTask1.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream1 = stationClient1.GetStream();

        var stationAcceptTask2 = _stationListener.AcceptTcpClientAsync();
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream2 = client2.GetStream();
        using var stationClient2 = await stationAcceptTask2.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream2 = stationClient2.GetStream();

        // Act: forcefully close connection 1 (simulate error).
        client1.Client.Close();
        await Task.Delay(200); // Allow time for error to propagate.

        // Connection 2 should still work.
        byte[] payload = "Still working"u8.ToArray();
        await clientStream2.WriteAsync(payload);
        await clientStream2.FlushAsync();

        byte[] buf = new byte[1024];
        int bytesRead = await ReadWithTimeoutAsync(stationStream2, buf, TimeSpan.FromSeconds(5));

        // Assert: connection 2 is alive and data flows.
        Assert.Equal(payload, buf[..bytesRead]);
    }

    /// <summary>
    /// Verifies that large data transfers work correctly with the 64KB buffer.
    /// </summary>
    [Theory]
    [InlineData(65536)]   // Exactly one buffer
    [InlineData(200000)]  // Multiple buffer fills
    public async Task LargeDataTransfer_AllBytesRelayed(int totalBytes)
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Act: send totalBytes of random data, then close to signal EOF.
        byte[] payload = new byte[totalBytes];
        Random.Shared.NextBytes(payload);

        var writeTask = Task.Run(async () =>
        {
            await clientStream.WriteAsync(payload);
            await clientStream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
        });

        // Read all data on Station side.
        using var ms = new MemoryStream();
        byte[] buf = new byte[8192];
        int read;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((read = await stationStream.ReadAsync(buf, readCts.Token)) > 0)
        {
            ms.Write(buf, 0, read);
        }

        await writeTask;

        // Assert: all bytes received correctly.
        Assert.Equal(totalBytes, (int)ms.Length);
        Assert.Equal(payload, ms.ToArray());
    }

    /// <summary>
    /// Verifies that stopping the manager cleanly tears down active connections.
    /// </summary>
    [Fact]
    public async Task ManagerStop_ClosesActiveConnections()
    {
        // Arrange
        using var manager = CreateManagerWithLoopbackDial();
        var ruleId = Guid.NewGuid();
        int clientPort = GetFreePort();
        var rule = new ForwardRule(ruleId, "tcp-test", ForwardProtocol.Tcp, clientPort, _stationPort, true);
        manager.AddRule(rule);
        manager.Start();
        await Task.Delay(100);

        var stationAcceptTask = _stationListener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, clientPort);
        using var clientStream = client.GetStream();

        using var stationClient = await stationAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var stationStream = stationClient.GetStream();

        // Verify connection is alive.
        byte[] payload = "alive"u8.ToArray();
        await clientStream.WriteAsync(payload);
        await clientStream.FlushAsync();
        byte[] buf = new byte[1024];
        int read = await ReadWithTimeoutAsync(stationStream, buf, TimeSpan.FromSeconds(5));
        Assert.Equal(payload, buf[..read]);

        // Act: stop the manager.
        manager.Stop();
        await Task.Delay(200);

        // Assert: reading from the client stream returns 0 or throws (connection closed).
        int eofOrError;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            eofOrError = await clientStream.ReadAsync(buf, cts.Token);
        }
        catch (IOException)
        {
            eofOrError = -1; // Connection reset.
        }
        catch (SocketException)
        {
            eofOrError = -1; // Socket error.
        }
        catch (OperationCanceledException)
        {
            eofOrError = -1; // Timeout.
        }

        Assert.True(eofOrError <= 0, "Expected EOF or socket error after manager stop.");
    }

    /// <summary>
    /// Gets an available TCP port on loopback.
    /// </summary>
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
