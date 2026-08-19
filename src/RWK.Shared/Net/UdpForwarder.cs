using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RWK.Shared.Net;

/// <summary>
/// UDP relay with NAT-style session tracking. Each unique sender endpoint gets its own
/// paired socket for forwarding to the destination. Reply datagrams received on the
/// session socket are routed back to the original sender, preserving datagram boundaries.
/// Sessions are evicted after 60 seconds of inactivity in either direction.
/// </summary>
/// <remarks>
/// _Requirements: 10.5, 10.6_
/// </remarks>
public sealed class UdpForwarder : IDisposable
{
    private readonly UdpClient _listener;
    private readonly IPEndPoint _destination;
    private readonly Guid _ruleId;
    private readonly PortForwardManager _manager;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<IPEndPoint, UdpSession> _sessions = new();
    private readonly Timer _scavengeTimer;
    private bool _disposed;

    /// <summary>Default idle timeout before a session is evicted (60 seconds).</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the idle timeout used by this forwarder. Exposed for testing purposes.
    /// </summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>
    /// Gets the current number of active sessions.
    /// </summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Creates a new UDP forwarder that relays between the listener and the destination.
    /// </summary>
    /// <param name="listener">The UdpClient bound to the Client port.</param>
    /// <param name="destination">The Station-side endpoint to forward to.</param>
    /// <param name="ruleId">The rule identifier for byte tracking.</param>
    /// <param name="manager">The PortForwardManager for byte counter updates.</param>
    /// <param name="idleTimeout">
    /// Override the default 60-second idle timeout. Pass null for the default.
    /// </param>
    public UdpForwarder(
        UdpClient listener,
        IPEndPoint destination,
        Guid ruleId,
        PortForwardManager manager,
        TimeSpan? idleTimeout = null)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _ruleId = ruleId;
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _cts = new CancellationTokenSource();
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;

        // Scavenge idle sessions every 15 seconds.
        _scavengeTimer = new Timer(ScavengeSessions, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Starts the receive loop. Call once after construction.
    /// </summary>
    public Task RunAsync(CancellationToken externalCt = default)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt);
        return ReceiveLoopAsync(linked.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
                IPEndPoint sender = result.RemoteEndPoint;
                byte[] data = result.Buffer;

                // Track outbound bytes (Client → Station direction).
                _manager.AddBytesOut(_ruleId, data.Length);

                UdpSession session = _sessions.GetOrAdd(sender, ep => CreateSession(ep, ct));
                session.Touch();

                // Forward datagram to destination, preserving boundaries (one send = one datagram).
                try
                {
                    await session.Socket.SendAsync(data, data.Length, _destination).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Session was evicted concurrently; remove and retry on next packet.
                    _sessions.TryRemove(sender, out _);
                }
                catch (SocketException)
                {
                    // Transient socket error — drop this datagram silently.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Listener was disposed.
        }
        catch (SocketException)
        {
            // Listener socket error.
        }
    }

    private UdpSession CreateSession(IPEndPoint sender, CancellationToken ct)
    {
        // Create a new UdpClient for this session. Bind to any available port.
        var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var session = new UdpSession(sender, socket);

        // Start the reply receive loop for this session.
        _ = ReceiveReplyLoopAsync(session, ct);
        return session;
    }

    private async Task ReceiveReplyLoopAsync(UdpSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !session.IsDisposed)
            {
                UdpReceiveResult reply = await session.Socket.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] data = reply.Buffer;

                session.Touch();

                // Track inbound bytes (Station → Client direction).
                _manager.AddBytesIn(_ruleId, data.Length);

                // Forward reply back to the original sender, preserving datagram boundaries.
                try
                {
                    await _listener.SendAsync(data, data.Length, session.OriginalSender).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Transient error — drop reply silently.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Session socket was disposed (eviction or shutdown).
        }
        catch (SocketException)
        {
            // Socket error — session is dead, let scavenger clean it up.
        }
    }

    private void ScavengeSessions(object? state)
    {
        DateTime cutoff = DateTime.UtcNow - IdleTimeout;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                if (_sessions.TryRemove(kvp.Key, out UdpSession? session))
                {
                    session.Dispose();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _scavengeTimer.Dispose();

        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out UdpSession? session))
            {
                session.Dispose();
            }
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Represents a NAT-style UDP session: one sender mapped to one paired socket.
    /// </summary>
    internal sealed class UdpSession : IDisposable
    {
        private long _lastActivityTicks;

        public UdpSession(IPEndPoint originalSender, UdpClient socket)
        {
            OriginalSender = originalSender;
            Socket = socket;
            _lastActivityTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>The original sender's endpoint (the session key).</summary>
        public IPEndPoint OriginalSender { get; }

        /// <summary>The paired socket used to communicate with the destination.</summary>
        public UdpClient Socket { get; }

        /// <summary>Whether this session's socket has been disposed.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Gets the last activity time (UTC).</summary>
        public DateTime LastActivity
            => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        /// <summary>Updates the last activity timestamp (called on every send or receive).</summary>
        public void Touch()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            try { Socket.Close(); } catch { /* best-effort */ }
            Socket.Dispose();
        }
    }
}
