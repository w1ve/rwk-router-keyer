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
using RWK.Shared.Discovery;

namespace RWK.Client.Discovery;

/// <summary>
/// Receives discovery announcements from the Station (forwarded over the control channel),
/// rewrites the IP/port to the Client's local forward rule endpoint, and broadcasts the
/// rewritten packet on the Client's local network so SmartSDR discovers the radio.
/// </summary>
/// <remarks>
/// The emitter only broadcasts while enabled (ClientConfig.DiscoveryEmitEnabled). When
/// disabled, incoming announcements are discarded. The re-broadcast uses the limited
/// broadcast address (255.255.255.255) on port 4992 — same as the original radio would
/// have used on its own LAN.
/// <para>
/// _Requirements: 15.3, 15.4, 15.5, 15.8, 15.9, 15.15, 15.17_
/// </para>
/// </remarks>
public sealed class ClientDiscoveryEmitter : IDisposable
{
    private readonly IDiscoveryPayloadCodec _codec;
    private readonly Action<string>? _log;
    private UdpClient? _broadcastSocket;
    private IPEndPoint _broadcastTarget;
    private bool _enabled;
    private bool _disposed;

    /// <summary>The local endpoint used for rewriting (Client's forward rule bind address + port).</summary>
    public IPEndPoint? LocalEndpoint { get; set; }

    /// <summary>Whether re-emission is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value && _broadcastSocket is null)
                InitBroadcastSocket();
        }
    }

    /// <summary>
    /// Raised when a radio is advertised or its state changes, for UI display.
    /// </summary>
    public event EventHandler<DiscoveredRadio>? RadioAdvertised;

    public ClientDiscoveryEmitter(
        IDiscoveryPayloadCodec codec,
        string broadcastAddress = "255.255.255.255",
        int broadcastPort = FlexVitaDiscoveryCodec.DiscoveryPort,
        Action<string>? log = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _broadcastTarget = new IPEndPoint(IPAddress.Parse(broadcastAddress), broadcastPort);
        _log = log;
    }

    /// <summary>
    /// Processes a discovery announcement received from the Station. If enabled and a local
    /// endpoint is configured, rewrites the payload and broadcasts it on the Client's LAN.
    /// </summary>
    /// <param name="rawPayload">The verbatim discovery packet as captured at the Station.</param>
    public void OnDiscoveryAnnounce(byte[] rawPayload)
    {
        if (!_enabled || _disposed) return;

        if (LocalEndpoint is null)
        {
            _log?.Invoke("Discovery emitter: no local endpoint configured, discarding.");
            return;
        }

        if (!_codec.TryRewriteEndpoint(rawPayload, LocalEndpoint, out byte[] rewritten, out string? failureReason))
        {
            _log?.Invoke($"Discovery emitter: rewrite failed — {failureReason}");
            return;
        }

        // Parse the rewritten packet to extract radio info for UI
        if (_codec.TryParse(rewritten, out DiscoveredRadio radio, out _))
        {
            RadioAdvertised?.Invoke(this, radio);
        }

        // Broadcast the rewritten packet on the Client's local network
        try
        {
            _broadcastSocket?.Send(rewritten, rewritten.Length, _broadcastTarget);
        }
        catch (SocketException ex)
        {
            _log?.Invoke($"Discovery broadcast failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the session is lost — stops all advertising.
    /// </summary>
    public void OnSessionLost()
    {
        // Nothing to clean up per announcement — we're report-driven only (no timer)
        _log?.Invoke("Discovery emitter: session lost, broadcasts ceased.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _broadcastSocket?.Dispose();
        _broadcastSocket = null;
    }

    private void InitBroadcastSocket()
    {
        if (_broadcastSocket is not null) return;

        // Use an ephemeral source port — binding to 4992 conflicts with SmartSDR.
        _broadcastSocket = new UdpClient();
        _broadcastSocket.EnableBroadcast = true;
    }
}
