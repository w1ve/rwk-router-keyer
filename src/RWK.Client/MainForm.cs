/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.IO.Ports;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using RWK.Client.Audio;
using RWK.Client.Controllers;
using RWK.Client.IO;
using RWK.Client.Keying;
using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.IO;
using RWK.Shared.Net;
using RWK.Shared.Protocol;

namespace RWK.Client;

/// <summary>
/// Client main window. Contains keyer controls, paddle indicators, device selection,
/// port forwarding, and FlexRadio discovery panels. Uses Windows system colors for
/// proper light/dark mode and high-contrast support.
/// Wired to <see cref="ClientController"/> for all backend operations.
/// </summary>
public partial class MainForm : Form
{
    // Safety indicator (not a theme color)
    private static readonly Color WarningRed = Color.FromArgb(200, 60, 60);

    private static readonly Color IndicatorOn = Color.FromArgb(0, 220, 0);
    private static readonly Color IndicatorOff = SystemColors.GrayText;

    /// <summary>Gets the application version string from the assembly.</summary>
    internal static string AppVersion =>
        typeof(MainForm).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private ClientController? _controller;
    private readonly LogService _logService = new();
    private bool _suppressGridEvents;
    private bool _suppressPortEvents;

    // Device monitoring
    private System.Windows.Forms.Timer? _portPollTimer;
    private string[] _lastKnownPorts = Array.Empty<string>();
    private MMDeviceEnumerator? _audioEnumerator;
    private AudioDeviceNotificationClient? _audioNotificationClient;

    public MainForm()
    {
        InitializeComponent();
        Text = $"RWK Router/Keyer Client Version {AppVersion} — Any Rig, Any Internet, Anytime";
        PopulateDefaults();
        InitializeDeviceMonitoring();
        InitializeLogService();

        // Auto-start the controller on form load so the interactive login
        // prompt appears immediately on first run (matching Station behavior).
        Load += async (_, _) =>
        {
            await Task.Yield(); // Let the form finish rendering first
            OnStartClick(this, EventArgs.Empty);
        };
    }

    private void PopulateDefaults()
    {
        // Keyer mode dropdown
        _modeCombo.Items.AddRange(new object[] { "Iambic B", "Iambic A", "Ultimatic", "Bug", "Straight" });
        _modeCombo.SelectedIndex = 0;

        // Speed default
        _speedSlider.Value = 20;
        _speedLabel.Text = "20";

        // Weight default
        _weightSlider.Value = 50;
        _weightValueLabel.Text = "50%";

        // Sidetone defaults
        _toneFreqSlider.Value = 600;
        _toneFreqValueLabel.Text = "600 Hz";
        _toneLevelSlider.Value = 70;
        _toneLevelValueLabel.Text = "70%";

        // Bind address defaults for forwards
        _bindAddressColumn.Items.AddRange("127.0.0.1", "0.0.0.0");

        // Status strip defaults
        _linkIndicator.Text = "●";
        _linkIndicator.ForeColor = Color.Gray;
        _pathLabel.Text = "Disconnected";
        _rttLabel.Text = "RTT: --";
        _bufferLabel.Text = "Buffer: --";
        _keyStateLabel.Text = "Key: Up";

        // Connect button wiring
        _connectButton.Click += OnConnectClick;

        // Station ARM toggle
        _stationArmToggle.CheckedChanged += OnStationArmToggleChanged;

        // Test CW button wiring
        _testTxButton.Click += OnTestTxClick;

        // Port selection wiring — connect WinKeyer/Paddle when user selects a port
        _winKeyerPortCombo.SelectedIndexChanged += OnWinKeyerPortChanged;
        _paddlePortCombo.SelectedIndexChanged += OnPaddlePortChanged;

        // WinKeyer mode radio buttons
        _wkModeLoggerRadio.CheckedChanged += OnWinKeyerModeChanged;
        _wkModeHardwareRadio.CheckedChanged += OnWinKeyerModeChanged;

        // WinKeyer loopback test
        _wkLoopbackTestBtn.Click += OnWinKeyerLoopbackTestClick;

        // FlexRadio discovery re-emission
        _flexEnableCheck.CheckedChanged += (_, _) =>
        {
            _controller?.SetDiscoveryEmitEnabled(_flexEnableCheck.Checked);
        };
    }

    // --- Event handlers (UI only, no backend) ---

    private void OnSpeedSliderScroll(object? sender, EventArgs e)
    {
        _speedLabel.Text = _speedSlider.Value.ToString();
        _controller?.SetSpeed(_speedSlider.Value);
    }

    private void OnWeightSliderScroll(object? sender, EventArgs e)
    {
        _weightValueLabel.Text = $"{_weightSlider.Value}%";
        _controller?.SetWeight(_weightSlider.Value);
    }

    private void OnToneFreqSliderScroll(object? sender, EventArgs e)
    {
        _toneFreqValueLabel.Text = $"{_toneFreqSlider.Value} Hz";
        _controller?.SetToneFrequency(_toneFreqSlider.Value);
    }

    private void OnToneLevelSliderScroll(object? sender, EventArgs e)
    {
        _toneLevelValueLabel.Text = $"{_toneLevelSlider.Value}%";
        _controller?.SetToneVolume(_toneLevelSlider.Value / 100.0);
    }

    private void OnAddForwardRuleClick(object? sender, EventArgs e)
    {
        // Add a new rule defaulting to OFF (unchecked). Persist and push immediately.
        var rule = new ForwardRule(
            Guid.NewGuid(),
            "New Rule",
            ForwardProtocol.Tcp,
            ClientPort: 4532,
            StationPort: 4532,
            Enabled: false,
            BindAddress: "127.0.0.1",
            StationTargetAddress: "127.0.0.1");

        try
        {
            _controller?.AddForwardRule(rule);
        }
        catch (Exception ex)
        {
            _logService.Info($"Add forward rule failed: {ex.Message}");
        }

        // Add to UI grid (unchecked = OFF) — suppress events during add
        _suppressGridEvents = true;
        _forwardGrid.Rows.Add("OFF", rule.Name, "TCP", rule.ClientPort, rule.StationPort, rule.BindAddress, rule.StationTargetAddress, "Idle");
        _forwardGrid.Rows[_forwardGrid.Rows.Count - 1].Tag = rule.Id;
        _suppressGridEvents = false;
        EvaluateBindWarning();
    }

