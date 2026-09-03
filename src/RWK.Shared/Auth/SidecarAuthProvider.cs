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
    public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged
    {
        // Forward the host-owned poller's state-change events so the wizard can
        // subscribe here instead of running its own poll timer (Requirements 2.1, 2.2).
        // Task 3.2 finalizes the read-through provider; this minimal forwarding keeps
        // the single-source-of-truth semantics intact.
        add => _host.StateChanged += value;
        remove => _host.StateChanged -= value;
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
