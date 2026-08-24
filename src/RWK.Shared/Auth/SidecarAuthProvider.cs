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
    /// cached state and applies an additional heuristic: if the state is NeedsAuth but
    /// the AuthUrl is now null/empty, auth has completed and we're effectively Connecting.
    /// This covers the timing window where the host hasn't yet transitioned its state enum.
    /// </remarks>
    public Task<TailscaleState> PollStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = _host.State;

        // Heuristic: if the host reports NeedsAuth but the auth URL has been cleared,
        // the browser login succeeded and the sidecar is transitioning. Report as
        // Connecting so the wizard advances past the BrowserAuth step.
        if (state == TailscaleState.NeedsAuth && string.IsNullOrEmpty(_host.AuthUrl))
            state = TailscaleState.Connecting;

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
