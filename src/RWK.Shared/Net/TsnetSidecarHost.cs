/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWK.Shared.Net;

/// <summary>
/// Resolves, launches, and supervises the Tailscale sidecar child process. Both Client
/// and Station consume this class to back their <see cref="ITailscaleNode"/> via
/// <see cref="TailscaleNode"/>.
/// </summary>
/// <remarks>
/// Design Component 13. Implements the IPC contract documented in the sidecar README:
/// <list type="bullet">
///   <item>Resolve path via <see cref="SidecarPath"/> (task 14.3)</item>
///   <item>Launch as child process with redirected stdin/stdout/stderr</item>
///   <item>Parse one JSON handshake line from stdout</item>
///   <item>Keep stdin open as parent-death signal</item>
///   <item>Poll GET /v1/status every 2s with X-RWK-Token header</item>
///   <item>Ordered shutdown: POST /v1/stop, release stdin, wait, kill</item>
/// </list>
/// <para>
/// _Requirements: 16.5, 16.7, 16.9, 16.10, 5.3, 5.4, 5.5, 5.8, 7.1, 9.9_
/// </para>
/// </remarks>
public sealed class TsnetSidecarHost : ITsnetSidecarHost
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Constants
    // ──────────────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(3);
    private const int ExpectedProtocolVersion = 1;

    // ──────────────────────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────────────────────

    private Process? _sidecarProcess;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;
    private TailscaleState _state = TailscaleState.Disconnected;
    private string? _peerAddress;
    private string? _selfAddress;
    private string? _selfDnsName;
    private PathType _currentPath = PathType.None;
    private double _roundTripMs = -1;
    private string? _derpRegion;
    private string _edgeTransport = "udp";
    private string _jitterProfile = "PathAdaptive";
    private IPEndPoint _edgeLocalEndpoint = new(IPAddress.Loopback, 0);
    private string _apiBaseAddress = string.Empty;
    private string _token = string.Empty;
    private string _hostname = "rwk-node";
    private SidecarFailure? _lastFailure;
    private string _resolvedPath = string.Empty;
    private bool _disposed;
    private string? _authUrl;

    // ──────────────────────────────────────────────────────────────────────────────
    //  ITsnetSidecarHost — Properties
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string ApiBaseAddress => _apiBaseAddress;

    /// <inheritdoc/>
    public string Token => _token;

    /// <inheritdoc/>
    public IPEndPoint EdgeLocalEndpoint => _edgeLocalEndpoint;

    /// <inheritdoc/>
    public string EdgeTransport => _edgeTransport;

    /// <inheritdoc/>
    public string JitterProfile => _jitterProfile;

    /// <summary>
    /// The hostname this sidecar presents to the tailnet. Must be unique per instance.
    /// Set before calling <see cref="StartAsync"/>.
    /// </summary>
    public string Hostname { get => _hostname; set => _hostname = value ?? "rwk-node"; }

    /// <summary>
    /// Fixed UDP port the sidecar binds on the tailnet for edge datagrams.
    /// Default 41373. Both Client and Station use the same port so each knows
    /// where to send edge data to the peer.
    /// </summary>
    public int EdgeTailnetPort { get; set; } = 41373;

    /// <inheritdoc/>
    public TailscaleState State => _state;

    /// <inheritdoc/>
    public string? PeerAddress => _peerAddress;

    /// <inheritdoc/>
    public string? SelfAddress => _selfAddress;

    /// <inheritdoc/>
    public string? SelfDnsName => _selfDnsName;

    /// <inheritdoc/>
    public PathType CurrentPath => _currentPath;

    /// <inheritdoc/>
    public double RoundTripMs => _roundTripMs;

    /// <inheritdoc/>
    public string? DerpRegion => _derpRegion;

    /// <summary>
    /// The resolved executable path that was attempted. Named verbatim in every
    /// failure message so the operator can see where the app looked (16.9).
    /// </summary>
    public string ResolvedExecutablePath => _resolvedPath;

    /// <summary>
    /// Non-null while the sidecar cannot be started or has been lost, carrying the
    /// specific cause for the UI to display (16.10).
    /// </summary>
    public SidecarFailure? LastFailure => _lastFailure;

    /// <inheritdoc/>
    public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public string? AuthUrl => _authUrl;

    /// <inheritdoc/>
    public event EventHandler<string>? AuthUrlAvailable;

    // ──────────────────────────────────────────────────────────────────────────────
    //  ITsnetSidecarHost — StartAsync
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Never throws. Failures are reported via <see cref="LastFailure"/> and
    /// <see cref="StateChanged"/> with <see cref="TailscaleState.Fault"/>.
    /// </remarks>
    public async Task StartAsync(string? authKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lastFailure = null;

        // 1. Resolve the sidecar path.
        string baseDir;
        try
        {
            baseDir = SidecarPath.GetBaseDirectory();
        }
        catch (InvalidOperationException ex)
        {
            _resolvedPath = "(could not determine base directory)";
            SetFailure(SidecarFailureKind.NotFound, ex.Message);
            return;
        }

        _resolvedPath = SidecarPath.Resolve(baseDir, SidecarPath.DefaultExecutableName);

        // 2. Verify file exists.
        if (!File.Exists(_resolvedPath))
        {
            SetFailure(SidecarFailureKind.NotFound,
                $"Tailscale sidecar not found at {_resolvedPath}. " +
                "Extract the whole archive and keep all three executables in one directory.");
            return;
        }

        // 3. Launch as child process.
        var psi = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            Arguments = $"-hostname {_hostname} -edge-tailnet-port {EdgeTailnetPort}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _sidecarProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetFailure(SidecarFailureKind.LaunchFailed,
                $"Failed to launch sidecar at {_resolvedPath}: {ex.Message}");
            return;
        }

        if (_sidecarProcess is null)
        {
            SetFailure(SidecarFailureKind.LaunchFailed,
                $"Process.Start returned null for {_resolvedPath}.");
            return;
        }

        // 4. Check for immediate exit (e.g., missing DLL, bad format).
        if (_sidecarProcess.HasExited)
        {
            string stderr = await ReadStderrAsync(_sidecarProcess).ConfigureAwait(false);
            SetFailure(SidecarFailureKind.LaunchFailed,
                $"Sidecar at {_resolvedPath} exited immediately (code {_sidecarProcess.ExitCode}). " +
                $"Stderr: {TruncateForMessage(stderr)}");
            CleanupProcess();
            return;
        }

        // 5. Read handshake: exactly one JSON line from stdout within the timeout.
        string? handshakeLine;
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(DefaultHandshakeTimeout);

            handshakeLine = await _sidecarProcess.StandardOutput.ReadLineAsync(handshakeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetFailure(SidecarFailureKind.HandshakeTimeout,
                $"Sidecar at {_resolvedPath} did not produce a handshake line within " +
                $"{DefaultHandshakeTimeout.TotalSeconds}s.");
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // Caller-requested cancellation; clean up silently.
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(handshakeLine))
        {
            // Process may have exited: check stderr.
            string stderr = _sidecarProcess.HasExited
                ? await ReadStderrAsync(_sidecarProcess).ConfigureAwait(false)
                : "stdout closed without a handshake line";

            SetFailure(SidecarFailureKind.HandshakeMalformed,
                $"Sidecar at {_resolvedPath} produced no handshake. {TruncateForMessage(stderr)}");
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }

        // 6. Parse handshake JSON.
        HandshakeJson? handshake;
        try
        {
            handshake = JsonSerializer.Deserialize(handshakeLine, SidecarJsonContext.Default.HandshakeJson);
        }
        catch (JsonException ex)
        {
            SetFailure(SidecarFailureKind.HandshakeMalformed,
                $"Sidecar at {_resolvedPath} produced invalid handshake JSON: {ex.Message}. " +
                $"Line: {TruncateForMessage(handshakeLine)}");
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }

        if (handshake is null || string.IsNullOrEmpty(handshake.ApiAddress) || string.IsNullOrEmpty(handshake.Token))
        {
            SetFailure(SidecarFailureKind.HandshakeMalformed,
                $"Sidecar at {_resolvedPath} handshake missing required fields (apiAddress, token). " +
                $"Line: {TruncateForMessage(handshakeLine)}");
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }

        if (handshake.Protocol != ExpectedProtocolVersion)
        {
            SetFailure(SidecarFailureKind.HandshakeMalformed,
                $"Sidecar at {_resolvedPath} reports protocol {handshake.Protocol}, " +
                $"expected {ExpectedProtocolVersion}. ProtocolMismatch.");
            await KillProcessAsync().ConfigureAwait(false);
            return;
        }

        // 7. Store handshake results.
        _apiBaseAddress = $"http://{handshake.ApiAddress}";
        _token = handshake.Token;
        _edgeTransport = handshake.EdgeTransport ?? "udp";

        if (!string.IsNullOrEmpty(handshake.EdgeLocalAddress) &&
            TryParseEndpoint(handshake.EdgeLocalAddress, out var edgeEp))
        {
            _edgeLocalEndpoint = edgeEp;
        }

        // 8. Set up HttpClient for status polling.
        _httpClient = new HttpClient { BaseAddress = new Uri(_apiBaseAddress) };
        _httpClient.DefaultRequestHeaders.Add("X-RWK-Token", _token);
        _httpClient.Timeout = TimeSpan.FromSeconds(5);

        // 9. Issue POST /v1/start to join the tailnet.
        // When authKey is provided, the sidecar uses it as a pre-auth key.
        // When authKey is null/empty, the sidecar starts WITHOUT a pre-auth key and
        // will emit an interactive login URL (authUrl) in its status document that
        // the user must open in a browser. Either way we must POST /v1/start to
        // trigger the tsnet server to begin the tailnet join.
        try
        {
            var startBody = new StringContent(
                JsonSerializer.Serialize(new AuthKeyRequest { AuthKey = authKey ?? "" }, SidecarJsonContext.Default.AuthKeyRequest),
                Encoding.UTF8,
                "application/json");
            using var startResponse = await _httpClient.PostAsync("/v1/start", startBody, cancellationToken)
                .ConfigureAwait(false);
            // 202 Accepted is expected; we poll /v1/status for the outcome.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the sidecar may already be joined from a previous session's
            // persisted state. We'll see the real state via status polling.
            Debug.WriteLine($"POST /v1/start failed (non-fatal): {ex.Message}");
        }

        // 10. Start status polling loop.
        TransitionState(string.IsNullOrEmpty(authKey) ? TailscaleState.NeedsAuth : TailscaleState.Connecting);
        _pollCts = new CancellationTokenSource();
        _pollLoop = PollStatusLoopAsync(_pollCts.Token);

        // 11. Monitor process exit.
        _sidecarProcess.EnableRaisingEvents = true;
        _sidecarProcess.Exited += OnProcessExited;

        // 12. Continuously read stderr to a log file for diagnostics.
        _ = ReadStderrContinuouslyAsync(_sidecarProcess);
    }

    private static async Task ReadStderrContinuouslyAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                IO.RotatingFileLog.Append("sidecar.log", line);
            }
        }
        catch { /* process gone */ }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  ITsnetSidecarHost — StopAsync
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Stop polling.
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync().ConfigureAwait(false);
            if (_pollLoop is not null)
            {
                try { await _pollLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _pollCts.Dispose();
            _pollCts = null;
            _pollLoop = null;
        }

        // 2. POST /v1/stop to request graceful shutdown.
        if (_httpClient is not null && _sidecarProcess is not null && !_sidecarProcess.HasExited)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var _ = await _httpClient.PostAsync("/v1/stop", null, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; we'll kill it below if it doesn't exit.
            }
        }

        // 3. Release stdin (parent-death signal).
        if (_sidecarProcess is not null)
        {
            try { _sidecarProcess.StandardInput.Close(); }
            catch { /* already closed or process gone */ }
        }

        // 4. Wait briefly for exit, then kill.
        if (_sidecarProcess is not null && !_sidecarProcess.HasExited)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ShutdownGracePeriod);
                await _sidecarProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Grace period expired; kill it.
            }

            if (!_sidecarProcess.HasExited)
            {
                try { _sidecarProcess.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
        }

        CleanupProcess();
        _httpClient?.Dispose();
        _httpClient = null;

        TransitionState(TailscaleState.Disconnected);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  ITsnetSidecarHost — CreateOutboundForwardAsync
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IPEndPoint> CreateOutboundForwardAsync(
        string peerAddress, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        var requestBody = JsonSerializer.Serialize(
            new ForwardRequest("out", peerAddress, port),
            SidecarJsonContext.Default.ForwardRequest);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/forwards", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var forwardResult = JsonSerializer.Deserialize(responseBody, SidecarJsonContext.Default.ForwardResponse);

        if (forwardResult is null || string.IsNullOrEmpty(forwardResult.ListenAddress))
        {
            throw new InvalidOperationException(
                "Sidecar returned an empty or missing listenAddress from POST /v1/forwards.");
        }

        if (!TryParseEndpoint(forwardResult.ListenAddress, out var endpoint))
        {
            throw new InvalidOperationException(
                $"Cannot parse listenAddress '{forwardResult.ListenAddress}' from sidecar forward response.");
        }

        return endpoint;
    }

    /// <summary>
    /// Creates an outbound UDP forward: the sidecar binds a loopback UDP socket and
    /// relays datagrams to the peer over the tailnet. Returns the loopback endpoint
    /// that the .NET UdpForwarder should send datagrams to.
    /// </summary>
    /// <param name="peerAddress">The peer's Tailscale address.</param>
    /// <param name="port">The UDP port on the peer to relay to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loopback endpoint to send datagrams to.</returns>
    public async Task<IPEndPoint> CreateOutboundUdpForwardAsync(
        string peerAddress, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        var requestBody = JsonSerializer.Serialize(
            new ForwardRequest("out-udp", peerAddress, port),
            SidecarJsonContext.Default.ForwardRequest);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/forwards", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var forwardResult = JsonSerializer.Deserialize(responseBody, SidecarJsonContext.Default.ForwardResponse);

        if (forwardResult is null || string.IsNullOrEmpty(forwardResult.ListenAddress))
        {
            throw new InvalidOperationException(
                "Sidecar returned an empty or missing listenAddress from POST /v1/forwards (out-udp).");
        }

        if (!TryParseEndpoint(forwardResult.ListenAddress, out var endpoint))
        {
            throw new InvalidOperationException(
                $"Cannot parse listenAddress '{forwardResult.ListenAddress}' from sidecar UDP forward response.");
        }

        return endpoint;
    }

    /// <summary>
    /// Creates an inbound forward: the sidecar listens on the tailnet at the given port
    /// and dials localhost on the given local port. Used by the Station to expose its
    /// SessionManager to incoming tailnet connections.
    /// </summary>
    public async Task CreateInboundForwardAsync(
        int tailnetPort, int localPort,
        string? targetAddress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        // The sidecar's BindAddress field in an inbound forward is the address it dials
        // on the Station side. Default is 127.0.0.1 (localhost). For Station-LAN hardware,
        // pass the device's IP here.
        string bindAddr = string.IsNullOrEmpty(targetAddress) ? "127.0.0.1" : targetAddress;

        var requestBody = JsonSerializer.Serialize(
            new ForwardRequest("in", "", tailnetPort, localPort) { BindAddr = bindAddr },
            SidecarJsonContext.Default.ForwardRequest);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/forwards", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates an inbound UDP forward: the sidecar listens on the tailnet at the given
    /// UDP port and relays datagrams to the target address and port. Used for UDP port
    /// forwarding to Station-LAN devices.
    /// </summary>
    public async Task CreateInboundUdpForwardAsync(
        int tailnetPort, int localPort,
        string? targetAddress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        string bindAddr = string.IsNullOrEmpty(targetAddress) ? "127.0.0.1" : targetAddress;

        var requestBody = JsonSerializer.Serialize(
            new ForwardRequest("in-udp", "", tailnetPort, localPort) { BindAddr = bindAddr },
            SidecarJsonContext.Default.ForwardRequest);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/forwards", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  ITsnetSidecarHost — SubmitAuthKeyAsync
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        if (string.IsNullOrWhiteSpace(authKey))
            throw new ArgumentException("Auth key cannot be null or empty.", nameof(authKey));

        var startBody = new StringContent(
            JsonSerializer.Serialize(new AuthKeyRequest { AuthKey = authKey }, SidecarJsonContext.Default.AuthKeyRequest),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.PostAsync("/v1/start", startBody, cancellationToken)
            .ConfigureAwait(false);
        // 202 Accepted is expected; status polling will pick up the state transition.
    }

    /// <inheritdoc/>
    public async Task RegisterEdgeCallbackAsync(string callbackAddress, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        var body = JsonSerializer.Serialize(
            new EdgeCallbackRequest { Address = callbackAddress },
            SidecarJsonContext.Default.EdgeCallbackRequest);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/edge/callback", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Configures the peer address for edge UDP forwarding via POST /v1/peer.
    /// The sidecar will forward edge datagrams received on its local socket to
    /// this peer over the tailnet.
    /// </summary>
    /// <param name="peerAddress">The peer's Tailscale IP address.</param>
    /// <param name="edgePort">The edge UDP port on the peer (0 = use default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetPeerAsync(string peerAddress, int edgePort = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_httpClient is null)
            throw new InvalidOperationException("Sidecar is not running. Call StartAsync first.");

        var body = JsonSerializer.Serialize(
            new PeerRequest { Address = peerAddress, EdgePort = edgePort },
            SidecarJsonContext.Default.PeerRequest);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/peer", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollCts?.Cancel();
        _pollCts?.Dispose();

        // Release stdin so the sidecar receives EOF and exits.
        try { _sidecarProcess?.StandardInput.Close(); }
        catch { /* already closed */ }

        // Give it a moment, then kill.
        if (_sidecarProcess is not null && !_sidecarProcess.HasExited)
        {
            if (!_sidecarProcess.WaitForExit(1000))
            {
                try { _sidecarProcess.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
        }

        CleanupProcess();
        _httpClient?.Dispose();
        _httpClient = null;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Status Polling
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task PollStatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DefaultPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PollStatusOnceAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PollStatusOnceAsync(CancellationToken ct)
    {
        if (_httpClient is null) return;

        try
        {
            using var response = await _httpClient.GetAsync("/v1/status", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize(body, SidecarJsonContext.Default.StatusJson);
            if (status is null) return;

            ApplyStatusUpdate(status);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Sidecar may be unavailable; surface as Fault if the process died.
            if (_sidecarProcess is null || _sidecarProcess.HasExited)
            {
                SetFailure(SidecarFailureKind.ExitedUnexpectedly,
                    $"Sidecar at {_resolvedPath} is no longer running.");
                TransitionState(TailscaleState.Fault);
            }
        }
    }

    private void ApplyStatusUpdate(StatusJson status)
    {
        // Map state string to enum.
        var newState = MapState(status.State);
        var newPath = MapPath(status.Path);
        var newRtt = status.RoundTripMs;
        var newDerp = status.DerpRegion;

        // Read edge transport and jitter profile from the edge sub-object.
        if (status.Edge is not null)
        {
            if (!string.IsNullOrEmpty(status.Edge.Transport))
                _edgeTransport = status.Edge.Transport;
            if (!string.IsNullOrEmpty(status.Edge.JitterProfile))
                _jitterProfile = status.Edge.JitterProfile;
        }

        _peerAddress = status.PeerAddress;
        _selfAddress = status.SelfAddress;
        _selfDnsName = status.SelfDnsName;
        var previousPath = _currentPath;
        var previousRtt = _roundTripMs;
        _currentPath = newPath;
        _roundTripMs = newRtt;
        _derpRegion = newDerp;

        // Handle authUrl: raise AuthUrlAvailable when it transitions from empty to non-empty.
        string? previousAuthUrl = _authUrl;
        _authUrl = status.AuthUrl;

        if (!string.IsNullOrEmpty(status.AuthUrl))
        {
            // Override state to NeedsAuth whenever the sidecar has a pending auth URL,
            // not just on the initial transition. This prevents the state from bouncing
            // to Disconnected on subsequent polls while the user hasn't yet authenticated.
            if (newState != TailscaleState.Connected)
                newState = TailscaleState.NeedsAuth;

            // Only fire the event on the initial transition (empty → non-empty).
            if (string.IsNullOrEmpty(previousAuthUrl))
            {
                AuthUrlAvailable?.Invoke(this, status.AuthUrl);
            }
        }

        // Clear authUrl-derived NeedsAuth when we actually become connected.
        if (newState == TailscaleState.Connected)
        {
            _authUrl = null;
        }

        if (newState != _state)
        {
            TransitionState(newState);
        }
        else if (newState == TailscaleState.Connected && (newPath != previousPath || Math.Abs(newRtt - previousRtt) > 0.5))
        {
            // Path or RTT changed while staying Connected — notify subscribers so UI updates.
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
                newState,
                _currentPath,
                _roundTripMs < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_roundTripMs),
                _derpRegion));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  State transitions
    // ──────────────────────────────────────────────────────────────────────────────

    private void TransitionState(TailscaleState newState)
    {
        var oldState = _state;
        _state = newState;

        if (oldState != newState)
        {
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
                newState,
                _currentPath,
                _roundTripMs < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_roundTripMs),
                _derpRegion,
                newState == TailscaleState.Fault ? _lastFailure?.Reason : null));
        }
    }

    private void SetFailure(SidecarFailureKind kind, string reason)
    {
        _lastFailure = new SidecarFailure(kind, _resolvedPath, reason);
        TransitionState(TailscaleState.Fault);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Process exit detection
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Surface lost sidecar as Fault so F9 fires (9.9).
        SetFailure(SidecarFailureKind.ExitedUnexpectedly,
            $"Sidecar at {_resolvedPath} exited unexpectedly " +
            $"(code {(_sidecarProcess?.ExitCode ?? -1)}).");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task KillProcessAsync()
    {
        if (_sidecarProcess is null) return;

        try { _sidecarProcess.StandardInput.Close(); }
        catch { /* ignore */ }

        if (!_sidecarProcess.HasExited)
        {
            try { _sidecarProcess.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            try { await _sidecarProcess.WaitForExitAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        CleanupProcess();
    }

    private void CleanupProcess()
    {
        if (_sidecarProcess is not null)
        {
            _sidecarProcess.Exited -= OnProcessExited;
            _sidecarProcess.Dispose();
            _sidecarProcess = null;
        }
    }

    private static async Task<string> ReadStderrAsync(Process process)
    {
        try
        {
            return await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return "(stderr unavailable)";
        }
    }

    private static string TruncateForMessage(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static bool TryParseEndpoint(string addressPort, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, 0);

        int lastColon = addressPort.LastIndexOf(':');
        if (lastColon <= 0) return false;

        string host = addressPort[..lastColon];
        string portStr = addressPort[(lastColon + 1)..];

        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (!int.TryParse(portStr, out int port) || port is < 0 or > 65535) return false;

        endpoint = new IPEndPoint(ip, port);
        return true;
    }

    private static TailscaleState MapState(string? state) => state switch
    {
        "Connected" => TailscaleState.Connected,
        "Connecting" => TailscaleState.Connecting,
        "Fault" => TailscaleState.Fault,
        "NeedsAuth" => TailscaleState.NeedsAuth,
        _ => TailscaleState.Disconnected
    };

    private static PathType MapPath(string? path) => path switch
    {
        "Direct" => PathType.Direct,
        "Derp" => PathType.Derp,
        _ => PathType.None
    };

    // ──────────────────────────────────────────────────────────────────────────────
    //  JSON DTOs (source-generated for AOT safety)
    // ──────────────────────────────────────────────────────────────────────────────

    internal sealed record HandshakeJson
    {
        [JsonPropertyName("protocol")]
        public int Protocol { get; init; }

        [JsonPropertyName("pid")]
        public int Pid { get; init; }

        [JsonPropertyName("apiAddress")]
        public string? ApiAddress { get; init; }

        [JsonPropertyName("token")]
        public string? Token { get; init; }

        [JsonPropertyName("edgeLocalAddress")]
        public string? EdgeLocalAddress { get; init; }

        [JsonPropertyName("edgeTransport")]
        public string? EdgeTransport { get; init; }
    }

    internal sealed record StatusJson
    {
        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("selfAddress")]
        public string? SelfAddress { get; init; }

        [JsonPropertyName("selfDnsName")]
        public string? SelfDnsName { get; init; }

        [JsonPropertyName("peerAddress")]
        public string? PeerAddress { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("roundTripMs")]
        public double RoundTripMs { get; init; } = -1;

        [JsonPropertyName("derpRegion")]
        public string? DerpRegion { get; init; }

        [JsonPropertyName("edge")]
        public EdgeStatusJson? Edge { get; init; }

        [JsonPropertyName("authUrl")]
        public string? AuthUrl { get; init; }
    }

    internal sealed record EdgeStatusJson
    {
        [JsonPropertyName("transport")]
        public string? Transport { get; init; }

        [JsonPropertyName("jitterProfile")]
        public string? JitterProfile { get; init; }
    }

    internal sealed record ForwardRequest(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("peerHost")] string PeerHost,
        [property: JsonPropertyName("tailnetPort")] int TailnetPort,
        [property: JsonPropertyName("localPort")] int LocalPort = 0)
    {
        [JsonPropertyName("bindAddress")]
        public string? BindAddr { get; init; }
    }

    internal sealed record ForwardResponse
    {
        [JsonPropertyName("listenAddress")]
        public string? ListenAddress { get; init; }
    }

    internal sealed record AuthKeyRequest
    {
        [JsonPropertyName("authKey")]
        public string? AuthKey { get; init; }
    }

    internal sealed record EdgeCallbackRequest
    {
        [JsonPropertyName("address")]
        public string? Address { get; init; }
    }

    internal sealed record PeerRequest
    {
        [JsonPropertyName("address")]
        public string? Address { get; init; }

        [JsonPropertyName("edgePort")]
        public int EdgePort { get; init; }
    }
}

/// <summary>
/// Describes why the sidecar could not be started or was lost, with enough context
/// for the operator to fix it (16.9, 16.10).
/// </summary>
/// <param name="Kind">The category of failure.</param>
/// <param name="ResolvedPath">The file path that was resolved and attempted.</param>
/// <param name="Reason">Human-readable description of what went wrong.</param>
public record SidecarFailure(SidecarFailureKind Kind, string ResolvedPath, string Reason);

/// <summary>
/// Categories of sidecar start/run failure.
/// </summary>
public enum SidecarFailureKind
{
    /// <summary>No file at the resolved path.</summary>
    NotFound,

    /// <summary>File present but OS could not execute it.</summary>
    NotExecutable,

    /// <summary>Process.Start or CreateProcess failed.</summary>
    LaunchFailed,

    /// <summary>No stdout line within the handshake timeout.</summary>
    HandshakeTimeout,

    /// <summary>First stdout line is not valid handshake JSON.</summary>
    HandshakeMalformed,

    /// <summary>Protocol field does not match expected version.</summary>
    ProtocolMismatch,

    /// <summary>Process died after handshake.</summary>
    ExitedUnexpectedly
}

/// <summary>
/// Source-generated JSON serialization context for sidecar IPC DTOs.
/// </summary>
[JsonSerializable(typeof(TsnetSidecarHost.HandshakeJson))]
[JsonSerializable(typeof(TsnetSidecarHost.StatusJson))]
[JsonSerializable(typeof(TsnetSidecarHost.EdgeStatusJson))]
[JsonSerializable(typeof(TsnetSidecarHost.ForwardRequest))]
[JsonSerializable(typeof(TsnetSidecarHost.ForwardResponse))]
[JsonSerializable(typeof(TsnetSidecarHost.AuthKeyRequest))]
[JsonSerializable(typeof(TsnetSidecarHost.EdgeCallbackRequest))]
[JsonSerializable(typeof(TsnetSidecarHost.PeerRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class SidecarJsonContext : JsonSerializerContext
{
}
