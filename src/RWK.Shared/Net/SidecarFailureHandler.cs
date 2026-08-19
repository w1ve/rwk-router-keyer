using System.Diagnostics;

namespace RWK.Shared.Net;

/// <summary>
/// Encapsulates the asymmetric sidecar-failure behaviour defined by requirements
/// 16.9–16.12, 4.7, and 8.7 (design Error Scenario 9, task 14.9).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Every failure message names the resolved path verbatim plus the reason.</item>
///   <item>The condition is kept displayed in the UI, distinct from ordinary Disconnected.</item>
///   <item>Retries periodically and re-runs resolution each time.</item>
///   <item><b>Client:</b> completes startup, keeps paddle/keyer/sidetone usable for practice;
///     only tailnet-dependent operations fail.</item>
///   <item><b>Station:</b> refuses to arm, stays out of armed state, leaves all output lines
///     de-asserted.</item>
/// </list>
/// <para>
/// This class is consumed by the ClientController and StationController (tasks 22/23).
/// It holds the policy logic as pure methods/state so the controllers delegate to it.
/// </para>
/// <para>
/// _Requirements: 16.9, 16.10, 16.11, 16.12, 4.7, 8.7_
/// </para>
/// </remarks>
public sealed class SidecarFailureHandler : IDisposable
{
    private readonly SidecarFailurePolicy _policy;
    private readonly TimeSpan _retryInterval;
    private CancellationTokenSource? _retryCts;
    private Task? _retryLoop;
    private SidecarFailure? _currentFailure;
    private bool _disposed;

