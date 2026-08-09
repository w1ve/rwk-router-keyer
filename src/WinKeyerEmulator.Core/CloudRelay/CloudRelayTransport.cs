using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Threading.Channels;

namespace WinKeyerEmulator.Core.CloudRelay;

/// <summary>
/// Endpoint type for the cloud relay connection.
/// </summary>
public enum RelayEndpointType
{
    /// <summary>The station side (server) — runs the WinKeyer emulator.</summary>
    StationSide,
    /// <summary>The remote side (client) — the remote operator.</summary>
    RemoteSide,
}

/// <summary>
/// Configuration for a cloud relay connection.
/// </summary>
public sealed class RelayConfig
{
    /// <summary>The relay WebSocket URL (e.g., "wss://wrs.w1ve.com/ws").</summary>
    public string RelayUrl { get; init; } = "wss://wrs.w1ve.com/ws";

    /// <summary>64-character lowercase hex pairing token.</summary>
    public required string PairingToken { get; init; }

    /// <summary>Which endpoint type this connection represents.</summary>
    public RelayEndpointType EndpointType { get; init; }

    /// <summary>Heartbeat interval in milliseconds.</summary>
    public int HeartbeatIntervalMs { get; init; } = 5000;

    /// <summary>Initial reconnect delay in milliseconds after a disconnect.</summary>
    public int ReconnectDelayMs { get; init; } = 500;

    /// <summary>Maximum reconnect delay in milliseconds (for exponential backoff).</summary>
    public int MaxReconnectDelayMs { get; init; } = 30000;

    /// <summary>
    /// Maximum reconnect attempts before giving up (0 = infinite).
    /// Default is 0 (infinite) for station side to handle flaky connections.
    /// </summary>
    public int MaxReconnectAttempts { get; init; } = 0;
}

