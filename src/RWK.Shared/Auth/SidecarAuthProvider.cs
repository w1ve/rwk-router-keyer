/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Net;

namespace RWK.Shared.Auth;

/// <summary>
/// Adapts <see cref="ITsnetSidecarHost"/> to <see cref="ITailscaleAuthProvider"/>
/// for use by the TailscaleAuthWizard. Reads state directly from the sidecar host's
/// cached status properties (updated by its internal poll loop).
/// </summary>
public sealed class SidecarAuthProvider : ITailscaleAuthProvider
{
    private readonly ITsnetSidecarHost _host;
    private string? _lastAuthUrl;
    private int _staleAuthUrlPolls;

    /// <summary>
    /// After this many consecutive polls where the state is NeedsAuth with the same
    /// non-empty authUrl, assume the sidecar is lagging and report Connecting so the
    /// wizard advances. At 1.5s poll interval, 12 polls = ~18 seconds.
    /// </summary>
    private const int StaleAuthUrlThreshold = 12;

    public SidecarAuthProvider(ITsnetSidecarHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc/>
    public TailscaleState CurrentState => _host.State;

    /// <inheritdoc/>
    public string? AuthUrl => _host.AuthUrl;

    /// <inheritdoc/>
    public string? SelfAddress => _host.SelfAddress;

    /// <inheritdoc/>
    public string? SelfDnsName => _host.SelfDnsName;

    /// <inheritdoc/>
    public string? TailnetName => ExtractTailnetName(_host.SelfDnsName);

    /// <inheritdoc/>
    public async Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
    {
        await _host.SubmitAuthKeyAsync(authKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The sidecar host polls internally every 2 seconds. This method reads the latest
    /// cached state and applies heuristics:
    /// 1. If NeedsAuth but AuthUrl is cleared → report Connecting (auth completed, transitioning).
    /// 2. If NeedsAuth with same authUrl for too many polls → assume auth completed but sidecar
    ///    is lagging, report Connecting so the wizard isn't stuck forever.
    /// 3. If Connected → report Connected (sidecar confirmed).
    /// </remarks>
    public Task<TailscaleState> PollStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = _host.State;

        // If already connected, reset stale counter and return immediately.
        if (state == TailscaleState.Connected)
        {
            _staleAuthUrlPolls = 0;
            _lastAuthUrl = null;
            return Task.FromResult(state);
        }

        // Heuristic 1: NeedsAuth but authUrl cleared → auth succeeded, transitioning.
        if (state == TailscaleState.NeedsAuth && string.IsNullOrEmpty(_host.AuthUrl))
        {
            _staleAuthUrlPolls = 0;
            _lastAuthUrl = null;
            return Task.FromResult(TailscaleState.Connecting);
        }

        // Heuristic 2: NeedsAuth with a non-empty authUrl — track how long it's been stale.
        // After the user authenticates in the browser, the sidecar may take many seconds to
        // notice (its own control-plane poll + internal processing). If we've seen the same
        // authUrl for StaleAuthUrlThreshold consecutive polls, assume auth completed.
        if (state == TailscaleState.NeedsAuth && !string.IsNullOrEmpty(_host.AuthUrl))
        {
            if (_host.AuthUrl == _lastAuthUrl)
            {
                _staleAuthUrlPolls++;
                if (_staleAuthUrlPolls >= StaleAuthUrlThreshold)
                {
                    // Assume auth completed — the sidecar just hasn't caught up yet.
                    return Task.FromResult(TailscaleState.Connecting);
                }
            }
            else
            {
                // New authUrl — reset counter (fresh auth attempt).
                _lastAuthUrl = _host.AuthUrl;
                _staleAuthUrlPolls = 1;
            }
        }
        else
        {
            _staleAuthUrlPolls = 0;
        }

        return Task.FromResult(state);
    }

    /// <summary>
    /// Extracts the tailnet name from the DNS name (e.g. "myhost.tail12345.ts.net" → "tail12345").
    /// </summary>
    private static string? ExtractTailnetName(string? dnsName)
    {
        if (string.IsNullOrEmpty(dnsName)) return null;

        // Format: hostname.tailnet-name.ts.net
        var parts = dnsName.Split('.');
        if (parts.Length >= 3 && parts[^1] == "net" && parts[^2] == "ts")
            return parts[^3];

        return null;
    }
}
