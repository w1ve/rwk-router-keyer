/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Net;

/// <summary>
/// Manages authenticated keying sessions from Clients (design Component 9).
/// </summary>
/// <remarks>
/// The Station allows at most one active session at a time. A new TCP control connection
/// is challenged with a random nonce; the Client must respond within 10 seconds with
/// HMAC-SHA256(nonce, pairing_secret). Valid auth establishes the session, wrong or late
/// responses close the connection.
/// <para>
/// _Requirements: 11.1–11.8_
/// </para>
/// </remarks>
public interface ISessionManager : IDisposable
{
    /// <summary>Raised when a session has been successfully authenticated and is now Active.</summary>
    event EventHandler<SessionEventArgs>? SessionStarted;

    /// <summary>Raised when a session ends for any reason (owner disconnect, fail-safe, client gone).</summary>
    event EventHandler<SessionEventArgs>? SessionEnded;

    /// <summary>
    /// Begins accepting connections on the specified TCP control port.
    /// </summary>
    /// <param name="controlPort">The TCP port to listen on for control connections.</param>
    void Start(int controlPort);

    /// <summary>
    /// Stops listening and disconnects the current session (if any).
    /// </summary>
    void Stop();

    /// <summary>The currently active session, or <see langword="null"/> if none.</summary>
    ActiveSession? CurrentSession { get; }

    /// <summary>
    /// Whether the manager will accept new session attempts. Setting to <see langword="false"/>
    /// closes the listener without disconnecting the current session.
    /// </summary>
    bool AcceptNewSessions { get; set; }

    /// <summary>
    /// Forcibly disconnects the current session (11.7). No-op if no session is active.
    /// </summary>
    void DisconnectSession();

    /// <summary>
    /// The monotonically increasing epoch counter. Incremented on each new authenticated session
    /// so that the EdgeSequenceTracker can detect stale frames (F4).
    /// </summary>
    ushort CurrentEpoch { get; }
}

/// <summary>
/// Snapshot of the currently active keying session.
/// </summary>
public record ActiveSession(
    string ClientAddress,
    string ClientName,
    DateTime StartedAtUtc,
    SessionState State
);