/// <summary>
/// Event args for relay status changes.
/// </summary>
public sealed class RelayStatusEventArgs : EventArgs
{
    public RelayStatus Status { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Relay connection status.
/// </summary>
public enum RelayStatus
{
    Disconnected,
    Connecting,
    Connected,
    Paired,
    Reconnecting,
    Error,
}

/// <summary>
/// WebSocket-based transport that connects to the WRS Cloudflare relay.
/// Implements ICommandSource and ICommandSink for drop-in use with the existing architecture.
/// 
/// Key design decisions:
/// - Single-writer send pump via Channel to avoid blocking callers and concurrent SendAsync
/// - TCP_NODELAY disabled (Nagle off) for minimal latency on small frames
/// - Exponential backoff with jitter for reconnects
/// - Dead peer detection via last-receive timestamp
/// - Proper WebSocket message reassembly for fragmented frames
/// - Sequence gap detection for dropped frame awareness
/// </summary>
public sealed class CloudRelayTransport : IO.ICommandSource, IO.ICommandSink, IDisposable
{
    private readonly RelayConfig _config;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Task? _heartbeatTask;
    private bool _disposed;
    private uint _sequenceNumber;
    private uint _lastReceivedSeq;
    private long _droppedFrameCount;
    private RelayStatus _status = RelayStatus.Disconnected;
    private DateTime _lastRxTimestamp = DateTime.UtcNow;
    private readonly Random _jitterRandom = new();

    // Single-writer send queue - callers never block, one task handles all sends
    private readonly Channel<byte[]> _sendQueue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <inheritdoc/>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>Raised when the relay connection status changes.</summary>
    public event EventHandler<RelayStatusEventArgs>? StatusChanged;

    /// <summary>Raised when an error occurs.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Gets the current connection status.</summary>
    public RelayStatus Status => _status;

    /// <summary>Gets the count of dropped frames detected via sequence gaps.</summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    public CloudRelayTransport(RelayConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CloudRelayTransport));
        if (_cts is not null) throw new InvalidOperationException("Transport is already running.");

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ConnectAndReceiveLoop(_cts.Token));
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _cts?.Cancel();

        // Complete the send queue to stop the send pump
        _sendQueue.Writer.TryComplete();

        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                // Send SESSION_CLOSE frame before disconnecting
                SendSessionCloseFrameSync();
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { }

        try { _receiveTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _sendTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _heartbeatTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        CleanupWebSocket();
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _sendTask = null;
        _heartbeatTask = null;
        _sequenceNumber = 0;
        _lastReceivedSeq = 0;

        SetStatus(RelayStatus.Disconnected, "Stopped");
    }

    /// <inheritdoc/>
    public void SendResponse(byte[] data)
    {
        if (_status != RelayStatus.Paired)
        {
            // Surface dropped data during non-paired state
            Interlocked.Increment(ref _droppedFrameCount);
            return;
        }

        var seq = Interlocked.Increment(ref _sequenceNumber);
        var frame = new WireFrame(
            Version: 1,
            Flags: FrameFlags.None,
            SessionId: 0,
            SequenceNumber: seq,
            Payload: data);

        var bytes = WireProtocol.Serialize(frame);
        
        // Non-blocking enqueue - send pump handles actual transmission
        if (!_sendQueue.Writer.TryWrite(bytes))
        {
            Interlocked.Increment(ref _droppedFrameCount);
            Error?.Invoke(this, "Send queue full, frame dropped");
        }
    }

    /// <summary>
    /// Sends data to the relay (alias for SendResponse, used by client side).
    /// </summary>
    public void SendData(byte[] data) => SendResponse(data);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
        }
    }

    /// <summary>
    /// Dedicated send pump task - only this task ever calls WebSocket.SendAsync.
    /// This eliminates concurrent send issues and makes SendResponse non-blocking.
    /// </summary>
    private async Task SendPump(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_ws?.State != WebSocketState.Open)
                {
                    Interlocked.Increment(ref _droppedFrameCount);
                    continue;
                }

                try
                {
                    await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    // Let receive loop drive reconnect
                    Interlocked.Increment(ref _droppedFrameCount);
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, $"Send failed: {ex.Message}");
                    Interlocked.Increment(ref _droppedFrameCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ConnectAndReceiveLoop(CancellationToken ct)
    {
        int attempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(RelayStatus.Connecting, "Connecting to relay...");
                await ConnectAsync(ct).ConfigureAwait(false);

                if (_ws?.State == WebSocketState.Open)
                {
                    attempts = 0;
                    _lastRxTimestamp = DateTime.UtcNow;
                    SetStatus(RelayStatus.Connected, "Connected, waiting for pairing...");

                    // Send SESSION_OPEN frame
                    await SendSessionOpenFrameAsync(ct).ConfigureAwait(false);

                    // Start send pump (new task for this connection)
                    _sendTask = Task.Run(() => SendPump(ct), ct);

                    // Start heartbeat with dead peer detection
                    _heartbeatTask = Task.Run(() => HeartbeatLoop(ct), ct);

                    // Receive loop
                    await ReceiveLoop(ct).ConfigureAwait(false);

                    // Wait for send pump to finish
                    try { if (_sendTask != null) await _sendTask.ConfigureAwait(false); }
                    catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var fullMsg = ex.InnerException != null
                    ? $"Connection error: {ex.Message} | Inner: {ex.InnerException.Message}"
                    : $"Connection error: {ex.Message}";
                Error?.Invoke(this, fullMsg);
            }

            if (ct.IsCancellationRequested) break;

            // Reconnect logic with exponential backoff
            attempts++;
            if (_config.MaxReconnectAttempts > 0 && attempts >= _config.MaxReconnectAttempts)
            {
                SetStatus(RelayStatus.Error, $"Failed after {attempts} reconnect attempts.");
                break;
            }

            CleanupWebSocket();

            // Exponential backoff: min(maxDelay, baseDelay * 2^attempt) + jitter
            int delay = Math.Min(
                _config.MaxReconnectDelayMs,
                _config.ReconnectDelayMs * (1 << Math.Min(attempts, 10)));
            int jitter = _jitterRandom.Next(0, delay / 4); // 0-25% jitter
            delay += jitter;

            SetStatus(RelayStatus.Reconnecting, $"Reconnecting in {delay}ms (attempt {attempts})...");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        CleanupWebSocket();
        _ws = new ClientWebSocket();
        _ws.Options.HttpVersion = HttpVersion.Version11;
        _ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        string typeStr = _config.EndpointType == RelayEndpointType.StationSide
            ? "STATION_SIDE" : "REMOTE_SIDE";
        var token = _config.PairingToken.Trim();
        var uri = new Uri($"{_config.RelayUrl}?token={token}&type={typeStr}");

        // Custom handler with TCP_NODELAY for minimal latency
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true // Disable Nagle's algorithm for low latency
                };
                try
                {
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
            }
        };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await _ws.ConnectAsync(uri, new HttpMessageInvoker(handler), connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // On failure, probe the endpoint with plain HTTP to get the actual response body
            try
            {
                using var httpClient = new System.Net.Http.HttpClient(new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
                    }
                });
                var httpUri = uri.ToString().Replace("wss://", "https://");
                var resp = await httpClient.GetAsync(httpUri, connectCts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(connectCts.Token).ConfigureAwait(false);
                Error?.Invoke(this, $"HTTP probe {(int)resp.StatusCode}: {body}");
            }
            catch (Exception probeEx)
            {
                Error?.Invoke(this, $"HTTP probe failed: {probeEx.Message}");
            }

            throw;
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    // Accumulate fragments until EndOfMessage
                    messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        _lastRxTimestamp = DateTime.UtcNow;
                        var data = messageBuffer.ToArray();
                        messageBuffer.Clear();
                        HandleIncomingFrame(data);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    }

    private void HandleIncomingFrame(byte[] data)
    {
        if (!WireProtocol.TryDeserialize(data, out var frame, out var error))
        {
            Error?.Invoke(this, $"Invalid frame: {error}");
            return;
        }

        // Sequence gap detection for data frames
        if (!frame.Flags.HasFlag(FrameFlags.Control) && 
            !frame.Flags.HasFlag(FrameFlags.Heartbeat) &&
            !frame.Flags.HasFlag(FrameFlags.SessionOpen) &&
            !frame.Flags.HasFlag(FrameFlags.SessionClose))
        {
            if (_lastReceivedSeq > 0 && frame.SequenceNumber > _lastReceivedSeq + 1)
            {
                var gap = frame.SequenceNumber - _lastReceivedSeq - 1;
                Interlocked.Add(ref _droppedFrameCount, gap);
                Error?.Invoke(this, $"Sequence gap detected: dropped {gap} frame(s)");
            }
            _lastReceivedSeq = frame.SequenceNumber;
        }

        // Determine packet type from flags
        if (frame.Flags.HasFlag(FrameFlags.Control))
        {
            HandleControlFrame(frame);
        }
        else if (frame.Flags.HasFlag(FrameFlags.SessionClose))
        {
            SetStatus(RelayStatus.Disconnected, "Peer closed session");
        }
        else if (frame.Flags.HasFlag(FrameFlags.Heartbeat))
        {
            // Heartbeat response received - _lastRxTimestamp already updated
        }
        else
        {
            // DATA frame — deliver payload to the consumer
            if (frame.Payload.Length > 0)
            {
                DataReceived?.Invoke(this, frame.Payload);
            }
        }
    }

    private void HandleControlFrame(in WireFrame frame)
    {
        if (frame.Payload.Length < 1) return;

        byte controlCommand = frame.Payload[0];
        switch (controlCommand)
        {
            case 0x03: // PAIRED notification
                SetStatus(RelayStatus.Paired, "Session paired — relay active");
                break;
            // Flow control and other commands can be handled here in the future
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        var deadPeerThreshold = TimeSpan.FromMilliseconds(_config.HeartbeatIntervalMs * 3);

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct).ConfigureAwait(false);

                // Dead peer detection: if no data received for 3x heartbeat interval, reconnect
                var timeSinceLastRx = DateTime.UtcNow - _lastRxTimestamp;
                if (timeSinceLastRx > deadPeerThreshold)
                {
                    Error?.Invoke(this, $"Dead peer detected (no data for {timeSinceLastRx.TotalSeconds:F1}s)");
                    try { _ws?.Abort(); } catch { }
                    break;
                }

                if (_ws?.State == WebSocketState.Open)
                {
                    var frame = new WireFrame(
                        Version: 1,
                        Flags: FrameFlags.Heartbeat,
                        SessionId: 0,
                        SequenceNumber: 0,
                        Payload: Array.Empty<byte>());

                    var bytes = WireProtocol.Serialize(frame);
                    
                    // Heartbeats go through send queue like everything else
                    _sendQueue.Writer.TryWrite(bytes);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task SendSessionOpenFrameAsync(CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var frame = new WireFrame(
            Version: 1,
            Flags: FrameFlags.SessionOpen,
            SessionId: 0,
            SequenceNumber: 0,
            Payload: Array.Empty<byte>());

        var bytes = WireProtocol.Serialize(frame);
        
        // Session open is sent directly (before send pump starts)
        await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    private void SendSessionCloseFrameSync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var frame = new WireFrame(
            Version: 1,
            Flags: FrameFlags.SessionClose,
            SessionId: 0,
            SequenceNumber: 0,
            Payload: new byte[] { 0x00 }); // reason: normal close

        var bytes = WireProtocol.Serialize(frame);
        try
        {
            _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None)
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    private void CleanupWebSocket()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    private void SetStatus(RelayStatus status, string? message = null)
    {
        _status = status;
        StatusChanged?.Invoke(this, new RelayStatusEventArgs { Status = status, Message = message });
    }
}
