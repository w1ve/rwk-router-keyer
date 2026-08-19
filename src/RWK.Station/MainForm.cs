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
    private DateTime? _sessionStartTime;

    public MainForm()
    {
        InitializeComponent();
        Text = $"RWK Router/Keyer Station Version {AppVersion} — Any Rig, Any Internet, Anytime";

        _toolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

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

        // FlexRadio discovery tooltip (greyed-out placeholder, 13.16).
        _toolTip.SetToolTip(_flexDiscoveryGroup, "Requires FlexRadio discovery configuration — coming soon");
        _toolTip.SetToolTip(_flexDiscoveryEnable, "Requires FlexRadio discovery configuration — coming soon");

        // Populate COM port dropdown with available ports.
        PopulateComPorts();

        // Set initial UI state.
        SetSafeState(latched: false);

        // Create and wire the controller on form load so the UI thread is available for Invoke.
        Load += OnFormLoad;
    }

    // ────────────────────────────────────────────────────────────────
    // Controller initialization and wiring
    // ────────────────────────────────────────────────────────────────

    private async void OnFormLoad(object? sender, EventArgs e)
    {
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
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "station.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
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

        // Start the controller — it will skip keying output if no port is configured,
        // and auto-connect Tailscale if an auth key is present.
        await _controller.StartAsync().ConfigureAwait(false);

        // If a COM port is already selected in the dropdown, connect it now.
        // (SelectedIndexChanged doesn't fire for the initial auto-selection.)
        if (_comPortCombo.InvokeRequired)
            Invoke(() => { LoadKeyingConfigToUi(); TryConnectSelectedPort(); });
        else
        {
            LoadKeyingConfigToUi();
            TryConnectSelectedPort();
        }
    }

    private void TryConnectSelectedPort()
    {
        string? selectedPort = _comPortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(selectedPort) && _controller is not null)
        {
            try
            {
                _controller.ConnectKeyingPort(selectedPort, GetKeyingConfig());
            }
            catch (Exception ex)
            {
                _comPortErrorLabel.Text = $"⚠ {ex.Message}";
                _comPortErrorLabel.Visible = true;
            }
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
    }

    private DateTime _lastKeyDownTime = DateTime.MinValue;
    private DateTime _lastPttOnTime = DateTime.MinValue;

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

    private void OnKeyingConfigChanged(object? sender, EventArgs e)
    {
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
            _linkIndicatorStatus.Text = "● Link: Up";
            _linkIndicatorStatus.ForeColor = Color.LimeGreen;
            DismissLoginPanel();

            // Update Station's own Tailscale IP for display.
            UpdateSelfAddress();
        }
        else if (e.State == TailscaleState.Connecting)
        {
            // Transitioning from NeedsAuth → Connecting → Connected.
            // Dismiss the login panel early since auth succeeded.
            _linkIndicatorStatus.Text = "● Link: Connecting...";
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

        // Don't show if already dismissed or if stored auth key exists (sidecar auto-connects).
        if (_loginDismissed) return;
        if (HasPersistedTailscaleState()) return;

        _pendingAuthUrl = authUrl;
        ShowLoginPanel(authUrl);
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
            _forwardRulesGrid.Rows.Add(
                rule.Name,
                rule.Protocol.ToUpperInvariant(),
                rule.Port,
                rule.Port,
                rule.Enabled,
                rule.TargetAddress);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Menu handlers
    // ────────────────────────────────────────────────────────────────

    private void OnShowPairingKeyClick(object? sender, EventArgs e)
    {
        string key = _controller?.PairingKey ?? "(not available)";

        var result = MessageBox.Show(
            $"Station Pairing Key:\n\n{key}\n\nCopy to clipboard?",
            "Pairing Key",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            Clipboard.SetText(key);
            _toolTip.Show("Copied!", this, Width / 2, Height / 2, 1500);
        }
    }

    private void OnDeleteTailscaleAuthClick(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Do you really want to delete the Tailscale authorization?\n\n" +
            "You will need to re-authenticate on the next connection.",
            "Delete Tailscale Authorization",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        try
        {
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RWK", "tailscale", "rwk-station");

            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);

            _controller?.ClearTailscaleAuth();

            MessageBox.Show(
                "Tailscale authorization has been deleted.\n\n" +
                "Restart the application to re-authenticate.",
                "Authorization Deleted",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