    /// <summary>
    /// Creates a failure handler with the specified policy and retry interval.
    /// </summary>
    /// <param name="policy">Whether this is a Client or Station instance.</param>
    /// <param name="retryInterval">How often to retry sidecar resolution. Default 10 seconds.</param>
    public SidecarFailureHandler(SidecarFailurePolicy policy, TimeSpan? retryInterval = null)
    {
        _policy = policy;
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// The current failure state, or null when the sidecar is healthy.
    /// The UI displays this while non-null (16.10).
    /// </summary>
    public SidecarFailure? CurrentFailure => _currentFailure;

    /// <summary>
    /// Whether the sidecar is currently in a failure state.
    /// </summary>
    public bool IsInFailure => _currentFailure is not null;

    /// <summary>
    /// The policy governing degradation behavior.
    /// </summary>
    public SidecarFailurePolicy Policy => _policy;

    /// <summary>
    /// Raised when the failure state changes (enters failure or recovers).
    /// </summary>
    public event EventHandler<SidecarFailureStateChangedEventArgs>? FailureStateChanged;

    /// <summary>
    /// Raised when the handler wants to attempt a sidecar restart.
    /// The subscriber (controller) should call <see cref="ReportRecovery"/> on success.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Called when a sidecar failure is detected. Records the failure, fires the event,
    /// and starts the periodic retry loop.
    /// </summary>
    /// <param name="failure">The sidecar failure with path and reason.</param>
    public void ReportFailure(SidecarFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _currentFailure = failure;
        FailureStateChanged?.Invoke(this, new SidecarFailureStateChangedEventArgs(
            failure, IsRecovered: false));

        StartRetryLoop();
    }

    /// <summary>
    /// Called when the sidecar has been successfully started. Clears the failure,
    /// stops the retry loop, and fires the recovery event.
    /// </summary>
    public void ReportRecovery()
    {
        _currentFailure = null;
        StopRetryLoop();

        FailureStateChanged?.Invoke(this, new SidecarFailureStateChangedEventArgs(
            Failure: null, IsRecovered: true));
    }

    /// <summary>
    /// Returns the user-facing failure message including the verbatim resolved path
    /// and the reason for the failure (16.9).
    /// </summary>
    /// <param name="failure">The failure to format.</param>
    /// <returns>A message suitable for UI display.</returns>
    public static string FormatFailureMessage(SidecarFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return $"Tailscale sidecar failure at {failure.ResolvedPath}: {failure.Reason}";
    }

    /// <summary>
    /// Evaluates whether the Client may operate in degraded mode.
    /// Client completes startup and keeps paddle/keyer/sidetone usable;
    /// only tailnet ops fail (16.11, 4.7).
    /// </summary>
    /// <returns>
    /// A <see cref="ClientDegradation"/> describing which subsystems are usable.
    /// </returns>
    public ClientDegradation GetClientDegradation()
    {
        if (_policy != SidecarFailurePolicy.Client)
            throw new InvalidOperationException(
                "GetClientDegradation is only valid for Client policy.");

        return new ClientDegradation(
            PaddleUsable: true,
            KeyerUsable: true,
            SidetoneUsable: true,
            TailnetOperational: !IsInFailure,
            FailureMessage: _currentFailure is not null
                ? FormatFailureMessage(_currentFailure)
                : null);
    }

    /// <summary>
    /// Evaluates whether the Station may arm.
    /// Station refuses to arm while the sidecar is in failure; all output lines
    /// stay de-asserted (16.12, 8.7).
    /// </summary>
    /// <returns>
    /// A <see cref="StationArmPolicy"/> describing the arming decision.
    /// </returns>
    public StationArmPolicy GetStationArmPolicy()
    {
        if (_policy != SidecarFailurePolicy.Station)
            throw new InvalidOperationException(
                "GetStationArmPolicy is only valid for Station policy.");

        return new StationArmPolicy(
            MayArm: !IsInFailure,
            AllLinesDeaserted: IsInFailure,
            FailureMessage: _currentFailure is not null
                ? FormatFailureMessage(_currentFailure)
                : null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRetryLoop();
    }

    // ---- Private retry loop ----

    private void StartRetryLoop()
    {
        if (_retryCts is not null) return; // already running

        _retryCts = new CancellationTokenSource();
        _retryLoop = RetryLoopAsync(_retryCts.Token);
    }

    private void StopRetryLoop()
    {
        if (_retryCts is null) return;

        _retryCts.Cancel();
        _retryCts.Dispose();
        _retryCts = null;
        _retryLoop = null;
    }

    private async Task RetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_retryInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_currentFailure is null)
                break; // Recovered externally.

            // Re-request a retry. The subscriber will re-run resolution and re-attempt
            // the sidecar launch. If it succeeds, they call ReportRecovery().
            RetryRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Which application is hosting this failure handler.
/// </summary>
public enum SidecarFailurePolicy
{
    /// <summary>
    /// Client: complete startup, keep local practice usable. Only tailnet ops fail (16.11, 4.7).
    /// </summary>
    Client,

    /// <summary>
    /// Station: refuse to arm, stay out of armed state, leave all lines de-asserted (16.12, 8.7).
    /// </summary>
    Station
}

/// <summary>
/// Describes the Client's degradation state when the sidecar is unavailable.
/// </summary>
/// <param name="PaddleUsable">Paddle input polling continues to work.</param>
/// <param name="KeyerUsable">SoftKeyer/WinKeyer core continues to work.</param>
/// <param name="SidetoneUsable">Local sidetone continues to work (4.7).</param>
/// <param name="TailnetOperational">Whether tailnet-dependent operations are available.</param>
/// <param name="FailureMessage">The current failure message for UI display, or null if healthy.</param>
public record ClientDegradation(
    bool PaddleUsable,
    bool KeyerUsable,
    bool SidetoneUsable,
    bool TailnetOperational,
    string? FailureMessage);

/// <summary>
/// Describes the Station's arming policy when the sidecar is unavailable.
/// </summary>
/// <param name="MayArm">Whether the Station is permitted to enter armed state.</param>
/// <param name="AllLinesDeaserted">Whether all key/PTT lines must be de-asserted.</param>
/// <param name="FailureMessage">The current failure message for UI display, or null if healthy.</param>
public record StationArmPolicy(
    bool MayArm,
    bool AllLinesDeaserted,
    string? FailureMessage);

/// <summary>
/// Event args for sidecar failure state transitions.
/// </summary>
/// <param name="Failure">The current failure, or null on recovery.</param>
/// <param name="IsRecovered">True when transitioning from failure to healthy.</param>
public record SidecarFailureStateChangedEventArgs(
    SidecarFailure? Failure,
    bool IsRecovered);
