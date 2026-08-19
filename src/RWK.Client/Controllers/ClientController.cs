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
using System.Security.Cryptography;
using RWK.Client.Audio;
using RWK.Client.IO;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.IO;
using RWK.Shared.Keying;
using RWK.Shared.Net;
using RWK.Shared.Protocol;
using RWK.Shared.Protocol.Edge;

namespace RWK.Client.Controllers;

/// <summary>
/// Orchestrates all Client-side components: paddle poller, WinKeyer protocol host,
/// keyer core, sidetone engine, Tailscale networking, and port forwarding.
/// </summary>
/// <remarks>
/// This class is NOT a WinForms control. It runs entirely off the UI thread.
/// The UI subscribes to events, which it must marshal via <see cref="SynchronizationContext"/>
/// or <c>Control.Invoke</c>.
/// <para>
/// Lifecycle: create → <see cref="StartAsync"/> → operate → <see cref="StopAsync"/> → dispose.
/// </para>
/// <para>
/// On sidecar failure, the controller keeps paddle + keyer + sidetone usable for local
/// practice; only tailnet-dependent operations degrade (Requirements 16.11, 4.7).
/// </para>
/// _Requirements: 1.1-4.7, 5.1-5.8, 6.1-6.8, 10.1-10.10_
/// </remarks>
public sealed class ClientController : IDisposable
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Owned components
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly IPaddleInputPoller _paddlePoller;
    private IWinKeyerProtocolHost _winKeyerHost;
    private HardwareWinKeyerHost? _hardwareWinKeyerHost;
    private WinKeyerMode _winKeyerMode = WinKeyerMode.LoggerApp;
    private readonly IWinKeyerProtocolHost _loggerWinKeyerHost;
    private readonly ISoftWinKeyerCore _keyer;
    private readonly ILocalSidetoneEngine _sidetone;
    private readonly ITsnetSidecarHost _sidecarHost;
    private readonly TailscaleNode _tailscaleNode;
    private readonly IPortForwardManager _portForwardManager;
    private readonly SidecarFailureHandler _failureHandler;
    private readonly ConfigStore<ClientConfig> _configStore;
    private readonly LogService? _log;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Edge framing state
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly object _edgeLock = new();
    private readonly EdgeEntry[] _recentEdges = new EdgeEntry[RwkPaddleFrame.MaxEdgeCount];
    private int _recentEdgeCount;
    private uint _edgeSequence;
    private long _sessionStartQpc;
    private ushort _sessionEpoch;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Heartbeat
    // ──────────────────────────────────────────────────────────────────────────────

    private System.Threading.Timer? _heartbeatTimer;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);

    // ──────────────────────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────────────────────

    private const int DefaultControlPort = 7373;
    private const int MaxReconnectAttempts = 5;

    private ClientConfig _config = new();
    private bool _running;
    private bool _disposed;
    private Stream? _controlStream;
    private bool _sessionActive;
    private string? _lastStationAddress;
    private int _reconnectAttempts;
    private System.Threading.Timer? _reconnectTimer;
    private volatile bool _suppressEdgeSend;
    private volatile bool _loopbackTestActive;
    private volatile bool _stationArmed = true;

    /// <summary>
    /// Creates a ClientController with all its owned components.
    /// </summary>
    public ClientController(
        IPaddleInputPoller paddlePoller,
        IWinKeyerProtocolHost winKeyerHost,
        ISoftWinKeyerCore keyer,
        ILocalSidetoneEngine sidetone,
        ITsnetSidecarHost sidecarHost,
        IPortForwardManager portForwardManager,
        ConfigStore<ClientConfig>? configStore = null,
        LogService? logService = null)
    {
        _paddlePoller = paddlePoller ?? throw new ArgumentNullException(nameof(paddlePoller));
        _winKeyerHost = winKeyerHost ?? throw new ArgumentNullException(nameof(winKeyerHost));
        _loggerWinKeyerHost = winKeyerHost;
        _keyer = keyer ?? throw new ArgumentNullException(nameof(keyer));
        _sidetone = sidetone ?? throw new ArgumentNullException(nameof(sidetone));
        _sidecarHost = sidecarHost ?? throw new ArgumentNullException(nameof(sidecarHost));
        _portForwardManager = portForwardManager ?? throw new ArgumentNullException(nameof(portForwardManager));
        _configStore = configStore ?? ConfigStore.ForClient();
        _log = logService;

        _tailscaleNode = new TailscaleNode(_sidecarHost);
        _failureHandler = new SidecarFailureHandler(SidecarFailurePolicy.Client);

        WireEvents();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Public properties
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the controller is running (all local components started).</summary>
    public bool IsRunning => _running;

    /// <summary>The current configuration snapshot.</summary>
    public ClientConfig Config => _config;

    /// <summary>Whether the sidecar is in a failure state.</summary>
    public bool IsSidecarFailed => _failureHandler.IsInFailure;

    /// <summary>Human-readable failure message when sidecar is in failure state, or null.</summary>
    public string? SidecarFailureMessage =>
        _failureHandler.CurrentFailure is { } f
            ? SidecarFailureHandler.FormatFailureMessage(f)
            : null;

    /// <summary>The current auth URL if the sidecar is waiting for interactive login, or null.</summary>
    public string? AuthUrl => _sidecarHost.AuthUrl;

    /// <summary>Current Tailscale connection state.</summary>
    public TailscaleState TailscaleState => _tailscaleNode.State;

    /// <summary>Current connection path type.</summary>
    public PathType CurrentPath => _tailscaleNode.CurrentPath;

    /// <summary>Current round-trip time to peer.</summary>
    public TimeSpan RoundTripTime => _tailscaleNode.RoundTripTime;

    /// <summary>DERP region when relayed.</summary>
    public string? DerpRegion => _tailscaleNode.DerpRegion;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Events for UI binding (marshal to UI thread!)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Raised when Tailscale connection state changes.</summary>
    public event EventHandler<TailscaleStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Raised when a paddle state transition occurs (for indicator lights).</summary>
    public event EventHandler<PaddleStateChangedEventArgs>? PaddleStateChanged;

    /// <summary>Raised on every key edge (for key-state indicator).</summary>
    public event EventHandler<EdgeEvent>? EdgeGenerated;

    /// <summary>Raised when the sidecar failure state changes.</summary>
    public event EventHandler<SidecarFailureStateChangedEventArgs>? SidecarFailureChanged;

    /// <summary>Raised when a forward rule's status changes.</summary>
    public event EventHandler<ForwardRuleStatusChangedEventArgs>? ForwardRuleStatusChanged;

    /// <summary>
    /// Raised when the sidecar requires interactive browser login. The string argument
    /// is the URL the user should open.
    /// </summary>
    public event EventHandler<string>? AuthUrlAvailable;

    /// <summary>
    /// Raised when the network session is lost and reconnection is being attempted.
    /// The string is a status message for the UI.
    /// </summary>
    public event EventHandler<string>? SessionStatusChanged;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads config and starts all components. Local practice (paddle/keyer/sidetone)
    /// always starts even if Tailscale fails.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _log?.Info("Starting Client controller...");

        // Load persisted config
        _config = _configStore.Load();
        ApplyConfigToComponents(_config);

        // Start local practice components (always succeed)
        StartLocalComponents();
        _log?.Info("Local components started (keyer, sidetone, paddle).");

        // Start Tailscale (may fail — sidecar missing, etc.)
        _log?.Info("Connecting to Tailnet...");
        await StartTailscaleAsync().ConfigureAwait(false);

        _running = true;
        _log?.Info("Client controller started.");
    }

    /// <summary>
    /// Stops all components and saves config.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;

        StopHeartbeat();
        StopLocalComponents();

        await StopTailscaleAsync().ConfigureAwait(false);

        // Save config on stop
        _configStore.TrySave(_config);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Config live-update methods (called by UI on slider/dropdown changes)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Updates keyer speed live from the UI.</summary>
    public void SetSpeed(int wpm)
    {
        _keyer.SpeedWpm = wpm;
        _sidetone.SpeedWpm = wpm;
        _config = _config with { SpeedWpm = wpm };
        _configStore.TrySave(_config);
        _log?.Info($"Speed set to {wpm} WPM.");
    }

    /// <summary>Updates keyer weight live from the UI.</summary>
    public void SetWeight(int weight)
    {
        _keyer.Weight = weight;
        _config = _config with { Weight = weight };
    }

    /// <summary>Updates keyer mode live from the UI.</summary>
    public void SetMode(KeyerMode mode)
    {
        _keyer.Mode = mode;
        _config = _config with { KeyerMode = mode };
    }

    /// <summary>Updates paddle reverse live from the UI.</summary>
    public void SetPaddleReverse(bool reverse)
    {
        _keyer.PaddleReverse = reverse;
        _config = _config with { PaddleReverse = reverse };
    }

    /// <summary>
    /// Sends a test message through the keyer. The keyer generates edges which flow
    /// through sidetone locally and over Tailscale to the Station's replayer.
    /// </summary>
    public void SendTestMessage(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_running) return;

        _keyer.EnqueueText(text);
    }

    /// <summary>
    /// Connects (or reconnects) the WinKeyer protocol host to the specified COM port.
    /// Uses the currently selected WinKeyer mode (Logger App or Hardware WinKey).
    /// Persists the port name to config.
    /// </summary>
    public void ConnectWinKeyerPort(string portName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        LogDebug($"ConnectWinKeyerPort: {portName} (mode={_winKeyerMode})");

        // Stop existing connection and unwire events
        UnwireWinKeyerEvents();
        try { _winKeyerHost.Stop(); } catch { }

        // Select the appropriate host based on mode
        if (_winKeyerMode == WinKeyerMode.HardwareWinKey)
        {
            // Create a new hardware host (or reuse existing)
            _hardwareWinKeyerHost?.Dispose();
            _hardwareWinKeyerHost = new HardwareWinKeyerHost();
            _winKeyerHost = _hardwareWinKeyerHost;
        }
        else
        {
            _winKeyerHost = _loggerWinKeyerHost;
        }

        // Wire events for the new host
        WireWinKeyerEvents();

        // Start on new port
        try
        {
            _winKeyerHost.Start(portName);
            _config = _config with { WinKeyerPortName = portName };
            _configStore.TrySave(_config);
            LogDebug($"ConnectWinKeyerPort: SUCCESS on {portName} (mode={_winKeyerMode})");
            _log?.Info($"WinKeyer connected on {portName} ({_winKeyerMode}).");

            // For hardware mode, set the speed immediately after connecting
            if (_winKeyerMode == WinKeyerMode.HardwareWinKey && _hardwareWinKeyerHost is not null)
            {
                _hardwareWinKeyerHost.SetSpeed(_config.SpeedWpm);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"ConnectWinKeyerPort: FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the WinKeyer operating mode. Does not reconnect the port — call
    /// <see cref="ConnectWinKeyerPort"/> afterwards to apply the change.
    /// </summary>
    /// <param name="mode">The new WinKeyer mode.</param>
    public void SetWinKeyerMode(WinKeyerMode mode)
    {
        _winKeyerMode = mode;
        LogDebug($"SetWinKeyerMode: {mode}");
    }

    /// <summary>
    /// Gets the current WinKeyer operating mode.
    /// </summary>
    public WinKeyerMode CurrentWinKeyerMode => _winKeyerMode;

    /// <summary>
    /// Runs a WinKeyer loopback test: injects WK2 protocol bytes directly into the
    /// WinKeyerProtocolHost's state machine — Admin Open, Set Speed 25 WPM, then
    /// Runs a WinKeyer loopback test: injects WK2 protocol bytes directly into the
    /// WinKeyerProtocolHost's state machine. Tests multiple speed changes:
    /// 25 WPM "WINKEY PROTOCOL TESTS", 30 WPM "VVV 30 WPM", 45 WPM "VVV 45 WPM",
    /// 20 WPM "TEST OK". Exercises the full WK2 protocol path without hardware.
    /// </summary>
    public async Task RunWinKeyerLoopbackTestAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The loopback test injects bytes into the Logger App mode host.
        var host = _loggerWinKeyerHost as WinKeyerProtocolHost;
        if (host is null)
            throw new InvalidOperationException("Loopback test requires the Logger App protocol host.");

        LogDebug("WinKeyer Loopback Test: starting multi-speed protocol test");

        // Suppress the echo path during the loopback test to prevent any
        // serial write attempts and ensure clean single-pass through the keyer.
        _loopbackTestActive = true;

        // Suppress edge sending — this is a local test, must not key the transmitter.
        // Remember the prior armed state so we can restore it after.
        bool wasArmed = _stationArmed;
        _suppressEdgeSend = true;

        // Stop any running host to prevent the reader thread from interfering.
        // The protocol state machine is still usable when the host is stopped.
        string? activePort = _config.WinKeyerPortName;
        try { host.Stop(); } catch { }

        try
        {
            // Step 1: Admin Open (0x00 0x02) — enters host mode
            host.InjectBytes(new byte[] { 0x00, 0x02 });

            // Step 2: Set Speed to 25 WPM, send "WINKEY PROTOCOL TESTS"
            await InjectSpeedAndTextAsync(host, 25, "WINKEY PROTOCOL TESTS").ConfigureAwait(false);

            // Step 3: Speed change to 30 WPM, send "VVV 30 WPM"
            await InjectSpeedAndTextAsync(host, 30, "VVV 30 WPM").ConfigureAwait(false);

            // Step 4: Speed change to 45 WPM, send "VVV 45 WPM"
            await InjectSpeedAndTextAsync(host, 45, "VVV 45 WPM").ConfigureAwait(false);

            // Step 5: Speed change to 20 WPM, send "TEST OK"
            await InjectSpeedAndTextAsync(host, 20, "TEST OK").ConfigureAwait(false);

            // Step 6: Admin Close (0x00 0x03) — exits host mode
            host.InjectBytes(new byte[] { 0x00, 0x03 });

            LogDebug("WinKeyer Loopback Test: complete");
        }
        finally
        {
            _loopbackTestActive = false;
            // Restore edge sending to the prior armed state.
            _suppressEdgeSend = !wasArmed;

            // Restore the previous connection in whatever mode the user had selected.
            if (!string.IsNullOrEmpty(activePort))
            {
                try { ConnectWinKeyerPort(activePort); } catch { }
            }
        }
    }

    /// <summary>
    /// Injects a speed command and text into the protocol host, then waits for the
    /// estimated keying duration.
    /// </summary>
    private static async Task InjectSpeedAndTextAsync(WinKeyerProtocolHost host, int wpm, string text)
    {
        // Set speed (0x02, WPM byte)
        host.InjectBytes(new byte[] { CommandDefinitions.SpeedCmd, (byte)wpm });

        // Small delay to let speed event process
        await Task.Delay(50).ConfigureAwait(false);

        // Send buffered text
        foreach (char c in text)
        {
            host.InjectByte((byte)c);
        }

        // Wait for keyer to finish. Dit duration = 1200/WPM ms.
        // Average character ≈ 10 dits. Add inter-character spacing.
        int ditMs = 1200 / wpm;
        int estimatedMs = text.Length * 10 * ditMs + 1000;
        await Task.Delay(estimatedMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects (or reconnects) the paddle input poller to the specified COM port.
    /// Persists the port name to config.
    /// </summary>
    public void ConnectPaddlePort(string portName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Stop existing connection
        try { _paddlePoller.Stop(); } catch { }

        // Start on new port
        try
        {
            _paddlePoller.Start(portName);
            _config = _config with { PaddlePortName = portName };
            _configStore.TrySave(_config);
        }
        catch (Exception)
        {
            // Port may not be available
        }
    }

    /// <summary>Updates sidetone frequency live from the UI.</summary>
    public void SetToneFrequency(int hz)
    {
        _sidetone.ToneFrequency = hz;
        _sidetone.Initialize(_config.Sidetone.DeviceId); // Re-init to apply new frequency
        _config = _config with { Sidetone = _config.Sidetone with { FrequencyHz = hz } };
        _configStore.TrySave(_config);
    }

    /// <summary>Updates sidetone volume live from the UI.</summary>
    public void SetToneVolume(double volume)
    {
        _sidetone.Volume = volume;
        _sidetone.Initialize(_config.Sidetone.DeviceId); // Re-init to apply new volume
        _config = _config with { Sidetone = _config.Sidetone with { Volume = volume } };
        _configStore.TrySave(_config);
    }

    /// <summary>
    /// Sets the Station armed state from the Client side. When disarmed, edge frames
    /// are suppressed so the Station does not key the transmitter. This allows the
    /// operator to control the Station remotely without physical access.
    /// </summary>
    /// <param name="armed">True to arm (allow TX), false to disarm (suppress TX).</param>
    public void SetStationArmed(bool armed)
    {
        _stationArmed = armed;
        _suppressEdgeSend = !armed;
        _log?.Info(armed ? "Remote key ARMED." : "Remote key DISARMED.");
        LogDebug($"SetStationArmed: {armed}");
    }

    /// <summary>Gets whether the Station is currently armed (TX enabled).</summary>
    public bool IsStationArmed => _stationArmed;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Port Forward Rule Management (dynamic add/remove while connected)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a forward rule at runtime. Starts the listener immediately if the manager
    /// is running, and pushes the updated rules to the Station if a session is active.
    /// </summary>
    public void AddForwardRule(ForwardRule rule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _portForwardManager.AddRule(rule);

        // Persist to config
        _config = _config with { ForwardRules = _config.ForwardRules.Add(rule) };
        _configStore.TrySave(_config);

        // Push to Station if connected
        if (_sessionActive)
            _ = PushForwardRulesToStationAsync();
    }

    /// <summary>
    /// Removes a forward rule at runtime. Stops its listener and pushes the update to Station.
    /// </summary>
    public void RemoveForwardRule(Guid ruleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _portForwardManager.RemoveRule(ruleId);

        // Persist
        _config = _config with { ForwardRules = _config.ForwardRules.RemoveAll(r => r.Id == ruleId) };
        _configStore.TrySave(_config);

        // Push to Station if connected
        if (_sessionActive)
            _ = PushForwardRulesToStationAsync();
    }

    /// <summary>
    /// Enables or disables a forward rule at runtime.
    /// </summary>
    public void SetForwardRuleEnabled(Guid ruleId, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _portForwardManager.SetRuleEnabled(ruleId, enabled);

        // Persist
        _config = _config with
        {
            ForwardRules = _config.ForwardRules.Replace(
                _config.ForwardRules.First(r => r.Id == ruleId),
                _config.ForwardRules.First(r => r.Id == ruleId) with { Enabled = enabled })
        };
        _configStore.TrySave(_config);

        // Push to Station if connected
        if (_sessionActive)
            _ = PushForwardRulesToStationAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — wiring
    // ──────────────────────────────────────────────────────────────────────────────

    private void WireEvents()
    {
        // Paddle → keyer
        _paddlePoller.StateChanged += OnPaddleStateChanged;

        // WinKeyer host → keyer
        WireWinKeyerEvents();

        // Keyer → sidetone + network
        _keyer.EdgeGenerated += OnEdgeGenerated;
        _keyer.CharacterCompleted += OnCharacterCompleted;

        // Tailscale state → UI
        _tailscaleNode.StateChanged += OnTailscaleStateChanged;

        // Sidecar interactive auth URL
        _sidecarHost.AuthUrlAvailable += OnAuthUrlAvailable;

        // Sidecar failure handler
        _failureHandler.FailureStateChanged += OnSidecarFailureStateChanged;
        _failureHandler.RetryRequested += OnSidecarRetryRequested;

        // Port forward status → UI
        _portForwardManager.RuleStatusChanged += OnForwardRuleStatusChanged;
    }

    private void WireWinKeyerEvents()
    {
        _winKeyerHost.TextReceived += OnWinKeyerTextReceived;
        _winKeyerHost.SpeedChanged += OnWinKeyerSpeedChanged;
        _winKeyerHost.KeyImmediate += OnWinKeyerKeyImmediate;
    }

    private void UnwireWinKeyerEvents()
    {
        _winKeyerHost.TextReceived -= OnWinKeyerTextReceived;
        _winKeyerHost.SpeedChanged -= OnWinKeyerSpeedChanged;
        _winKeyerHost.KeyImmediate -= OnWinKeyerKeyImmediate;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — paddle events
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnPaddleStateChanged(object? sender, PaddleStateChangedEventArgs e)
    {
        _keyer.SetPaddleState(e.DitPressed, e.DahPressed, e.StraightKeyPressed, e.QpcTimestamp);
        PaddleStateChanged?.Invoke(this, e);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — WinKeyer events
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnWinKeyerTextReceived(object? sender, char c)
    {
        // During loopback test, skip echo and status (no serial port to write to,
        // and the echo path was causing characters to appear doubled).
        if (!_loopbackTestActive)
        {
            // Echo immediately — WK2 protocol requires echo when character starts,
            // not when it finishes. N1MM uses echoes for flow control.
            _winKeyerHost.SendCharacterEcho(c);

            // Send "busy/sending" status so N1MM knows we're working.
            // Status 0xC4 = bits 7:6 set (status marker) + bit 2 (buffer sending).
            _winKeyerHost.SendStatus(0xC4);
        }

        _keyer.EnqueueText(c.ToString());
    }

    private void OnWinKeyerSpeedChanged(object? sender, int wpm)
    {
        _keyer.SpeedWpm = wpm;
        _config = _config with { SpeedWpm = wpm };
    }

    private void OnWinKeyerKeyImmediate(object? sender, bool down)
    {
        _keyer.SetKeyImmediate(down);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — keyer edge events
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnEdgeGenerated(object? sender, EdgeEvent e)
    {
        // Always drive sidetone regardless of network state (4.7)
        if (e.KeyDown)
            _sidetone.KeyDown();
        else
            _sidetone.KeyUp();

        // Build and send RWK-PADDLE frame if connected (and not suppressed for local-only playback)
        if (_tailscaleNode.State == TailscaleState.Connected && !_suppressEdgeSend)
        {
            SendEdgeFrame(e);
        }

        // Raise for UI key-state indicator
        EdgeGenerated?.Invoke(this, e);
    }

    private void OnCharacterCompleted(object? sender, char c)
    {
        // Send idle status after each character completes. N1MM uses this to know
        // it can send more. Since we echo immediately on receive, this just confirms
        // the keyer is progressing. When the last char completes, this tells N1MM
        // the whole message is done.
        _winKeyerHost.SendStatus(0xC0);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — RWK-PADDLE frame building (6.1-6.4)
    // ──────────────────────────────────────────────────────────────────────────────

    private void SendEdgeFrame(EdgeEvent e)
    {
        EdgeEntry entry;
        RwkPaddleFrame frame;

        lock (_edgeLock)
        {
            if (_sessionStartQpc == 0)
                _sessionStartQpc = e.QpcTimestamp;

            // Convert QPC ticks to session-relative milliseconds
            long qpcFrequency = Stopwatch.Frequency;
            uint timestampMs = (uint)((e.QpcTimestamp - _sessionStartQpc) * 1000 / qpcFrequency);

            entry = new EdgeEntry(
                _edgeSequence++,
                timestampMs,
                e.KeyDown ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp);

            // Shift recent edges: newest at [0], oldest at end
            int count = Math.Min(_recentEdgeCount + 1, RwkPaddleFrame.MaxEdgeCount);
            for (int i = count - 1; i > 0; i--)
                _recentEdges[i] = _recentEdges[i - 1];
            _recentEdges[0] = entry;
            _recentEdgeCount = count;

            // Build frame with redundancy (6.4)
            ReadOnlySpan<EdgeEntry> edges = _recentEdges.AsSpan(0, _recentEdgeCount);
            RwkPaddleFrame.TryCreate(_sessionEpoch, edges, out frame);
        }

        // Serialize and send asynchronously (fire-and-forget on the keying path)
        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        if (frame.TryWrite(buffer, out int written))
        {
            byte[] data = buffer[..written].ToArray();
            LogDebug($"Sending edge frame: {written} bytes, seq={entry.Sequence}, keyDown={e.KeyDown}");
            _ = _tailscaleNode.SendEdgeAsync(data);
        }
        else
        {
            LogDebug("Frame.TryWrite FAILED — no data sent.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — Heartbeat (6.8)
    // ──────────────────────────────────────────────────────────────────────────────

    private void StartHeartbeat()
    {
        _heartbeatTimer = new System.Threading.Timer(SendHeartbeat, null, HeartbeatInterval, HeartbeatInterval);
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private void SendHeartbeat(object? state)
    {
        if (_tailscaleNode.State != TailscaleState.Connected) return;

        // A heartbeat is an RWK-PADDLE frame carrying just the last edge (no new transition)
        RwkPaddleFrame frame;
        lock (_edgeLock)
        {
            if (_recentEdgeCount == 0)
            {
                // No edges yet — send a single key-up edge at time 0
                var idle = EdgeEntry.KeyUpAt(0, 0);
                RwkPaddleFrame.TryCreate(_sessionEpoch, new ReadOnlySpan<EdgeEntry>(in idle), out frame);
            }
            else
            {
                ReadOnlySpan<EdgeEntry> edges = _recentEdges.AsSpan(0, 1);
                RwkPaddleFrame.TryCreate(_sessionEpoch, edges, out frame);
            }
        }

        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        if (frame.TryWrite(buffer, out int written))
        {
            byte[] data = buffer[..written].ToArray();
            _ = _tailscaleNode.SendEdgeAsync(data);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — Tailscale state
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnTailscaleStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        _log?.Info($"Tailnet: {e.State}" +
            (e.Path != PathType.None ? $" ({e.Path})" : "") +
            (e.RoundTripTime > TimeSpan.Zero ? $" RTT={e.RoundTripTime.TotalMilliseconds:F0}ms" : ""));

        if (e.State == TailscaleState.Connected)
        {
            StartPortForwarding();

            // If we lost a session and just reconnected, try to re-establish.
            if (_sessionActive && _controlStream is null && !string.IsNullOrEmpty(_lastStationAddress))
            {
                _ = AttemptReconnectAsync();
            }
        }
        else if (e.State is TailscaleState.Fault or TailscaleState.Disconnected)
        {
            StopHeartbeat();
            StopPortForwarding();

            // If a session was active, signal disconnect and attempt reconnect.
            if (_sessionActive)
            {
                _controlStream?.Dispose();
                _controlStream = null;

                // Play prosign AS (dit-dah dit-dit-dit) via sidetone to indicate standby.
                _keyer.EnqueueText("AS");
                SessionStatusChanged?.Invoke(this, "Network lost — standby (AS)");

                // Schedule reconnect attempt after a short delay.
                ScheduleReconnect();
            }
        }

        ConnectionStateChanged?.Invoke(this, e);
    }

    private void OnAuthUrlAvailable(object? sender, string authUrl)
    {
        AuthUrlAvailable?.Invoke(this, authUrl);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Reconnect logic
    // ──────────────────────────────────────────────────────────────────────────────

    private void ScheduleReconnect()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            _sessionActive = false;
            SessionStatusChanged?.Invoke(this, "Reconnect failed — session ended.");
            return;
        }

        // Exponential backoff: 2s, 4s, 8s, 16s, 32s
        int delayMs = (int)Math.Pow(2, _reconnectAttempts + 1) * 1000;
        _reconnectAttempts++;

        SessionStatusChanged?.Invoke(this, $"Reconnecting ({_reconnectAttempts}/{MaxReconnectAttempts}) in {delayMs / 1000}s...");

        _reconnectTimer?.Dispose();
        _reconnectTimer = new System.Threading.Timer(_ => _ = AttemptReconnectAsync(), null, delayMs, Timeout.Infinite);
    }

    private async Task AttemptReconnectAsync()
    {
        if (!_sessionActive || string.IsNullOrEmpty(_lastStationAddress)) return;
        if (_tailscaleNode.State != TailscaleState.Connected)
        {
            // Network not back yet — wait for next state change.
            return;
        }

        try
        {
            SessionStatusChanged?.Invoke(this, $"Reconnecting to {_lastStationAddress}...");
            await ConnectToStationAsync(_lastStationAddress).ConfigureAwait(false);
            // Success — ConnectToStationAsync sets _sessionActive and plays "OK READY"
            SessionStatusChanged?.Invoke(this, $"Reconnected to {_lastStationAddress}");
        }
        catch
        {
            // Failed — schedule another attempt.
            ScheduleReconnect();
        }
    }

    /// <summary>
    /// Submits an auth key to the running sidecar (for the manual "paste key" fallback).
    /// After submission, status polling will pick up the state transition.
    /// If the key is accepted, the config is updated with the DPAPI-encrypted key.
    /// </summary>
    public async Task SubmitAuthKeyAsync(string authKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sidecarHost.SubmitAuthKeyAsync(authKey).ConfigureAwait(false);

        // Persist the key DPAPI-encrypted so headless re-use works.
        _config = _config with { Tailscale = _config.Tailscale with { AuthKey = authKey } };
        _configStore.TrySave(_config);
    }

    /// <summary>
    /// Clears the persisted Tailscale auth key from config.
    /// Called when the user chooses to delete their authorization.
    /// </summary>
    public void ClearTailscaleAuth()
    {
        _config = _config with { Tailscale = _config.Tailscale with { AuthKey = null } };
        _configStore.TrySave(_config);
        _log?.Info("Tailscale authorization cleared from config.");
    }

    /// <summary>
    /// Sets the pairing secret used for HMAC authentication with the Station.
    /// Persists to config so it survives restarts.
    /// </summary>
    public void SetPairingSecret(string secret)
    {
        _config = _config with { Tailscale = _config.Tailscale with { PairingSecret = secret } };
        _configStore.TrySave(_config);
        _log?.Info("Station pairing key updated.");
    }

    /// <summary>
    /// Sets the Station address and persists it immediately. Called by the UI before
    /// Connect so that reconnect attempts and restarts use the new address.
    /// </summary>
    public void SetStationAddress(string address)
    {
        _config = _config with { Tailscale = _config.Tailscale with { StationAddress = address } };
        _lastStationAddress = address;
        _configStore.TrySave(_config);
    }

    /// <summary>
    /// Initiates a control-channel connection to the Station at the given Tailscale address.
    /// Performs the HMAC challenge/response handshake (11.2-11.4).
    /// Persists the address in config for reconnection on next launch.
    /// </summary>
    /// <param name="stationAddress">The Station's Tailscale IP (e.g. "100.107.101.81").</param>
    /// <exception cref="InvalidOperationException">Thrown if Tailscale is not connected.</exception>
    public async Task ConnectToStationAsync(string stationAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(stationAddress);

        if (_tailscaleNode.State != TailscaleState.Connected)
            throw new InvalidOperationException("Tailscale is not connected. Wait for link to come up.");

        // Persist the station address for next time.
        _config = _config with { Tailscale = _config.Tailscale with { StationAddress = stationAddress } };
        _configStore.TrySave(_config);
        _lastStationAddress = stationAddress;

        // Configure the peer on the sidecar so edge UDP datagrams are forwarded to the Station.
        // Both sidecars use the same fixed edge tailnet port (41373).
        const int EdgePort = 41373;
        await _sidecarHost.SetPeerAsync(stationAddress, EdgePort).ConfigureAwait(false);
        LogDebug($"Peer set to {stationAddress}:{EdgePort} on sidecar.");

        // Open a TCP control channel to the Station's SessionManager port.
        _controlStream = await _tailscaleNode.ConnectControlAsync(stationAddress, DefaultControlPort)
            .ConfigureAwait(false);

        // Perform HMAC challenge/response handshake (11.2-11.4).
        // 1. Read 32-byte nonce from Station.
        byte[] nonce = new byte[32];
        int totalRead = 0;
        while (totalRead < 32)
        {
            int read = await _controlStream.ReadAsync(nonce.AsMemory(totalRead, 32 - totalRead))
                .ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException(
                    "Station closed connection before sending nonce. " +
                    "Ensure the Station has completed Tailscale login and is showing 'ARMED'.");
            totalRead += read;
        }

        // 2. Compute HMAC-SHA256(nonce, pairing_secret).
        string? pairingSecret = _config.Tailscale.PairingSecret;
        if (string.IsNullOrEmpty(pairingSecret))
            pairingSecret = "rwk-default-pairing-secret-v2";

        byte[] secretBytes = System.Text.Encoding.UTF8.GetBytes(pairingSecret);
        byte[] hmac = System.Security.Cryptography.HMACSHA256.HashData(secretBytes, nonce);

        // 3. Send HMAC response.
        await _controlStream.WriteAsync(hmac).ConfigureAwait(false);
        await _controlStream.FlushAsync().ConfigureAwait(false);

        // 4. Read response: "OK" (2 bytes) or "FAIL"/"BUSY" (4 bytes).
        byte[] responseBuf = new byte[4];
        int respRead = await _controlStream.ReadAsync(responseBuf).ConfigureAwait(false);
        string response = System.Text.Encoding.UTF8.GetString(responseBuf, 0, respRead);

        if (response.StartsWith("OK"))
        {
            // Session established!
            _log?.Info($"Session established with Station at {stationAddress}.");
            _sessionEpoch++;
            _sessionStartQpc = 0;
            _edgeSequence = 0;
            _recentEdgeCount = 0;
            _sessionActive = true;
            _reconnectAttempts = 0;

            // Play "OK READY" locally via sidetone only — suppress network sending.
            _suppressEdgeSend = true;
            _keyer.EnqueueText("OK READY");
            // The flag will be cleared when we detect the text queue is empty (next idle).
            _ = ClearSuppressAfterTextAsync();

            StartHeartbeat();

            // Push forward rules to the Station so it registers inbound forwards.
            await PushForwardRulesToStationAsync().ConfigureAwait(false);

            // Wire the tunnel dial delegate so TCP/UDP forwards actually relay through the sidecar.
            string peer = stationAddress;
            _portForwardManager.TunnelDial = async (port, ct) =>
            {
                IPEndPoint ep = await _sidecarHost.CreateOutboundForwardAsync(peer, port, ct)
                    .ConfigureAwait(false);
                var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
                await tcp.ConnectAsync(ep, ct).ConfigureAwait(false);
                return tcp.GetStream();
            };

            _portForwardManager.UdpTunnelBind = async (port, ct) =>
            {
                var host = (TsnetSidecarHost)_sidecarHost;
                return await host.CreateOutboundUdpForwardAsync(peer, port, ct)
                    .ConfigureAwait(false);
            };
        }
        else if (response.StartsWith("BUSY"))
        {
            _controlStream.Dispose();
            _controlStream = null;
            throw new InvalidOperationException("Station is busy with another session.");
        }
        else
        {
            _controlStream.Dispose();
            _controlStream = null;
            throw new InvalidOperationException(
                $"Station rejected connection: {response}. " +
                "Check that the Station Key matches (RWK menu → Show Pairing Key on Station).");
        }
    }

    private void OnSidecarFailureStateChanged(object? sender, SidecarFailureStateChangedEventArgs e)
    {
        SidecarFailureChanged?.Invoke(this, e);
    }

    private async void OnSidecarRetryRequested(object? sender, EventArgs e)
    {
        // Attempt to restart the sidecar
        try
        {
            string? authKey = _config.Tailscale.AuthKey;
            if (!string.IsNullOrEmpty(authKey))
            {
                await _tailscaleNode.StartAsync(authKey).ConfigureAwait(false);
                _failureHandler.ReportRecovery();
            }
        }
        catch
        {
            // Retry failed; handler will try again on next interval
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — Port forwarding
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnForwardRuleStatusChanged(object? sender, ForwardRuleStatusChangedEventArgs e)
    {
        ForwardRuleStatusChanged?.Invoke(this, e);
    }

    private void StartPortForwarding()
    {
        try
        {
            _portForwardManager.Start();
        }
        catch
        {
            // Non-fatal: individual rules may error, but that's reported via RuleStatusChanged
        }
    }

    private void StopPortForwarding()
    {
        try
        {
            _portForwardManager.Stop();
        }
        catch
        {
            // Best effort
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — component start/stop
    // ──────────────────────────────────────────────────────────────────────────────

    private void StartLocalComponents()
    {
        // Sidetone always starts (4.7)
        _sidetone.Initialize(_config.Sidetone.DeviceId);

        // Keyer core
        _keyer.Start();

        // Paddle poller (only if a port is configured)
        if (!string.IsNullOrEmpty(_config.PaddlePortName))
        {
            try
            {
                _paddlePoller.Start(_config.PaddlePortName);
            }
            catch
            {
                // Port may not exist; practice still works via WinKeyer input
            }
        }

        // WinKeyer host (only if a port is configured)
        if (!string.IsNullOrEmpty(_config.WinKeyerPortName))
        {
            try
            {
                _winKeyerHost.Start(_config.WinKeyerPortName);
            }
            catch
            {
                // Port may not exist; paddle input still works
            }
        }
    }

    private void StopLocalComponents()
    {
        _paddlePoller.Stop();
        _winKeyerHost.Stop();
        _keyer.Stop();
        _sidetone.Stop();
    }

    private async Task StartTailscaleAsync()
    {
        string? authKey = _config.Tailscale.AuthKey;

        try
        {
            // Start the sidecar regardless of whether an auth key is present.
            // If no key is provided, the sidecar will wait in NeedsAuth state and emit
            // an interactive login URL via the status document. If the sidecar's state
            // directory already has a persisted identity, it goes straight to Connected.
            await _tailscaleNode.StartAsync(authKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Sidecar failed to start — degrade gracefully (16.11)
            string resolvedPath = SidecarPath.Resolve(
                SidecarPath.GetBaseDirectory(),
                SidecarPath.DefaultExecutableName);

            _failureHandler.ReportFailure(new SidecarFailure(
                SidecarFailureKind.NotFound,
                resolvedPath,
                ex.Message));
        }
    }

    private async Task StopTailscaleAsync()
    {
        StopPortForwarding();

        try
        {
            await _tailscaleNode.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Private — config application
    // ──────────────────────────────────────────────────────────────────────────────

    private void ApplyConfigToComponents(ClientConfig cfg)
    {
        _keyer.SpeedWpm = cfg.SpeedWpm;
        _keyer.Weight = cfg.Weight;
        _keyer.Mode = cfg.KeyerMode;
        _keyer.PaddleReverse = cfg.PaddleReverse;
        _paddlePoller.DebounceTime = cfg.DebounceTime;
        _sidetone.ToneFrequency = cfg.Sidetone.FrequencyHz;
        _sidetone.Volume = cfg.Sidetone.Volume;

        // Load forward rules into the manager
        foreach (var rule in cfg.ForwardRules)
        {
            _portForwardManager.AddRule(rule);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopHeartbeat();
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _controlStream?.Dispose();
        _controlStream = null;

        _paddlePoller.StateChanged -= OnPaddleStateChanged;
        UnwireWinKeyerEvents();
        _keyer.EdgeGenerated -= OnEdgeGenerated;
        _keyer.CharacterCompleted -= OnCharacterCompleted;
        _tailscaleNode.StateChanged -= OnTailscaleStateChanged;
        _sidecarHost.AuthUrlAvailable -= OnAuthUrlAvailable;
        _failureHandler.FailureStateChanged -= OnSidecarFailureStateChanged;
        _failureHandler.RetryRequested -= OnSidecarRetryRequested;
        _portForwardManager.RuleStatusChanged -= OnForwardRuleStatusChanged;

        _tailscaleNode.Dispose();
        _failureHandler.Dispose();
        _portForwardManager.Dispose();
        _sidetone.Dispose();
        _keyer.Dispose();
        _hardwareWinKeyerHost?.Dispose();
        _winKeyerHost.Dispose();
        _paddlePoller.Dispose();
    }

    private void LogDebug(string msg)
    {
        _log?.Debug(msg);
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "client-debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private async Task ClearSuppressAfterTextAsync()
    {
        // Wait a few seconds for "OK READY" to finish playing via sidetone.
        await Task.Delay(3000).ConfigureAwait(false);
        // Only resume edge sending if the station is armed.
        _suppressEdgeSend = !_stationArmed;
    }

    /// <summary>
    /// Pushes the Client's active forward rules to the Station over the control channel.
    /// The Station registers corresponding inbound forwards on its sidecar so that
    /// tailnet connections on the Station port are relayed to localhost.
    /// </summary>
    private async Task PushForwardRulesToStationAsync()
    {
        if (_controlStream is null) return;

        var rules = _config.ForwardRules;
        if (rules.Count == 0) return;

        try
        {
            // Simple length-prefixed JSON message: 4-byte big-endian length + UTF-8 JSON body.
            // Message format: { "type": "forward_rules", "rules": [ { "port": N, "protocol": "tcp"|"udp" }, ... ] }
            var ruleList = rules
                .Where(r => r.Enabled)
                .Select(r => new { port = r.StationPort, protocol = r.Protocol.ToString().ToLowerInvariant(), targetAddress = r.StationTargetAddress })
                .ToArray();

            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "forward_rules",
                rules = ruleList
            });

            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(body.Length));

            await _controlStream.WriteAsync(lengthPrefix).ConfigureAwait(false);
            await _controlStream.WriteAsync(body).ConfigureAwait(false);
            await _controlStream.FlushAsync().ConfigureAwait(false);

            LogDebug($"Pushed {ruleList.Length} forward rules to Station.");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to push forward rules: {ex.Message}");
        }
    }
}
