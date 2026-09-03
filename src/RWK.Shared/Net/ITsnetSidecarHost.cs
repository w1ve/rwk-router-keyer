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

namespace RWK.Shared.Net;

/// <summary>
/// Abstraction over the Tailscale sidecar child process. The sidecar host owns the
/// process lifecycle (launch, handshake, status polling, stdin keepalive, shutdown)
/// and surfaces the IPC endpoints and state that <see cref="TailscaleNode"/> needs to
/// implement <see cref="ITailscaleNode"/>.
/// </summary>
/// <remarks>
/// Task 14.7 (TsnetSidecarHost) will implement this interface. Task 14.2 defines it
/// as the consumption boundary so <see cref="TailscaleNode"/> stays a thin façade.
/// <para>
/// _Requirements: 5.6, 5.7, 5.8_
/// </para>
/// </remarks>
public interface ITsnetSidecarHost : IDisposable
{
    /// <summary>
    /// The HTTP API base address of the running sidecar (from the handshake line's
    /// <c>apiAddress</c> field), e.g. "http://127.0.0.1:52341".
    /// </summary>
    string ApiBaseAddress { get; }

    /// <summary>
    /// The shared authentication token for the X-RWK-Token header (from the handshake
    /// line's <c>token</c> field).
    /// </summary>
    string Token { get; }

    /// <summary>
    /// The loopback UDP endpoint the sidecar listens on for outbound edge datagrams
    /// (from the handshake line's <c>edgeLocalAddress</c> field), e.g. "127.0.0.1:52342".
    /// </summary>
    IPEndPoint EdgeLocalEndpoint { get; }

    /// <summary>
    /// The edge transport declared by the sidecar: "udp" or "tcp".
    /// </summary>
    string EdgeTransport { get; }

    /// <summary>
    /// The jitter profile declared by the sidecar's status document
    /// (<c>edge.jitterProfile</c>): "PathAdaptive" or "DerpClassOnly".
    /// </summary>
    string JitterProfile { get; }

    /// <summary>
    /// The current connection state derived from the sidecar's status document.
    /// </summary>
    TailscaleState State { get; }

    /// <summary>
    /// The peer's Tailscale address from the status document, or null when no peer is set.
    /// </summary>
    string? PeerAddress { get; }

    /// <summary>
    /// This node's own Tailscale IPv4 address from the status document, or null before joining.
    /// </summary>
    string? SelfAddress { get; }

    /// <summary>
    /// This node's own Tailscale DNS name from the status document, or null before joining.
    /// </summary>
    string? SelfDnsName { get; }

    /// <summary>
    /// The current path type from the status document's <c>path</c> field.
    /// </summary>
    PathType CurrentPath { get; }

    /// <summary>
    /// The most recently measured round-trip time in milliseconds from the status
    /// document's <c>roundTripMs</c> field. -1 when unmeasured.
    /// </summary>
    double RoundTripMs { get; }

    /// <summary>
    /// The DERP region identifier from the status document, or null/empty when not relayed.
    /// </summary>
    string? DerpRegion { get; }

    /// <summary>
    /// The interactive login URL from the sidecar's status document, or null when no
    /// interactive login is required (either already authenticated or using an auth key).
    /// </summary>
    string? AuthUrl { get; }

    /// <summary>
    /// Raised when the sidecar's status document transitions from no <c>authUrl</c> to a
    /// non-empty <c>authUrl</c>, indicating the sidecar is waiting for interactive browser
    /// login. The event argument is the login URL string.
    /// </summary>
    event EventHandler<string>? AuthUrlAvailable;

    /// <summary>
    /// Raised by the host when it observes a state transition in the polled status document.
    /// </summary>
    event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Starts the sidecar process and joins the tailnet.
    /// </summary>
    /// <param name="authKey">
    /// The Tailscale pre-auth key. If null or empty, the sidecar is launched without
    /// POSTing to /v1/start — it will wait in a NeedsAuth state and emit an interactive
    /// login URL via the status document's <c>authUrl</c> field.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StartAsync(string? authKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the sidecar process and leaves the tailnet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an outbound TCP forward via POST /v1/forwards and returns the local
    /// loopback endpoint the caller should connect a TcpClient to.
    /// </summary>
    /// <param name="peerAddress">The peer's Tailscale address to dial.</param>
    /// <param name="port">The port on the peer to dial.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loopback endpoint of the created forward's listen address.</returns>
    Task<IPEndPoint> CreateOutboundForwardAsync(string peerAddress, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an auth key to a running sidecar via POST /v1/start. Used as a fallback
    /// when the user chooses to paste an auth key instead of completing interactive login.
    /// </summary>
    /// <param name="authKey">The Tailscale pre-auth key to submit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers the loopback UDP endpoint where the sidecar should deliver inbound
    /// edge datagrams via POST /v1/edge/callback.
    /// </summary>
    /// <param name="callbackAddress">The loopback endpoint string, e.g. "127.0.0.1:51500".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterEdgeCallbackAsync(string callbackAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the peer address for edge UDP forwarding via POST /v1/peer.
    /// </summary>
    /// <param name="peerAddress">The peer's Tailscale IP address.</param>
    /// <param name="edgePort">The edge UDP port on the peer (0 = use default from handshake).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetPeerAsync(string peerAddress, int edgePort = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the configured edge peer via POST /v1/peer with an empty address, so the
    /// sidecar stops probing it. Used after a FAILED or abandoned pairing attempt: a peer
    /// is configured before the HMAC handshake, and if it were left set against a stale/dead
    /// station IP the sidecar would keep probing it, cross the fault threshold, and report
    /// Fault — dropping the link display even though the tailnet is healthy. Clearing the
    /// peer makes PeerConfigured=false so a failed pair can never fault the node.
    /// </summary>
    /// <remarks>
    /// Best-effort cleanup: this method never throws and never faults the link on a transient
    /// failure. A non-success response or transport error is swallowed (logged), because
    /// clearing the peer is cleanup that must not itself disrupt an otherwise healthy link.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearPeerAsync(CancellationToken cancellationToken = default);
}
