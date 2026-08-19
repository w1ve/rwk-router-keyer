using System.Net.Sockets;

namespace RWK.Shared.Net;

/// <summary>
/// Relays data bidirectionally between a local TCP client and a remote stream,
/// with half-close propagation and byte tracking.
/// </summary>
/// <remarks>
/// Each accepted TCP connection spawns one <see cref="TcpForwarder"/> instance.
/// The forwarder runs two concurrent pump tasks — one for each direction — and
/// propagates half-close (FIN) from one side to the other without tearing down
/// the reverse direction (requirement 10.4).
/// <para>
/// Buffer size is 64 KB per direction. Byte counters are reported via delegates
/// so the owning <see cref="PortForwardManager"/> can aggregate totals per rule.
/// </para>
/// _Requirements: 10.2, 10.3, 10.4_
/// </remarks>
internal sealed class TcpForwarder : IDisposable
{
    /// <summary>Buffer size per direction (64 KB).</summary>
    internal const int BufferSize = 65536;

    private readonly TcpClient _client;
    private readonly Stream _remoteStream;
    private readonly Action<long> _addBytesIn;
    private readonly Action<long> _addBytesOut;
    private readonly CancellationTokenSource _cts;
    private readonly Task _relayTask;
    private bool _disposed;

    /// <summary>
    /// Creates a forwarder and immediately starts the bidirectional relay pump.
    /// </summary>
    /// <param name="client">The accepted TCP client from the local listener.</param>
    /// <param name="remoteStream">The stream connected to the Station-side target.</param>
    /// <param name="addBytesIn">Callback to report bytes flowing from remote to client (toward Client).</param>
    /// <param name="addBytesOut">Callback to report bytes flowing from client to remote (toward Station).</param>
    /// <param name="linkedToken">A token tied to the rule's lifetime; cancellation tears down the connection.</param>
    public TcpForwarder(
        TcpClient client,
        Stream remoteStream,
        Action<long> addBytesIn,
        Action<long> addBytesOut,
        CancellationToken linkedToken)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _remoteStream = remoteStream ?? throw new ArgumentNullException(nameof(remoteStream));
        _addBytesIn = addBytesIn ?? throw new ArgumentNullException(nameof(addBytesIn));
        _addBytesOut = addBytesOut ?? throw new ArgumentNullException(nameof(addBytesOut));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        _relayTask = RunRelayAsync(_cts.Token);
    }

    /// <summary>
    /// The task representing the entire relay lifetime. Completes when both directions
    /// have finished or the token is cancelled.
    /// </summary>
    public Task Completion => _relayTask;

    private async Task RunRelayAsync(CancellationToken ct)
    {
        NetworkStream localStream = _client.GetStream();
        Socket localSocket = _client.Client;

        try
        {
            // Outbound: client → remote. On client FIN, shutdown remote's send side.
            Task outbound = PumpAsync(
                localStream, _remoteStream,
                () => ShutdownRemoteSend(_remoteStream),
                _addBytesOut, ct);

            // Inbound: remote → client. On remote FIN, shutdown local socket's send side.
            Task inbound = PumpAsync(
                _remoteStream, localStream,
                () => ShutdownLocalSend(localSocket),
                _addBytesIn, ct);

            await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal cancellation from rule stop/dispose.
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// Pumps bytes from <paramref name="source"/> to <paramref name="destination"/>.
    /// When the read side returns 0 (FIN/EOF), calls <paramref name="onReadEof"/> to
    /// propagate half-close to the other endpoint.
    /// </summary>
    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        Action onReadEof,
        Action<long> reportBytes,
        CancellationToken ct)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // Source sent FIN — propagate half-close.
                    onReadEof();
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                reportBytes(bytesRead);
            }
        }
        catch (IOException)
        {
            // Connection reset or broken pipe — normal for TCP relay teardown.
        }
        catch (SocketException)
        {
            // Socket-level error during read/write.
        }
        catch (ObjectDisposedException)
        {
            // Stream was disposed by the other direction or by disposal.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal cancellation.
        }
    }

    /// <summary>
    /// Shuts down the send direction of the remote stream. For NetworkStream-backed
    /// connections, this sends a TCP FIN. For generic streams (e.g., Tailscale tunnel),
    /// half-close is not supported at the stream level; the remote will see EOF when
    /// the stream is disposed after both pumps complete.
    /// </summary>
    private static void ShutdownRemoteSend(Stream remoteStream)
    {
        try
        {
            if (remoteStream is NetworkStream ns)
            {
                ns.Socket.Shutdown(SocketShutdown.Send);
            }
            // Generic streams don't support half-close; the pump exit is sufficient.
            // The remote side sees EOF when the relay completes and the stream is disposed.
        }
        catch (SocketException) { /* Already closed or reset */ }
        catch (ObjectDisposedException) { /* Already disposed */ }
    }

    /// <summary>
    /// Shuts down the send direction of the local client socket (propagates remote FIN
    /// to the local client as EOF on its read side).
    /// </summary>
    private static void ShutdownLocalSend(Socket localSocket)
    {
        try
        {
            localSocket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException) { /* Already closed or reset */ }
        catch (ObjectDisposedException) { /* Already disposed */ }
    }

    private void Cleanup()
    {
        try { _remoteStream.Dispose(); } catch { /* best-effort */ }
        try { _client.Dispose(); } catch { /* best-effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        Cleanup();
    }
}
