using System.Net;
using System.Net.Sockets;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.IO;

/// <summary>
/// Implements ICommandSource and ICommandSink over UDP.
/// Listens for incoming WinKeyer command datagrams and sends
/// response bytes back to the last sender.
/// </summary>
public sealed class UdpCommandSource : ICommandSource, ICommandSink
{
    private UdpClient? _client;
    private IPEndPoint? _lastSender;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>
    /// Raised when a socket error occurs (e.g., bind failure).
    /// </summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Starts listening on the specified endpoint.
    /// </summary>
    /// <param name="endpoint">The local IP endpoint to bind to.</param>
    public void Start(IPEndPoint endpoint)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UdpCommandSource));
        if (_client is not null) throw new InvalidOperationException("Source is already running.");

        try
        {
            _client = new UdpClient(endpoint);
        }
        catch (SocketException ex)
        {
            Error?.Invoke(this, $"Failed to bind UDP to {endpoint}: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    /// <inheritdoc/>
    public void Start()
    {
        throw new InvalidOperationException("Use Start(IPEndPoint endpoint) to specify the bind address.");
    }

    /// <inheritdoc/>
    public void SendResponse(byte[] data)
    {
        if (_lastSender is null || _client is null) return;

        try
        {
            _client.Send(data, data.Length, _lastSender);
        }
        catch (SocketException)
        {
            // Best effort; sender may have disconnected
        }
        catch (ObjectDisposedException)
        {
            // Client was disposed
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _client?.Close();
        }
        catch
        {
            // Best effort
        }

        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Task was cancelled
        }

        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _lastSender = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client!.ReceiveAsync(ct);
                _lastSender = result.RemoteEndPoint;
                DataReceived?.Invoke(this, result.Buffer);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
                break;
            }
            catch (SocketException ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Error?.Invoke(this, $"UDP receive error: {ex.Message}");
                }
                break;
            }
            catch (ObjectDisposedException)
            {
                // Client was disposed during receive
                break;
            }
        }
    }
}
