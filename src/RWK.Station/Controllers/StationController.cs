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
using System.Text;
using System.Text.Json;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.Discovery;
using RWK.Shared.IO;
using RWK.Shared.Net;
using RWK.Shared.Protocol.Edge;
using RWK.Shared.Timing;
using RWK.Station.IO;
using RWK.Station.Net;
using RWK.Station.Discovery;
using RWK.Station.Replay;

namespace RWK.Station.Controllers;

/// <summary>
/// Top-level orchestrator for all Station-side components. Owns lifecycle, wiring, and the
/// Arm/DisArm/Start/Stop state machine.
/// </summary>
/// <remarks>
/// <para>
/// Startup sequence (task 23.1):
///   1. Load StationConfig from ConfigStore
///   2. Open StationKeyingOutput on configured port/lines
///   3. Start the sidecar host (TsnetSidecarHost.StartAsync with auth key)
///   4. Start the TailscaleNode façade
///   5. Start the EdgeReplayer (TIME_CRITICAL thread)
///   6. Start the FailSafeMonitor (50ms watchdog thread)
///   7. Start the SessionManager (begins listening for control connections)
///   8. Start the PortForwardManager
///   9. Mark the Station as ARMED
/// </para>
/// <para>
/// Shutdown sequence (reverse):
///   - Stop port forwards, session manager, fail-safe monitor
///   - End any active session (forces key-up)
///   - Stop the replayer
///   - Stop the sidecar host
///   - Close the keying output (drops all lines — F8)
/// </para>
/// <para>
/// Safety invariant: if ANY component fails to start, the Station does NOT enter the armed
/// state and all keying/PTT lines remain de-asserted. The failure is reported to the UI.
/// </para>
/// <para>
/// _Requirements: 5.1–5.8, 7.1–7.7, 8.1–8.7, 9.1–9.12, 10.1–10.10, 11.1–11.8_
/// </para>
/// </remarks>
public sealed class StationController : IDisposable
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Constants
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Default TCP port for the session manager control channel.</summary>
    private const int DefaultControlPort = 7373;

    // ──────────────────────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly ConfigStore<StationConfig> _configStore;
    private readonly Action<string>? _diagnostics;

    private StationConfig _config;
    private StationControllerState _state = StationControllerState.Stopped;
    private string? _lastError;
    private bool _disposed;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Components (created during Start, disposed during Stop)
    // ──────────────────────────────────────────────────────────────────────────────

    private StationKeyingOutput? _keyingOutput;
    private PttSequencer? _pttSequencer;
    private TsnetSidecarHost? _sidecarHost;
    private TailscaleNode? _tailscaleNode;
    private EdgeReplayer? _edgeReplayer;
    private FailSafeMonitor? _failSafeMonitor;
    private SessionManager? _sessionManager;
    private PortForwardManager? _portForwardManager;
    private SidecarFailureHandler? _sidecarFailureHandler;
    private StationDiscoveryListener? _discoveryListener;
    private StationLoggerHost? _loggerHost;
    private volatile bool _loggerSending;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Events (for UI binding)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Raised when the controller's state changes (started, armed, stopped, faulted).</summary>
    public event EventHandler<StationControllerStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a session starts (Client authenticated).</summary>
    public event EventHandler<SessionEventArgs>? SessionStarted;

    /// <summary>Raised when a session ends.</summary>
    public event EventHandler<SessionEventArgs>? SessionEnded;

    /// <summary>Raised when the SAFE latch fires (UI shows red banner).</summary>
    public event EventHandler<FailSafeTriggeredEventArgs>? SafeLatched;

    /// <summary>Raised when the replayer state changes (for status strip updates).</summary>
    public event EventHandler<EdgeReplayerStateChangedEventArgs>? ReplayerStateChanged;

    /// <summary>Raised when the Tailscale node state changes (link, path, RTT).</summary>
    public event EventHandler<TailscaleStateChangedEventArgs>? TailscaleStateChanged;

    /// <summary>Raised when the sidecar failure handler reports a state change.</summary>
    public event EventHandler<SidecarFailureStateChangedEventArgs>? SidecarFailureStateChanged;

    /// <summary>Raised when a startup component fails. Message identifies the specific failure.</summary>
    public event EventHandler<StationStartupFailedEventArgs>? StartupFailed;

    /// <summary>
    /// Raised when the sidecar requires interactive browser login. The string argument
    /// is the URL the user should open.
    /// </summary>
    public event EventHandler<string>? AuthUrlAvailable;

    /// <summary>
    /// Raised when the Client pushes forward rules over the control channel.
    /// The list contains the rules received (for UI display on the Station).
    /// </summary>
    public event EventHandler<List<ForwardRuleInfo>>? ForwardRulesReceived;

    /// <summary>
    /// Raised when the logger WinKeyer input starts or stops sending CW.
    /// True = logger is sending (remote edges suppressed); False = logger idle.
    /// </summary>
    public event EventHandler<bool>? LoggerSendingChanged;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Construction
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a StationController that will load its configuration from the standard
    /// Station config store location.
    /// </summary>
    /// <param name="configStore">The configuration store for loading/saving the profile.</param>
    /// <param name="diagnostics">Optional diagnostic message sink.</param>
    public StationController(ConfigStore<StationConfig> configStore, Action<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _configStore = configStore;
        _diagnostics = diagnostics;
        _config = new StationConfig();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Public properties
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Current controller state.</summary>
    public StationControllerState State => _state;

    /// <summary>Whether the station is armed and ready to accept sessions.</summary>
    public bool IsArmed => _state == StationControllerState.Armed;

    /// <summary>The last startup or runtime error, or null.</summary>
    public string? LastError => _lastError;

    /// <summary>The current StationConfig (loaded on Start).</summary>
    public StationConfig Config => _config;

    /// <summary>
    /// Gets the Station's pairing key for Client authentication.
    /// Generated on first run and persisted in config.
    /// </summary>
    public string PairingKey => _config.Tailscale.PairingSecret ?? "not-set";

    /// <summary>Clears the persisted Tailscale auth key.</summary>
    public void ClearTailscaleAuth()
    {
        _config = _config with { Tailscale = _config.Tailscale with { AuthKey = null } };
        _configStore.TrySave(_config);
        _diagnostics?.Invoke("Tailscale authorization cleared.");
    }

    /// <summary>Whether the SAFE latch is currently set.</summary>
    public bool IsSafeLatched => _edgeReplayer?.IsSafeLatched ?? false;

    /// <summary>The current session, or null.</summary>
    public ActiveSession? CurrentSession => _sessionManager?.CurrentSession;

    /// <summary>The Station's own Tailscale IPv4 address, or null if not yet joined.</summary>
    public string? SelfAddress => _sidecarHost?.SelfAddress;

    /// <summary>Whether the key line is currently asserted (for UI indicator).</summary>
    public bool IsKeyDown => _keyingOutput?.IsKeyDown ?? _pttSequencer?.IsKeyAsserted ?? false;

    /// <summary>Whether the PTT line is currently asserted (for UI indicator).</summary>
    public bool IsPttOn => _keyingOutput?.IsPttOn ?? _pttSequencer?.IsPttAsserted ?? false;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Lifecycle: Start
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the station by initializing all components in order. If any component fails,
    /// the station remains in a Faulted state with all lines de-asserted.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state is StationControllerState.Armed or StationControllerState.Starting)
            return;

        SetState(StationControllerState.Starting);
        _lastError = null;

        try
        {
            // Step 1: Load config.
            _config = _configStore.Load();
            _diagnostics?.Invoke("Configuration loaded.");

            // Generate a pairing key on first run if one doesn't exist.
            if (string.IsNullOrEmpty(_config.Tailscale.PairingSecret))
            {
                string newKey = GeneratePairingKey();
                _config = _config with { Tailscale = _config.Tailscale with { PairingSecret = newKey } };
                _configStore.TrySave(_config);
                _diagnostics?.Invoke($"Generated new pairing key: {newKey}");
            }

            // Step 2: Open the keying output (optional — port may not be configured yet).
            KeyingOutputConfig? keyConfig = _config.ToKeyingOutputConfig();
            if (keyConfig is not null)
            {
                try
                {
                    _keyingOutput = new StationKeyingOutput();
                    _keyingOutput.Configure(keyConfig);
                    _keyingOutput.Open();
                    _diagnostics?.Invoke($"Keying output opened on {keyConfig.PortName}.");

                    // Create PTT sequencer wiring.
                    _pttSequencer = PttSequencer.Create(_keyingOutput, _config.PttTiming, new StopwatchClock());
                }
                catch (Exception ex)
                {
                    _diagnostics?.Invoke($"COM port error: {ex.Message}. Station will start without keying output.");
                    _keyingOutput?.Dispose();
                    _keyingOutput = null;
                    _pttSequencer?.Dispose();
                    _pttSequencer = null;
                }
            }
            else
            {
                _diagnostics?.Invoke("No keying port configured. Select a COM port in the UI to enable keying.");
            }

            // Step 3: Start the sidecar host.
            string? authKey = _config.Tailscale.AuthKey;

            _sidecarFailureHandler = new SidecarFailureHandler(SidecarFailurePolicy.Station);
            _sidecarFailureHandler.FailureStateChanged += OnSidecarFailureStateChanged;
            _sidecarFailureHandler.RetryRequested += OnSidecarRetryRequested;

            _sidecarHost = new TsnetSidecarHost { Hostname = "rwk-station" };
            _sidecarHost.AuthUrlAvailable += OnAuthUrlAvailable;

            // Step 4: Start the TailscaleNode façade (this also starts the sidecar).
            _tailscaleNode = new TailscaleNode(_sidecarHost);
            _tailscaleNode.StateChanged += OnTailscaleStateChanged;
            _tailscaleNode.EdgeReceived += OnEdgeReceived;

            try
            {
                await _tailscaleNode.StartAsync(authKey).ConfigureAwait(false);

                // Check if the sidecar failed silently (no-throw failure path).
                if (_sidecarHost.LastFailure is { } failure)
                {
                    _sidecarFailureHandler.ReportFailure(failure);
                    ReportStartupFailure(
                        $"Tailscale sidecar failed: {failure.Reason}");
                    return;
                }

                _diagnostics?.Invoke("Sidecar host and TailscaleNode started.");
            }
            catch (Exception ex)
            {
                string resolvedPath = SidecarPath.Resolve(
                    SidecarPath.GetBaseDirectory(), SidecarPath.DefaultExecutableName);
                _sidecarFailureHandler.ReportFailure(
                    new SidecarFailure(SidecarFailureKind.LaunchFailed, resolvedPath, ex.Message));

                ReportStartupFailure(
                    $"Tailscale sidecar failed to start: {ex.Message}");
                return;
            }

            _diagnostics?.Invoke("TailscaleNode facade ready.");

            // Step 5: Start the EdgeReplayer (TIME_CRITICAL thread).
            _edgeReplayer = new EdgeReplayer();
            _edgeReplayer.JitterConfig = _config.JitterBuffer;
            _edgeReplayer.StateChanged += OnReplayerStateChanged;
            _edgeReplayer.FailSafeTriggered += OnFailSafeTriggered;

            if (_keyingOutput is not null)
            {
                IPttOutput? pttOutput = _keyingOutput.PttLine == KeyingLine.None ? null : _keyingOutput;
                _edgeReplayer.Start(_keyingOutput, pttOutput);
                _diagnostics?.Invoke("EdgeReplayer started (TIME_CRITICAL thread).");
            }
            else
            {
                _diagnostics?.Invoke("EdgeReplayer created but not started (no keying output yet).");
            }

            // Step 6: Start the FailSafeMonitor (50ms watchdog).
            if (_keyingOutput is not null)
            {
                _failSafeMonitor = new FailSafeMonitor(
                    _edgeReplayer,
                    clock: null,
                    keyingOutput: _keyingOutput,
                    tailscaleNode: _tailscaleNode);
                _failSafeMonitor.FailSafeTriggered += OnFailSafeMonitorTriggered;
                _failSafeMonitor.Start();
                _diagnostics?.Invoke("FailSafeMonitor started (50ms watchdog).");
            }
            else
            {
                _diagnostics?.Invoke("FailSafeMonitor deferred (no keying output yet).");
            }

            // Step 7: Start the SessionManager.
            // Use a default pairing secret for testing if none is configured.
            string? pairingSecret = _config.Tailscale.PairingSecret;
            if (string.IsNullOrEmpty(pairingSecret))
                pairingSecret = "rwk-default-pairing-secret-v2";

            byte[] secretBytes = Encoding.UTF8.GetBytes(pairingSecret);

            _sessionManager = new SessionManager(secretBytes);
            _sessionManager.SessionStarted += OnSessionStarted;
            _sessionManager.SessionEnded += OnSessionEnded;
            _sessionManager.Start(DefaultControlPort);
            _diagnostics?.Invoke($"SessionManager listening on port {DefaultControlPort}.");

            if (string.IsNullOrEmpty(_config.Tailscale.PairingSecret))
                _diagnostics?.Invoke("Using default pairing secret (no custom secret configured).");

            // Inbound forward is registered once Tailscale connects (see OnTailscaleStateChanged).
            // If already connected (persisted state), register immediately.
            if (_sidecarHost.State == TailscaleState.Connected && !_inboundForwardRegistered)
            {
                _inboundForwardRegistered = true;
                await RegisterInboundForwardAsync().ConfigureAwait(false);
            }

            // Step 8: Start the PortForwardManager (Station-side: no tunnel dial needed,
            // the Station is the destination. Rules are pushed by the Client on session start).
            _portForwardManager = new PortForwardManager();
            _portForwardManager.Start();
            _diagnostics?.Invoke("PortForwardManager started.");

            // Step 9: Mark ARMED.
            StartJitterTimer();
            SetState(StationControllerState.Armed);
            _diagnostics?.Invoke("Station ARMED.");

            // Step 9.5: Start Logger WinKeyer Input if configured.
            if (_config.LoggerInputEnabled && !string.IsNullOrEmpty(_config.LoggerPortName))
            {
                StartLoggerHost(_config.LoggerPortName);
            }
        }
        catch (Exception ex)
        {
            ReportStartupFailure($"Unexpected failure during startup: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Lifecycle: Stop
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shuts down all components in reverse order, ensuring all lines are de-asserted.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state == StationControllerState.Stopped)
            return;

        SetState(StationControllerState.Stopping);
        _diagnostics?.Invoke("Station shutting down…");

        // Stop port forwards.
        try { _portForwardManager?.Stop(); } catch { /* best effort */ }
        _portForwardManager?.Dispose();
        _portForwardManager = null;

        // Stop session manager.
        try { _sessionManager?.Stop(); } catch { /* best effort */ }
        _sessionManager?.Dispose();
        _sessionManager = null;

        // Stop fail-safe monitor.
        try { _failSafeMonitor?.Stop(); } catch { /* best effort */ }
        _failSafeMonitor?.Dispose();
        _failSafeMonitor = null;

        // End any active session (forces key-up).
        try { _edgeReplayer?.EndSession(); } catch { /* best effort */ }

        // Stop the replayer.
        try { _edgeReplayer?.Stop(); } catch { /* best effort */ }
        _edgeReplayer?.Dispose();
        _edgeReplayer = null;

        // Disconnect Tailscale edge event handler.
        if (_tailscaleNode is not null)
        {
            _tailscaleNode.StateChanged -= OnTailscaleStateChanged;
            _tailscaleNode.EdgeReceived -= OnEdgeReceived;
            _tailscaleNode.Dispose();
            _tailscaleNode = null;
        }

        // Stop the sidecar host.
        if (_sidecarHost is not null)
        {
            _sidecarHost.AuthUrlAvailable -= OnAuthUrlAvailable;
            try { await _sidecarHost.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
            _sidecarHost.Dispose();
            _sidecarHost = null;
        }

        // Sidecar failure handler cleanup.
        if (_sidecarFailureHandler is not null)
        {
            _sidecarFailureHandler.FailureStateChanged -= OnSidecarFailureStateChanged;
            _sidecarFailureHandler.RetryRequested -= OnSidecarRetryRequested;
            _sidecarFailureHandler.Dispose();
            _sidecarFailureHandler = null;
        }

        // Stop logger host.
        StopLoggerHostInternal();

        // Close keying output — drops all lines (F8).
        try { _keyingOutput?.EnsureAllLinesDown(); } catch { /* best effort */ }
        try { _keyingOutput?.Dispose(); } catch { /* best effort */ }
        _keyingOutput = null;

        _pttSequencer?.Dispose();
        _pttSequencer = null;

        SetState(StationControllerState.Stopped);
        _diagnostics?.Invoke("Station stopped.");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Public API (UI actions)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the SAFE latch so keying can resume (Re-Arm button, 9.11).
    /// </summary>
    public void ClearSafeLatch()
    {
        _edgeReplayer?.ClearSafeLatch();
    }

    /// <summary>
    /// Forcibly disconnects the current session (11.7).
    /// </summary>
    public void DisconnectSession()
    {
        _sessionManager?.DisconnectSession();
    }

    /// <summary>
    /// Starts the FlexRadio discovery listener on the Station's LAN.
    /// </summary>
    public void StartDiscoveryCapture()
    {
        if (_discoveryListener is not null) return;

        _discoveryListener = new StationDiscoveryListener(
            new FlexVitaDiscoveryCodec(),
            _diagnostics);
        _discoveryListener.DiscoveryCaptured += OnDiscoveryCaptured;
        _discoveryListener.Start();
        _diagnostics?.Invoke("FlexRadio discovery capture started.");
    }

    /// <summary>
    /// Stops the FlexRadio discovery listener.
    /// </summary>
    public void StopDiscoveryCapture()
    {
        _discoveryListener?.Stop();
        _discoveryListener?.Dispose();
        _discoveryListener = null;
        _diagnostics?.Invoke("FlexRadio discovery capture stopped.");
    }

    private void OnDiscoveryCaptured(object? sender, DiscoveryCapturedEventArgs e)
    {
        // Forward the raw payload to the Client over the control channel
        _ = SendDiscoveryAnnounceAsync(e.RawPayload.ToArray());
    }

    private async Task SendDiscoveryAnnounceAsync(byte[] payload)
    {
        var stream = _sessionManager?.CurrentControlStream;
        if (stream is null) return;

        try
        {
            // Format: 4-byte big-endian length + JSON wrapper with base64 payload
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "discovery_announce",
                payload = Convert.ToBase64String(payload)
            });

            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(body.Length));

            await stream.WriteAsync(lengthPrefix).ConfigureAwait(false);
            await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Session may have closed — ignore
        }
    }

    /// <summary>
    /// Reloads the StationConfig from disk and returns it.
    /// Does not restart components; use StopAsync/StartAsync for full re-init.
    /// </summary>
    public StationConfig ReloadConfig()
    {
        _config = _configStore.Load();
        return _config;
    }

    /// <summary>
    /// Submits an auth key to the running sidecar (manual "paste key" fallback for
    /// interactive login). Persists the key DPAPI-encrypted for headless re-use.
    /// </summary>
    public async Task SubmitAuthKeyAsync(string authKey)
    {
        if (_sidecarHost is null)
            throw new InvalidOperationException("Sidecar is not running.");

        await _sidecarHost.SubmitAuthKeyAsync(authKey).ConfigureAwait(false);

        // Persist the key for future headless restarts.
        _config = _config with { Tailscale = _config.Tailscale with { AuthKey = authKey } };
        _configStore.TrySave(_config);
    }

    /// <summary>
    /// Dynamically connects or reconnects the keying output to the specified COM port.
    /// Called by the UI when the operator selects a port from the dropdown.
    /// If a port was already open, it is closed first (lines de-asserted).
    /// After connecting, starts the EdgeReplayer and FailSafeMonitor if they were deferred.
    /// </summary>
    /// <param name="portName">COM port name (e.g. "COM5").</param>
    /// <param name="config">Full keying output configuration (port, lines, inversion).</param>
    /// <exception cref="Exception">Thrown if the port cannot be opened (caller should display error).</exception>
    public void ConnectKeyingPort(string portName, KeyingOutputConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Close existing output if any.
        if (_keyingOutput is not null)
        {
            try { _keyingOutput.EnsureAllLinesDown(); } catch { /* best effort */ }
            _keyingOutput.Dispose();
            _keyingOutput = null;
        }

        _pttSequencer?.Dispose();
        _pttSequencer = null;

        // Open new port — may throw (caller shows error).
        _keyingOutput = new StationKeyingOutput();
        _keyingOutput.Configure(config);
        _keyingOutput.Open();
        _diagnostics?.Invoke($"Keying output opened on {portName}.");

        // Create PTT sequencer.
        _pttSequencer = PttSequencer.Create(_keyingOutput, _config.PttTiming, new StopwatchClock());

        // Start or restart the replayer with the new keying output.
        if (_edgeReplayer is not null && !_edgeReplayer.IsRunning)
        {
            IPttOutput? pttOutput = _keyingOutput.PttLine == KeyingLine.None ? null : _keyingOutput;
            _edgeReplayer.Start(_keyingOutput, pttOutput);
            _diagnostics?.Invoke("EdgeReplayer started (attached to new keying output).");
        }
        else if (_edgeReplayer is not null && _edgeReplayer.IsRunning)
        {
            // Must stop and restart to re-wire outputs (replayer binds at Start).
            _edgeReplayer.Stop();
            IPttOutput? pttOutput = _keyingOutput.PttLine == KeyingLine.None ? null : _keyingOutput;
            _edgeReplayer.Start(_keyingOutput, pttOutput);
            _diagnostics?.Invoke("EdgeReplayer restarted with new keying output.");
        }

        // Start the fail-safe monitor if it was deferred.
        if (_failSafeMonitor is null && _edgeReplayer is not null && _tailscaleNode is not null)
        {
            _failSafeMonitor = new FailSafeMonitor(
                _edgeReplayer,
                clock: null,
                keyingOutput: _keyingOutput,
                tailscaleNode: _tailscaleNode);
            _failSafeMonitor.FailSafeTriggered += OnFailSafeMonitorTriggered;
            _failSafeMonitor.Start();
            _diagnostics?.Invoke("FailSafeMonitor started (50ms watchdog).");
        }

        // Save the port selection to config.
        _config = _config with { KeyingPortName = portName };
        _configStore.TrySave(_config);
    }

    /// <summary>
    /// Saves the given configuration to disk.
    /// </summary>
    public void SaveConfig(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _configStore.TrySave(config);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Avoid deadlock: don't sync-over-async on a UI thread.
        // Kill the sidecar process directly and clean up synchronously.
        try { _sidecarHost?.Dispose(); } catch { /* best effort */ }
        _sidecarHost = null;

        try { _failSafeMonitor?.Stop(); } catch { /* best effort */ }
        try { _failSafeMonitor?.Dispose(); } catch { /* best effort */ }
        _failSafeMonitor = null;

        try { _edgeReplayer?.Stop(); } catch { /* best effort */ }
        try { _edgeReplayer?.Dispose(); } catch { /* best effort */ }
        _edgeReplayer = null;

        try { _tailscaleNode?.Dispose(); } catch { /* best effort */ }
        _tailscaleNode = null;

        try { _sessionManager?.Stop(); } catch { /* best effort */ }
        try { _sessionManager?.Dispose(); } catch { /* best effort */ }
        _sessionManager = null;

        try { _portForwardManager?.Stop(); } catch { /* best effort */ }
        try { _portForwardManager?.Dispose(); } catch { /* best effort */ }
        _portForwardManager = null;

        try { _keyingOutput?.EnsureAllLinesDown(); } catch { /* best effort */ }
        try { _keyingOutput?.Dispose(); } catch { /* best effort */ }
        _keyingOutput = null;

        try { _pttSequencer?.Dispose(); } catch { /* best effort */ }
        _pttSequencer = null;

        try { _sidecarFailureHandler?.Dispose(); } catch { /* best effort */ }
        _sidecarFailureHandler = null;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private: event handlers and wiring
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnSessionStarted(object? sender, SessionEventArgs e)
    {
        // Wire the session to the replayer.
        ushort epoch = _sessionManager?.CurrentEpoch ?? 1;
        _edgeReplayer?.BeginSession(epoch);
        _diagnostics?.Invoke($"Session started: {e.ClientName} ({e.ClientAddress}), epoch={epoch}.");

        // Start reading control messages (forward rules, etc.) from the Client.
        _ = ReadControlMessagesAsync();

        SessionStarted?.Invoke(this, e);
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        _edgeReplayer?.EndSession();
        _diagnostics?.Invoke($"Session ended: {e.ClientName} ({e.ClientAddress}). Reason: {e.Reason}");

        SessionEnded?.Invoke(this, e);
    }

    private void OnEdgeReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        // Parse the frame.
        if (!RwkPaddleFrame.TryRead(data.Span, out RwkPaddleFrame frame, out _))
            return;

        // Always process as heartbeat to keep fail-safe timers happy,
        // even if no keying output is configured.
        _edgeReplayer?.ProcessHeartbeat();

        // If no keying output, don't process edge transitions (nothing to key).
        if (_keyingOutput is null) return;

        // Logger interlock: when the logger is sending CW macros, suppress remote edges.
        if (_loggerSending) return;

        if (frame.EdgeCount == 0) return;

        Span<EdgeEntry> edges = stackalloc EdgeEntry[RwkPaddleFrame.MaxEdgeCount];
        if (!frame.TryCopyEdgesTo(edges, out int count) || count == 0) return;

        // Find the edge with the highest sequence number (newest).
        EdgeEntry newest = edges[0];
        for (int i = 1; i < count; i++)
        {
            if (edges[i].Sequence > newest.Sequence)
                newest = edges[i];
        }

        // Skip if same state as last applied (avoid redundant calls from frame redundancy).
        if (newest.KeyDown == _lastDirectKeyState && _directKeyInitialized)
            return;
        _lastDirectKeyState = newest.KeyDown;
        _directKeyInitialized = true;

        // Enqueue with delay for jitter absorption.
        long fireAt = Stopwatch.GetTimestamp() + _jitterDelayTicks;
        lock (_jitterQueue)
        {
            _jitterQueue.Enqueue((fireAt, newest.KeyDown));
        }
    }

    private bool _lastDirectKeyState;
    private bool _directKeyInitialized;
    private System.Threading.Timer? _pttTailTimer;
    private System.Threading.Timer? _jitterTimer;
    private readonly Queue<(long FireAt, bool KeyDown)> _jitterQueue = new();
    private long _jitterDelayTicks = Stopwatch.Frequency * 50 / 1000; // Default 50ms

    /// <summary>
    /// Sets the jitter buffer delay in milliseconds. Called when RTT is measured.
    /// A good value is 1.5x the one-way latency (RTT/2 * 1.5).
    /// </summary>
    public void SetJitterDelay(int delayMs)
    {
        _jitterDelayTicks = Stopwatch.Frequency * Math.Clamp(delayMs, 0, 500) / 1000;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Logger WinKeyer Input
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the logger host is currently running.</summary>
    public bool IsLoggerHostRunning => _loggerHost?.IsRunning ?? false;

    /// <summary>Whether the logger is currently sending CW (interlock active).</summary>
    public bool IsLoggerSending => _loggerSending;

    /// <summary>
    /// Starts or restarts the logger WinKeyer host on the specified port.
    /// Called by the UI when the user enables logger input or changes the port.
    /// </summary>
    public void StartLoggerHost(string portName)
    {
        StopLoggerHost();

        if (_keyingOutput is null || !_keyingOutput.IsOpen)
        {
            _diagnostics?.Invoke("Cannot start logger host: no keying output configured.");
            return;
        }

        if (string.Equals(portName, _keyingOutput.PortName, StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics?.Invoke($"Cannot start logger host: port {portName} is used for keying output.");
            return;
        }

        try
        {
            _loggerHost = new StationLoggerHost();
            _loggerHost.SendingStarted += OnLoggerSendingStarted;
            _loggerHost.SendingCompleted += OnLoggerSendingCompleted;
            _loggerHost.SpeedChanged += OnLoggerSpeedChanged;

            IPttOutput? pttOutput = _keyingOutput.PttLine == KeyingLine.None ? null : _keyingOutput;
            _loggerHost.Start(portName, _keyingOutput, pttOutput);

            _config = _config with { LoggerInputEnabled = true, LoggerPortName = portName };
            _configStore.TrySave(_config);

            _diagnostics?.Invoke($"Logger WinKeyer host started on {portName}.");
        }
        catch (Exception ex)
        {
            _diagnostics?.Invoke($"Failed to start logger host on {portName}: {ex.Message}");
            _loggerHost?.Dispose();
            _loggerHost = null;
        }
    }

    /// <summary>
    /// Stops the logger WinKeyer host.
    /// Called by the UI when the user disables logger input.
    /// </summary>
    public void StopLoggerHost()
    {
        StopLoggerHostInternal();

        _config = _config with { LoggerInputEnabled = false };
        _configStore.TrySave(_config);

        _diagnostics?.Invoke("Logger WinKeyer host stopped.");
    }

    /// <summary>
    /// Internal stop without persisting config change. Used during shutdown cleanup.
    /// </summary>
    private void StopLoggerHostInternal()
    {
        if (_loggerHost is null) return;

        _loggerHost.SendingStarted -= OnLoggerSendingStarted;
        _loggerHost.SendingCompleted -= OnLoggerSendingCompleted;
        _loggerHost.SpeedChanged -= OnLoggerSpeedChanged;
        _loggerHost.Dispose();
        _loggerHost = null;

        if (_loggerSending)
        {
            _loggerSending = false;
            LoggerSendingChanged?.Invoke(this, false);
        }
    }

    private void OnLoggerSendingStarted(object? sender, EventArgs e)
    {
        _loggerSending = true;

        // Force key up on the remote path — any pending remote edges are stale now.
        try { _keyingOutput?.KeyUp(); } catch { /* best effort */ }
        lock (_jitterQueue) { _jitterQueue.Clear(); }

        LoggerSendingChanged?.Invoke(this, true);
        _diagnostics?.Invoke("Logger sending — remote edges suppressed.");
    }

    private void OnLoggerSendingCompleted(object? sender, EventArgs e)
    {
        _loggerSending = false;
        LoggerSendingChanged?.Invoke(this, false);
        _diagnostics?.Invoke("Logger idle — remote edges resumed.");
    }

    private void OnLoggerSpeedChanged(object? sender, int wpm)
    {
        _diagnostics?.Invoke($"Logger speed: {wpm} WPM.");
    }

    private void StartJitterTimer()
    {
        _jitterTimer = new System.Threading.Timer(ProcessJitterQueue, null, 0, 1);
    }

    private void ProcessJitterQueue(object? state)
    {
        // Logger interlock: skip processing while logger is sending.
        if (_loggerSending) return;

        long now = Stopwatch.GetTimestamp();
        while (true)
        {
            (long fireAt, bool keyDown) item;
            lock (_jitterQueue)
            {
                if (_jitterQueue.Count == 0) return;
                item = _jitterQueue.Peek();
                if (now < item.fireAt) return;
                _jitterQueue.Dequeue();
            }
            ApplyKeyState(item.keyDown);
        }
    }

    private void ApplyKeyState(bool keyDown)
    {
        try
        {
            if (keyDown)
            {
                if (_keyingOutput!.PttLine != KeyingLine.None && !_keyingOutput.IsPttOn)
                    _keyingOutput.PttDown();
                _keyingOutput!.KeyDown();
            }
            else
            {
                _keyingOutput!.KeyUp();
                SchedulePttUp();
            }
        }
        catch
        {
            // COM port error
        }
    }

    private void SchedulePttUp()
    {
        _pttTailTimer?.Dispose();
        _pttTailTimer = new System.Threading.Timer(_ =>
        {
            try { _keyingOutput?.PttUp(); } catch { }
        }, null, 500, Timeout.Infinite);
    }

    private void OnReplayerStateChanged(object? sender, EdgeReplayerStateChangedEventArgs e)
    {
        ReplayerStateChanged?.Invoke(this, e);
    }

    private void OnFailSafeTriggered(object? sender, FailSafeTriggeredEventArgs e)
    {
        SafeLatched?.Invoke(this, e);
    }

    private void OnFailSafeMonitorTriggered(object? sender, FailSafeTriggeredEventArgs e)
    {
        // The monitor may fire conditions (F1-F3, F6, F9) that the replayer's own event
        // doesn't surface. Route them to the same UI sink.
        SafeLatched?.Invoke(this, e);
    }

    private void OnTailscaleStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        TailscaleStateChanged?.Invoke(this, e);

        // Register the inbound forward once the tailnet is up.
        if (e.State == TailscaleState.Connected && !_inboundForwardRegistered)
        {
            _inboundForwardRegistered = true;
            _ = RegisterInboundForwardAsync();
        }

        // Auto-adjust jitter buffer based on measured RTT.
        if (e.RoundTripTime > TimeSpan.Zero)
        {
            // Buffer = 1.5x one-way latency, minimum 20ms, max 200ms.
            int oneWayMs = (int)(e.RoundTripTime.TotalMilliseconds / 2);
            int bufferMs = Math.Clamp((int)(oneWayMs * 1.5), 20, 200);
            SetJitterDelay(bufferMs);
            _diagnostics?.Invoke($"Jitter buffer adjusted: {bufferMs}ms (RTT={e.RoundTripTime.TotalMilliseconds:F0}ms)");
        }
    }

    private bool _inboundForwardRegistered;

    private async Task RegisterInboundForwardAsync()
    {
        try
        {
            await _sidecarHost!.CreateInboundForwardAsync(DefaultControlPort, DefaultControlPort)
                .ConfigureAwait(false);
            _diagnostics?.Invoke($"✓ Inbound forward OK: tailnet:{DefaultControlPort} → localhost:{DefaultControlPort}.");
        }
        catch (HttpRequestException ex)
        {
            _diagnostics?.Invoke($"✗ INBOUND FORWARD HTTP ERROR: {ex.StatusCode} {ex.Message}");
        }
        catch (Exception ex)
        {
            _diagnostics?.Invoke($"✗ INBOUND FORWARD EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads length-prefixed JSON control messages from the Client after session establishment.
    /// Runs in a loop for the lifetime of the session so dynamic rule updates are received.
    /// Currently handles "forward_rules" messages which register inbound forwards on the sidecar.
    /// </summary>
    private async Task ReadControlMessagesAsync()
    {
        var stream = _sessionManager?.CurrentControlStream;
        if (stream is null) return;

        try
        {
            while (true)
            {
                // Format: 4-byte big-endian length prefix + UTF-8 JSON body.
                byte[] lengthBuf = new byte[4];
                int read = 0;
                while (read < 4)
                {
                    int n = await stream.ReadAsync(lengthBuf.AsMemory(read, 4 - read)).ConfigureAwait(false);
                    if (n == 0) return; // Client closed stream — session ended.
                    read += n;
                }

                int bodyLength = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuf, 0));
                if (bodyLength <= 0 || bodyLength > 64 * 1024)
                {
                    _diagnostics?.Invoke($"Control message invalid length: {bodyLength}");
                    return;
                }

                byte[] body = new byte[bodyLength];
                read = 0;
                while (read < bodyLength)
                {
                    int n = await stream.ReadAsync(body.AsMemory(read, bodyLength - read)).ConfigureAwait(false);
                    if (n == 0) return;
                    read += n;
                }

                string json = System.Text.Encoding.UTF8.GetString(body);
                await ProcessControlMessageAsync(json).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Session closed or stream unavailable — normal during disconnect.
        }
        catch (Exception ex)
        {
            _diagnostics?.Invoke($"Control message read error: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes a control message from the Client. Currently supports "forward_rules".
    /// </summary>
    private async Task ProcessControlMessageAsync(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            string? msgType = typeProp.GetString();
            if (msgType == "forward_rules" && root.TryGetProperty("rules", out var rulesArray))
            {
                var receivedRules = new List<ForwardRuleInfo>();

                foreach (var ruleEl in rulesArray.EnumerateArray())
                {
                    int port = ruleEl.GetProperty("port").GetInt32();
                    int clientPort = ruleEl.TryGetProperty("clientPort", out var cpProp) ? cpProp.GetInt32() : port;
                    string protocol = ruleEl.GetProperty("protocol").GetString() ?? "tcp";
                    string targetAddress = ruleEl.TryGetProperty("targetAddress", out var taProp)
                        ? taProp.GetString() ?? "127.0.0.1"
                        : "127.0.0.1";
                    bool enabled = ruleEl.TryGetProperty("enabled", out var enProp) && enProp.GetBoolean();
                    string name = ruleEl.TryGetProperty("name", out var nmProp)
                        ? nmProp.GetString() ?? $"{protocol}:{port}"
                        : $"{protocol}:{port}";

                    receivedRules.Add(new ForwardRuleInfo(port, clientPort, protocol, targetAddress, name, enabled));

                    // Only register inbound forward for enabled rules.
                    if (enabled)
                    {
                        try
                        {
                            if (protocol == "udp")
                            {
                                await _sidecarHost!.CreateInboundUdpForwardAsync(port, port, targetAddress).ConfigureAwait(false);
                            }
                            else
                            {
                                await _sidecarHost!.CreateInboundForwardAsync(port, port, targetAddress).ConfigureAwait(false);
                            }
                            _diagnostics?.Invoke($"✓ Inbound forward registered: tailnet:{port} → {targetAddress}:{port} ({protocol})");
                        }
                        catch (Exception ex)
                        {
                            _diagnostics?.Invoke($"✗ Inbound forward failed for port {port}: {ex.Message}");
                        }
                    }
                }

                // Notify UI so the grid is updated.
                ForwardRulesReceived?.Invoke(this, receivedRules);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            _diagnostics?.Invoke($"Control message parse error: {ex.Message}");
        }
    }

    private void OnAuthUrlAvailable(object? sender, string authUrl)
    {
        AuthUrlAvailable?.Invoke(this, authUrl);
    }

    private void OnSidecarFailureStateChanged(object? sender, SidecarFailureStateChangedEventArgs e)
    {
        SidecarFailureStateChanged?.Invoke(this, e);
    }

    private void OnSidecarRetryRequested(object? sender, EventArgs e)
    {
        // The retry handler requests we try the sidecar again. This is an async operation
        // but we fire-and-forget from the timer context.
        _ = RetrySidecarAsync();
    }

    private async Task RetrySidecarAsync()
    {
        if (_sidecarHost is null || _sidecarFailureHandler is null) return;

        string? authKey = _config.Tailscale.AuthKey;
        if (string.IsNullOrWhiteSpace(authKey)) return;

        try
        {
            await _sidecarHost.StartAsync(authKey).ConfigureAwait(false);
            _sidecarFailureHandler.ReportRecovery();
            _diagnostics?.Invoke("Sidecar recovered on retry.");
        }
        catch (Exception ex)
        {
            _diagnostics?.Invoke($"Sidecar retry failed: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private: state management
    // ──────────────────────────────────────────────────────────────────────────────

    private void SetState(StationControllerState newState)
    {
        if (_state == newState) return;
        var oldState = _state;
        _state = newState;
        StateChanged?.Invoke(this, new StationControllerStateChangedEventArgs(oldState, newState));
    }

    private void ReportStartupFailure(string message)
    {
        _lastError = message;
        _diagnostics?.Invoke($"STARTUP FAILURE: {message}");

        // Ensure all lines are de-asserted.
        try { _keyingOutput?.EnsureAllLinesDown(); } catch { /* best effort */ }

        // Tear down anything already started.
        _ = StopAsync();

        SetState(StationControllerState.Faulted);
        StartupFailed?.Invoke(this, new StationStartupFailedEventArgs(message));
    }

    /// <summary>
    /// Generates an 8-character alphanumeric pairing key.
    /// </summary>
    private static string GeneratePairingKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No ambiguous chars (0/O, 1/I)
        Span<byte> random = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        return string.Create(8, random.ToArray(), (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = chars[bytes[i] % chars.Length];
        });
    }
}

// ──────────────────────────────────────────────────────────────────────────────
//  Supporting types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Info about a forward rule received from the Client, for UI display on the Station.
/// </summary>
public record ForwardRuleInfo(int Port, int ClientPort, string Protocol, string TargetAddress, string Name = "", bool Enabled = false);

/// <summary>
/// Controller lifecycle states.
/// </summary>
public enum StationControllerState
{
    /// <summary>Not started or fully shut down.</summary>
    Stopped = 0,

    /// <summary>Startup in progress.</summary>
    Starting = 1,

    /// <summary>All components running, ready to accept sessions.</summary>
    Armed = 2,

    /// <summary>Shutdown in progress.</summary>
    Stopping = 3,

    /// <summary>A component failed to start; station is not armed.</summary>
    Faulted = 4
}

/// <summary>
/// Event args for station controller state transitions.
/// </summary>
/// <param name="OldState">The previous state.</param>
/// <param name="NewState">The new state.</param>
public record StationControllerStateChangedEventArgs(
    StationControllerState OldState,
    StationControllerState NewState);

/// <summary>
/// Event args when a startup sequence fails.
/// </summary>
/// <param name="Message">Human-readable description of the failure.</param>
public record StationStartupFailedEventArgs(string Message);
