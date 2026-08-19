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

namespace RWK.Station.Net;

/// <summary>
/// Station-side session manager: listens for TCP control connections, authenticates via
/// HMAC challenge/response, and enforces a single active session (design Component 9).
/// </summary>
/// <remarks>
/// _Requirements: 11.1–11.8_
/// </remarks>
public sealed class SessionManager : ISessionManager
{
    /// <summary>Length of the random challenge nonce in bytes (11.2).</summary>
    public const int NonceLength = 32;

    /// <summary>Length of the expected HMAC-SHA256 response in bytes.</summary>
    public const int HmacResponseLength = 32;

    /// <summary>Response sent to a Client when the Station already has an active session (11.6).</summary>
    private static readonly byte[] BusyResponse = "BUSY"u8.ToArray();

    /// <summary>Response sent when authentication succeeds (11.4).</summary>
    private static readonly byte[] OkResponse = "OK"u8.ToArray();

    /// <summary>Response sent when authentication fails (11.5).</summary>
    private static readonly byte[] FailResponse = "FAIL"u8.ToArray();

    private readonly byte[] _pairingSecret;
    private readonly TimeSpan _authTimeout;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    private ActiveSession? _currentSession;
    private TcpClient? _currentClient;
    private ushort _epoch;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<SessionEventArgs>? SessionStarted;

    /// <inheritdoc/>
    public event EventHandler<SessionEventArgs>? SessionEnded;