    private void OnRemoveForwardRuleClick(object? sender, EventArgs e)
    {
        if (_forwardGrid.CurrentRow != null && !_forwardGrid.CurrentRow.IsNewRow)
        {
            var ruleId = _forwardGrid.CurrentRow.Tag as Guid?;
            if (ruleId.HasValue)
            {
                try
                {
                    _controller?.RemoveForwardRule(ruleId.Value);
                }
                catch (Exception ex)
                {
                    _logService.Info($"Remove forward rule failed: {ex.Message}");
                }
            }
            _forwardGrid.Rows.Remove(_forwardGrid.CurrentRow);
            EvaluateBindWarning();
        }
    }

    private void OnForwardGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _controller is null || _suppressGridEvents) return;

        var row = _forwardGrid.Rows[e.RowIndex];
        var ruleId = row.Tag as Guid?;

        // Any cell edit: delete old rule and re-add with new values, OFF state.
        if (ruleId.HasValue && e.ColumnIndex != _forwardGrid.Columns["Enabled"]?.Index
            && e.ColumnIndex != _forwardGrid.Columns["Status"]?.Index)
        {
            try { _controller.RemoveForwardRule(ruleId.Value); } catch { }

            var newRule = BuildRuleFromRow(row);
            try
            {
                _controller.AddForwardRule(newRule);
                row.Tag = newRule.Id;
                row.Cells["Enabled"].Value = "OFF";
                row.Cells["Status"].Value = "Idle";
                _logService.Info($"Forward rule '{newRule.Name}' updated (disabled until re-enabled).");
            }
            catch (Exception ex)
            {
                row.Cells["Status"].Value = "Error";
                _logService.Info($"Forward rule update error: {ex.Message}");
            }
        }

        EvaluateBindWarning();
    }

    private void OnForwardGridSelectionChanged(object? sender, EventArgs e)
    {
        bool hasSelection = _forwardGrid.CurrentRow != null && !_forwardGrid.CurrentRow.IsNewRow;
        _enableSelectedBtn.Enabled = hasSelection;
        _disableSelectedBtn.Enabled = hasSelection;
    }

    private void OnEnableSelectedClick(object? sender, EventArgs e)
    {
        if (_forwardGrid.CurrentRow == null || _controller is null) return;
        var ruleId = _forwardGrid.CurrentRow.Tag as Guid?;
        if (!ruleId.HasValue) return;

        try
        {
            _controller.SetForwardRuleEnabled(ruleId.Value, true);
            _forwardGrid.CurrentRow.Cells["Enabled"].Value = "ON";
            _logService.Info($"Forward rule '{_forwardGrid.CurrentRow.Cells["RuleName"]?.Value}' enabled.");
        }
        catch (Exception ex)
        {
            _logService.Info($"Enable forward rule failed: {ex.Message}");
        }
    }

    private void OnDisableSelectedClick(object? sender, EventArgs e)
    {
        if (_forwardGrid.CurrentRow == null || _controller is null) return;
        var ruleId = _forwardGrid.CurrentRow.Tag as Guid?;
        if (!ruleId.HasValue) return;

        try
        {
            _controller.SetForwardRuleEnabled(ruleId.Value, false);
            _forwardGrid.CurrentRow.Cells["Enabled"].Value = "OFF";
            _forwardGrid.CurrentRow.Cells["Status"].Value = "Idle";
            _logService.Info($"Forward rule '{_forwardGrid.CurrentRow.Cells["RuleName"]?.Value}' disabled.");
        }
        catch (Exception ex)
        {
            _logService.Info($"Disable forward rule failed: {ex.Message}");
        }
    }

    private void OnEnableAllClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;
        foreach (DataGridViewRow row in _forwardGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var ruleId = row.Tag as Guid?;
            if (!ruleId.HasValue) continue;
            try
            {
                _controller.SetForwardRuleEnabled(ruleId.Value, true);
                row.Cells["Enabled"].Value = "ON";
            }
            catch { }
        }
        _logService.Info("All forward rules enabled.");
    }

    private void OnDisableAllClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;
        foreach (DataGridViewRow row in _forwardGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var ruleId = row.Tag as Guid?;
            if (!ruleId.HasValue) continue;
            try
            {
                _controller.SetForwardRuleEnabled(ruleId.Value, false);
                row.Cells["Enabled"].Value = "OFF";
                row.Cells["Status"].Value = "Idle";
            }
            catch { }
        }
        _logService.Info("All forward rules disabled.");
    }

    private void OnWizardClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;

        var catalog = Wizard.CatalogLoader.Load();
        if (catalog.Entries.Count == 0)
        {
            MessageBox.Show("No radio catalog found. Ensure radios.json is alongside the executable.",
                "Catalog Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existingRules = _controller.Config.ForwardRules.ToList();
        using var wizard = new Wizard.WizardForm(catalog, existingRules);

        if (wizard.ShowDialog(this) == DialogResult.OK && wizard.GeneratedRules.Count > 0)
        {
            MergeWizardRules(wizard.GeneratedRules);
        }
    }

    private void OnImportProfileClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;

        using var ofd = new OpenFileDialog
        {
            Title = "Import RWK Profile",
            Filter = "RWK Profiles (*.rwkprofile.json)|*.rwkprofile.json|All Files (*.*)|*.*",
            InitialDirectory = Wizard.ProfileManager.GetProfilesDirectory()
        };

        if (ofd.ShowDialog(this) != DialogResult.OK)
            return;

        var profile = Wizard.ProfileManager.LoadProfile(ofd.FileName, out string? error);
        if (profile is null)
        {
            MessageBox.Show($"Failed to load profile:\n\n{error}",
                "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Run conflict detection.
        var existingRules = _controller.Config.ForwardRules.ToList();
        var conflicts = Wizard.ConflictDetector.Detect(profile.Forwards, existingRules, trialBind: true);

        if (Wizard.ConflictDetector.HasErrors(conflicts))
        {
            string msg = "Cannot import — there are conflicts:\n\n" +
                string.Join("\n", conflicts.Where(c => c.Severity == Wizard.ConflictSeverity.Error).Select(c => c.Message));
            MessageBox.Show(msg, "Import Conflicts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (conflicts.Count > 0)
        {
            string msg = "Warnings:\n\n" +
                string.Join("\n", conflicts.Select(c => c.Message)) +
                "\n\nProceed with import?";
            if (MessageBox.Show(msg, "Import Warnings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        MergeWizardRules(profile.Forwards);
        _logService.Info($"Imported profile: {profile.Profile.Name} ({profile.Forwards.Count} rules).");
    }

    private void MergeWizardRules(IReadOnlyList<Wizard.ProfileForwardRule> rules)
    {
        if (_controller is null) return;

        foreach (var pfr in rules)
        {
            var protocol = pfr.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)
                ? ForwardProtocol.Udp : ForwardProtocol.Tcp;

            // Check if a rule with this name already exists (merge by name).
            var existingRule = _controller.Config.ForwardRules
                .FirstOrDefault(r => string.Equals(r.Name, pfr.Name, StringComparison.OrdinalIgnoreCase));

            if (existingRule is not null)
            {
                // Update existing rule (preserve hand-edited StationTargetAddress if profile has placeholder).
                string target = pfr.StationTarget;
                if (target == "127.0.0.1" && existingRule.StationTargetAddress != "127.0.0.1")
                    target = existingRule.StationTargetAddress;

                var updated = existingRule with
                {
                    Protocol = protocol,
                    ClientPort = pfr.ClientPort,
                    StationPort = pfr.StationPort,
                    BindAddress = pfr.BindAddress,
                    StationTargetAddress = target,
                    Enabled = pfr.Enabled
                };

                try { _controller.RemoveForwardRule(existingRule.Id); } catch { }
                try { _controller.AddForwardRule(updated); } catch { }
            }
            else
            {
                // Add new rule.
                var newRule = new ForwardRule(
                    Guid.NewGuid(),
                    pfr.Name,
                    protocol,
                    pfr.ClientPort,
                    pfr.StationPort,
                    pfr.Enabled,
                    pfr.BindAddress,
                    ForwardRuleType.Generic,
                    pfr.StationTarget);

                try { _controller.AddForwardRule(newRule); } catch { }
            }
        }

        // Reload grid from controller state.
        LoadForwardRulesIntoGrid();
        _logService.Info($"Wizard/Import: {rules.Count} rules merged into forwarding table.");
    }

    private ForwardRule BuildRuleFromRow(DataGridViewRow row)
    {
        string name = row.Cells["RuleName"]?.Value?.ToString() ?? "Rule";
        string proto = row.Cells["Protocol"]?.Value?.ToString() ?? "TCP";
        int.TryParse(row.Cells["ClientPort"]?.Value?.ToString(), out int clientPort);
        int.TryParse(row.Cells["StationPort"]?.Value?.ToString(), out int stationPort);
        string bind = row.Cells["BindAddress"]?.Value?.ToString() ?? "127.0.0.1";
        string target = row.Cells["StationTarget"]?.Value?.ToString() ?? "127.0.0.1";

        return new ForwardRule(
            Guid.NewGuid(),
            name,
            proto.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? ForwardProtocol.Udp : ForwardProtocol.Tcp,
            clientPort > 0 ? clientPort : 4532,
            stationPort > 0 ? stationPort : 4532,
            Enabled: false,
            BindAddress: bind,
            StationTargetAddress: target);
    }

    private void LoadForwardRulesIntoGrid()
    {
        if (_controller is null) return;

        _suppressGridEvents = true;
        try
        {
            _forwardGrid.Rows.Clear();
            foreach (var rule in _controller.Config.ForwardRules)
            {
                _forwardGrid.Rows.Add(
                    rule.Enabled ? "ON" : "OFF",
                    rule.Name,
                    rule.Protocol.ToString().ToUpperInvariant(),
                    rule.ClientPort,
                    rule.StationPort,
                    rule.BindAddress,
                    rule.StationTargetAddress,
                    rule.Enabled ? "Listening" : "Idle");
                _forwardGrid.Rows[_forwardGrid.Rows.Count - 1].Tag = rule.Id;
            }
        }
        finally
        {
            _suppressGridEvents = false;
        }
    }

    private void EvaluateBindWarning()
    {
        bool hasNonLoopback = false;
        bool hasRemoteRig = false;

        foreach (DataGridViewRow row in _forwardGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var bind = row.Cells["BindAddress"]?.Value?.ToString() ?? "127.0.0.1";
            if (bind != "127.0.0.1")
                hasNonLoopback = true;

            var name = row.Cells["RuleName"]?.Value?.ToString() ?? "";
            if (name.Contains("RemoteRig", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RRC", StringComparison.OrdinalIgnoreCase))
                hasRemoteRig = true;
        }

        // Exposure warning (10.14, 13.15)
        _bindWarningLabel.Visible = hasNonLoopback;

        // RemoteRig unverified label (10.18)
        _remoteRigWarningLabel.Visible = hasRemoteRig;
    }

    private static void RefreshPortList(ComboBox combo)
    {
        // Legacy helper — no longer called by buttons; kept for compatibility.
        var ports = GetSortedComPorts();
        var selected = combo.SelectedItem?.ToString();
        combo.Items.Clear();
        combo.Items.AddRange(ports);
        if (selected != null && combo.Items.Contains(selected))
            combo.SelectedItem = selected;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Device Monitoring — COM ports (2-second timer) + Audio (NAudio notifications)
    // ──────────────────────────────────────────────────────────────────────────────

    private void InitializeDeviceMonitoring()
    {
        // Enumerate COM ports immediately
        RefreshComPortDropdowns();

        // Enumerate audio devices immediately
        RefreshAudioDeviceDropdown();

        // Start a 2-second timer to detect COM port arrival/removal
        _portPollTimer = new System.Windows.Forms.Timer();
        _portPollTimer.Interval = 2000;
        _portPollTimer.Tick += OnPortPollTimerTick;
        _portPollTimer.Start();

        // Wire up NAudio device change notifications
        try
        {
            _audioEnumerator = new MMDeviceEnumerator();
            _audioNotificationClient = new AudioDeviceNotificationClient(this);
            _audioEnumerator.RegisterEndpointNotificationCallback(_audioNotificationClient);
        }
        catch
        {
            // If notification registration fails, audio list still works from initial enumeration
        }
    }

    private void InitializeLogService()
    {
        // Wire the log service to append text to the log TextBox on the UI thread.
        _logService.SetUiCallback(text =>
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(() => AppendLogText(text));
                return;
            }
            AppendLogText(text);
        });

        // Wire the level dropdown.
        _logLevelCombo.SelectedIndexChanged += (_, _) =>
        {
            _logService.Level = _logLevelCombo.SelectedIndex switch
            {
                0 => LogLevel.None,
                1 => LogLevel.Descriptive,
                2 => LogLevel.Debug,
                _ => LogLevel.Descriptive
            };
        };
    }

    private void AppendLogText(string text)
    {
        if (_logTextBox.IsDisposed) return;

        _logTextBox.AppendText(text);

        // Trim if over 5000 lines (bulk remove oldest half).
        if (_logTextBox.Lines.Length > LogService.MaxLines)
        {
            int removeCount = _logTextBox.Lines.Length / 2;
            int charIndex = _logTextBox.GetFirstCharIndexFromLine(removeCount);
            _logTextBox.Select(0, charIndex);
            _logTextBox.SelectedText = "";
            _logTextBox.SelectionStart = _logTextBox.TextLength;
        }
    }

    private void OnPortPollTimerTick(object? sender, EventArgs e)
    {
        var currentPorts = GetSortedComPorts();
        if (!currentPorts.SequenceEqual(_lastKnownPorts))
        {
            RefreshComPortDropdowns();
        }
    }

    private void RefreshComPortDropdowns()
    {
        var ports = GetSortedComPorts();
        _lastKnownPorts = ports;

        var portsWithNone = new[] { "(None)" }.Concat(ports).ToArray();
        UpdateComboPreservingSelection(_paddlePortCombo, portsWithNone);
        UpdateComboPreservingSelection(_winKeyerPortCombo, portsWithNone);
    }

    private void RefreshAudioDeviceDropdown()
    {
        string[] deviceNames;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            deviceNames = devices.Select(d => d.FriendlyName).ToArray();
        }
        catch
        {
            deviceNames = Array.Empty<string>();
        }

        if (deviceNames.Length == 0)
            deviceNames = new[] { "(Default Output)" };

        UpdateComboPreservingSelection(_audioDeviceCombo, deviceNames);
    }

    /// <summary>
    /// Called from the NAudio notification client (may be on a background thread).
    /// </summary>
    internal void OnAudioDeviceChanged()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshAudioDeviceDropdown);
            return;
        }
        RefreshAudioDeviceDropdown();
    }

    private static void UpdateComboPreservingSelection(ComboBox combo, string[] items)
    {
        var selected = combo.SelectedItem?.ToString();
        combo.Items.Clear();
        combo.Items.AddRange(items);
        if (selected != null && combo.Items.Contains(selected))
            combo.SelectedItem = selected;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    /// <summary>
    /// Returns COM port names sorted numerically (COM1, COM2, ..., COM10, COM11).
    /// </summary>
    private static string[] GetSortedComPorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, CompareComPorts);
            return ports;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int CompareComPorts(string a, string b)
    {
        int numA = ExtractPortNumber(a);
        int numB = ExtractPortNumber(b);

        if (numA >= 0 && numB >= 0)
            return numA.CompareTo(numB);
        if (numA >= 0)
            return -1; // Numeric ports come first
        if (numB >= 0)
            return 1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractPortNumber(string portName)
    {
        // Expected format: "COMn" where n is one or more digits
        if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(portName.AsSpan(3), out int number))
        {
            return number;
        }
        return -1;
    }

    /// <summary>
    /// NAudio notification client that fires when audio endpoints change.
    /// </summary>
    private sealed class AudioDeviceNotificationClient : IMMNotificationClient
    {
        private readonly MainForm _form;

        public AudioDeviceNotificationClient(MainForm form) => _form = form;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _form.OnAudioDeviceChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => _form.OnAudioDeviceChanged();
        public void OnDeviceRemoved(string deviceId) => _form.OnAudioDeviceChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Controller wiring
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and starts the ClientController. Called by the form's Load or a Start button.
    /// </summary>
    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_controller is { IsRunning: true }) return;

        try
        {
            _controller = CreateController();
            SubscribeToControllerEvents(_controller);
            await _controller.StartAsync().ConfigureAwait(true);

            // Load persisted station address
            if (!string.IsNullOrEmpty(_controller.Config.Tailscale.StationAddress))
                _stationAddressTextBox.Text = _controller.Config.Tailscale.StationAddress;

            // Load persisted port selections
            // Suppress event handlers during load so we don't double-connect.
            _suppressPortEvents = true;

            // Restore WinKeyer mode BEFORE port selection so reconnect uses the right mode.
            if (_controller.Config.WinKeyerMode == RWK.Shared.IO.WinKeyerMode.HardwareWinKey)
            {
                _wkModeHardwareRadio.Checked = true;
                _controller.SetWinKeyerMode(RWK.Shared.IO.WinKeyerMode.HardwareWinKey);
                _sidetoneMuteLabel.Visible = true;
            }
            else
            {
                _wkModeLoggerRadio.Checked = true;
                _sidetoneMuteLabel.Visible = false;
            }

            if (!string.IsNullOrEmpty(_controller.Config.WinKeyerPortName) && _winKeyerPortCombo.Items.Contains(_controller.Config.WinKeyerPortName))
                _winKeyerPortCombo.SelectedItem = _controller.Config.WinKeyerPortName;
            else
                _winKeyerPortCombo.SelectedItem = "(None)";

            if (!string.IsNullOrEmpty(_controller.Config.PaddlePortName) && _paddlePortCombo.Items.Contains(_controller.Config.PaddlePortName))
                _paddlePortCombo.SelectedItem = _controller.Config.PaddlePortName;
            else
                _paddlePortCombo.SelectedItem = "(None)";

            _suppressPortEvents = false;

            // Now manually connect the persisted ports (mode is already set correctly).
            string? paddlePort = _paddlePortCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(paddlePort) && paddlePort != "(None)")
                _controller.ConnectPaddlePort(paddlePort);

            string? wkPort = _winKeyerPortCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(wkPort) && wkPort != "(None)")
                _controller.ConnectWinKeyerPort(wkPort);

            // Sync keyer settings from loaded config to UI
            _speedSlider.Value = Math.Clamp(_controller.Config.SpeedWpm, _speedSlider.Minimum, _speedSlider.Maximum);
            _speedLabel.Text = _speedSlider.Value.ToString();
            _weightSlider.Value = Math.Clamp(_controller.Config.Weight, _weightSlider.Minimum, _weightSlider.Maximum);
            _weightValueLabel.Text = $"{_weightSlider.Value}%";
            _toneFreqSlider.Value = Math.Clamp(_controller.Config.Sidetone.FrequencyHz, _toneFreqSlider.Minimum, _toneFreqSlider.Maximum);
            _toneFreqValueLabel.Text = $"{_toneFreqSlider.Value} Hz";
            int volPct = (int)(_controller.Config.Sidetone.Volume * 100);
            _toneLevelSlider.Value = Math.Clamp(volPct, _toneLevelSlider.Minimum, _toneLevelSlider.Maximum);
            _toneLevelValueLabel.Text = $"{_toneLevelSlider.Value}%";

            // Load persisted forward rules into the grid
            LoadForwardRulesIntoGrid();

            UpdateStatusForState(TailscaleState.Connecting);

            // If the sidecar failed to start, show the failure prominently.
            // Login panel is NOT shown proactively here — the AuthUrlAvailable event
            // handler has proper guards (HasPersistedTailscaleState, _loginDismissed)
            // and will show it only when genuinely needed.
            if (_controller.IsSidecarFailed)
            {
                // Show the failure prominently
                _pathLabel.Text = $"Sidecar: {_controller.SidecarFailureMessage}";
                _linkIndicator.ForeColor = WarningRed;
            }
        }
        catch (Exception ex)
        {
            _pathLabel.Text = $"Start failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Stops the ClientController. Called by a Stop button or form closing.
    /// </summary>
    private async void OnStopClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;

        try
        {
            await _controller.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // Best effort
        }
        finally
        {
            _controller.Dispose();
            _controller = null;
            UpdateStatusForState(TailscaleState.Disconnected);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        // Stop device monitoring
        _portPollTimer?.Stop();
        _portPollTimer?.Dispose();
        _portPollTimer = null;

        if (_audioNotificationClient != null && _audioEnumerator != null)
        {
            try { _audioEnumerator.UnregisterEndpointNotificationCallback(_audioNotificationClient); } catch { }
        }
        _audioEnumerator?.Dispose();
        _audioEnumerator = null;
        _audioNotificationClient = null;

        if (_controller is { IsRunning: true })
        {
            try
            {
                await _controller.StopAsync().ConfigureAwait(true);
            }
            catch
            {
                // Best effort
            }
            _controller.Dispose();
            _controller = null;
        }

        base.OnFormClosing(e);

        _logService.Dispose();
    }

    private ClientController CreateController()
    {
        var paddlePoller = new PaddleInputPoller();
        var winKeyerHost = new WinKeyerProtocolHost(new NullProtocolLogger());
        var keyer = new SoftWinKeyerCore();
        var sidetone = new LocalSidetoneEngine();
        var sidecarHost = new TsnetSidecarHost { Hostname = "rwk-client" };
        var portForwardManager = new PortForwardManager();

        return new ClientController(
            paddlePoller,
            winKeyerHost,
            keyer,
            sidetone,
            sidecarHost,
            portForwardManager,
            logService: _logService);
    }

    private void SubscribeToControllerEvents(ClientController controller)
    {
        controller.ConnectionStateChanged += OnConnectionStateChanged;
        controller.PaddleStateChanged += OnControllerPaddleStateChanged;
        controller.EdgeGenerated += OnControllerEdgeGenerated;
        controller.SidecarFailureChanged += OnControllerSidecarFailure;
        controller.AuthUrlAvailable += OnControllerAuthUrlAvailable;
        controller.SessionStatusChanged += OnSessionStatusChanged;
        controller.ForwardRuleStatusChanged += OnForwardRuleStatusChanged;
        controller.HardwareWinKeyerConnected += OnHardwareWinKeyerConnected;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Controller event handlers (marshal to UI thread)
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnConnectionStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnConnectionStateChanged(sender, e));
            return;
        }

        UpdateStatusForState(e.State);

        if (e.Path != PathType.None)
        {
            _pathLabel.Text = e.Path == PathType.Direct
                ? "Direct"
                : $"DERP ({e.DerpRegion ?? "?"})";
        }

        if (e.RoundTripTime > TimeSpan.Zero)
            _rttLabel.Text = $"RTT: {e.RoundTripTime.TotalMilliseconds:F0}ms";
    }

    private void OnControllerPaddleStateChanged(object? sender, PaddleStateChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnControllerPaddleStateChanged(sender, e));
            return;
        }

        _ditIndicator.ForeColor = e.DitPressed ? IndicatorOn : IndicatorOff;
        _dahIndicator.ForeColor = e.DahPressed ? IndicatorOn : IndicatorOff;
        _skIndicator.ForeColor = e.StraightKeyPressed ? IndicatorOn : IndicatorOff;
    }

    private void OnControllerEdgeGenerated(object? sender, EdgeEvent e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnControllerEdgeGenerated(sender, e));
            return;
        }

        _keyStateLabel.Text = e.KeyDown ? "Key: Down" : "Key: Up";
    }

    private void OnControllerSidecarFailure(object? sender, SidecarFailureStateChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnControllerSidecarFailure(sender, e));
            return;
        }

        if (e.IsRecovered)
        {
            _pathLabel.Text = "Recovered — reconnecting...";
        }
        else if (e.Failure is not null)
        {
            _pathLabel.Text = $"Sidecar: {e.Failure.Reason}";
            _linkIndicator.ForeColor = WarningRed;
        }
    }

    private void OnSessionStatusChanged(object? sender, string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnSessionStatusChanged(sender, status));
            return;
        }

        _pathLabel.Text = status;

        // If session ended (unpaired), re-enable the Pair button.
        if (status.Contains("unpaired", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("ended", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _connectButton.Text = "Pair";
            _connectButton.Enabled = true;
        }
    }

    private void OnForwardRuleStatusChanged(object? sender, ForwardRuleStatusChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnForwardRuleStatusChanged(sender, e));
            return;
        }

        // Find the grid row with this rule ID and update its Status column.
        foreach (DataGridViewRow row in _forwardGrid.Rows)
        {
            if (row.IsNewRow) continue;
            if (row.Tag is Guid id && id == e.RuleId)
            {
                row.Cells["Status"].Value = e.Status.ToString();
                string ruleName = row.Cells["RuleName"]?.Value?.ToString() ?? "?";

                // Log the status transition
                _logService.Info($"Forward '{ruleName}': {e.Status}" +
                    (!string.IsNullOrEmpty(e.Message) ? $" — {e.Message}" : ""));

                // Make row read-only when actively listening/active, editable when idle/error
                bool active = e.Status is ForwardRuleStatus.Listening or ForwardRuleStatus.Active;
                row.Cells["RuleName"].ReadOnly = active;
                row.Cells["Protocol"].ReadOnly = active;
                row.Cells["ClientPort"].ReadOnly = active;
                row.Cells["StationPort"].ReadOnly = active;
                row.Cells["BindAddress"].ReadOnly = active;
                row.Cells["StationTarget"].ReadOnly = active;

                // Update the State column
                if (active)
                    row.Cells["Enabled"].Value = "ON";

                break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Interactive Tailscale Login
    // ──────────────────────────────────────────────────────────────────────────────

    private Panel? _loginPanel;
    private Label? _loginMessageLabel;
    private Button? _openBrowserButton;
    private Button? _pasteKeyButton;
    private TextBox? _authKeyTextBox;
    private Button? _submitKeyButton;
    private Label? _loginStatusLabel;
    private string? _pendingAuthUrl;

    private bool _loginDismissed; // Once dismissed, never show again in this session

    private static bool HasPersistedTailscaleState()
    {
        try
        {
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RWK", "tailscale", "rwk-client", "tailscaled.state");
            return File.Exists(stateDir);
        }
        catch { return false; }
    }

    private void OnControllerAuthUrlAvailable(object? sender, string authUrl)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnControllerAuthUrlAvailable(sender, authUrl));
            return;
        }

        // Don't show the panel if it was already dismissed (auth succeeded).
        // Note: we do NOT check HasPersistedTailscaleState() here because the sidecar
        // is actively reporting NeedsAuth — the persisted state is stale or invalid.
        if (_loginDismissed) return;

        _pendingAuthUrl = authUrl;
        if (_loginPanel is not null)
        {
            if (_loginStatusLabel is not null && !string.IsNullOrEmpty(authUrl))
                _loginStatusLabel.Text = "Login URL ready — click Open Browser.";
        }
        else
        {
            ShowLoginPanel(authUrl);
        }
    }

    private void ShowLoginPanel(string authUrl)
    {
        if (_loginPanel is not null)
        {
            // Already showing — just update the URL.
            _pendingAuthUrl = authUrl;
            return;
        }

        _loginPanel = new Panel
        {
            Size = new Size(460, 200),
            BackColor = SystemColors.Info,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.None
        };
        _loginPanel.Location = new Point(
            (ClientSize.Width - _loginPanel.Width) / 2,
            (ClientSize.Height - _loginPanel.Height) / 2);

        _loginMessageLabel = new Label
        {
            Text = "Sign in with Tailscale to connect.\nA browser window will open.",
            ForeColor = SystemColors.InfoText,
            Font = new Font(Font.FontFamily, 9.5f),
            AutoSize = false,
            Size = new Size(420, 44),
            Location = new Point(20, 12),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _openBrowserButton = new Button
        {
            Text = "Open Browser",
            Size = new Size(130, 34),
            Location = new Point(20, 62),
            UseVisualStyleBackColor = true
        };
        _openBrowserButton.Click += OnOpenBrowserClick;

        _pasteKeyButton = new Button
        {
            Text = "Paste Auth Key",
            Size = new Size(140, 34),
            Location = new Point(165, 62),
            UseVisualStyleBackColor = true
        };
        _pasteKeyButton.Click += OnPasteKeyInsteadClick;

        _authKeyTextBox = new TextBox
        {
            Size = new Size(290, 26),
            Location = new Point(20, 108),
            Visible = false,
            PlaceholderText = "tskey-auth-..."
        };

        _submitKeyButton = new Button
        {
            Text = "Submit",
            Size = new Size(80, 26),
            Location = new Point(320, 108),
            UseVisualStyleBackColor = true,
            Visible = false
        };
        _submitKeyButton.Click += OnSubmitAuthKeyClick;

        _loginStatusLabel = new Label
        {
            Text = "Waiting for browser login...",
            ForeColor = SystemColors.InfoText,
            Font = new Font(Font.FontFamily, 8.5f),
            AutoSize = true,
            Location = new Point(20, 150)
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
        // Always mark as dismissed so the panel can never reappear in this session,
        // even if called before the panel was shown (e.g. Connecting state arrives
        // before AuthUrlAvailable event is processed).
        _loginDismissed = true;

        if (_loginPanel is null) return;

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

    private void OnOpenBrowserClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingAuthUrl))
        {
            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = "Waiting for sidecar to provide login URL...";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _pendingAuthUrl,
                UseShellExecute = true
            });
            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = "Browser opened — complete login there, then return here.";
        }
        catch (Exception ex)
        {
            // Fallback: show the URL for manual copy
            if (_loginStatusLabel is not null)
                _loginStatusLabel.Text = $"Could not open browser ({ex.Message}). Copy this URL:\n{_pendingAuthUrl}";
        }
    }

    private void OnPasteKeyInsteadClick(object? sender, EventArgs e)
    {
        if (_authKeyTextBox is not null)
            _authKeyTextBox.Visible = true;
        if (_submitKeyButton is not null)
            _submitKeyButton.Visible = true;
        if (_loginStatusLabel is not null)
            _loginStatusLabel.Text = "Paste your auth key and click Submit.";
    }

    private async void OnSubmitAuthKeyClick(object? sender, EventArgs e)
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

    private void UpdateStatusForState(TailscaleState state)
    {
        switch (state)
        {
            case TailscaleState.Connected:
                _linkIndicator.ForeColor = Color.LimeGreen;
                _linkIndicator.Text = "●";
                _pathLabel.Text = "Connected";
                DismissLoginPanel();
                break;
            case TailscaleState.Connecting:
                _linkIndicator.ForeColor = SystemColors.Highlight;
                _linkIndicator.Text = "●";
                _pathLabel.Text = "Connecting...";
                // Don't dismiss login panel on Connecting — only dismiss on Connected.
                // A transient Connecting state shouldn't permanently block the auth panel.
                break;
            case TailscaleState.NeedsAuth:
                _linkIndicator.ForeColor = SystemColors.Highlight;
                _linkIndicator.Text = "●";
                _pathLabel.Text = "Waiting for login...";
                break;
            case TailscaleState.Fault:
                _linkIndicator.ForeColor = WarningRed;
                _linkIndicator.Text = "●";
                _pathLabel.Text = "Path lost";
                break;
            default:
                _linkIndicator.ForeColor = Color.Gray;
                _linkIndicator.Text = "●";
                _pathLabel.Text = "Disconnected";
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  UI control changes → live config updates
    // ──────────────────────────────────────────────────────────────────────────────

    private void OnSpeedSliderScrollLive(object? sender, EventArgs e)
    {
        _controller?.SetSpeed(_speedSlider.Value);
    }

    private void OnWeightSliderScrollLive(object? sender, EventArgs e)
    {
        _controller?.SetWeight(_weightSlider.Value);
    }

    private void OnToneFreqSliderScrollLive(object? sender, EventArgs e)
    {
        _controller?.SetToneFrequency(_toneFreqSlider.Value);
    }

    private void OnToneLevelSliderScrollLive(object? sender, EventArgs e)
    {
        _controller?.SetToneVolume(_toneLevelSlider.Value / 100.0);
    }

    private void OnModeComboChanged(object? sender, EventArgs e)
    {
        if (_modeCombo.SelectedIndex < 0) return;
        // Map combo index → KeyerMode (same order as PopulateDefaults)
        var mode = _modeCombo.SelectedIndex switch
        {
            0 => KeyerMode.IambicB,
            1 => KeyerMode.IambicA,
            2 => KeyerMode.Ultimatic,
            3 => KeyerMode.Bug,
            4 => KeyerMode.Straight,
            _ => KeyerMode.IambicB
        };
        _controller?.SetMode(mode);
    }

    private void OnPaddleReverseChanged(object? sender, EventArgs e)
    {
        _controller?.SetPaddleReverse(_paddleReverseCheck.Checked);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Station connection
    // ──────────────────────────────────────────────────────────────────────────────

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        string address = _stationAddressTextBox.Text.Trim();
        if (string.IsNullOrEmpty(address))
        {
            _pathLabel.Text = "Enter Station Address first.";
            return;
        }

        if (_controller is null || !_controller.IsRunning)
        {
            _pathLabel.Text = "Controller not running.";
            return;
        }

        // Save the address immediately so reconnect attempts use the new value
        _controller.SetStationAddress(address);

        try
        {
            _connectButton.Enabled = false;
            _connectButton.Text = "...";
            _pathLabel.Text = $"Connecting to {address}...";

            await _controller.ConnectToStationAsync(address).ConfigureAwait(true);

            _pathLabel.Text = $"Session active to {address}";
            _connectButton.Text = "Paired";
        }
        catch (Exception ex)
        {
            string msg = ex.InnerException?.Message ?? ex.Message;
            _pathLabel.Text = $"Connect failed: {msg}";
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "client.log"), $"[{DateTime.Now:HH:mm:ss.fff}] CONNECT ERROR: {ex}\n"); } catch { }
            _connectButton.Text = "Pair";
            _connectButton.Enabled = true;
        }
    }

    private void OnTestTxClick(object? sender, EventArgs e)
    {
        // Sends "VVV TESTING" through the keyer — plays sidetone locally
        // AND generates edge frames sent to the Station for keying.
        _controller?.SendTestMessage("VVV TESTING");
    }

    private void OnStationArmToggleChanged(object? sender, EventArgs e)
    {
        if (_controller is null) return;
        _controller.SetStationArmed(_stationArmToggle.Checked);
    }

    private void OnSetStationKeyClick(object? sender, EventArgs e)
    {
        string currentKey = _controller?.Config.Tailscale.PairingSecret ?? "";

        string? input = ShowInputDialog(
            "Enter the Station Pairing Key",
            "Set Station Key",
            currentKey);

        if (input is null) return; // User cancelled

        input = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("Key cannot be empty.", "Invalid Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _controller?.SetPairingSecret(input);
        _logService.Info($"Station pairing key set: {input}");
    }

    private static string? ShowInputDialog(string prompt, string title, string defaultValue)
    {
        var form = new Form
        {
            Text = title,
            Size = new Size(350, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var label = new Label { Text = prompt, Left = 10, Top = 15, AutoSize = true };
        var textBox = new TextBox { Left = 10, Top = 40, Width = 310, Text = defaultValue,
            CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 11F) };
        var okBtn = new Button { Text = "OK", Left = 160, Top = 75, Width = 75, DialogResult = DialogResult.OK };
        var cancelBtn = new Button { Text = "Cancel", Left = 245, Top = 75, Width = 75, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { label, textBox, okBtn, cancelBtn });
        form.AcceptButton = okBtn;
        form.CancelButton = cancelBtn;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }

    private async void OnDeleteTailscaleAuthClick(object? sender, EventArgs e)
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
            // Stop the sidecar first so it releases file locks on the state directory.
            if (_controller is not null)
            {
                await _controller.StopSidecarAsync().ConfigureAwait(true);
            }

            // Small delay to let the process fully exit and release handles.
            await Task.Delay(500).ConfigureAwait(true);

            // Delete the persisted Tailscale state directory
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RWK", "tailscale", "rwk-client");

            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }

            // Clear the auth key from config
            if (_controller is not null)
            {
                _controller.ClearTailscaleAuth();
            }

            MessageBox.Show(
                "Tailscale authorization has been deleted.\n\n" +
                "Restart the application to re-authenticate.",
                "Authorization Deleted",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to delete authorization: {ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnWinKeyerPortChanged(object? sender, EventArgs e)
    {
        if (_suppressPortEvents) return;
        string? port = _winKeyerPortCombo.SelectedItem as string;
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "winkeyer.log"), $"[{DateTime.Now:HH:mm:ss.fff}] UI: WinKeyer port selected: '{port}'\n"); } catch { }
        if (string.IsNullOrEmpty(port) || port == "(None)" || _controller is null)
        {
            // (None) selected — stop existing WinKeyer connection.
            if (_controller is not null)
            {
                try { _controller.DisconnectWinKeyerPort(); } catch { }
            }
            return;
        }

        // Uniqueness check: can't use same port as paddle.
        string? paddlePort = _paddlePortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(paddlePort) && paddlePort != "(None)" &&
            string.Equals(port, paddlePort, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("WinKeyer port cannot be the same as the Paddle port.",
                "Port Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _winKeyerPortCombo.SelectedItem = "(None)";
            return;
        }

        _controller.ConnectWinKeyerPort(port);
    }

    private void OnPaddlePortChanged(object? sender, EventArgs e)
    {
        if (_suppressPortEvents) return;
        string? port = _paddlePortCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(port) || port == "(None)" || _controller is null)
        {
            // (None) selected — stop existing paddle connection.
            if (_controller is not null)
            {
                try { _controller.DisconnectPaddlePort(); } catch { }
            }
            return;
        }

        // Uniqueness check: can't use same port as WinKeyer.
        string? wkPort = _winKeyerPortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(wkPort) && wkPort != "(None)" &&
            string.Equals(port, wkPort, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Paddle port cannot be the same as the WinKeyer port.",
                "Port Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _paddlePortCombo.SelectedItem = "(None)";
            return;
        }

        _controller.ConnectPaddlePort(port);
    }

    private void OnWinKeyerModeChanged(object? sender, EventArgs e)
    {
        if (_suppressPortEvents) return;
        if (_controller is null) return;

        // Only act on the radio button that became checked (avoid double-fire)
        if (sender is RadioButton rb && !rb.Checked) return;

        // Hide hardware status indicator when mode changes.
        _wkHardwareStatus.Visible = false;
        _wkHardwareStatus.Text = "";

        // Show/hide sidetone mute indicator.
        bool isHardwareMode = _wkModeHardwareRadio.Checked;
        _sidetoneMuteLabel.Visible = isHardwareMode;

        var mode = isHardwareMode ? RWK.Shared.IO.WinKeyerMode.HardwareWinKey : RWK.Shared.IO.WinKeyerMode.LoggerApp;
        _controller.SetWinKeyerMode(mode);

        // Persist the mode selection.
        _controller.PersistWinKeyerMode(mode);

        // Reconnect the current WinKeyer port with the new mode.
        string? port = _winKeyerPortCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(port) && port != "(None)")
        {
            _controller.ConnectWinKeyerPort(port);
        }
    }

    private void OnHardwareWinKeyerConnected(object? sender, int chipVersion)
    {
        if (InvokeRequired) { BeginInvoke(() => OnHardwareWinKeyerConnected(sender, chipVersion)); return; }

        _wkHardwareStatus.Text = $"\u2714 WK{chipVersion}";
        _wkHardwareStatus.Visible = true;
    }

    private async void OnWinKeyerLoopbackTestClick(object? sender, EventArgs e)
    {
        if (_controller is null) return;

        _wkLoopbackTestBtn.Enabled = false;
        _wkLoopbackTestBtn.Text = "Testing...";

        try
        {
            await _controller.RunWinKeyerLoopbackTestAsync().ConfigureAwait(true);
            _wkLoopbackTestBtn.Text = "Test OK";
        }
        catch (Exception ex)
        {
            _wkLoopbackTestBtn.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            // Re-enable after a short delay so the result is visible
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                if (!IsDisposed)
                    BeginInvoke(() =>
                    {
                        _wkLoopbackTestBtn.Text = "WinKeyer Loopback Test";
                        _wkLoopbackTestBtn.Enabled = true;
                    });
            });
        }
    }
}
