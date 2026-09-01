/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.IO;
using RWK.Shared.Net;
using RWK.Station.Controllers;

namespace RWK.Station;

/// <summary>
/// Station main window implementing the full layout per requirements 13.5–13.13, 13.16, 11.7, 15.6, 15.7.
/// Wired to <see cref="StationController"/> which orchestrates all backend components.
/// 
/// Key behaviors:
/// - Uses Windows system colors (no dark theme).
/// - COM port is optional at startup — keying output connects when a port is selected.
/// - Tailscale auto-connects if an auth key is stored; shows interactive login if not.
/// </summary>
public partial class MainForm : Form
{
    private static readonly Color ArmedGreen = Color.FromArgb(0, 128, 0);
    private static readonly Color SafeRed = Color.FromArgb(180, 0, 0);

    /// <summary>Gets the application version string from the assembly.</summary>
    internal static string AppVersion =>
        typeof(MainForm).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private bool _isSafeLatched;
    private readonly ToolTip _toolTip;
    private StationController? _controller;
    private System.Windows.Forms.Timer? _portPollTimer;
    private System.Windows.Forms.Timer? _keyIndicatorTimer;
    private NotifyIcon? _trayIcon;

    // "PLEASE WAIT" overlay — shown from startup until Connected or Wizard opens
    private Panel? _waitOverlay;
    private DateTime? _sessionStartTime;

    public MainForm()
    {
        InitializeComponent();
        Text = $"RWK Router/Keyer Station Version {AppVersion} — Any Rig, Any Internet, Anytime";

        _toolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

        // System tray icon — minimize to tray
        InitializeTrayIcon();

        // Wire Re-Arm button click (13.8).
        _reArmButton.Click += OnReArmClick;

        // Wire Disconnect button (11.7).
        _disconnectButton.Click += OnDisconnectClick;

        // Wire copy IP button.
        _copyIpButton.Click += OnCopyIpClick;

        // Wire COM port selection changed — connect the port dynamically.
        _comPortCombo.SelectedIndexChanged += OnComPortSelectionChanged;

        // Wire pin selection changes to persist config.
        _keyLineRts.CheckedChanged += OnKeyingConfigChanged;
        _keyLineDtr.CheckedChanged += OnKeyingConfigChanged;
        _keyInvertCheck.CheckedChanged += OnKeyingConfigChanged;
        _pttLineRts.CheckedChanged += OnKeyingConfigChanged;
        _pttLineDtr.CheckedChanged += OnKeyingConfigChanged;
        _pttLineNone.CheckedChanged += OnKeyingConfigChanged;
        _pttInvertCheck.CheckedChanged += OnKeyingConfigChanged;

        // FlexRadio discovery capture is auto-enabled when Client pushes [Flex] rules.

        // Logger Input controls.
        _loggerEnableCheck.CheckedChanged += OnLoggerEnableChanged;
        _loggerComPortCombo.SelectedIndexChanged += OnLoggerPortChanged;

        // Populate COM port dropdown with available ports.
        PopulateComPorts();

        // Set initial UI state.
        SetSafeState(latched: false);

        // Create and wire the controller on form load so the UI thread is available for Invoke.
        Load += OnFormLoad;
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "RWK Station",
            Visible = false
        };

        string icoPath = Path.Combine(AppContext.BaseDirectory, "rwk.ico");
        if (File.Exists(icoPath))
            _trayIcon.Icon = new Icon(icoPath);

        _trayIcon.Click += (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            if (_trayIcon is not null)
                _trayIcon.Visible = true;
        }
        else
        {
            if (_trayIcon is not null)
                _trayIcon.Visible = false;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Controller initialization and wiring
    // ────────────────────────────────────────────────────────────────

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        ShowWaitOverlay();

        // Start polling for COM port changes (2s interval, same pattern as Client).
        _portPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _portPollTimer.Tick += OnPortPollTimerTick;
        _portPollTimer.Start();

        // Fast poll for KEY/PTT indicator LEDs (50ms).
        _keyIndicatorTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _keyIndicatorTimer.Tick += OnKeyIndicatorTimerTick;
        _keyIndicatorTimer.Start();

        var configStore = ConfigStore.ForStation(diagnostics: msg => SetStatusText(msg));
        _controller = new StationController(configStore, diagnostics: msg =>
        {
            SetStatusText(msg);
            // Also append to a log file next to the exe for debugging.
            try { RotatingFileLog.Append("station.log", msg); } catch { }
        });

        // Subscribe to controller events.
        _controller.StateChanged += OnControllerStateChanged;
        _controller.SessionStarted += OnControllerSessionStarted;
        _controller.SessionEnded += OnControllerSessionEnded;
        _controller.SafeLatched += OnControllerSafeLatched;
        _controller.ReplayerStateChanged += OnControllerReplayerStateChanged;
        _controller.TailscaleStateChanged += OnControllerTailscaleStateChanged;
        _controller.SidecarFailureStateChanged += OnControllerSidecarFailureStateChanged;
        _controller.StartupFailed += OnControllerStartupFailed;
        _controller.AuthUrlAvailable += OnControllerAuthUrlAvailable;
        _controller.ForwardRulesReceived += OnForwardRulesReceived;
        _controller.LoggerPortOpenFailed += OnControllerLoggerPortOpenFailed;

        // Start the controller — it will skip keying output if no port is configured,
        // and auto-connect Tailscale if an auth key is present.
        await _controller.StartAsync().ConfigureAwait(false);

        // If a COM port is already selected in the dropdown, connect it now.
        // (SelectedIndexChanged doesn't fire for the initial auto-selection.)
        if (_comPortCombo.InvokeRequired)
            Invoke(() => { LoadKeyingConfigToUi(); TryConnectSelectedPort(); LoadLoggerConfigToUi(); });
        else
        {
            LoadKeyingConfigToUi();
            TryConnectSelectedPort();
            LoadLoggerConfigToUi();
        }

        // Check GitHub for a newer build (fire-and-forget; fails silently offline).
        _ = CheckForUpdatesAsync();
    }

