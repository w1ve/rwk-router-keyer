/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Station;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    // ─── SAFE/ARMED banner (13.5, 13.6, 13.7) ───
    private Panel _safeBannerPanel = null!;
    private Label _safeBannerLabel = null!;

    // ─── Re-Arm button (13.8) ───
    private Button _reArmButton = null!;

    // ─── KEY / PTT live indicators ───
    private Label _keyIndicator = null!;
    private Label _keyIndicatorCaption = null!;
    private Label _pttIndicator = null!;
    private Label _pttIndicatorCaption = null!;

    // ─── Keying output configuration (13.11) ───
    private GroupBox _keyingOutputGroup = null!;
    private Label _comPortLabel = null!;
    private ComboBox _comPortCombo = null!;
    private Label _keyLineLabel = null!;
    private RadioButton _keyLineRts = null!;
    private RadioButton _keyLineDtr = null!;
    private CheckBox _keyInvertCheck = null!;
    private Label _pttLineLabel = null!;
    private RadioButton _pttLineRts = null!;
    private RadioButton _pttLineDtr = null!;
    private RadioButton _pttLineNone = null!;
    private CheckBox _pttInvertCheck = null!;
    private Label _comPortErrorLabel = null!;

    // ─── Forward rules panel (13.13) ───
    private GroupBox _forwardRulesGroup = null!;
    private DataGridView _forwardRulesGrid = null!;

    // ─── Session panel (11.7) ───
    private GroupBox _sessionGroup = null!;
    private Label _sessionClientLabel = null!;
    private Label _sessionClientValue = null!;
    private Label _sessionDurationLabel = null!;
    private Label _sessionDurationValue = null!;
    private Button _disconnectButton = null!;
    private Label _tailscaleIpCaptionLabel = null!;
    private Label _tailscaleIpValue = null!;
    private Button _copyIpButton = null!;

    // ─── FlexRadio discovery (auto-enabled by Client flex rules) ───
    private Label _flexForwardingLabel = null!;
    private Label _flexForwardingIndicator = null!;

    // ─── Logger Input ───
    private GroupBox _loggerInputGroup = null!;
    private CheckBox _loggerEnableCheck = null!;
    private Label _loggerPortLabel = null!;
    private ComboBox _loggerComPortCombo = null!;

    // ─── Menu ───
    private MenuStrip _mainMenu = null!;

    // ─── Status strip (13.10) ───
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _linkIndicatorStatus = null!;
    private ToolStripStatusLabel _pathStatus = null!;
    private ToolStripStatusLabel _rttStatus = null!;
    private ToolStripStatusLabel _sessionStateStatus = null!;
    private ToolStripStatusLabel _clientNameStatus = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        SuspendLayout();

        // ═══════════════════════════════════════════════════════════
        // SAFE / ARMED Banner (13.5, 13.6, 13.7)
        // ═══════════════════════════════════════════════════════════
        _safeBannerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(0, 128, 0),
            Name = "_safeBannerPanel"
        };

        _safeBannerLabel = new Label
        {
            Text = "ARMED",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Name = "_safeBannerLabel"
        };
        _safeBannerPanel.Controls.Add(_safeBannerLabel);

        // ═══════════════════════════════════════════════════════════
        // Re-Arm button (13.8)
        // ═══════════════════════════════════════════════════════════
        _reArmButton = new Button
        {
            Text = "Re-Arm",
            Size = new Size(100, 32),
            Location = new Point(12, 86),
            Enabled = false,
            UseVisualStyleBackColor = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Name = "_reArmButton"
        };

        // ═══════════════════════════════════════════════════════════
        // KEY / PTT live indicators (circle dots)
        // ═══════════════════════════════════════════════════════════
        _keyIndicator = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 22F),
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(125, 82),
            Name = "_keyIndicator"
        };

        _keyIndicatorCaption = new Label
        {
            Text = "KEY",
            AutoSize = true,
            Location = new Point(130, 118),
            Font = new Font("Segoe UI", 8F),
            Name = "_keyIndicatorCaption"
        };

        _pttIndicator = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 22F),
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(168, 82),
            Name = "_pttIndicator"
        };

        _pttIndicatorCaption = new Label
        {
            Text = "PTT",
            AutoSize = true,
            Location = new Point(173, 118),
            Font = new Font("Segoe UI", 8F),
            Name = "_pttIndicatorCaption"
        };

        // ═══════════════════════════════════════════════════════════
        // Keying Output Configuration (13.11) — system colors
        // ═══════════════════════════════════════════════════════════
        _keyingOutputGroup = new GroupBox
        {
            Text = "Keying Output",
            Location = new Point(12, 148),
            Size = new Size(300, 135),
            Name = "_keyingOutputGroup"
        };

        _comPortLabel = new Label { Text = "COM Port:", Location = new Point(10, 26), AutoSize = true };
        _comPortCombo = new ComboBox
        {
            Location = new Point(90, 23),
            Size = new Size(100, 23),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Name = "_comPortCombo"
        };

        _keyLineLabel = new Label { Text = "Key Line:", Location = new Point(10, 56), AutoSize = true };
        _keyLineRts = new RadioButton { Text = "RTS", Location = new Point(0, 0), AutoSize = true, Checked = true, Name = "_keyLineRts" };
        _keyLineDtr = new RadioButton { Text = "DTR", Location = new Point(55, 0), AutoSize = true, Name = "_keyLineDtr" };
        _keyInvertCheck = new CheckBox { Text = "Invert", Location = new Point(210, 54), AutoSize = true, Name = "_keyInvertCheck" };

        // Panel to group Key Line radio buttons separately from PTT Line
        var keyLinePanel = new Panel { Location = new Point(90, 52), Size = new Size(120, 22), Name = "keyLinePanel" };
        keyLinePanel.Controls.AddRange(new Control[] { _keyLineRts, _keyLineDtr });

        _pttLineLabel = new Label { Text = "PTT Line:", Location = new Point(10, 86), AutoSize = true };
        _pttLineRts = new RadioButton { Text = "RTS", Location = new Point(0, 0), AutoSize = true, Name = "_pttLineRts" };
        _pttLineDtr = new RadioButton { Text = "DTR", Location = new Point(55, 0), AutoSize = true, Checked = true, Name = "_pttLineDtr" };
        _pttLineNone = new RadioButton { Text = "None", Location = new Point(110, 0), AutoSize = true, Name = "_pttLineNone" };
        _pttInvertCheck = new CheckBox { Text = "Invert", Location = new Point(210, 114), AutoSize = true, Name = "_pttInvertCheck" };

        // Panel to group PTT Line radio buttons separately from Key Line
        var pttLinePanel = new Panel { Location = new Point(90, 82), Size = new Size(170, 22), Name = "pttLinePanel" };
        pttLinePanel.Controls.AddRange(new Control[] { _pttLineRts, _pttLineDtr, _pttLineNone });

        _comPortErrorLabel = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(200, 60, 60),
            Font = new Font("Segoe UI", 8F),
            AutoSize = false,
            Size = new Size(280, 18),
            Location = new Point(10, 140),
            Visible = false,
            Name = "_comPortErrorLabel"
        };

        _keyingOutputGroup.Controls.AddRange(new Control[]
        {
            _comPortLabel, _comPortCombo,
            _keyLineLabel, keyLinePanel, _keyInvertCheck,
            _pttLineLabel, pttLinePanel, _pttInvertCheck,
            _comPortErrorLabel
        });

        // ═══════════════════════════════════════════════════════════
        // Session Panel (11.7)
        // ═══════════════════════════════════════════════════════════
        _sessionGroup = new GroupBox
        {
            Text = "Session",
            Location = new Point(324, 148),
            Size = new Size(300, 135),
            Name = "_sessionGroup"
        };

        _tailscaleIpCaptionLabel = new Label { Text = "Station IP:", Location = new Point(10, 24), AutoSize = true, Font = new Font("Segoe UI", 8F) };
        _tailscaleIpValue = new Label
        {
            Text = "(not connected)",
            Location = new Point(76, 22),
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Name = "_tailscaleIpValue"
        };
        _copyIpButton = new Button
        {
            Text = "📋",
            Size = new Size(26, 22),
            Location = new Point(240, 22),
            UseVisualStyleBackColor = true,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F),
            Name = "_copyIpButton"
        };
        _copyIpButton.FlatAppearance.BorderSize = 1;

        _sessionClientLabel = new Label { Text = "Client:", Location = new Point(10, 50), AutoSize = true };
        _sessionClientValue = new Label { Text = "(none)", Location = new Point(70, 50), AutoSize = true, Name = "_sessionClientValue" };
        _sessionDurationLabel = new Label { Text = "Duration:", Location = new Point(10, 72), AutoSize = true };
        _sessionDurationValue = new Label { Text = "—", Location = new Point(70, 72), AutoSize = true, Name = "_sessionDurationValue" };

        _disconnectButton = new Button
        {
            Text = "Unpair",
            Location = new Point(10, 96),
            Size = new Size(100, 26),
            Enabled = false,
            UseVisualStyleBackColor = true,
            Name = "_disconnectButton"
        };

        _flexForwardingLabel = new Label
        {
            Text = "Flex Forwarding",
            Location = new Point(120, 101),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F),
            Name = "_flexForwardingLabel"
        };

        _flexForwardingIndicator = new Label
        {
            Text = "✓",
            Location = new Point(225, 98),
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.Red,
            Visible = false,
            Name = "_flexForwardingIndicator"
        };

        _sessionGroup.Controls.AddRange(new Control[]
        {
            _tailscaleIpCaptionLabel, _tailscaleIpValue, _copyIpButton,
            _sessionClientLabel, _sessionClientValue,
            _sessionDurationLabel, _sessionDurationValue,
            _disconnectButton, _flexForwardingLabel, _flexForwardingIndicator
        });

        // ═══════════════════════════════════════════════════════════
        // Forward Rules Panel (13.13)
        // ═══════════════════════════════════════════════════════════
        _forwardRulesGroup = new GroupBox
        {
            Text = "Forward Rules (pushed from Client)",
            Location = new Point(12, 290),
            Size = new Size(612, 140),
            Name = "_forwardRulesGroup"
        };

        _forwardRulesGrid = new DataGridView
        {
            Location = new Point(6, 22),
            Size = new Size(600, 110),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            ReadOnly = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Name = "_forwardRulesGrid"
        };

        // Columns: Rule Name, Protocol, Client Port, Station Port, Allow/Deny, Target Override
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Rule", Name = "ColRuleName", FillWeight = 22, ReadOnly = true });
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Proto", Name = "ColProtocol", FillWeight = 12, ReadOnly = true });
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Client Port", Name = "ColClientPort", FillWeight = 16, ReadOnly = true });
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Station Port", Name = "ColStationPort", FillWeight = 16, ReadOnly = true });
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Enabled", Name = "ColEnabled", FillWeight = 12, ReadOnly = true });
        _forwardRulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Target Host", Name = "ColTargetOverride", FillWeight = 22, ReadOnly = true });

        _forwardRulesGroup.Controls.Add(_forwardRulesGrid);

        // ═══════════════════════════════════════════════════════════
        // Logger Input (WK2 from logging software on Station PC)
        // ═══════════════════════════════════════════════════════════
        _loggerInputGroup = new GroupBox
        {
            Text = "Logger Input",
            Location = new Point(220, 85),
            Size = new Size(200, 60),
            Name = "_loggerInputGroup"
        };

        _loggerEnableCheck = new CheckBox
        {
            Text = "Enable",
            Location = new Point(10, 18),
            AutoSize = true,
            Name = "_loggerEnableCheck"
        };

        _loggerPortLabel = new Label
        {
            Text = "Port:",
            Location = new Point(10, 40),
            AutoSize = true,
            Name = "_loggerPortLabel"
        };

        _loggerComPortCombo = new ComboBox
        {
            Location = new Point(50, 37),
            Size = new Size(90, 23),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = false,
            Name = "_loggerComPortCombo"
        };

        _loggerInputGroup.Controls.AddRange(new Control[]
        {
            _loggerEnableCheck, _loggerPortLabel, _loggerComPortCombo
        });

        // ═══════════════════════════════════════════════════════════
        // Status Strip (13.10)
        // ═══════════════════════════════════════════════════════════
        _statusStrip = new StatusStrip
        {
            SizingGrip = true,
            Name = "_statusStrip"
        };

        _linkIndicatorStatus = new ToolStripStatusLabel { Text = "● Link: —", Name = "_linkIndicatorStatus", ForeColor = Color.Gray };
        _pathStatus = new ToolStripStatusLabel { Text = "Path: —", Name = "_pathStatus", BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
        _rttStatus = new ToolStripStatusLabel { Text = "RTT: —", Name = "_rttStatus", BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
        _sessionStateStatus = new ToolStripStatusLabel { Text = "Session: Idle", Name = "_sessionStateStatus", BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
        _clientNameStatus = new ToolStripStatusLabel { Text = "", Name = "_clientNameStatus", Spring = true, TextAlign = ContentAlignment.MiddleRight };

        _statusStrip.Items.AddRange(new ToolStripItem[]
        {
            _linkIndicatorStatus,
            _pathStatus,
            _rttStatus,
            _sessionStateStatus,
            _clientNameStatus
        });

        // ═══════════════════════════════════════════════════════════
        // Main Form assembly — system colors, no dark theme
        // ═══════════════════════════════════════════════════════════
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 470);
        MinimumSize = new Size(656, 510);

        Controls.Add(_safeBannerPanel);
        Controls.Add(_reArmButton);
        Controls.Add(_keyIndicator);
        Controls.Add(_keyIndicatorCaption);
        Controls.Add(_pttIndicator);
        Controls.Add(_pttIndicatorCaption);
        Controls.Add(_loggerInputGroup);
        Controls.Add(_keyingOutputGroup);
        Controls.Add(_sessionGroup);
        Controls.Add(_forwardRulesGroup);
        Controls.Add(_statusStrip);

        // Main menu
        _mainMenu = new MenuStrip();
        _mainMenu.Name = "_mainMenu";
        var rwkMenuItem = new ToolStripMenuItem("&File");
        var aboutMenuItem = new ToolStripMenuItem("About RWK");
        aboutMenuItem.Click += (_, _) => { using var dlg = new AboutDialog(); dlg.ShowDialog(this); };
        var showPairingKeyMenuItem = new ToolStripMenuItem("Show Pairing Key...");
        showPairingKeyMenuItem.Click += OnShowPairingKeyClick;
        var deleteTsAuthMenuItem = new ToolStripMenuItem("Delete Tailscale Authorization...");
        deleteTsAuthMenuItem.Click += OnDeleteTailscaleAuthClick;
        var tsAdminMenuItem = new ToolStripMenuItem("Go to Tailscale Admin Page...");
        tsAdminMenuItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://login.tailscale.com/admin/machines",
                UseShellExecute = true
            });
        };
        var exitMenuItem = new ToolStripMenuItem("E&xit");
        exitMenuItem.Click += (_, _) => Close();
        rwkMenuItem.DropDownItems.Add(aboutMenuItem);
        rwkMenuItem.DropDownItems.Add(new ToolStripSeparator());
        rwkMenuItem.DropDownItems.Add(showPairingKeyMenuItem);
        rwkMenuItem.DropDownItems.Add(deleteTsAuthMenuItem);
        rwkMenuItem.DropDownItems.Add(tsAdminMenuItem);
        rwkMenuItem.DropDownItems.Add(new ToolStripSeparator());
        var deleteLogsMenuItem = new ToolStripMenuItem("Delete Debugging &Logs");
        deleteLogsMenuItem.Click += (_, _) =>
        {
            int count = RWK.Shared.IO.RotatingFileLog.DeleteAll(
                "station.log", "station-logger.log", "replayer.log", "sidecar.log", "crash.log");
            MessageBox.Show($"Deleted {count} log file(s).", "Logs Deleted",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        rwkMenuItem.DropDownItems.Add(deleteLogsMenuItem);
        rwkMenuItem.DropDownItems.Add(new ToolStripSeparator());
        rwkMenuItem.DropDownItems.Add(exitMenuItem);
        _mainMenu.Items.Add(rwkMenuItem);
        Controls.Add(_mainMenu);
        MainMenuStrip = _mainMenu;

        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "RWK Station";

        // Form icon
        string icoPath = Path.Combine(AppContext.BaseDirectory, "rwk.ico");
        if (File.Exists(icoPath))
            Icon = new Icon(icoPath);

        ResumeLayout(false);
        PerformLayout();
    }
}