    /// <summary>
    /// Creates a new SessionManager.
    /// </summary>
    /// <param name="pairingSecret">
    /// The shared secret used for HMAC verification (plaintext at runtime, DPAPI at rest).
    /// </param>
    /// <param name="authTimeout">
    /// How long to wait for the Client's HMAC response before closing. Default 10 seconds (11.3).
    /// </param>
    public SessionManager(byte[] pairingSecret, TimeSpan? authTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(pairingSecret);
        if (pairingSecret.Length == 0)
            throw new ArgumentException("Pairing secret must not be empty.", nameof(pairingSecret));

        _pairingSecret = pairingSecret;
        _authTimeout = authTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc/>
    public ActiveSession? CurrentSession
    {
        get { lock (_gate) { return _currentSession; } }
    }

    /// <summary>
    /// Gets the NetworkStream of the current active session, or null if no session is active.
    /// Used by the StationController to read control messages (e.g. forward rules) from the Client.
    /// </summary>
    public NetworkStream? CurrentControlStream
    {
        get
        {
            lock (_gate)
            {
                return _currentClient?.Connected == true ? _currentClient.GetStream() : null;
            }
        }
    }

    /// <inheritdoc/>
    public bool AcceptNewSessions { get; set; } = true;

    /// <inheritdoc/>
    public ushort CurrentEpoch
    {
        get { lock (_gate) { return _epoch; } }
    }

    /// <inheritdoc/>
    public void Start(int controlPort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_listener is not null)
                throw new InvalidOperationException("SessionManager is already started.");

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, controlPort);
            _listener.Start();
            _acceptLoopTask = AcceptLoopAsync(_cts.Token);
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            loop = _acceptLoopTask;
            _listener = null;
            _cts = null;
            _acceptLoopTask = null;
        }

        cts?.Cancel();
        listener?.Stop();

        // Force-close the current session before awaiting the accept loop so the loop can exit.
        DisconnectSessionInternal("Station stopped");

        try { loop?.GetAwaiter().GetResult(); } catch { /* accept loop is expected to throw on cancel */ }
        cts?.Dispose();
    }

    /// <inheritdoc/>
    public void DisconnectSession()
    {
        DisconnectSessionInternal("Owner forced disconnect");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ----- Private helpers -----

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            // Handle connection off the accept path so we keep listening.
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        string remoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        try
        {
            // Single-session enforcement (11.6).
            if (!AcceptNewSessions || HasActiveSession())
            {
                await SendAndCloseAsync(client, BusyResponse, ct).ConfigureAwait(false);
                RaiseSessionEnded(remoteAddress, "unknown", SessionState.Closed,
                    "Rejected: session already active");
                return;
            }

            NetworkStream stream = client.GetStream();

            // Generate and send 32-byte nonce (11.2).
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            await stream.WriteAsync(nonce, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // Wait for HMAC response with timeout (11.3).
            byte[] response = new byte[HmacResponseLength];
            int totalRead = 0;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_authTimeout);

            try
            {
                while (totalRead < HmacResponseLength)
                {
                    int read = await stream.ReadAsync(
                        response.AsMemory(totalRead, HmacResponseLength - totalRead),
                        timeoutCts.Token).ConfigureAwait(false);

                    if (read == 0)
                    {
                        // Client disconnected before sending full response.
                        CloseClient(client);
                        return;
                    }

                    totalRead += read;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Auth timeout (11.5).
                await SendAndCloseAsync(client, FailResponse, ct).ConfigureAwait(false);
                RaiseSessionEnded(remoteAddress, "unknown", SessionState.Closed, "Auth timeout");
                return;
            }

            // Verify HMAC-SHA256(nonce, pairing_secret) with constant-time comparison (11.4).
            byte[] expected = HMACSHA256.HashData(_pairingSecret, nonce);
            if (!CryptographicOperations.FixedTimeEquals(expected, response))
            {
                // Invalid secret (11.5).
                await SendAndCloseAsync(client, FailResponse, ct).ConfigureAwait(false);
                RaiseSessionEnded(remoteAddress, "unknown", SessionState.Closed, "Invalid HMAC");
                return;
            }

            // Double-check single session: another connection may have completed auth in parallel.
            lock (_gate)
            {
                if (_currentSession is not null)
                {
                    _ = SendAndCloseAsync(client, BusyResponse, CancellationToken.None);
                    return;
                }

                // Increment epoch for the new session.
                _epoch++;

                _currentClient = client;
                _currentSession = new ActiveSession(
                    remoteAddress,
                    remoteAddress, // ClientName defaults to address; can be upgraded from control channel.
                    DateTime.UtcNow,
                    SessionState.Active);
            }

            // Send OK to confirm session (11.4).
            await stream.WriteAsync(OkResponse, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            RaiseSessionStarted(_currentSession);
        }
        catch (OperationCanceledException)
        {
            CloseClient(client);
        }
        catch (Exception)
        {
            CloseClient(client);
        }
    }

    private bool HasActiveSession()
    {
        lock (_gate)
        {
            return _currentSession is not null;
        }
    }

    private void DisconnectSessionInternal(string reason)
    {
        ActiveSession? session;
        TcpClient? client;

        lock (_gate)
        {
            session = _currentSession;
            client = _currentClient;
            _currentSession = null;
            _currentClient = null;
        }

        if (client is not null)
        {
            CloseClient(client);
        }

        if (session is not null)
        {
            RaiseSessionEnded(session.ClientAddress, session.ClientName, SessionState.Closed, reason);
        }
    }

    private static async Task SendAndCloseAsync(TcpClient client, byte[] data, CancellationToken ct)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { /* Best-effort send before close. */ }
        finally
        {
            CloseClient(client);
        }
    }

    private static void CloseClient(TcpClient client)
    {
        try { client.Close(); } catch { /* Swallow dispose errors. */ }
    }

    private void RaiseSessionStarted(ActiveSession session)
    {
        SessionStarted?.Invoke(this, new SessionEventArgs(
            session.ClientAddress,
            session.ClientName,
            SessionState.Active,
            DateTime.UtcNow));
    }

    private void RaiseSessionEnded(string address, string name, SessionState state, string reason)
    {
        SessionEnded?.Invoke(this, new SessionEventArgs(
            address,
            name,
            state,
            DateTime.UtcNow,
            reason));
    }
}
