using System.Net.WebSockets;

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

    /// <summary>Reconnect delay in milliseconds after a disconnect.</summary>
    public int ReconnectDelayMs { get; init; } = 3000;

    /// <summary>Maximum reconnect attempts before giving up (0 = infinite).</summary>
    public int MaxReconnectAttempts { get; init; } = 10;
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
/// </summary>
public sealed class CloudRelayTransport : IO.ICommandSource, IO.ICommandSink, IDisposable
{
    private readonly RelayConfig _config;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private bool _disposed;
    private uint _sequenceNumber;
    private RelayStatus _status = RelayStatus.Disconnected;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>Raised when the relay connection status changes.</summary>
    public event EventHandler<RelayStatusEventArgs>? StatusChanged;

    /// <summary>Raised when an error occurs.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Gets the current connection status.</summary>
    public RelayStatus Status => _status;

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

        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                // Send SESSION_CLOSE frame before disconnecting
                SendSessionCloseFrame();
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { }

        try { _receiveTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _heartbeatTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        CleanupWebSocket();
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _heartbeatTask = null;
        _sequenceNumber = 0;

        SetStatus(RelayStatus.Disconnected, "Stopped");
    }

    /// <inheritdoc/>
    public void SendResponse(byte[] data)
    {
        if (_ws?.State != WebSocketState.Open || _status != RelayStatus.Paired)
            return;

        try
        {
            var seq = Interlocked.Increment(ref _sequenceNumber);
            var frame = new WireFrame(
                Version: 1,
                Flags: FrameFlags.None,
                SessionId: 0,
                SequenceNumber: (uint)seq,
                Payload: data);

            var bytes = WireProtocol.Serialize(frame);
            _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None)
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Send failed: {ex.Message}");
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
                    SetStatus(RelayStatus.Connected, "Connected, waiting for pairing...");

                    // Send SESSION_OPEN frame
                    SendSessionOpenFrame();

                    // Start heartbeat
                    _heartbeatTask = Task.Run(() => HeartbeatLoop(ct), ct);

                    // Receive loop
                    await ReceiveLoop(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Connection error: {ex.Message}");
            }

            if (ct.IsCancellationRequested) break;

            // Reconnect logic
            attempts++;
            if (_config.MaxReconnectAttempts > 0 && attempts >= _config.MaxReconnectAttempts)
            {
                SetStatus(RelayStatus.Error, $"Failed after {attempts} reconnect attempts.");
                break;
            }

            CleanupWebSocket();
            SetStatus(RelayStatus.Reconnecting, $"Reconnecting (attempt {attempts})...");
            try { await Task.Delay(_config.ReconnectDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        CleanupWebSocket();
        _ws = new ClientWebSocket();

        string typeStr = _config.EndpointType == RelayEndpointType.StationSide
            ? "STATION_SIDE" : "REMOTE_SIDE";
        var uri = new Uri($"{_config.RelayUrl}?token={_config.PairingToken}&type={typeStr}");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));

        await _ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];

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
                    var data = new ReadOnlySpan<byte>(buffer, 0, result.Count);
                    HandleIncomingFrame(data);
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

    private void HandleIncomingFrame(ReadOnlySpan<byte> data)
    {
        if (!WireProtocol.TryDeserialize(data, out var frame, out var error))
        {
            Error?.Invoke(this, $"Invalid frame: {error}");
            return;
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
            // Heartbeat response — no action needed
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
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct).ConfigureAwait(false);

                if (_ws?.State == WebSocketState.Open)
                {
                    var frame = new WireFrame(
                        Version: 1,
                        Flags: FrameFlags.Heartbeat,
                        SessionId: 0,
                        SequenceNumber: 0,
                        Payload: Array.Empty<byte>());

                    var bytes = WireProtocol.Serialize(frame);
                    await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private void SendSessionOpenFrame()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var frame = new WireFrame(
            Version: 1,
            Flags: FrameFlags.SessionOpen,
            SessionId: 0,
            SequenceNumber: 0,
            Payload: Array.Empty<byte>());

        var bytes = WireProtocol.Serialize(frame);
        _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None)
            .Wait(TimeSpan.FromSeconds(5));
    }

    private void SendSessionCloseFrame()
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
