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
    private bool _suppressFlexCheckEvent;
    private NotifyIcon? _trayIcon;

    // "PLEASE WAIT" overlay — shown from startup until Connected or Wizard opens
    private Panel? _waitOverlay;

    // Device monitoring
    private System.Windows.Forms.Timer? _portPollTimer;
    private string[] _lastKnownPorts = Array.Empty<string>();
    private MMDeviceEnumerator? _audioEnumerator;
    private AudioDeviceNotificationClient? _audioNotificationClient;

    public MainForm()
    {
        InitializeComponent();
        _instance = this;
        Text = $"RWK Router/Keyer Client Version {AppVersion} — Any Rig, Any Internet, Anytime";

        // Keyer sliders are positioned relative to the actual (runtime) group width so
        // the Speed slider always ends a few px inside the group edge, and the Weight
        // slider mirrors the Sidetone Volume slider's left/right extents.
        _keyerGroup.SizeChanged += (_, _) => LayoutKeyerSliders();
        LayoutKeyerSliders();

        PopulateDefaults();
        InitializeDeviceMonitoring();
        InitializeLogService();

        // System tray icon — minimize to tray
        InitializeTrayIcon();

        // Auto-start the controller on form load so the interactive login
        // prompt appears immediately on first run (matching Station behavior).
        Load += async (_, _) =>
        {
            ShowWaitOverlay();
            await Task.Yield(); // Let the form finish rendering first
            OnStartClick(this, EventArgs.Empty);

            // Check GitHub for a newer build (fire-and-forget; fails silently offline).
            _ = CheckForUpdatesAsync();
        };
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

        // Installer launched — close cleanly so ports/sidecar release before it replaces files.
        _updateBannerLabel.Text = "Update started — closing…";
        _updateBannerLabel.LinkArea = new LinkArea(0, 0);
        Close();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "RWK Client",
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

    /// <summary>Static reference to the live form for global exception handlers.</summary>
    private static MainForm? _instance;

    /// <summary>
    /// Called by Program.cs global exception handlers to surface a system error toast.
    /// </summary>
    public static void NotifySystemError()
    {
        _instance?.ShowToast("System Error occurred. Please restart RWK-Client.", ToolTipIcon.Error);
    }

    /// <summary>
    /// Shows a toast notification (balloon tip) in the lower-right of the screen.
    /// Works whether the window is full size, minimized, or hidden to the tray.
    /// </summary>
    private void ShowToast(string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowToast(message, icon)); return; }
        if (_trayIcon is null) return;

        // The NotifyIcon must be visible for the balloon to appear. If the window
        // is not minimized (tray icon normally hidden), briefly make it visible.
        bool wasVisible = _trayIcon.Visible;
        _trayIcon.Visible = true;
        _trayIcon.BalloonTipTitle = "RWK-Client";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(4000);

        // If the window is not minimized, hide the tray icon again after the
        // balloon has had time to display.
        if (!wasVisible && WindowState != FormWindowState.Minimized)
        {
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                t.Dispose();
                if (_trayIcon is not null && WindowState != FormWindowState.Minimized)
                    _trayIcon.Visible = false;
            };
            t.Start();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            if (_trayIcon is not null)
                _trayIcon.Visible = true;
            ShowToast("Client Minimized. Click the icon in the system tray to restore.");
        }
        else
        {
            if (_trayIcon is not null)
                _trayIcon.Visible = false;
        }
    }

    /// <summary>
    /// Positions the Speed and Weight sliders inside the Keyer group based on the
    /// group's real runtime width (the window is fixed-size, so this runs once at
    /// startup and whenever the group is sized by the layout panel).
    ///
    /// Speed slider: right edge sits <see cref="EdgeGap"/> px inside the group's inner
    /// right edge, left edge fixed at X=105 (aligns with the Weight label reference).
    ///
    /// Weight row: mirrors the Sidetone "Volume" slider's group-relative extents —
    /// the "Weight:" label left edge lines up with the Volume slider's left (X=20),
    /// and the Weight slider's right edge lines up with the Volume slider's right
    /// (X=220). The slider is shortened on its left so the label + value fit on the
    /// same vertical line as the slider.
    /// </summary>
    private void LayoutKeyerSliders()
    {
        if (_speedSlider is null || _weightSlider is null) return;

        const int EdgeGap = 5;     // px gap between slider right edge and group inner edge
        const int ModeLabelLeft = 12; // "Mode:" label left (weight label aligns to this)

        int innerRight = _keyerGroup.ClientSize.Width - _keyerGroup.Padding.Right;
        int rightEdge = innerRight - EdgeGap;

        // --- Speed slider: keep left at 105, stretch to rightEdge ---
        int speedLeft = _speedSlider.Left; // fixed reference (105)
        int speedWidth = Math.Max(60, rightEdge - speedLeft);
        _speedSlider.Width = speedWidth;

        // --- Weight row: label/value left-aligned with "Mode:" label; slider right edge
        //     matches the speed slider's right edge. Moved up so it clears the Mode row. ---
        _weightCaptionLabel.Left = ModeLabelLeft;
        _weightValueLabel.Left = _weightCaptionLabel.Right + 4;

        // Slider starts after the value label (+ small gap), right edge = speed right edge.
        int weightSliderLeft = _weightValueLabel.Right + 8;
        int weightWidth = Math.Max(50, rightEdge - weightSliderLeft);
        _weightSlider.Left = weightSliderLeft;
        _weightSlider.Width = weightWidth;

        // Label/value top-aligned with the weight slider (share the same top).
        _weightCaptionLabel.Top = _weightSlider.Top;
        _weightValueLabel.Top = _weightSlider.Top;
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
        _audioDeviceCombo.SelectedIndexChanged += OnAudioDeviceComboChanged;

        // Bind address presets are built into the DataGridViewIpAddressColumn

        // Status strip defaults
        _linkIndicator.Text = "●";
        _linkIndicator.ForeColor = Color.Gray;
        _pathLabel.Text = "Disconnected";
        _rttLabel.Text = "RTT: --";
        _bufferLabel.Text = "Buffer: --";
        _keyStateLabel.Text = "Key: Up";

        // Connect button wiring
        _connectButton.Click += OnConnectClick;

        // Pair button validation: enable only when a station is selected
        // (combo wired in Designer via SelectedIndexChanged)

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

        // Keyer mode combo
        _modeCombo.SelectedIndexChanged += OnModeComboChanged;

        // FlexRadio discovery re-emission
        _flexEnableCheck.CheckedChanged += (_, _) =>
        {
            if (_suppressFlexCheckEvent) return;
            _controller?.SetDiscoveryEmitEnabled(_flexEnableCheck.Checked);
        };

        // Keyboard paddle presets
        foreach (var preset in IO.KeyboardPaddleInput.Presets)
            _keyboardPaddleCombo.Items.Add(preset);
        _keyboardPaddleCombo.SelectedIndex = 0;
        _keyboardPaddleCheck.CheckedChanged += OnKeyboardPaddleCheckChanged;
        _keyboardPaddleCombo.SelectedIndexChanged += OnKeyboardPaddlePresetChanged;

        // CW Macros + Type-ahead
        WireMacroButtons();

        // PTT button, hotkey, footswitch
        WirePttControls();

        // Keyer and Inputs panels stay enabled so the operator can test the keyer
        // locally (sidetone only) even before pairing with a Station.

        // PageUp/PageDn speed adjustment (global within the app)
        KeyPreview = true;
        KeyDown += OnFormKeyDown;
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.PageUp)
        {
            int newSpeed = Math.Min(_speedSlider.Value + 2, _speedSlider.Maximum);
            _speedSlider.Value = newSpeed;
            _speedLabel.Text = newSpeed.ToString();
            _controller?.SetSpeed(newSpeed);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.PageDown)
        {
            int newSpeed = Math.Max(_speedSlider.Value - 2, _speedSlider.Minimum);
            _speedSlider.Value = newSpeed;
            _speedLabel.Text = newSpeed.ToString();
            _controller?.SetSpeed(newSpeed);
            e.Handled = true;
        }
    }

    private Label? _keyerBusyLabel;

    private void OnControllerKeyerBusy(object? sender, EventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(() => OnControllerKeyerBusy(sender, e)); return; }

        // Show a red KEYER BUSY label below the Pair button
        if (_keyerBusyLabel is null)
        {
            _keyerBusyLabel = new Label
            {
                Text = " KEYER BUSY ",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(200, 40, 40),
                AutoSize = true,
                Location = new Point(460, 32),
                Padding = new Padding(4, 2, 4, 2),
                Name = "_keyerBusyLabel"
            };
            // Add to the connection panel (parent of _connectButton)
            _connectButton.Parent?.Controls.Add(_keyerBusyLabel);
        }
        _keyerBusyLabel.Visible = true;
        _pathLabel.Text = "Keyer Busy (N1MM relay active)";
    }



    private IO.KeyboardPaddleInput? _keyboardPaddle;

    private void OnKeyboardPaddleCheckChanged(object? sender, EventArgs e)
    {
        _keyboardPaddleCombo.Enabled = _keyboardPaddleCheck.Checked;

        if (_keyboardPaddleCheck.Checked)
        {
            _keyboardPaddle ??= new IO.KeyboardPaddleInput();
            if (_keyboardPaddleCombo.SelectedItem is IO.KeyPairPreset preset)
                _keyboardPaddle.SetKeyPair(preset);
            _keyboardPaddle.StateChanged += OnKeyboardPaddleStateChanged;
            _keyboardPaddle.Start("keyboard");
            _logService.Info($"Keyboard paddle enabled: {_keyboardPaddle.ActivePreset.DisplayName}");
        }
        else
        {
            if (_keyboardPaddle is not null)
            {
                _keyboardPaddle.StateChanged -= OnKeyboardPaddleStateChanged;
                _keyboardPaddle.Stop();
            }
            _logService.Info("Keyboard paddle disabled.");
        }
    }

    private void OnKeyboardPaddlePresetChanged(object? sender, EventArgs e)
    {
        if (_keyboardPaddle is not null && _keyboardPaddleCheck.Checked &&
            _keyboardPaddleCombo.SelectedItem is IO.KeyPairPreset preset)
        {
            _keyboardPaddle.SetKeyPair(preset);
            _logService.Info($"Keyboard paddle keys: {preset.DisplayName}");
        }
    }

    private void OnKeyboardPaddleStateChanged(object? sender, PaddleStateChangedEventArgs e)
    {
        // Feed keyboard paddle state into the controller's keyer, same as the serial paddle.
        _controller?.InjectPaddleState(e);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  CW Macros
    // ──────────────────────────────────────────────────────────────────────────────

    private string[] _macroTexts = new string[8] { "CQ DE MYCALL", "599", "TU", "73", "MYCALL", "QRL?", "?", "QRX" };
    private string[] _macroNames = new string[8] { "CQ", "599", "TU", "73", "MYCALL", "QRL?", "?", "QRX" };

    private void WireMacroButtons()
    {
        _macro1Btn.Click += (_, _) => SendMacro(0);
        _macro2Btn.Click += (_, _) => SendMacro(1);
        _macro3Btn.Click += (_, _) => SendMacro(2);
        _macro4Btn.Click += (_, _) => SendMacro(3);
        _macro5Btn.Click += (_, _) => SendMacro(4);
        _macro6Btn.Click += (_, _) => SendMacro(5);
        _macro7Btn.Click += (_, _) => SendMacro(6);
        _macro8Btn.Click += (_, _) => SendMacro(7);
        _macroEditBtn.Click += OnMacroEditClick;

        // Load persisted macros from config
        LoadMacrosFromConfig();
        UpdateMacroButtonLabels();

        // Type-ahead CW box: send each character as it's typed
        _cwTypeAheadBox.KeyPress += OnCwTypeAheadKeyPress;
    }

    private void SendMacro(int slot)
    {
        if (slot < 0 || slot >= _macroTexts.Length) return;
        string text = _macroTexts[slot];
        if (!string.IsNullOrEmpty(text))
        {
            _controller?.SendCwText(text);
            _logService.Info($"Macro F{slot + 1}: {text}");
        }
    }

    private void OnCwTypeAheadKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_controller is null) return;
        char c = char.ToUpperInvariant(e.KeyChar);
        if (c >= ' ' && c <= '~') // Printable ASCII
        {
            _controller.SendCwText(c.ToString());
            e.Handled = true;
        }
    }

    private void OnMacroEditClick(object? sender, EventArgs e)
    {
        using var dlg = new Form
        {
            Text = "Edit CW Macros",
            Size = new Size(420, 480),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var labels = new Label[8];
        var nameBoxes = new TextBox[8];
        var textBoxes = new TextBox[8];

        for (int i = 0; i < 8; i++)
        {
            int y = 12 + i * 50;
            labels[i] = new Label { Text = $"F{i + 1}:", Location = new Point(10, y + 3), AutoSize = true };
            nameBoxes[i] = new TextBox { Location = new Point(35, y), Size = new Size(60, 22), Text = _macroNames[i] };
            textBoxes[i] = new TextBox { Location = new Point(100, y), Size = new Size(290, 22), Text = _macroTexts[i] };
            dlg.Controls.AddRange(new Control[] { labels[i], nameBoxes[i], textBoxes[i] });
        }

        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(230, 418), Size = new Size(75, 28) };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(310, 418), Size = new Size(75, 28) };
        dlg.Controls.AddRange(new Control[] { okBtn, cancelBtn });
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            for (int i = 0; i < 8; i++)
            {
                _macroNames[i] = nameBoxes[i].Text.Trim();
                _macroTexts[i] = textBoxes[i].Text.Trim();
            }
            UpdateMacroButtonLabels();
            SaveMacrosToConfig();
        }
    }

    private void UpdateMacroButtonLabels()
    {
        _macro1Btn.Text = string.IsNullOrEmpty(_macroNames[0]) ? "F1" : _macroNames[0];
        _macro2Btn.Text = string.IsNullOrEmpty(_macroNames[1]) ? "F2" : _macroNames[1];
        _macro3Btn.Text = string.IsNullOrEmpty(_macroNames[2]) ? "F3" : _macroNames[2];
        _macro4Btn.Text = string.IsNullOrEmpty(_macroNames[3]) ? "F4" : _macroNames[3];
        _macro5Btn.Text = string.IsNullOrEmpty(_macroNames[4]) ? "F5" : _macroNames[4];
        _macro6Btn.Text = string.IsNullOrEmpty(_macroNames[5]) ? "F6" : _macroNames[5];
        _macro7Btn.Text = string.IsNullOrEmpty(_macroNames[6]) ? "F7" : _macroNames[6];
        _macro8Btn.Text = string.IsNullOrEmpty(_macroNames[7]) ? "F8" : _macroNames[7];
    }

    private void LoadMacrosFromConfig()
    {
        // Macros stored in a simple text file alongside the config
        string path = Path.Combine(AppContext.BaseDirectory, "macros.txt");
        if (!File.Exists(path)) return;
        try
        {
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < Math.Min(lines.Length / 2, 8); i++)
            {
                _macroNames[i] = lines[i * 2];
                _macroTexts[i] = lines[i * 2 + 1];
            }
        }
        catch { }
    }

    private void SaveMacrosToConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "macros.txt");
        try
        {
            var lines = new string[16];
            for (int i = 0; i < 8; i++)
            {
                lines[i * 2] = _macroNames[i];
                lines[i * 2 + 1] = _macroTexts[i];
            }
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  PTT Controls (button, hotkey, footswitch)
    // ──────────────────────────────────────────────────────────────────────────────

    private IO.PttHotKeyHook? _pttHotKeyHook;
    private IO.PttFootswitchPoller? _pttFootswitch;
    private bool _pttHookEnabledForSession;

    private void WirePttControls()
    {
        // PTT button: momentary (MouseDown = assert, MouseUp = deassert)
        _pttButton.MouseDown += OnPttButtonMouseDown;
        _pttButton.MouseUp += OnPttButtonMouseUp;
        _pttButton.MouseLeave += OnPttButtonMouseLeave; // safety: release if cursor leaves

        // Set Hot Key button
        _pttSetHotKeyBtn.Click += OnPttSetHotKeyClick;

        // Initialize the hotkey hook (but don't start it yet — starts on pair)
        _pttHotKeyHook = new IO.PttHotKeyHook();
        _pttHotKeyHook.PttStateChanged += OnPttHotKeyStateChanged;
        _pttHotKeyHook.HotKeyCaptured += OnPttHotKeyCaptured;

        // Load persisted hotkey
        var config = _controller?.Config;
        if (config is not null && !string.IsNullOrEmpty(config.PttHotKey))
        {
            var info = IO.PttHotKeyInfo.Deserialize(config.PttHotKey);
            if (info is not null)
            {
                _pttHotKeyHook.SetHotKey(info);
                _pttHotKeyLabel.Text = $"Hot Key: {info.ToDisplayString()}";
                _pttHotKeyLabel.ForeColor = SystemColors.ControlText;
                _pttSetHotKeyBtn.Text = "Clear Hot Key";
            }
        }

        // DCD = PTT checkbox: uses DCD pin on the paddle serial port for footswitch
        _pttDcdCheck.CheckedChanged += OnPttDcdCheckChanged;
        if (config is not null && !string.IsNullOrEmpty(config.PttInputPortName))
            _pttDcdCheck.Checked = true; // Restore persisted state
    }

    private void OnPttButtonMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pttButton.BackColor = Color.FromArgb(200, 40, 40); // Red when active
            _controller?.AssertPtt();
        }
    }

    private void OnPttButtonMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pttButton.BackColor = Color.FromArgb(60, 60, 60);
            _controller?.DeassertPtt();
        }
    }

    private void OnPttButtonMouseLeave(object? sender, EventArgs e)
    {
        // Safety: if mouse leaves button while held, release PTT
        _pttButton.BackColor = Color.FromArgb(60, 60, 60);
        if (_controller?.IsPttAsserted == true)
            _controller.DeassertPtt();
    }

    private void OnPttSetHotKeyClick(object? sender, EventArgs e)
    {
        if (_pttHotKeyHook is null) return;

        // If a hotkey is already configured, this button acts as "Clear Hot Key".
        if (_pttHotKeyHook.HasHotKey)
        {
            _pttHotKeyHook.ClearHotKey();
            _controller?.UpdateConfig(c => c with { PttHotKey = null });
            _pttSetHotKeyBtn.Text = "Set Hot Key";
            _pttHotKeyLabel.Text = "(none)";
            _pttHotKeyLabel.ForeColor = SystemColors.GrayText;
            _logService.Info("PTT hotkey cleared");
            return;
        }

        _pttSetHotKeyBtn.Text = "Press key...";
        _pttSetHotKeyBtn.Enabled = false;
        _pttHotKeyLabel.Text = "Waiting for key combo...";
        _pttHotKeyLabel.ForeColor = Color.FromArgb(200, 120, 0);
        _pttHotKeyHook.StartCapture();
    }

    private void OnPttHotKeyCaptured(object? sender, IO.PttHotKeyInfo info)
    {
        if (InvokeRequired) { BeginInvoke(() => OnPttHotKeyCaptured(sender, info)); return; }

        _pttSetHotKeyBtn.Text = "Clear Hot Key";
        _pttSetHotKeyBtn.Enabled = true;
        _pttHotKeyLabel.Text = $"Hot Key: {info.ToDisplayString()}";
        _pttHotKeyLabel.ForeColor = SystemColors.ControlText;

        // Persist to config
        if (_controller is not null)
        {
            _controller.UpdateConfig(c => c with { PttHotKey = info.Serialize() });
        }

        _logService.Info($"PTT hotkey set: {info.ToDisplayString()}");
    }

    private void OnPttHotKeyStateChanged(object? sender, bool pttDown)
    {
        if (InvokeRequired) { BeginInvoke(() => OnPttHotKeyStateChanged(sender, pttDown)); return; }

        if (pttDown)
        {
            _pttButton.BackColor = Color.FromArgb(200, 40, 40);
            _controller?.AssertPtt();
        }
        else
        {
            _pttButton.BackColor = Color.FromArgb(60, 60, 60);
            _controller?.DeassertPtt();
        }
    }

    private void OnPttDcdCheckChanged(object? sender, EventArgs e)
    {
        if (_pttDcdCheck.Checked)
        {
            // Use DCD pin on the paddle serial port
            _controller?.UpdateConfig(c => c with { PttInputPortName = "DCD" });
            _logService.Info("PTT via DCD pin enabled (uses paddle port).");
        }
        else
        {
            StopPttFootswitch();
            _controller?.UpdateConfig(c => c with { PttInputPortName = null });
            _logService.Info("PTT via DCD pin disabled.");
        }
    }

    private void StartPttFootswitchIfPaired(string portName)
    {
        if (!_pttHookEnabledForSession) return; // Only active while paired

        // DCD pin is read via DsrHolding on a port where we monitor DCD.
        // Use DSR line (which reads DCD on many USB-serial adapters).
        _pttFootswitch = new IO.PttFootswitchPoller(IO.PttFootswitchPoller.PttInputLine.DSR);
        _pttFootswitch.PttStateChanged += OnPttFootswitchStateChanged;
        try
        {
            _pttFootswitch.Start(portName);
            _logService.Info($"PTT footswitch (DCD) started on {portName}.");
        }
        catch (Exception ex)
        {
            _logService.Info($"PTT footswitch failed on {portName}: {ex.Message}");
            _pttFootswitch.Dispose();
            _pttFootswitch = null;
        }
    }

    private void StopPttFootswitch()
    {
        if (_pttFootswitch is not null)
        {
            _pttFootswitch.PttStateChanged -= OnPttFootswitchStateChanged;
            _pttFootswitch.Dispose();
            _pttFootswitch = null;
        }
    }

    private void OnPttFootswitchStateChanged(object? sender, bool pressed)
    {
        if (InvokeRequired) { BeginInvoke(() => OnPttFootswitchStateChanged(sender, pressed)); return; }

        if (pressed)
        {
            _pttButton.BackColor = Color.FromArgb(200, 40, 40);
            _controller?.AssertPtt();
        }
        else
        {
            _pttButton.BackColor = Color.FromArgb(60, 60, 60);
            _controller?.DeassertPtt();
        }
    }

    /// <summary>
    /// Called when pairing succeeds — enables the PTT hotkey hook and footswitch.
    /// </summary>
    private void EnablePttForSession()
    {
        _pttHookEnabledForSession = true;

        // Start the hotkey hook if a hotkey is configured
        if (_pttHotKeyHook?.HasHotKey == true && !_pttHotKeyHook.IsRunning)
            _pttHotKeyHook.Start();

        // Start DCD footswitch on paddle port if enabled
        if (_pttDcdCheck.Checked)
        {
            string? paddlePort = _paddlePortCombo.SelectedItem?.ToString();
            if (paddlePort is not null and not "(None)")
                StartPttFootswitchIfPaired(paddlePort);
        }
    }

    /// <summary>
    /// Called on unpair / session loss — disables PTT hotkey hook and footswitch.
    /// </summary>
    private void DisablePttForSession()
    {
        _pttHookEnabledForSession = false;

        // Stop the hotkey hook (but don't clear the configured key)
        _pttHotKeyHook?.Stop();

        // Stop footswitch
        StopPttFootswitch();

        // Ensure PTT is released
        if (_controller?.IsPttAsserted == true)
            _controller.DeassertPtt();
        _pttButton.BackColor = Color.FromArgb(60, 60, 60);
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

    private void OnAudioDeviceComboChanged(object? sender, EventArgs e)
    {
        if (_controller is null) return;
        string? selected = _audioDeviceCombo.SelectedItem?.ToString();
        if (selected is null or "(Default Output)") 
        {
            _controller.SetSidetoneDevice(null);
            _logService.Info("Sidetone device: (Default Output)");
            return;
        }

        // Look up the MMDevice ID from the friendly name
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
            var match = devices.FirstOrDefault(d => d.FriendlyName == selected);
            if (match is not null)
            {
                _controller.SetSidetoneDevice(match.ID);
                _logService.Info($"Sidetone device changed: {selected}");
            }
            else
            {
                _controller.SetSidetoneDevice(null);
                _logService.Info($"Sidetone device '{selected}' not found, using default.");
            }
        }
        catch
        {
            _controller.SetSidetoneDevice(null);
        }
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
        _forwardGrid.Rows.Add("OFF", DirectionArrow(rule.Direction), rule.Name, "TCP", rule.ClientPort, rule.StationPort, rule.BindAddress, rule.StationTargetAddress, "Idle");
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

        var conflicts = new List<string>();

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
                var direction = pfr.Direction.Equals("StationToClient", StringComparison.OrdinalIgnoreCase)
                    ? ForwardDirection.StationToClient : ForwardDirection.ClientToStation;
                var newRule = new ForwardRule(
                    Guid.NewGuid(),
                    pfr.Name,
                    protocol,
                    pfr.ClientPort,
                    pfr.StationPort,
                    pfr.Enabled,
                    pfr.BindAddress,
                    ForwardRuleType.Generic,
                    pfr.StationTarget,
                    direction);

                try
                {
                    _controller.AddForwardRule(newRule);
                }
                catch (Exception ex)
                {
                    // Surface duplicate/port-conflict failures instead of silently dropping.
                    conflicts.Add($"'{pfr.Name}' ({pfr.Protocol} {pfr.ClientPort}): {ex.Message}");
                    _logService.Info($"Wizard/Import rule '{pfr.Name}' rejected: {ex.Message}");
                }
            }
        }

        // Reload grid from controller state.
        LoadForwardRulesIntoGrid();
        _logService.Info($"Wizard/Import: {rules.Count} rules merged into forwarding table.");

        // If any rules failed (usually a port already in use by another device of the
        // same type), tell the user and remind them ports are editable in the grid.
        if (conflicts.Count > 0)
        {
            MessageBox.Show(
                "Some rules could not be added because their ports are already in use:\n\n" +
                string.Join("\n", conflicts) +
                "\n\nThis usually happens when you add a second device of the same type " +
                "(both want the same default ports). You can edit the Client Port / Station Port " +
                "columns in the Ham Router grid to assign different ports, then enable the rules.",
                "Port Conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private ForwardRule BuildRuleFromRow(DataGridViewRow row)
    {
        string name = row.Cells["RuleName"]?.Value?.ToString() ?? "Rule";
        string proto = row.Cells["Protocol"]?.Value?.ToString() ?? "TCP";
        int.TryParse(row.Cells["ClientPort"]?.Value?.ToString(), out int clientPort);
        int.TryParse(row.Cells["StationPort"]?.Value?.ToString(), out int stationPort);
        string bind = row.Cells["BindAddress"]?.Value?.ToString() ?? "127.0.0.1";
        string target = row.Cells["StationTarget"]?.Value?.ToString() ?? "127.0.0.1";
        string dirStr = row.Cells["Direction"]?.Value?.ToString() ?? "\u2192";
        var direction = dirStr == "\u2190" ? ForwardDirection.StationToClient : ForwardDirection.ClientToStation;

        return new ForwardRule(
            Guid.NewGuid(),
            name,
            proto.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? ForwardProtocol.Udp : ForwardProtocol.Tcp,
            clientPort > 0 ? clientPort : 4532,
            stationPort > 0 ? stationPort : 4532,
            Enabled: false,
            BindAddress: bind,
            StationTargetAddress: target,
            Direction: direction);
    }

    /// <summary>Returns a Unicode arrow indicating forward direction: → for ClientToStation, ← for StationToClient.</summary>
    private static string DirectionArrow(ForwardDirection dir)
        => dir == ForwardDirection.StationToClient ? "\u2190" : "\u2192";

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
                    DirectionArrow(rule.Direction),
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
        var maxExposure = AddressExposure.Loopback;
        bool hasRemoteRig = false;

        foreach (DataGridViewRow row in _forwardGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var bind = row.Cells["BindAddress"]?.Value?.ToString() ?? "127.0.0.1";
            var exposure = AddressExposureClassifier.Classify(bind);
            if (exposure > maxExposure) maxExposure = exposure;

            var name = row.Cells["RuleName"]?.Value?.ToString() ?? "";
            if (name.Contains("RemoteRig", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RRC", StringComparison.OrdinalIgnoreCase))
                hasRemoteRig = true;
        }

        // Exposure warning (10.14, 10.28, 13.15) — differentiated by severity.
        if (maxExposure == AddressExposure.GlobalUnicast)
        {
            _bindWarningLabel.Text = "\u26A0 GLOBAL address detected: this is a globally routable address " +
                "with no NAT in front of it \u2014 this tunnel path may be reachable from the public internet, " +
                "not just your LAN, with no authentication of its own.";
            _bindWarningLabel.Visible = true;
        }
        else if (maxExposure == AddressExposure.PrivateOrLinkLocal)
        {
            _bindWarningLabel.Text = "\u26A0 Non-loopback bind detected: this exposes an unauthenticated " +
                "tunnel path into the Station's network to every host on your local network.";
            _bindWarningLabel.Visible = true;
        }
        else
        {
            _bindWarningLabel.Visible = false;
        }

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
            // Load persisted station list and select previously used station
            RefreshStationDropdown();
            // Try to auto-select a station matching the persisted address
            string? lastAddr = _controller.Config.Tailscale.StationAddress;
            if (!string.IsNullOrEmpty(lastAddr))
            {
                for (int i = 1; i < _stationCombo.Items.Count; i++)
                {
                    if (_stationCombo.Items[i] is RWK.Shared.Config.StationEntry se &&
                        se.TailscaleIp == lastAddr)
                    {
                        _stationCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            ValidatePairButton();

            // Load persisted port selections
            // Suppress event handlers during load so we don't double-connect.
            _suppressPortEvents = true;

            // Restore WinKeyer mode BEFORE port selection so reconnect uses the right mode.
            if (_controller.Config.WinKeyerMode == RWK.Shared.IO.WinKeyerMode.HardwareWinKey)
            {
                _wkModeHardwareRadio.Checked = true;
                _controller.SetWinKeyerMode(RWK.Shared.IO.WinKeyerMode.HardwareWinKey);
                _wkModeHelpLabel.Text = "Warning: one-character delay in sending. Local sidetone muted.";
            }
            else
            {
                _wkModeLoggerRadio.Checked = true;
                _wkModeHelpLabel.Text = "N1MM, DXLog, Wintest, etc.";
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

            // Restore keyer mode to combo (same order as PopulateDefaults: IambicB=0, IambicA=1, Ultimatic=2, Bug=3, Straight=4)
            int modeIndex = _controller.Config.KeyerMode switch
            {
                KeyerMode.IambicB => 0,
                KeyerMode.IambicA => 1,
                KeyerMode.Ultimatic => 2,
                KeyerMode.Bug => 3,
                KeyerMode.Straight => 4,
                _ => 0
            };
            _modeCombo.SelectedIndex = modeIndex;
            _toneFreqSlider.Value = Math.Clamp(_controller.Config.Sidetone.FrequencyHz, _toneFreqSlider.Minimum, _toneFreqSlider.Maximum);
            _toneFreqValueLabel.Text = $"{_toneFreqSlider.Value} Hz";
            int volPct = (int)(_controller.Config.Sidetone.Volume * 100);
            _toneLevelSlider.Value = Math.Clamp(volPct, _toneLevelSlider.Minimum, _toneLevelSlider.Maximum);
            _toneLevelValueLabel.Text = $"{_toneLevelSlider.Value}%";

            // Restore FlexRadio discovery checkbox state visually (disabled until paired).
            // Don't trigger SetDiscoveryEmitEnabled here — rules are already in config
            // and will be pushed when the session is established.
            _suppressFlexCheckEvent = true;
            _flexEnableCheck.Checked = _controller.Config.DiscoveryEmitEnabled;
            _suppressFlexCheckEvent = false;

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

        // Stop PTT hook and footswitch
        DisablePttForSession();
        _pttHotKeyHook?.Dispose();
        _pttHotKeyHook = null;

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
        controller.ForwardRulesChanged += OnForwardRulesChanged;
        controller.HardwareWinKeyerConnected += OnHardwareWinKeyerConnected;
        controller.KeyerBusy += OnControllerKeyerBusy;
        controller.VersionMismatchDetected += OnControllerVersionMismatch;
    }

    private void OnControllerVersionMismatch(object? sender, RWK.Client.Controllers.VersionMismatchEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(() => OnControllerVersionMismatch(sender, e)); return; }

        // Show major.minor.patch to the operator (build number is intentionally ignored here).
        string clientV = $"{e.ClientVersion.Major}.{e.ClientVersion.Minor}.{e.ClientVersion.Build}";
        string stationV = e.StationVersion is null
            ? "older/unknown"
            : $"{e.StationVersion.Major}.{e.StationVersion.Minor}.{e.StationVersion.Build}";

        string detail = e.StationVersion is null
            ? "The Station did not report its version, which means it is running an older " +
              "release that predates this compatibility check."
            : "Different versions can behave inconsistently and may cause keying problems. " +
              "It is strongly recommended that both ends run the same version.";

        var result = MessageBox.Show(
            this,
            $"Version mismatch between Client and Station:\n\n" +
            $"    Client:   {clientV}\n" +
            $"    Station:  {stationV}\n\n" +
            detail + "\n\n" +
            "Pair anyway?",
            "RWK — Version Mismatch",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2); // default to "No" (do not pair)

        if (result != DialogResult.Yes)
        {
            // Decline: unpair immediately.
            _controller?.Disconnect();
            DisablePttForSession();
            _logService.Info($"Unpaired due to version mismatch (Client {clientV} / Station {stationV}).");
            ShowToast($"Unpaired — version mismatch (Station {stationV}, Client {clientV}).", ToolTipIcon.Warning);
            ValidatePairButton();
        }
        else
        {
            _logService.Info($"Operator chose to pair despite version mismatch (Client {clientV} / Station {stationV}).");
        }
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
            ShowToast("System Error occurred. Please restart RWK-Client.", ToolTipIcon.Error);
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
            _connectButton.BackColor = Color.FromArgb(200, 40, 40);
            _connectButton.ForeColor = Color.White;
            _connectButton.FlatStyle = FlatStyle.Flat;
            _connectButton.Enabled = true;
            _keySetIndicator.ForeColor = Color.FromArgb(200, 0, 0);
            _flexEnableCheck.Enabled = false;
            DisablePttForSession();

            // Toast: unpaired from station
            string stationName = (_stationCombo.SelectedItem as RWK.Shared.Config.StationEntry)?.Name ?? "Station";
            ShowToast($"Unpaired from Station {stationName}");
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

    private void OnForwardRulesChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnForwardRulesChanged(sender, e));
            return;
        }

        LoadForwardRulesIntoGrid();
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

        // Don't show the wizard if auth was already completed in this session.
        if (_loginDismissed) return;

        _pendingAuthUrl = authUrl;

        // Show the Auth Wizard instead of the old login panel.
        // The wizard is modal and owns its own polling — no dismiss races.
        _loginDismissed = true; // Prevent re-entry while wizard is open
        ShowAuthWizard();
    }

    private void ShowAuthWizard()
    {
        if (_controller is null) return;

        DismissWaitOverlay(); // Remove overlay before showing wizard

        var provider = new RWK.Shared.Auth.SidecarAuthProvider(_controller.SidecarHost);
        using var wizard = new Auth.TailscaleAuthWizard(provider);
        wizard.ShowDialog(this);

        if (wizard.AuthSucceeded)
        {
            _logService.Info("Tailscale authentication completed via wizard.");
        }
        else
        {
            // User cancelled — allow re-showing if auth URL appears again
            _loginDismissed = false;
            _logService.Info("Tailscale auth wizard cancelled by user.");
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

    private TailscaleState? _lastToastedState;

    private void UpdateStatusForState(TailscaleState state)
    {
        switch (state)
        {
            case TailscaleState.Connected:
                _linkIndicator.ForeColor = Color.LimeGreen;
                _linkIndicator.Text = "\u25CF";
                _pathLabel.Text = "Connected";
                DismissLoginPanel();
                DismissWaitOverlay();
                if (_lastToastedState != TailscaleState.Connected)
                    ShowToast("Connected to the Tailnet");
                break;
            case TailscaleState.Connecting:
                _linkIndicator.ForeColor = SystemColors.Highlight;
                _linkIndicator.Text = "\u25CF";
                _pathLabel.Text = "Connecting...";
                break;
            case TailscaleState.NeedsAuth:
                _linkIndicator.ForeColor = SystemColors.Highlight;
                _linkIndicator.Text = "\u25CF";
                _pathLabel.Text = "Waiting for login...";
                break;
            case TailscaleState.Fault:
                _linkIndicator.ForeColor = WarningRed;
                _linkIndicator.Text = "\u25CF";
                _pathLabel.Text = "Path lost";
                DismissWaitOverlay();
                if (_lastToastedState == TailscaleState.Connected)
                    ShowToast("Disconnected from the Tailnet", ToolTipIcon.Warning);
                break;
            default:
                _linkIndicator.ForeColor = Color.Gray;
                _linkIndicator.Text = "\u25CF";
                _pathLabel.Text = "Disconnected";
                if (_lastToastedState == TailscaleState.Connected)
                    ShowToast("Disconnected from the Tailnet", ToolTipIcon.Warning);
                break;
        }
        _lastToastedState = state;
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

        // Center the box on the full window (not just client area)
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
        // Get the selected station entry from the dropdown
        if (_stationCombo.SelectedItem is not RWK.Shared.Config.StationEntry entry)
        {
            _pathLabel.Text = "Select a Station first.";
            return;
        }

        string address = entry.TailscaleIp;

        if (_controller is null || !_controller.IsRunning)
        {
            _pathLabel.Text = "Controller not running.";
            return;
        }

        // Set the pairing key and address from the station entry
        _controller.SetPairingSecret(entry.PairingKey);
        _controller.SetStationAddress(address);

        try
        {
            _connectButton.Enabled = false;
            _connectButton.Text = "...";
            _pathLabel.Text = $"Connecting to {address}...";

            await _controller.ConnectToStationAsync(address).ConfigureAwait(true);

            _pathLabel.Text = $"Session active to {address}";
            _connectButton.Text = "Unpair";
            _connectButton.BackColor = SystemColors.Control;
            _connectButton.ForeColor = SystemColors.ControlText;
            _connectButton.FlatStyle = FlatStyle.Standard;

            // Toast: paired with station
            string stationName = (_stationCombo.SelectedItem as RWK.Shared.Config.StationEntry)?.Name ?? address;
            ShowToast($"Paired with Station {stationName}");

            // Key indicator turns green when paired and armed
            _keySetIndicator.ForeColor = _stationArmToggle.Checked ? Color.Green : Color.FromArgb(200, 0, 0);

            // Hide KEYER BUSY label if it was shown from a previous attempt
            if (_keyerBusyLabel is not null) _keyerBusyLabel.Visible = false;

            // (Keyer and Inputs panels stay enabled at all times for local testing.)

            // Enable FlexRadio discovery checkbox now that session is active.
            _flexEnableCheck.Enabled = true;

            // Enable PTT hotkey hook and footswitch for this session.
            EnablePttForSession();

            // If discovery was already enabled (from config), activate the emitter
            // and ensure forward rules are pushed (ConnectToStationAsync already
            // pushed all persisted rules, but the emitter needs to be initialized).
            if (_flexEnableCheck.Checked)
                _controller.SetDiscoveryEmitEnabled(true);
        }
        catch (Exception ex)
        {
            string msg = ex.InnerException?.Message ?? ex.Message;
            _pathLabel.Text = $"Connect failed: {msg}";
            try { RotatingFileLog.Append("client.log", $"CONNECT ERROR: {ex}"); } catch { }
            _connectButton.Text = "Pair";
            _connectButton.BackColor = Color.FromArgb(200, 40, 40);
            _connectButton.ForeColor = Color.White;
            _connectButton.FlatStyle = FlatStyle.Flat;
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

        // Update indicator color: green when armed+paired, red when not armed
        if (_connectButton.Text == "Unpair") // We're paired
        {
            _keySetIndicator.ForeColor = _stationArmToggle.Checked ? Color.Green : Color.FromArgb(200, 0, 0);
        }
    }

    private void OnImportStationClick(object? sender, EventArgs e)
    {
        using var dlg = new RWK.Client.Controls.ImportStationDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null)
            return;

        var entry = dlg.Result;

        // Add to persisted list
        var stations = RWK.Client.Controls.StationListStore.Load();
        // Replace if same name exists
        stations.RemoveAll(s => s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        stations.Add(entry);
        RWK.Client.Controls.StationListStore.Save(stations);

        // Add to dropdown (or update)
        RefreshStationDropdown(stations, entry.Name);

        _logService.Info($"Station imported: {entry.Name} → {entry.TailscaleIp}");
    }

    private void RefreshStationDropdown(List<RWK.Shared.Config.StationEntry>? stations = null, string? selectName = null)
    {
        stations ??= RWK.Client.Controls.StationListStore.Load();
        _stationCombo.Items.Clear();
        _stationCombo.Items.Add("(None)");
        foreach (var s in stations)
            _stationCombo.Items.Add(s);

        if (selectName is not null)
        {
            for (int i = 1; i < _stationCombo.Items.Count; i++)
            {
                if (_stationCombo.Items[i] is RWK.Shared.Config.StationEntry se &&
                    se.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase))
                {
                    _stationCombo.SelectedIndex = i;
                    return;
                }
            }
        }
        _stationCombo.SelectedIndex = 0;
    }

    private void ValidatePairButton()
    {
        bool stationSelected = _stationCombo.SelectedIndex > 0; // index 0 = "(None)"
        _connectButton.Enabled = stationSelected;
        _keySetIndicator.Visible = stationSelected;
    }

    private void OnStationComboChanged(object? sender, EventArgs e)
    {
        // If currently paired and the user changes the station selection, unpair first.
        if (_connectButton.Text == "Unpair" && _controller is not null)
        {
            _controller.Disconnect();
            _connectButton.Text = "Pair with Station";
            _connectButton.BackColor = Color.FromArgb(200, 40, 40);
            _connectButton.ForeColor = Color.White;
            _connectButton.FlatStyle = FlatStyle.Flat;
            _keySetIndicator.ForeColor = Color.FromArgb(200, 0, 0);
            _flexEnableCheck.Enabled = false;
            DisablePttForSession();
            _logService.Info("Unpaired (station selection changed).");
        }
        ValidatePairButton();
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
            "This will disconnect from the Tailscale network and delete the stored authorization.\n\n" +
            "You will be guided through re-authentication immediately.",
            "Delete Tailscale Authorization",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        try
        {
            // Show overlay while we reset
            ShowWaitOverlay();

            // Stop the sidecar so it releases file locks on the state directory.
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

            // Restart the sidecar (it will enter NeedsAuth state)
            if (_controller is not null)
            {
                await _controller.RestartSidecarAsync().ConfigureAwait(true);
            }

            // Reset the login-dismissed flag so the wizard can appear
            _loginDismissed = false;

            // Wait briefly for the sidecar to start and report NeedsAuth
            await Task.Delay(1000).ConfigureAwait(true);

            // Dismiss overlay and show the auth wizard directly
            DismissWaitOverlay();
            ShowAuthWizard();
        }
        catch (Exception ex)
        {
            DismissWaitOverlay();
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
        try { RotatingFileLog.Append("winkeyer.log", $"UI: WinKeyer port selected: '{port}'"); } catch { }
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

        // Update the italic help text under the radio buttons.
        bool isHardwareMode = _wkModeHardwareRadio.Checked;
        _wkModeHelpLabel.Text = isHardwareMode
            ? "Warning: one-character delay in sending. Local sidetone muted."
            : "N1MM, DXLog, Wintest, etc.";
        _sidetoneMuteLabel.Visible = false;

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

}
