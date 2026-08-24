/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Auth;

/// <summary>
/// Abstraction over Tailscale authentication operations, consumed by the
/// <see cref="TailscaleAuthWizard"/>. This decouples the wizard from the
/// concrete sidecar host, enabling unit testing with a mock provider.
/// </summary>
/// <remarks>
/// Implementations wrap <see cref="Net.ITsnetSidecarHost"/> to expose only
/// the auth-relevant surface: current state, auth URL, self address, key submission.
/// </remarks>
public interface ITailscaleAuthProvider
{
    /// <summary>Current Tailscale connection state.</summary>
    TailscaleState CurrentState { get; }

    /// <summary>
    /// The interactive login URL the user must visit, or null if not in NeedsAuth state.
    /// </summary>
    string? AuthUrl { get; }

    /// <summary>
    /// This node's Tailscale IPv4 address once connected, or null before joining.
    /// </summary>
    string? SelfAddress { get; }

    /// <summary>
    /// This node's Tailscale DNS hostname once connected, or null before joining.
    /// </summary>
    string? SelfDnsName { get; }

    /// <summary>
    /// The tailnet name (e.g. "myuser.github") from the status document, or null.
    /// </summary>
    string? TailnetName { get; }

    /// <summary>
    /// Submits a pre-auth key to the sidecar. Used as a fallback when the user
    /// cannot complete interactive browser login.
    /// </summary>
    /// <param name="authKey">The Tailscale auth key to submit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls the sidecar for the latest status and returns the current state.
    /// The implementation should update <see cref="CurrentState"/>, <see cref="AuthUrl"/>,
    /// <see cref="SelfAddress"/>, and <see cref="SelfDnsName"/> as side effects.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current state after polling.</returns>
    Task<TailscaleState> PollStatusAsync(CancellationToken cancellationToken = default);
}