    // ────────────────────────────────────────────────────────────────
    // Auto-update check (banner above the status strip)
    // ────────────────────────────────────────────────────────────────

    private string? _updateInstallerUrl;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = typeof(MainForm).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
            var info = await RWK.Shared.Net.UpdateChecker.CheckForUpdateAsync(current).ConfigureAwait(false);
            if (info is null) return;

            void ShowBanner()
            {
                _updateInstallerUrl = info.InstallerUrl;
                _updateBannerLabel.Text = "";
                _updateBannerLabel.Links.Clear();
                string prefix = $"New version {info.Version} available — ";
                string link = "Install";
                _updateBannerLabel.Text = prefix + link;
                _updateBannerLabel.LinkArea = new LinkArea(prefix.Length, link.Length);
                _updateBannerLabel.LinkClicked -= OnUpdateLinkClicked;
                _updateBannerLabel.LinkClicked += OnUpdateLinkClicked;
                _updateBanner.Visible = true;
            }

            if (InvokeRequired) Invoke(ShowBanner); else ShowBanner();
        }
        catch { /* never let the update check disrupt the app */ }
    }

    private async void OnUpdateLinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateInstallerUrl)) return;

        var proceed = MessageBox.Show(
            this,
            "RWK will download the latest installer and launch it.\n\n" +
            "If Windows shows a \"Windows protected your PC\" (SmartScreen) message, " +
            "choose \"More info\" and then \"Run anyway\" to continue.\n\n" +
            "The app will close so the installer can update it. Continue?",
            "Install Update",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (proceed != DialogResult.OK) return;

        _updateBannerLabel.Text = "Downloading update…";
        _updateBannerLabel.LinkArea = new LinkArea(0, 0);

        string? launched = await RWK.Shared.Net.UpdateChecker
            .DownloadAndLaunchInstallerAsync(_updateInstallerUrl).ConfigureAwait(true);

        if (launched is null)
        {
            MessageBox.Show(
                this,
                "The update could not be downloaded. Please check your internet connection, " +
                "or download the latest release manually from:\n\n" +
                "https://github.com/w1ve/rwk-router-keyer/releases/latest",
                "Update Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _updateBannerLabel.Text = "Update download failed — click to retry: Install";
            _updateBannerLabel.LinkArea = new LinkArea(_updateBannerLabel.Text.Length - "Install".Length, "Install".Length);
            return;
        }

        // Installer launched successfully — shut this app down cleanly and promptly so the
        // COM ports and sidecar are released before the installer replaces the executables.
        // Close() runs OnFormClosing, which stops the controller (releases ports, sidecar).
        _updateBannerLabel.Text = "Update started — closing…";
        _updateBannerLabel.LinkArea = new LinkArea(0, 0);
        Close();
    }

    private void TryConnectSelectedPort()
    {
        string? selectedPort = _comPortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(selectedPort) && _controller is not null)
        {
            try
            {
                // ConnectKeyingPort retries the open once internally before throwing.
                _controller.ConnectKeyingPort(selectedPort, GetKeyingConfig());
                _comPortErrorLabel.Visible = false;
            }
            catch (Exception ex)
            {
                _comPortErrorLabel.Text = $"⚠ {ex.Message}";
                _comPortErrorLabel.Visible = true;
                ShowPortOpenError("keying", selectedPort, ex);
            }
        }
    }

    /// <summary>
    /// Shows a modal error dialog when a serial port (keying or logger) cannot be opened
    /// after the automatic retry, with guidance to restart VSPE if virtual ports are in use.
    /// </summary>
    private void ShowPortOpenError(string role, string portName, Exception ex)
    {
        if (InvokeRequired) { Invoke(() => ShowPortOpenError(role, portName, ex)); return; }

        MessageBox.Show(
            this,
            $"The {role} port {portName} could not be opened, even after a retry.\n\n" +
            $"Error: {ex.Message}\n\n" +
            "If you are using virtual COM ports (e.g. VSPE / com0com), try restarting that " +
            "software — the port may be held by a stale or crashed instance. Also confirm no " +
            "other application (logger, CAT software) has the port open, then reselect it.",
            $"RWK Station — {char.ToUpper(role[0]) + role.Substring(1)} Port Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void OnControllerLoggerPortOpenFailed(object? sender, LoggerPortOpenFailedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerLoggerPortOpenFailed(sender, e)); return; }
        ShowPortOpenError("logger", e.PortName, e.Error);
    }

    /// <summary>
    /// Opens the current (non-rotated) log file for the given name in Notepad. Creates an
    /// empty file first if it doesn't exist yet, so Notepad always opens cleanly.
    /// </summary>
    private void OpenLogInNotepad(string logFileName)
    {
        try
        {
            string path = RWK.Shared.IO.RotatingFileLog.GetLogFilePath(logFileName);
            if (!File.Exists(path))
                File.WriteAllText(path, $"(no {logFileName} entries yet)\n");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {logFileName}:\n{ex.Message}",
                "Open Log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // COM port polling and dynamic connection
    // ────────────────────────────────────────────────────────────────

    private void OnPortPollTimerTick(object? sender, EventArgs e)
    {
        RefreshComPorts();
    }

    private void OnKeyIndicatorTimerTick(object? sender, EventArgs e)
    {
        if (_controller is null) return;
        
        bool keyDown = _controller.IsKeyDown;
        bool pttOn = _controller.IsPttOn;
        
        // Make LED "sticky" — once it turns on, keep it on for at least 200ms
        // so fast CW elements are visible to the eye.
        if (keyDown) _lastKeyDownTime = DateTime.UtcNow;
        if (pttOn) _lastPttOnTime = DateTime.UtcNow;
        
        bool showKey = keyDown || (DateTime.UtcNow - _lastKeyDownTime).TotalMilliseconds < 200;
        bool showPtt = pttOn || (DateTime.UtcNow - _lastPttOnTime).TotalMilliseconds < 200;
        
        _keyIndicator.ForeColor = showKey ? Color.FromArgb(255, 60, 60) : SystemColors.GrayText;
        _pttIndicator.ForeColor = showPtt ? Color.FromArgb(255, 180, 0) : SystemColors.GrayText;

        // Update session duration display
        if (_sessionStartTime.HasValue)
        {
            var elapsed = DateTime.UtcNow - _sessionStartTime.Value;
            _sessionDurationValue.Text = elapsed.ToString(@"hh\:mm\:ss");
        }

        // Reconcile the Session box against the authoritative pairing state every tick.
        // This self-heals any UI drift from missed/spurious session events (e.g. a rejected
        // non-owner reconnect, or a silently-dropped socket) so the box ALWAYS reflects reality.
        ReconcileSessionBox();
    }

    private DateTime _lastKeyDownTime = DateTime.MinValue;
    private DateTime _lastPttOnTime = DateTime.MinValue;

    /// <summary>
    /// Forces the Session box (client text + Unpair button) to match the controller's
    /// authoritative <see cref="StationController.CurrentSession"/>. Idempotent: only
    /// writes when the displayed state diverges from the real state.
    /// </summary>
    private void ReconcileSessionBox()
    {
        var session = _controller?.CurrentSession;
        bool paired = session is not null;

        if (paired)
        {
            // Ensure text + button reflect the active session.
            string expectedText = $"{session!.ClientName} ({session.ClientAddress})";
            if (!_disconnectButton.Enabled || _sessionClientValue.Text != expectedText)
            {
                _sessionClientValue.Text = expectedText;
                _clientNameStatus.Text = session.ClientName;
                _disconnectButton.Enabled = true;
                _sessionStartTime ??= DateTime.UtcNow;
            }
        }
        else
        {
            // No session: ensure the box shows "(none)" and Unpair is disabled.
            if (_disconnectButton.Enabled || _sessionClientValue.Text != "(none)")
            {
                _sessionClientValue.Text = "(none)";
                _sessionDurationValue.Text = "\u2014";
                _clientNameStatus.Text = "";
                _disconnectButton.Enabled = false;
                _sessionStartTime = null;
            }
        }
    }

    private void RefreshComPorts()
    {
        var currentPorts = GetSortedComPorts();
        var existingPorts = new string[_comPortCombo.Items.Count];
        for (int i = 0; i < _comPortCombo.Items.Count; i++)
            existingPorts[i] = (string)_comPortCombo.Items[i]!;

        if (!currentPorts.SequenceEqual(existingPorts))
        {
            string? selected = _comPortCombo.SelectedItem as string;
            _comPortCombo.Items.Clear();
            foreach (var p in currentPorts)
                _comPortCombo.Items.Add(p);

            if (selected is not null && _comPortCombo.Items.Contains(selected))
                _comPortCombo.SelectedItem = selected;
            else if (_comPortCombo.Items.Count > 0)
                _comPortCombo.SelectedIndex = 0;
        }

        // Also refresh the logger port list (excludes the keying port).
        RefreshLoggerComPorts();
    }

    private void OnComPortSelectionChanged(object? sender, EventArgs e)
    {
        string? selectedPort = _comPortCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedPort)) return;

        // Hide previous error
        _comPortErrorLabel.Visible = false;
        _comPortErrorLabel.Text = "";

        // Attempt to connect the keying output on this port.
        try
        {
            _controller?.ConnectKeyingPort(selectedPort, GetKeyingConfig());
            SetStatusText($"Keying output opened on {selectedPort}.");
            PersistKeyingConfig();
        }
        catch (Exception ex)
        {
            _comPortErrorLabel.Text = $"⚠ {ex.Message}";
            _comPortErrorLabel.Visible = true;
            SetStatusText($"COM port error: {ex.Message}");
        }
    }

    private bool _suppressKeyingConfigEvents;

    private void OnKeyingConfigChanged(object? sender, EventArgs e)
    {
        if (_suppressKeyingConfigEvents) return;

        // Guard: Key and PTT cannot use the same line. If the user selects a PTT line that
        // matches the Key line (both DTR or both RTS), force PTT to None — a shared line is
        // invalid and would fail to open the keying port.
        bool keyIsRts = _keyLineRts.Checked;
        bool pttSharesKeyLine =
            (keyIsRts && _pttLineRts.Checked) || (!keyIsRts && _pttLineDtr.Checked);
        if (pttSharesKeyLine)
        {
            _suppressKeyingConfigEvents = true;
            try
            {
                _pttLineNone.Checked = true; // resets _pttLineRts/_pttLineDtr in the radio group
            }
            finally
            {
                _suppressKeyingConfigEvents = false;
            }
            SetStatusText("PTT set to (None): it cannot share the same line as the Key output.");
        }

        // Reconnect with new pin settings if a port is selected.
        string? selectedPort = _comPortCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedPort) || _controller is null) return;

        _comPortErrorLabel.Visible = false;
        try
        {
            _controller.ConnectKeyingPort(selectedPort, GetKeyingConfig());
            PersistKeyingConfig();
        }
        catch (Exception ex)
        {
            _comPortErrorLabel.Text = $"⚠ {ex.Message}";
            _comPortErrorLabel.Visible = true;
        }
    }

    private void PersistKeyingConfig()
    {
        if (_controller is null) return;
        string? port = _comPortCombo.SelectedItem as string;
        var config = _controller.Config with
        {
            KeyingPortName = port,
            KeyLine = _keyLineRts.Checked ? KeyingLine.RTS : KeyingLine.DTR,
            PttLine = _pttLineNone.Checked ? KeyingLine.None
                : _pttLineRts.Checked ? KeyingLine.RTS : KeyingLine.DTR,
            KeyInvert = _keyInvertCheck.Checked,
            PttInvert = _pttInvertCheck.Checked
        };
        _controller.SaveConfig(config);
    }

    private void LoadKeyingConfigToUi()
    {
        if (_controller is null) return;
        var config = _controller.Config;

        // Set COM port
        if (!string.IsNullOrEmpty(config.KeyingPortName) && _comPortCombo.Items.Contains(config.KeyingPortName))
            _comPortCombo.SelectedItem = config.KeyingPortName;

        // Set Key Line
        if (config.KeyLine == KeyingLine.RTS) _keyLineRts.Checked = true;
        else _keyLineDtr.Checked = true;

        // Set PTT Line
        if (config.PttLine == KeyingLine.None) _pttLineNone.Checked = true;
        else if (config.PttLine == KeyingLine.RTS) _pttLineRts.Checked = true;
        else _pttLineDtr.Checked = true;

        // Set inversion
        _keyInvertCheck.Checked = config.KeyInvert;
        _pttInvertCheck.Checked = config.PttInvert;
    }

    private KeyingOutputConfig GetKeyingConfig()
    {
        string portName = (_comPortCombo.SelectedItem as string) ?? "";
        KeyingLine keyLine = _keyLineRts.Checked ? KeyingLine.RTS : KeyingLine.DTR;
        KeyingLine pttLine = _pttLineNone.Checked ? KeyingLine.None
            : _pttLineRts.Checked ? KeyingLine.RTS : KeyingLine.DTR;
        bool keyInvert = _keyInvertCheck.Checked;
        bool pttInvert = _pttInvertCheck.Checked;

        return new KeyingOutputConfig(portName, keyLine, pttLine, keyInvert, pttInvert);
    }

    // ────────────────────────────────────────────────────────────────
    // Logger Input controls
    // ────────────────────────────────────────────────────────────────

    private void OnLoggerEnableChanged(object? sender, EventArgs e)
    {
        bool enabled = _loggerEnableCheck.Checked;
        _loggerComPortCombo.Enabled = enabled;

        if (_controller is null) return;

        if (enabled)
        {
            string? port = _loggerComPortCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(port) && port != "(None)")
            {
                _controller.StartLoggerHost(port);
            }
        }
        else
        {
            _controller.StopLoggerHost();
        }
    }

    private void OnLoggerPortChanged(object? sender, EventArgs e)
    {
        if (!_loggerEnableCheck.Checked || _controller is null) return;

        string? port = _loggerComPortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(port) && port != "(None)")
        {
            _controller.StartLoggerHost(port);
        }
    }

    private void RefreshLoggerComPorts()
    {
        string? keyingPort = _comPortCombo.SelectedItem as string;
        var allPorts = GetSortedComPorts();

        // Filter out the keying port, prepend (None).
        var loggerPorts = new[] { "(None)" }.Concat(
            allPorts.Where(p =>
                !string.Equals(p, keyingPort, StringComparison.OrdinalIgnoreCase))).ToArray();

        string? selected = _loggerComPortCombo.SelectedItem as string;
        var existingPorts = new string[_loggerComPortCombo.Items.Count];
        for (int i = 0; i < _loggerComPortCombo.Items.Count; i++)
            existingPorts[i] = (string)_loggerComPortCombo.Items[i]!;

        if (!loggerPorts.SequenceEqual(existingPorts))
        {
            _loggerComPortCombo.Items.Clear();
            foreach (var p in loggerPorts)
                _loggerComPortCombo.Items.Add(p);

            if (selected is not null && _loggerComPortCombo.Items.Contains(selected))
                _loggerComPortCombo.SelectedItem = selected;
            else if (_loggerComPortCombo.Items.Count > 0)
                _loggerComPortCombo.SelectedIndex = 0;
        }
    }

    private void LoadLoggerConfigToUi()
    {
        if (_controller is null) return;
        var config = _controller.Config;

        RefreshLoggerComPorts();

        if (config.LoggerInputEnabled && !string.IsNullOrEmpty(config.LoggerPortName))
        {
            if (_loggerComPortCombo.Items.Contains(config.LoggerPortName))
                _loggerComPortCombo.SelectedItem = config.LoggerPortName;

            _loggerEnableCheck.Checked = true;
            _loggerComPortCombo.Enabled = true;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Controller event handlers (all marshal to UI thread)
    // ────────────────────────────────────────────────────────────────

    private void OnControllerStateChanged(object? sender, StationControllerStateChangedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerStateChanged(sender, e)); return; }

        switch (e.NewState)
        {
            case StationControllerState.Armed:
                SetSafeState(latched: false);
                // Don't overwrite "Session: Active" if a session is already connected
                if (_controller?.CurrentSession is null)
                    _sessionStateStatus.Text = "Session: Idle";
                _linkIndicatorStatus.Text = "● Link: Up";
                _linkIndicatorStatus.ForeColor = Color.LimeGreen;
                break;
            case StationControllerState.Faulted:
                _linkIndicatorStatus.Text = "● Link: FAULT";
                _linkIndicatorStatus.ForeColor = Color.Red;
                break;
            case StationControllerState.Stopped:
                _linkIndicatorStatus.Text = "● Link: —";
                _linkIndicatorStatus.ForeColor = Color.Gray;
                _sessionStateStatus.Text = "Session: Stopped";
                break;
        }
    }

    private void OnControllerSessionStarted(object? sender, SessionEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerSessionStarted(sender, e)); return; }
        _sessionStartTime = DateTime.UtcNow;
        SetSessionInfo(e.ClientName, e.ClientAddress);
    }

    private void OnControllerSessionEnded(object? sender, SessionEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerSessionEnded(sender, e)); return; }
        _sessionStartTime = null;
        ClearSessionInfo(e.Reason);
    }

    private void OnControllerSafeLatched(object? sender, FailSafeTriggeredEventArgs e)
    {
        NotifySafeLatched();
    }

    private void OnControllerReplayerStateChanged(object? sender, EdgeReplayerStateChangedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerReplayerStateChanged(sender, e)); return; }

        if (e.IsSafeLatched)
        {
            SetSafeState(latched: true);
        }
        else if (e.State == EdgeReplayerState.Idle || e.State == EdgeReplayerState.Active)
        {
            SetSafeState(latched: false);
        }

        _sessionStateStatus.Text = e.State switch
        {
            EdgeReplayerState.Active => "Session: Active",
            EdgeReplayerState.Degraded => "Session: Degraded",
            EdgeReplayerState.SafeLatched => "Session: SAFE",
            EdgeReplayerState.Idle => "Session: Idle",
            _ => "Session: —"
        };
    }

    private void OnControllerTailscaleStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerTailscaleStateChanged(sender, e)); return; }

        _pathStatus.Text = e.Path switch
        {
            PathType.Direct => "Path: Direct",
            PathType.Derp => $"Path: DERP ({e.DerpRegion ?? "?"})",
            _ => "Path: —"
        };

        if (e.RoundTripTime > TimeSpan.Zero)
            _rttStatus.Text = $"RTT: {e.RoundTripTime.TotalMilliseconds:F0}ms";

        if (e.State == TailscaleState.Connected)
        {
            _linkIndicatorStatus.Text = "\u25CF Link: Up";
            _linkIndicatorStatus.ForeColor = Color.LimeGreen;
            DismissLoginPanel();
            DismissWaitOverlay();

            // Update Station's own Tailscale IP for display.
            UpdateSelfAddress();
        }
        else if (e.State == TailscaleState.Connecting)
        {
            // Transitioning from NeedsAuth → Connecting → Connected.
            // Dismiss the login panel early since auth succeeded.
            _linkIndicatorStatus.Text = "\u25CF Link: Connecting...";
            _linkIndicatorStatus.ForeColor = Color.Gold;
            DismissLoginPanel();
        }
        else if (e.State == TailscaleState.Fault)
        {
            _linkIndicatorStatus.Text = "● Link: FAULT";
            _linkIndicatorStatus.ForeColor = Color.Red;
        }
        else if (e.State == TailscaleState.NeedsAuth)
        {
            _linkIndicatorStatus.Text = "● Link: Waiting for login";
            _linkIndicatorStatus.ForeColor = Color.Gold;

            // Show the login panel proactively when NeedsAuth is detected,
            // even if the auth URL hasn't arrived yet from the sidecar poll.
            if (!_loginDismissed && !HasPersistedTailscaleState() && _loginPanel is null)
            {
                ShowLoginPanel(_pendingAuthUrl ?? "");
            }
        }
    }

    private void OnControllerSidecarFailureStateChanged(object? sender, SidecarFailureStateChangedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerSidecarFailureStateChanged(sender, e)); return; }

        if (!e.IsRecovered && e.Failure is not null)
        {
            _linkIndicatorStatus.Text = "● Link: Sidecar Fault";
            _linkIndicatorStatus.ForeColor = Color.Red;
            SetStatusText(SidecarFailureHandler.FormatFailureMessage(e.Failure));
        }
        else
        {
            _linkIndicatorStatus.Text = "● Link: Up";
            _linkIndicatorStatus.ForeColor = Color.LimeGreen;
        }
    }

    private void OnControllerStartupFailed(object? sender, StationStartupFailedEventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnControllerStartupFailed(sender, e)); return; }

        _safeBannerPanel.BackColor = Color.FromArgb(120, 60, 0);
        _safeBannerLabel.Text = "NOT ARMED";
        SetStatusText(e.Message);
    }

    // ────────────────────────────────────────────────────────────────
    // SAFE / ARMED state management (13.6, 13.7, 13.8)
    // ────────────────────────────────────────────────────────────────

    private void SetSafeState(bool latched)
    {
        _isSafeLatched = latched;

        if (latched)
        {
            _safeBannerPanel.BackColor = SafeRed;
            _safeBannerLabel.Text = "SAFE \u2014 KEY LOCKED";
            _reArmButton.Enabled = true;
        }
        else
        {
            _safeBannerPanel.BackColor = ArmedGreen;
            _safeBannerLabel.Text = "ARMED";
            _reArmButton.Enabled = false;
        }
    }

    private void OnReArmClick(object? sender, EventArgs e)
    {
        if (_isSafeLatched)
        {
            _controller?.ClearSafeLatch();
            SetSafeState(latched: false);
        }
    }

    private void OnDisconnectClick(object? sender, EventArgs e)
    {
        _controller?.DisconnectSession();
    }

    private void OnCopyIpClick(object? sender, EventArgs e)
    {
        string ip = _tailscaleIpValue.Text;
        if (!string.IsNullOrEmpty(ip) && ip != "(not connected)")
        {
            Clipboard.SetText(ip);
            _toolTip.Show("Copied!", _copyIpButton, 0, -20, 1500);
        }
    }

    private void PopulateComPorts()
    {
        _comPortCombo.Items.Clear();
        var ports = GetSortedComPorts();
        foreach (var port in ports)
            _comPortCombo.Items.Add(port);

        if (_comPortCombo.Items.Count > 0)
            _comPortCombo.SelectedIndex = 0;
    }

    private static string[] GetSortedComPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        Array.Sort(ports, CompareComPorts);
        return ports;
    }

    private static int CompareComPorts(string a, string b)
    {
        int na = ExtractPortNumber(a);
        int nb = ExtractPortNumber(b);
        return na.CompareTo(nb);
    }

    private static int ExtractPortNumber(string portName)
    {
        // Extract numeric suffix from "COMn"
        int i = 3; // skip "COM"
        if (portName.Length > 3 && int.TryParse(portName.AsSpan(i), out int num))
            return num;
        return int.MaxValue;
    }

    // ────────────────────────────────────────────────────────────────
    // Public API for backend wiring
    // ────────────────────────────────────────────────────────────────

    public void NotifySafeLatched()
    {
        if (InvokeRequired) { Invoke(NotifySafeLatched); return; }
        SetSafeState(latched: true);
    }

    public void SetKeyIndicator(bool active)
    {
        if (InvokeRequired) { Invoke(() => SetKeyIndicator(active)); return; }
        _keyIndicator.ForeColor = active ? Color.FromArgb(255, 60, 60) : SystemColors.GrayText;
    }

    public void SetPttIndicator(bool active)
    {
        if (InvokeRequired) { Invoke(() => SetPttIndicator(active)); return; }
        _pttIndicator.ForeColor = active ? Color.FromArgb(255, 180, 0) : SystemColors.GrayText;
    }

    public void SetSessionInfo(string clientName, string clientAddress)
    {
        if (InvokeRequired) { Invoke(() => SetSessionInfo(clientName, clientAddress)); return; }
        _sessionClientValue.Text = $"{clientName} ({clientAddress})";
        _disconnectButton.Enabled = true;
        _sessionStateStatus.Text = "Session: Active";
        _clientNameStatus.Text = clientName;
    }

    private void ClearSessionInfo(string? reason)
    {
        _sessionClientValue.Text = "(none)";
        _sessionDurationValue.Text = "\u2014";
        _disconnectButton.Enabled = false;
        _sessionStateStatus.Text = "Session: Idle";
        _clientNameStatus.Text = "";
    }

    private void SetStatusText(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatusText(text)); return; }
        _clientNameStatus.Text = text;
    }

    private void UpdateSelfAddress()
    {
        // Read the self address from the sidecar host via the controller.
        string? selfIp = _controller?.SelfAddress;
        if (!string.IsNullOrEmpty(selfIp))
        {
            _tailscaleIpValue.Text = selfIp;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Interactive Tailscale Login (same pattern as Client)
    // ────────────────────────────────────────────────────────────────

    private Panel? _loginPanel;
    private Label? _loginMessageLabel;
    private Button? _openBrowserButton;
    private Button? _pasteKeyButton;
    private TextBox? _authKeyTextBox;
    private Button? _submitKeyButton;
    private Label? _loginStatusLabel;
    private string? _pendingAuthUrl;

    private bool _loginDismissed;

    private static bool HasPersistedTailscaleState()
    {
        try
        {
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RWK", "tailscale", "rwk-station", "tailscaled.state");
            return File.Exists(stateDir);
        }
        catch { return false; }
    }

    private void OnControllerAuthUrlAvailable(object? sender, string authUrl)
    {
        if (InvokeRequired) { Invoke(() => OnControllerAuthUrlAvailable(sender, authUrl)); return; }

        // Don't show the wizard if auth was already completed in this session.
        if (_loginDismissed) return;

        _pendingAuthUrl = authUrl;
        _loginDismissed = true; // Prevent re-entry while wizard is open
        ShowAuthWizard();
    }

    private void ShowAuthWizard()
    {
        if (_controller?.SidecarHost is null) return;

        DismissWaitOverlay(); // Remove overlay before showing wizard

        var provider = new RWK.Shared.Auth.SidecarAuthProvider(_controller.SidecarHost);
        using var wizard = new Auth.TailscaleAuthWizard(provider);
        wizard.ShowDialog(this);

        if (!wizard.AuthSucceeded)
        {
            // User cancelled — allow re-showing if auth URL appears again
            _loginDismissed = false;
        }
    }

    private void ShowWaitOverlay()
    {
        if (_waitOverlay is not null) return;

        _waitOverlay = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(220, 240, 240, 240),
            Name = "_waitOverlay"
        };

        // White box with black border, centered on the entire window
        var box = new Panel
        {
            Size = new Size(340, 80),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Name = "_waitBox"
        };

        var label = new Label
        {
            Text = "Wait... Connecting to Tailscale",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.Black,
            AutoSize = true,
            Name = "_waitLabel"
        };

        // Center label inside the box
        box.Controls.Add(label);
        box.Layout += (_, _) =>
        {
            label.Location = new Point(
                (box.Width - label.Width) / 2,
                (box.Height - label.Height) / 2);
        };

        // Center the box on the full window
        _waitOverlay.Controls.Add(box);
        _waitOverlay.Resize += (_, _) =>
        {
            box.Location = new Point(
                (_waitOverlay.Width - box.Width) / 2,
                (_waitOverlay.Height - box.Height) / 2 - 20);
        };

        Controls.Add(_waitOverlay);
        _waitOverlay.BringToFront();

        // Trigger initial centering
        box.Location = new Point(
            (ClientSize.Width - box.Width) / 2,
            (ClientSize.Height - box.Height) / 2 - 20);
        label.Location = new Point(
            (box.Width - label.PreferredWidth) / 2,
            (box.Height - label.PreferredHeight) / 2);
    }

    private void DismissWaitOverlay()
    {
        if (_waitOverlay is null) return;
        Controls.Remove(_waitOverlay);
        _waitOverlay.Dispose();
        _waitOverlay = null;
    }

    private void ShowLoginPanel(string authUrl)
    {
        if (_loginPanel is not null)
        {
            _pendingAuthUrl = authUrl;
            return;
        }

        _loginPanel = new Panel
        {
            Size = new Size(420, 180),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Info,
            Anchor = AnchorStyles.None
        };
        _loginPanel.Location = new Point(
            (ClientSize.Width - _loginPanel.Width) / 2,
            (ClientSize.Height - _loginPanel.Height) / 2);

        _loginMessageLabel = new Label
        {
            Text = "Sign in with Tailscale to connect.\nA browser window will open.",
            Font = new Font(Font.FontFamily, 9.5f),
            AutoSize = false,
            Size = new Size(380, 40),
            Location = new Point(20, 15),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _openBrowserButton = new Button
        {
            Text = "Open Browser",
            Size = new Size(120, 32),
            Location = new Point(20, 62),
            UseVisualStyleBackColor = true
        };
        _openBrowserButton.Click += OnLoginOpenBrowserClick;

        _pasteKeyButton = new Button
        {
            Text = "Paste Auth Key Instead",
            Size = new Size(160, 32),
            Location = new Point(155, 62),
            UseVisualStyleBackColor = true
        };
        _pasteKeyButton.Click += OnLoginPasteKeyClick;

        _authKeyTextBox = new TextBox
        {
            Size = new Size(260, 24),
            Location = new Point(20, 105),
            Visible = false,
            PlaceholderText = "tskey-auth-..."
        };

        _submitKeyButton = new Button
        {
            Text = "Submit",
            Size = new Size(80, 24),
            Location = new Point(290, 105),
            UseVisualStyleBackColor = true,
            Visible = false
        };
        _submitKeyButton.Click += OnLoginSubmitKeyClick;

        _loginStatusLabel = new Label
        {
            Text = "Waiting for browser login...",
            ForeColor = Color.FromArgb(180, 140, 20),
            Font = new Font(Font.FontFamily, 8.5f),
            AutoSize = true,
            Location = new Point(20, 145)
        };

        _loginPanel.Controls.AddRange(new Control[]
        {
            _loginMessageLabel, _openBrowserButton, _pasteKeyButton,
            _authKeyTextBox, _submitKeyButton!, _loginStatusLabel
        });

        Controls.Add(_loginPanel);
        _loginPanel.BringToFront();
    }

    private void DismissLoginPanel()
    {
        if (_loginPanel is null) return;

        _loginDismissed = true;

        Controls.Remove(_loginPanel);
        _loginPanel.Dispose();
        _loginPanel = null;
        _loginMessageLabel = null;
        _openBrowserButton = null;
        _pasteKeyButton = null;
        _authKeyTextBox = null;
        _submitKeyButton = null;
        _loginStatusLabel = null;
        _pendingAuthUrl = null;
    }

    private void OnLoginOpenBrowserClick(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingAuthUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_pendingAuthUrl) { UseShellExecute = true });
            }
            catch
            {
                if (_loginStatusLabel is not null)
                    _loginStatusLabel.Text = $"Open manually: {_pendingAuthUrl}";
            }
        }
    }

    private void OnLoginPasteKeyClick(object? sender, EventArgs e)
    {
        if (_authKeyTextBox is not null) _authKeyTextBox.Visible = true;
        if (_submitKeyButton is not null) _submitKeyButton.Visible = true;
        if (_loginStatusLabel is not null) _loginStatusLabel.Text = "Paste your auth key and click Submit.";
    }

    private async void OnLoginSubmitKeyClick(object? sender, EventArgs e)
    {
        string key = _authKeyTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(key)) return;

        try
        {
            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = "Submitting auth key...";

            await _controller!.SubmitAuthKeyAsync(key).ConfigureAwait(true);

            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = "Key accepted — connecting...";
        }
        catch (Exception ex)
        {
            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = $"Failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Clean up controller on form close. Uses a fire-and-forget with a timeout
    /// to prevent UI hangs if the sidecar is unresponsive.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _portPollTimer?.Stop();
        _portPollTimer?.Dispose();
        _keyIndicatorTimer?.Stop();
        _keyIndicatorTimer?.Dispose();

        if (_controller is not null)
        {
            // Give the controller 3 seconds to shut down gracefully, then kill it.
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                _controller.StopAsync().Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timed out — force-dispose (kills sidecar process).
            }
            catch (AggregateException)
            {
                // StopAsync threw — ignore on close.
            }

            try { _controller.Dispose(); } catch { /* best effort */ }
            _controller = null;
        }
    }

    private void OnForwardRulesReceived(object? sender, List<ForwardRuleInfo> rules)
    {
        if (InvokeRequired) { Invoke(() => OnForwardRulesReceived(sender, rules)); return; }

        _forwardRulesGrid.Rows.Clear();
        foreach (var rule in rules)
        {
            int idx = _forwardRulesGrid.Rows.Add(
                rule.Name,
                rule.Protocol.ToUpperInvariant(),
                rule.ClientPort,
                rule.Port,
                rule.Enabled ? "✓" : "✗",
                rule.TargetAddress);

            // Color the enabled cell
            var cell = _forwardRulesGrid.Rows[idx].Cells["ColEnabled"];
            cell.Style.ForeColor = rule.Enabled ? Color.Green : Color.Red;
            cell.Style.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        }

        // Show Flex Forwarding indicator when any [Flex] rule is enabled
        bool flexActive = rules.Any(r =>
            r.Name.StartsWith("[Flex]", StringComparison.OrdinalIgnoreCase) && r.Enabled);
        _flexForwardingIndicator.Visible = flexActive;

        // Auto-enable/disable discovery capture based on Flex rule presence.
        if (flexActive)
            _controller?.StartDiscoveryCapture();
        else
            _controller?.StopDiscoveryCapture();
    }

    // ────────────────────────────────────────────────────────────────
    // Menu handlers
    // ────────────────────────────────────────────────────────────────

    private void OnCopyStationInfoClick(object? sender, EventArgs e)
    {
        string key = _controller?.PairingKey ?? "";
        string ip = _controller?.SidecarHost?.SelfAddress ?? "";

        if (string.IsNullOrEmpty(ip))
        {
            MessageBox.Show("Station is not connected to Tailscale yet.\nWait for the tailnet to connect, then try again.",
                "Not Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string stationInfo = $"{ip}|{key}";
        Clipboard.SetText(stationInfo);

        MessageBox.Show(
            $"Station Info copied to clipboard:\n\n{stationInfo}\n\nPaste this into the Client's Import Station dialog.",
            "Copied",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async void OnDeleteTailscaleAuthClick(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "This will disconnect from the Tailscale network and delete the stored authorization.\n\n" +
            "You will be guided through re-authentication immediately.",
            "Delete Tailscale Authorization",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        try
        {
            ShowWaitOverlay();

            // Stop the controller (including sidecar) so file locks are released.
            if (_controller is not null)
            {
                await _controller.StopAsync().ConfigureAwait(true);
            }

            // Small delay to let the process fully exit and release handles.
            await Task.Delay(500).ConfigureAwait(true);

            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RWK", "tailscale", "rwk-station");

            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);

            _controller?.ClearTailscaleAuth();

            // Restart the controller (sidecar will enter NeedsAuth)
            if (_controller is not null)
            {
                await _controller.StartAsync().ConfigureAwait(true);
            }

            _loginDismissed = false;
            await Task.Delay(1000).ConfigureAwait(true);

            DismissWaitOverlay();
            ShowAuthWizard();
        }
        catch (Exception ex)
        {
            DismissWaitOverlay();
            MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
