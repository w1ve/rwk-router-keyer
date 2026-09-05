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

namespace RWK.Station.Controllers;

/// <summary>
/// Owns the registration of the Station's control-channel inbound forward
/// (<c>tailnet:port → 127.0.0.1:port</c>) on the Tailscale sidecar.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to fix the field bug where a single transient sidecar failure at arm time
/// left the Station believing the control forward was registered while nothing listened on the
/// tailnet control port. The prior code in <c>StationController</c> set its
/// <c>_inboundForwardRegistered</c> latch to <c>true</c> <b>before</b> calling the sidecar and
/// swallowed every exception with no retry.
/// </para>
/// <para>
/// This registrar guarantees three invariants:
/// <list type="number">
///   <item><b>Latch on success only.</b> <see cref="IsRegistered"/> becomes <c>true</c> only after
///     a <see cref="ITsnetSidecarHost.CreateInboundForwardAsync"/> call returns without throwing.</item>
///   <item><b>Bounded retry.</b> Transient failures are retried up to <see cref="MaxAttempts"/>
///     times with a delay between attempts, instead of being swallowed on the first failure.</item>
///   <item><b>Idempotency.</b> Once registered, a subsequent <see cref="TryRegisterAsync"/> returns
///     success immediately without posting again, so a retry or operator re-attempt cannot create a
///     duplicate forward.</item>
/// </list>
/// </para>
/// <para>
/// It is self-contained (depends only on <see cref="ITsnetSidecarHost"/>, a diagnostics callback,
/// and an injectable delay function) so it can be unit-tested with a fake sidecar host, with no
/// hardware and no real tailnet.
/// </para>
/// </remarks>
internal sealed class ControlForwardRegistrar
{
    private readonly ITsnetSidecarHost _sidecar;
    private readonly int _tailnetPort;
    private readonly int _localPort;
    private readonly Action<string>? _diagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private bool _isRegistered;

    /// <summary>Maximum number of registration attempts per <see cref="TryRegisterAsync"/> call.</summary>
    public int MaxAttempts { get; }

    /// <summary>Base delay between retry attempts. Backoff multiplies this by the attempt index.</summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>
    /// Whether the control forward is currently registered on the sidecar. Set to <c>true</c> only
    /// after a successful sidecar call, and never before.
    /// </summary>
    public bool IsRegistered
    {
        get { lock (_gate) { return _isRegistered; } }
    }

    /// <summary>
    /// Creates a registrar bound to a sidecar host and the control port.
    /// </summary>
    /// <param name="sidecar">The sidecar host to register the inbound forward on.</param>
    /// <param name="tailnetPort">The tailnet port the sidecar should listen on.</param>
    /// <param name="localPort">The local port the sidecar should dial.</param>
    /// <param name="diagnostics">Optional diagnostics sink for human-readable log lines.</param>
    /// <param name="maxAttempts">Maximum attempts per registration call (default 5).</param>
    /// <param name="retryDelay">Base delay between attempts (default 2s). Backoff scales it linearly.</param>
    /// <param name="delay">
    /// Injectable delay function (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
    /// Tests inject an instant no-op so retries do not sleep.
    /// </param>
    public ControlForwardRegistrar(
        ITsnetSidecarHost sidecar,
        int tailnetPort,
        int localPort,
        Action<string>? diagnostics = null,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");

        _sidecar = sidecar;
        _tailnetPort = tailnetPort;
        _localPort = localPort;
        _diagnostics = diagnostics;
        MaxAttempts = maxAttempts;
        RetryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Attempts to register the control forward, retrying transient failures with bounded backoff.
    /// Idempotent: if already registered, returns success without contacting the sidecar again.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ControlForwardRegistrationResult"/> describing whether the forward is now
    /// registered and, on failure, the reason of the last attempt.
    /// </returns>
    public async Task<ControlForwardRegistrationResult> TryRegisterAsync(CancellationToken cancellationToken = default)
    {
        // Idempotency: never post a duplicate forward once registered.
        if (IsRegistered)
            return ControlForwardRegistrationResult.Success();

        string? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _sidecar.CreateInboundForwardAsync(_tailnetPort, _localPort, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // Latch ONLY after a successful call.
                lock (_gate) { _isRegistered = true; }
                _diagnostics?.Invoke(
                    $"\u2713 Inbound forward OK: tailnet:{_tailnetPort} \u2192 localhost:{_localPort} (attempt {attempt}/{MaxAttempts}).");
                return ControlForwardRegistrationResult.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                _diagnostics?.Invoke(
                    $"\u2717 Inbound forward attempt {attempt}/{MaxAttempts} failed: {lastError}");

                if (attempt < MaxAttempts)
                {
                    // Linear backoff: attempt 1 waits 1x, attempt 2 waits 2x, ...
                    var wait = TimeSpan.FromTicks(RetryDelay.Ticks * attempt);
                    try
                    {
                        await _delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        _diagnostics?.Invoke(
            $"\u2717 Inbound forward registration FAILED after {MaxAttempts} attempts: {lastError}");
        return ControlForwardRegistrationResult.Failure(lastError ?? "unknown error");
    }

    /// <summary>
    /// Resets the registration latch. Called when the sidecar/session tears down so a fresh
    /// arm cycle re-registers.
    /// </summary>
    public void Reset()
    {
        lock (_gate) { _isRegistered = false; }
    }
}

/// <summary>
/// Outcome of a <see cref="ControlForwardRegistrar.TryRegisterAsync"/> call.
/// </summary>
/// <param name="Registered">Whether the control forward is registered.</param>
/// <param name="Error">The last failure reason when <paramref name="Registered"/> is false.</param>
internal readonly record struct ControlForwardRegistrationResult(bool Registered, string? Error)
{
    /// <summary>Creates a successful result.</summary>
    public static ControlForwardRegistrationResult Success() => new(true, null);

    /// <summary>Creates a failed result carrying the last error reason.</summary>
    public static ControlForwardRegistrationResult Failure(string error) => new(false, error);
}
