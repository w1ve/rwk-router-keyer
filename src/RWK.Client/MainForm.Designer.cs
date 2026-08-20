/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Client;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    // === Status Strip (13.10) ===
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _linkIndicator = null!;
    private ToolStripStatusLabel _pathLabel = null!;
    private ToolStripStatusLabel _rttLabel = null!;
    private ToolStripStatusLabel _bufferLabel = null!;
    private ToolStripStatusLabel _keyStateLabel = null!;

    // === Paddle State Indicators (13.2) ===
    private GroupBox _paddleGroup = null!;
    private Label _ditIndicator = null!;
    private Label _dahIndicator = null!;
    private Label _skIndicator = null!;
    private Label _ditLabel = null!;
    private Label _dahLabel = null!;
    private Label _skLabel = null!;

    // === Keyer Controls (13.3, 13.4) ===
    private GroupBox _keyerGroup = null!;
    private Label _speedLabel = null!;
    private Label _speedCaptionLabel = null!;
    private TrackBar _speedSlider = null!;
    private Label _weightCaptionLabel = null!;
    private TrackBar _weightSlider = null!;
    private Label _weightValueLabel = null!;
    private Label _modeCaptionLabel = null!;
    private ComboBox _modeCombo = null!;
    private CheckBox _paddleReverseCheck = null!;
    private Button _testTxButton = null!;

    // === Sidetone Panel ===
    private GroupBox _sidetoneGroup = null!;
    private Label _toneDeviceCaptionLabel = null!;
    private ComboBox _audioDeviceCombo = null!;
    private Label _toneFreqCaptionLabel = null!;
    private TrackBar _toneFreqSlider = null!;
    private Label _toneFreqValueLabel = null!;
    private Label _toneLevelCaptionLabel = null!;
    private TrackBar _toneLevelSlider = null!;
    private Label _toneLevelValueLabel = null!;
    private Label _sidetoneMuteLabel = null!;

    // === Device Selection (13.11) ===
    private GroupBox _portsGroup = null!;
    private Label _paddlePortCaptionLabel = null!;
    private ComboBox _paddlePortCombo = null!;
    private Label _wkPortCaptionLabel = null!;
    private ComboBox _winKeyerPortCombo = null!;
    private RadioButton _wkModeLoggerRadio = null!;
    private RadioButton _wkModeHardwareRadio = null!;
    private Label _wkHardwareStatus = null!;
    private Button _wkLoopbackTestBtn = null!;
    private Label _wkDitDot = null!;
    private Label _wkDahDot = null!;
    private Label _wkSkDot = null!;

    // === Port Forwarding (13.12, 13.14, 13.15) ===
    private GroupBox _forwardGroup = null!;
    private DataGridView _forwardGrid = null!;
    private DataGridViewTextBoxColumn _enabledColumn = null!;
    private DataGridViewTextBoxColumn _ruleNameColumn = null!;
    private DataGridViewComboBoxColumn _protocolColumn = null!;
    private DataGridViewTextBoxColumn _clientPortColumn = null!;
    private DataGridViewTextBoxColumn _stationPortColumn = null!;
    private DataGridViewComboBoxColumn _bindAddressColumn = null!;
    private DataGridViewTextBoxColumn _stationTargetColumn = null!;
    private DataGridViewTextBoxColumn _statusColumn = null!;
    private Button _addRuleBtn = null!;
    private Button _removeRuleBtn = null!;
    private Button _enableSelectedBtn = null!;
    private Button _disableSelectedBtn = null!;
    private Button _enableAllBtn = null!;
    private Button _disableAllBtn = null!;
    private Button _wizardBtn = null!;
    private Button _importBtn = null!;
    private Label _bindWarningLabel = null!;
    private Label _remoteRigWarningLabel = null!;

    // === FlexRadio Discovery (13.17-13.20, 15.19) ===
    private GroupBox _flexGroup = null!;
    private CheckBox _flexEnableCheck = null!;
    private ListBox _flexRadioList = null!;
    private Label _flexPlaceholderLabel = null!;

    // === Layout containers ===
    private MenuStrip _mainMenu = null!;
    private TabControl _tabControl = null!;
    private TabPage _mainTab = null!;
    private TabPage _logTab = null!;
    private TableLayoutPanel _mainLayout = null!;
    private GroupBox _remoteWinKeyerGroup = null!;
    private GroupBox _networkControlGroup = null!;

    // === Log tab controls ===
    private ComboBox _logLevelCombo = null!;
    private TextBox _logTextBox = null!;
    private Label _stationAddressCaptionLabel = null!;
    private TextBox _stationAddressTextBox = null!;
    private Button _connectButton = null!;
    private CheckBox _stationArmToggle = null!;
    private Button _setStationKeyBtn = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        // Safety indicator color (not theme-dependent)
        var warningRed = System.Drawing.Color.FromArgb(200, 60, 60);

        SuspendLayout();

        // ============================================================
        // STATUS STRIP (13.1, 13.10)
        // ============================================================
        _statusStrip = new StatusStrip();
        _linkIndicator = new ToolStripStatusLabel();
        _pathLabel = new ToolStripStatusLabel();
        _rttLabel = new ToolStripStatusLabel();
        _bufferLabel = new ToolStripStatusLabel();
        _keyStateLabel = new ToolStripStatusLabel();

        _statusStrip.SizingGrip = false;
        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.Name = "_statusStrip";

        _linkIndicator.Name = "_linkIndicator";
        _linkIndicator.Font = new Font("Segoe UI", 12F);
        _linkIndicator.ForeColor = System.Drawing.Color.Gray;

        _pathLabel.Name = "_pathLabel";
        _pathLabel.Spring = true;
        _pathLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

        _rttLabel.Name = "_rttLabel";
        _rttLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _rttLabel.BorderStyle = Border3DStyle.Etched;

        _bufferLabel.Name = "_bufferLabel";
        _bufferLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _bufferLabel.BorderStyle = Border3DStyle.Etched;

        _keyStateLabel.Name = "_keyStateLabel";
        _keyStateLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _keyStateLabel.BorderStyle = Border3DStyle.Etched;

        _statusStrip.Items.AddRange(new ToolStripItem[] {
            _linkIndicator, _pathLabel, _rttLabel, _bufferLabel, _keyStateLabel
        });

        // ============================================================
        // PADDLE STATE INDICATORS (13.2)
        // ============================================================
        _paddleGroup = new GroupBox();
        _ditIndicator = new Label();
        _dahIndicator = new Label();
        _skIndicator = new Label();
        _ditLabel = new Label();
        _dahLabel = new Label();
        _skLabel = new Label();

        _paddleGroup.Text = "Paddle";
        _paddleGroup.Dock = DockStyle.Fill;
        _paddleGroup.Name = "_paddleGroup";
        _paddleGroup.Padding = new Padding(8);

        ConfigureIndicator(_ditIndicator, "●", SystemColors.GrayText, 12, 22, "_ditIndicator");
        ConfigureIndicator(_dahIndicator, "●", SystemColors.GrayText, 50, 22, "_dahIndicator");
        ConfigureIndicator(_skIndicator, "●", SystemColors.GrayText, 88, 22, "_skIndicator");

        _ditLabel.Text = "Dit"; _ditLabel.AutoSize = true;
        _ditLabel.Location = new Point(10, 50); _ditLabel.Name = "_ditLabel";
        _dahLabel.Text = "Dah"; _dahLabel.AutoSize = true;
        _dahLabel.Location = new Point(46, 50); _dahLabel.Name = "_dahLabel";
        _skLabel.Text = "SK"; _skLabel.AutoSize = true;
        _skLabel.Location = new Point(88, 50); _skLabel.Name = "_skLabel";

        _paddleGroup.Controls.AddRange(new Control[] {
            _ditIndicator, _dahIndicator, _skIndicator,
            _ditLabel, _dahLabel, _skLabel
        });

        // ============================================================
        // KEYER CONTROLS (13.3, 13.4)
        // ============================================================
        _keyerGroup = new GroupBox();
        _speedLabel = new Label();
        _speedCaptionLabel = new Label();
        _speedSlider = new TrackBar();
        _weightCaptionLabel = new Label();
        _weightSlider = new TrackBar();
        _weightValueLabel = new Label();
        _modeCaptionLabel = new Label();
        _modeCombo = new ComboBox();
        _paddleReverseCheck = new CheckBox();

        _keyerGroup.Text = "Keyer";
        _keyerGroup.Dock = DockStyle.Fill;
        _keyerGroup.Name = "_keyerGroup";
        _keyerGroup.Padding = new Padding(8);

        // Large WPM readout
        _speedLabel.Text = "20";
        _speedLabel.Font = new Font("Segoe UI", 28F, FontStyle.Bold);
        _speedLabel.ForeColor = SystemColors.Highlight;
        _speedLabel.AutoSize = true;
        _speedLabel.Location = new Point(12, 20);
        _speedLabel.Name = "_speedLabel";

        _speedCaptionLabel.Text = "WPM";
        _speedCaptionLabel.AutoSize = true;
        _speedCaptionLabel.Location = new Point(14, 68);
        _speedCaptionLabel.Name = "_speedCaptionLabel";

        _speedSlider.Minimum = 5;
        _speedSlider.Maximum = 60;
        _speedSlider.Value = 20;
        _speedSlider.TickFrequency = 5;
        _speedSlider.Location = new Point(70, 24);
        _speedSlider.Size = new Size(200, 45);
        _speedSlider.Name = "_speedSlider";
        _speedSlider.Scroll += OnSpeedSliderScroll;

        _weightCaptionLabel.Text = "Weight:";
        _weightCaptionLabel.AutoSize = true;
        _weightCaptionLabel.Location = new Point(12, 82);
        _weightCaptionLabel.Name = "_weightCaptionLabel";

        _weightSlider.Minimum = 25;
        _weightSlider.Maximum = 75;
        _weightSlider.Value = 50;
        _weightSlider.TickFrequency = 5;
        _weightSlider.Location = new Point(70, 76);
        _weightSlider.Size = new Size(150, 30);
        _weightSlider.AutoSize = false;
        _weightSlider.Name = "_weightSlider";
        _weightSlider.Scroll += OnWeightSliderScroll;

        _weightValueLabel.Text = "50%";
        _weightValueLabel.AutoSize = true;
        _weightValueLabel.Location = new Point(224, 82);
        _weightValueLabel.Name = "_weightValueLabel";

        _modeCaptionLabel.Text = "Mode:";
        _modeCaptionLabel.AutoSize = true;
        _modeCaptionLabel.Location = new Point(12, 112);
        _modeCaptionLabel.Name = "_modeCaptionLabel";

        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.Location = new Point(60, 109);
        _modeCombo.Size = new Size(130, 23);
        _modeCombo.Name = "_modeCombo";

        _paddleReverseCheck.Text = "Paddle Reverse";
        _paddleReverseCheck.AutoSize = true;
        _paddleReverseCheck.Location = new Point(12, 140);
        _paddleReverseCheck.Name = "_paddleReverseCheck";

        _testTxButton = new Button();
        _testTxButton.Text = "TestTX";
        _testTxButton.Size = new Size(70, 26);
        _testTxButton.Location = new Point(12, 168);
        _testTxButton.UseVisualStyleBackColor = true;
        _testTxButton.Name = "_testTxButton";

        _keyerGroup.Controls.AddRange(new Control[] {
            _speedLabel, _speedCaptionLabel, _speedSlider,
            _weightCaptionLabel, _weightSlider, _weightValueLabel,
            _modeCaptionLabel, _modeCombo, _paddleReverseCheck,
            _testTxButton
        });

        // ============================================================
        // SIDETONE PANEL — uses inner TableLayoutPanel for no-clip layout
        // ============================================================
        _sidetoneGroup = new GroupBox();
        _toneDeviceCaptionLabel = new Label();
        _audioDeviceCombo = new ComboBox();
        _toneFreqCaptionLabel = new Label();
        _toneFreqSlider = new TrackBar();
        _toneFreqValueLabel = new Label();
        _toneLevelCaptionLabel = new Label();
        _toneLevelSlider = new TrackBar();
        _toneLevelValueLabel = new Label();

        _sidetoneGroup.Text = "Sidetone";
        _sidetoneGroup.Dock = DockStyle.Fill;
        _sidetoneGroup.Name = "_sidetoneGroup";
        _sidetoneGroup.Padding = new Padding(4);

        var sidetoneInnerLayout = new TableLayoutPanel();
        sidetoneInnerLayout.Dock = DockStyle.Fill;
        sidetoneInnerLayout.ColumnCount = 2;
        sidetoneInnerLayout.RowCount = 3;
        sidetoneInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
        sidetoneInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sidetoneInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        sidetoneInnerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        sidetoneInnerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        sidetoneInnerLayout.Name = "sidetoneInnerLayout";

        _toneDeviceCaptionLabel.Text = "Device:";
        _toneDeviceCaptionLabel.AutoSize = true;
        _toneDeviceCaptionLabel.Dock = DockStyle.Fill;
        _toneDeviceCaptionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _toneDeviceCaptionLabel.Name = "_toneDeviceCaptionLabel";

        _audioDeviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _audioDeviceCombo.Dock = DockStyle.Fill;
        _audioDeviceCombo.Name = "_audioDeviceCombo";

        // Freq row: label + panel containing slider + value label
        _toneFreqCaptionLabel.Text = "Frequency:";
        _toneFreqCaptionLabel.AutoSize = true;
        _toneFreqCaptionLabel.Dock = DockStyle.Fill;
        _toneFreqCaptionLabel.TextAlign = ContentAlignment.TopLeft;
        _toneFreqCaptionLabel.Name = "_toneFreqCaptionLabel";

        var freqPanel = new Panel();
        freqPanel.Dock = DockStyle.Fill;
        freqPanel.Name = "freqPanel";

        _toneFreqSlider.Minimum = 400;
        _toneFreqSlider.Maximum = 1000;
        _toneFreqSlider.Value = 600;
        _toneFreqSlider.TickFrequency = 100;
        _toneFreqSlider.Dock = DockStyle.Top;
        _toneFreqSlider.Height = 30;
        _toneFreqSlider.AutoSize = false;
        _toneFreqSlider.Name = "_toneFreqSlider";
        _toneFreqSlider.Scroll += OnToneFreqSliderScroll;

        _toneFreqValueLabel.Text = "600 Hz";
        _toneFreqValueLabel.AutoSize = false;
        _toneFreqValueLabel.Dock = DockStyle.Bottom;
        _toneFreqValueLabel.Height = 18;
        _toneFreqValueLabel.TextAlign = ContentAlignment.TopCenter;
        _toneFreqValueLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _toneFreqValueLabel.Name = "_toneFreqValueLabel";

        freqPanel.Controls.Add(_toneFreqValueLabel);
        freqPanel.Controls.Add(_toneFreqSlider);

        // Volume row: label + panel containing slider + value label
        _toneLevelCaptionLabel.Text = "Volume:";
        _toneLevelCaptionLabel.AutoSize = true;
        _toneLevelCaptionLabel.Dock = DockStyle.Fill;
        _toneLevelCaptionLabel.TextAlign = ContentAlignment.TopLeft;
        _toneLevelCaptionLabel.Name = "_toneLevelCaptionLabel";

        var levelPanel = new Panel();
        levelPanel.Dock = DockStyle.Fill;
        levelPanel.Name = "levelPanel";

        _toneLevelSlider.Minimum = 0;
        _toneLevelSlider.Maximum = 100;
        _toneLevelSlider.Value = 70;
        _toneLevelSlider.TickFrequency = 10;
        _toneLevelSlider.Dock = DockStyle.Top;
        _toneLevelSlider.Height = 30;
        _toneLevelSlider.AutoSize = false;
        _toneLevelSlider.Name = "_toneLevelSlider";
        _toneLevelSlider.Scroll += OnToneLevelSliderScroll;

        _toneLevelValueLabel.Text = "70%";
        _toneLevelValueLabel.AutoSize = false;
        _toneLevelValueLabel.Dock = DockStyle.Bottom;
        _toneLevelValueLabel.Height = 18;
        _toneLevelValueLabel.TextAlign = ContentAlignment.TopCenter;
        _toneLevelValueLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _toneLevelValueLabel.Name = "_toneLevelValueLabel";

        levelPanel.Controls.Add(_toneLevelValueLabel);
        levelPanel.Controls.Add(_toneLevelSlider);

        sidetoneInnerLayout.Controls.Add(_toneDeviceCaptionLabel, 0, 0);
        sidetoneInnerLayout.Controls.Add(_audioDeviceCombo, 1, 0);
        sidetoneInnerLayout.Controls.Add(_toneFreqCaptionLabel, 0, 1);
        sidetoneInnerLayout.Controls.Add(freqPanel, 1, 1);
        sidetoneInnerLayout.Controls.Add(_toneLevelCaptionLabel, 0, 2);
        sidetoneInnerLayout.Controls.Add(levelPanel, 1, 2);

        _sidetoneGroup.Controls.Add(sidetoneInnerLayout);

        // Mute indicator (shown when Hardware WinKey mode is active)
        _sidetoneMuteLabel = new Label();
        _sidetoneMuteLabel.Text = "\U0001F507 Muted (HW WK sidetone)";
        _sidetoneMuteLabel.Font = new Font("Segoe UI", 8F, FontStyle.Italic);
        _sidetoneMuteLabel.ForeColor = Color.FromArgb(160, 60, 60);
        _sidetoneMuteLabel.AutoSize = true;
        _sidetoneMuteLabel.Location = new Point(8, 0);
        _sidetoneMuteLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _sidetoneMuteLabel.Visible = false;
        _sidetoneMuteLabel.Name = "_sidetoneMuteLabel";
        _sidetoneGroup.Controls.Add(_sidetoneMuteLabel);
        _sidetoneMuteLabel.BringToFront();

        // ============================================================
        // DEVICE SELECTION / INPUT PORTS (13.11) — uses inner TableLayoutPanel
        // ============================================================
        _portsGroup = new GroupBox();
        _paddlePortCaptionLabel = new Label();
        _paddlePortCombo = new ComboBox();
        _wkPortCaptionLabel = new Label();
        _winKeyerPortCombo = new ComboBox();
        _wkModeLoggerRadio = new RadioButton();
        _wkModeHardwareRadio = new RadioButton();
        _wkLoopbackTestBtn = new Button();
        _wkDitDot = new Label();
        _wkDahDot = new Label();
        _wkSkDot = new Label();

        _portsGroup.Text = "Input Ports";
        _portsGroup.Dock = DockStyle.Fill;
        _portsGroup.Name = "_portsGroup";
        _portsGroup.Padding = new Padding(4);

        var portsInnerLayout = new TableLayoutPanel();
        portsInnerLayout.Dock = DockStyle.Fill;
        portsInnerLayout.ColumnCount = 2;
        portsInnerLayout.RowCount = 5;
        portsInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
        portsInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        portsInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // Paddle port
        portsInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // WinKeyer port
        portsInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));  // WK mode radios
        portsInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));  // Loopback test button
        portsInnerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // Indicators
        portsInnerLayout.Name = "portsInnerLayout";

        _paddlePortCaptionLabel.Text = "Paddle:";
        _paddlePortCaptionLabel.AutoSize = true;
        _paddlePortCaptionLabel.Dock = DockStyle.Fill;
        _paddlePortCaptionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _paddlePortCaptionLabel.Name = "_paddlePortCaptionLabel";

        _paddlePortCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _paddlePortCombo.Dock = DockStyle.Fill;
        _paddlePortCombo.Name = "_paddlePortCombo";

        _wkPortCaptionLabel.Text = "WinKeyer:";
        _wkPortCaptionLabel.AutoSize = true;
        _wkPortCaptionLabel.Dock = DockStyle.Fill;
        _wkPortCaptionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _wkPortCaptionLabel.Name = "_wkPortCaptionLabel";

        _winKeyerPortCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _winKeyerPortCombo.Dock = DockStyle.Fill;
        _winKeyerPortCombo.Name = "_winKeyerPortCombo";

        // WinKeyer mode selection: Logger App (emulator) vs Hardware WinKey
        var wkModePanel = new Panel();
        wkModePanel.Dock = DockStyle.Fill;
        wkModePanel.Name = "wkModePanel";

        _wkModeLoggerRadio.Text = "Logger App";
        _wkModeLoggerRadio.AutoSize = true;
        _wkModeLoggerRadio.Location = new Point(0, 2);
        _wkModeLoggerRadio.Checked = true;
        _wkModeLoggerRadio.Name = "_wkModeLoggerRadio";

        _wkModeHardwareRadio.Text = "Hardware WinKey";
        _wkModeHardwareRadio.AutoSize = true;
        _wkModeHardwareRadio.Location = new Point(95, 2);
        _wkModeHardwareRadio.Name = "_wkModeHardwareRadio";

        _wkHardwareStatus = new Label();
        _wkHardwareStatus.Text = "";
        _wkHardwareStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _wkHardwareStatus.ForeColor = Color.FromArgb(200, 0, 0);
        _wkHardwareStatus.AutoSize = true;
        _wkHardwareStatus.Location = new Point(230, 3);
        _wkHardwareStatus.Name = "_wkHardwareStatus";
        _wkHardwareStatus.Visible = false;

        wkModePanel.Controls.AddRange(new Control[] { _wkModeLoggerRadio, _wkModeHardwareRadio, _wkHardwareStatus });

        // Loopback test button
        _wkLoopbackTestBtn.Text = "WinKeyer Loopback Test";
        _wkLoopbackTestBtn.UseVisualStyleBackColor = true;
        _wkLoopbackTestBtn.AutoSize = true;
        _wkLoopbackTestBtn.Dock = DockStyle.Left;
        _wkLoopbackTestBtn.Name = "_wkLoopbackTestBtn";

        // WinKeyer dit/dah/SK live indicator dots in a small panel
        var wkIndicatorPanel = new Panel();
        wkIndicatorPanel.Dock = DockStyle.Fill;
        wkIndicatorPanel.Name = "wkIndicatorPanel";

        ConfigureIndicator(_wkDitDot, "●", SystemColors.GrayText, 0, 4, "_wkDitDot");
        ConfigureIndicator(_wkDahDot, "●", SystemColors.GrayText, 22, 4, "_wkDahDot");
        ConfigureIndicator(_wkSkDot, "●", SystemColors.GrayText, 44, 4, "_wkSkDot");

        wkIndicatorPanel.Controls.AddRange(new Control[] { _wkDitDot, _wkDahDot, _wkSkDot });

        portsInnerLayout.Controls.Add(_paddlePortCaptionLabel, 0, 0);
        portsInnerLayout.Controls.Add(_paddlePortCombo, 1, 0);
        portsInnerLayout.Controls.Add(_wkPortCaptionLabel, 0, 1);
        portsInnerLayout.Controls.Add(_winKeyerPortCombo, 1, 1);
        portsInnerLayout.SetColumnSpan(wkModePanel, 2);
        portsInnerLayout.Controls.Add(wkModePanel, 0, 2);
        portsInnerLayout.SetColumnSpan(_wkLoopbackTestBtn, 2);
        portsInnerLayout.Controls.Add(_wkLoopbackTestBtn, 0, 3);
        portsInnerLayout.SetColumnSpan(wkIndicatorPanel, 2);
        portsInnerLayout.Controls.Add(wkIndicatorPanel, 0, 4);

        _portsGroup.Controls.Add(portsInnerLayout);

        // ============================================================
        // PORT FORWARDING (13.12, 13.14, 13.15, 10.14, 10.18)
        // Uses inner TableLayoutPanel: grid in row 0 (Fill), buttons+warnings in row 1 (fixed)
        // ============================================================
        _forwardGroup = new GroupBox();
        _forwardGrid = new DataGridView();
        _enabledColumn = new DataGridViewTextBoxColumn();
        _ruleNameColumn = new DataGridViewTextBoxColumn();
        _protocolColumn = new DataGridViewComboBoxColumn();
        _clientPortColumn = new DataGridViewTextBoxColumn();
        _stationPortColumn = new DataGridViewTextBoxColumn();
        _bindAddressColumn = new DataGridViewComboBoxColumn();
        _statusColumn = new DataGridViewTextBoxColumn();
        _addRuleBtn = new Button();
        _removeRuleBtn = new Button();
        _bindWarningLabel = new Label();
        _remoteRigWarningLabel = new Label();

        _forwardGroup.Text = "Port Forwards";
        _forwardGroup.Dock = DockStyle.Fill;
        _forwardGroup.Name = "_forwardGroup";
        _forwardGroup.Padding = new Padding(4);

        var forwardInnerLayout = new TableLayoutPanel();
        forwardInnerLayout.Dock = DockStyle.Fill;
        forwardInnerLayout.ColumnCount = 1;
        forwardInnerLayout.RowCount = 2;
        forwardInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        forwardInnerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // grid fills
        forwardInnerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F)); // buttons + warnings
        forwardInnerLayout.Name = "forwardInnerLayout";

        // DataGridView — fills row 0
        _forwardGrid.AllowUserToAddRows = false;
        _forwardGrid.AllowUserToDeleteRows = false;
        _forwardGrid.RowHeadersVisible = false;
        _forwardGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _forwardGrid.MultiSelect = false;
        _forwardGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _forwardGrid.BorderStyle = BorderStyle.FixedSingle;
        _forwardGrid.Dock = DockStyle.Fill;
        _forwardGrid.Name = "_forwardGrid";
        _forwardGrid.CellValueChanged += OnForwardGridCellValueChanged;
        _forwardGrid.SelectionChanged += OnForwardGridSelectionChanged;

        _enabledColumn.HeaderText = "State";
        _enabledColumn.Name = "Enabled";
        _enabledColumn.Width = 50;
        _enabledColumn.FillWeight = 12;
        _enabledColumn.ReadOnly = true;

        _ruleNameColumn.HeaderText = "Name";
        _ruleNameColumn.Name = "RuleName";
        _ruleNameColumn.FillWeight = 30;

        _protocolColumn.HeaderText = "Proto";
        _protocolColumn.Name = "Protocol";
        _protocolColumn.Items.AddRange("TCP", "UDP");
        _protocolColumn.FillWeight = 18;

        _clientPortColumn.HeaderText = "Client Port";
        _clientPortColumn.Name = "ClientPort";
        _clientPortColumn.FillWeight = 20;

        _stationPortColumn.HeaderText = "Station Port";
        _stationPortColumn.Name = "StationPort";
        _stationPortColumn.FillWeight = 20;

        _bindAddressColumn.HeaderText = "Bind Address";
        _bindAddressColumn.Name = "BindAddress";
        _bindAddressColumn.FillWeight = 25;

        _stationTargetColumn = new DataGridViewTextBoxColumn();
        _stationTargetColumn.HeaderText = "Station Target";
        _stationTargetColumn.Name = "StationTarget";
        _stationTargetColumn.FillWeight = 25;

        _statusColumn.HeaderText = "Status";
        _statusColumn.Name = "Status";
        _statusColumn.ReadOnly = true;
        _statusColumn.FillWeight = 15;

        _forwardGrid.Columns.AddRange(new DataGridViewColumn[] {
            _enabledColumn, _ruleNameColumn, _protocolColumn,
            _clientPortColumn, _stationPortColumn, _bindAddressColumn, _stationTargetColumn, _statusColumn
        });

        // Buttons + warnings panel (row 1, fixed 80px height)
        var forwardButtonPanel = new Panel();
        forwardButtonPanel.Dock = DockStyle.Fill;
        forwardButtonPanel.Name = "forwardButtonPanel";

        _addRuleBtn.Text = "+ Add";
        _addRuleBtn.UseVisualStyleBackColor = true;
        _addRuleBtn.Size = new Size(70, 26);
        _addRuleBtn.Location = new Point(0, 4);
        _addRuleBtn.Name = "_addRuleBtn";
        _addRuleBtn.Click += OnAddForwardRuleClick;

        _removeRuleBtn.Text = "− Remove";
        _removeRuleBtn.UseVisualStyleBackColor = true;
        _removeRuleBtn.Size = new Size(80, 26);
        _removeRuleBtn.Location = new Point(76, 4);
        _removeRuleBtn.Name = "_removeRuleBtn";
        _removeRuleBtn.Click += OnRemoveForwardRuleClick;

        _enableSelectedBtn = new Button();
        _enableSelectedBtn.Text = "Enable Sel";
        _enableSelectedBtn.UseVisualStyleBackColor = true;
        _enableSelectedBtn.Size = new Size(80, 26);
        _enableSelectedBtn.Location = new Point(170, 4);
        _enableSelectedBtn.Name = "_enableSelectedBtn";
        _enableSelectedBtn.Enabled = false;
        _enableSelectedBtn.Click += OnEnableSelectedClick;

        _disableSelectedBtn = new Button();
        _disableSelectedBtn.Text = "Disable Sel";
        _disableSelectedBtn.UseVisualStyleBackColor = true;
        _disableSelectedBtn.Size = new Size(80, 26);
        _disableSelectedBtn.Location = new Point(254, 4);
        _disableSelectedBtn.Name = "_disableSelectedBtn";
        _disableSelectedBtn.Enabled = false;
        _disableSelectedBtn.Click += OnDisableSelectedClick;

        _enableAllBtn = new Button();
        _enableAllBtn.Text = "Enable All";
        _enableAllBtn.UseVisualStyleBackColor = true;
        _enableAllBtn.Size = new Size(74, 26);
        _enableAllBtn.Location = new Point(348, 4);
        _enableAllBtn.Name = "_enableAllBtn";
        _enableAllBtn.Click += OnEnableAllClick;

        _disableAllBtn = new Button();
        _disableAllBtn.Text = "Disable All";
        _disableAllBtn.UseVisualStyleBackColor = true;
        _disableAllBtn.Size = new Size(74, 26);
        _disableAllBtn.Location = new Point(426, 4);
        _disableAllBtn.Name = "_disableAllBtn";
        _disableAllBtn.Click += OnDisableAllClick;

        // Exposure warning (10.14, 13.15)
        _bindWarningLabel.Text = "⚠ Non-loopback bind detected: this exposes an unauthenticated tunnel path into the Station's network to every host on your local network.";
        _bindWarningLabel.ForeColor = warningRed;
        _bindWarningLabel.Font = new Font("Segoe UI", 8F);
        _bindWarningLabel.AutoSize = false;
        _bindWarningLabel.Size = new Size(500, 32);
        _bindWarningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _bindWarningLabel.Location = new Point(160, 34);
        _bindWarningLabel.Visible = false;
        _bindWarningLabel.Name = "_bindWarningLabel";

        // RemoteRig unverified warning (10.18)
        _remoteRigWarningLabel.Text = "⚠ RemoteRig/RRC: unverified — RRC compatibility has not been confirmed against physical RRC hardware.";
        _remoteRigWarningLabel.ForeColor = System.Drawing.Color.FromArgb(180, 140, 20);
        _remoteRigWarningLabel.Font = new Font("Segoe UI", 8F);
        _remoteRigWarningLabel.AutoSize = false;
        _remoteRigWarningLabel.Size = new Size(500, 18);
        _remoteRigWarningLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _remoteRigWarningLabel.Location = new Point(160, 56);
        _remoteRigWarningLabel.Visible = false;
        _remoteRigWarningLabel.Name = "_remoteRigWarningLabel";

        // Wizard and Import buttons (second row)
        _wizardBtn = new Button();
        _wizardBtn.Text = "Wizard";
        _wizardBtn.UseVisualStyleBackColor = true;
        _wizardBtn.Size = new Size(70, 26);
        _wizardBtn.Location = new Point(0, 34);
        _wizardBtn.Name = "_wizardBtn";
        _wizardBtn.Click += OnWizardClick;

        _importBtn = new Button();
        _importBtn.Text = "Import";
        _importBtn.UseVisualStyleBackColor = true;
        _importBtn.Size = new Size(70, 26);
        _importBtn.Location = new Point(76, 34);
        _importBtn.Name = "_importBtn";
        _importBtn.Click += OnImportProfileClick;

        forwardButtonPanel.Controls.AddRange(new Control[] {
            _addRuleBtn, _removeRuleBtn, _enableSelectedBtn, _disableSelectedBtn, _enableAllBtn, _disableAllBtn,
            _wizardBtn, _importBtn, _bindWarningLabel, _remoteRigWarningLabel
        });

        forwardInnerLayout.Controls.Add(_forwardGrid, 0, 0);
        forwardInnerLayout.Controls.Add(forwardButtonPanel, 0, 1);

        _forwardGroup.Controls.Add(forwardInnerLayout);

        // ============================================================
        // FLEXRADIO DISCOVERY (13.17-13.20, 15.19) — greyed-out placeholder
        // ============================================================
        _flexGroup = new GroupBox();
        _flexEnableCheck = new CheckBox();
        _flexRadioList = new ListBox();
        _flexPlaceholderLabel = new Label();

        _flexGroup.Text = "FlexRadio Discovery";
        _flexGroup.Dock = DockStyle.Fill;
        _flexGroup.Name = "_flexGroup";
        _flexGroup.Padding = new Padding(8);
        _flexGroup.Enabled = true; // FlexRadio VITA-49 discovery relay implemented

        _flexEnableCheck.Text = "Enable discovery re-emission";
        _flexEnableCheck.AutoSize = true;
        _flexEnableCheck.Location = new Point(12, 24);
        _flexEnableCheck.Checked = false;
        _flexEnableCheck.Name = "_flexEnableCheck";

        _flexRadioList.BorderStyle = BorderStyle.FixedSingle;
        _flexRadioList.Location = new Point(12, 50);
        _flexRadioList.Size = new Size(240, 60);
        _flexRadioList.Name = "_flexRadioList";

        _flexPlaceholderLabel.Text = "Discovered radios appear here when enabled and paired.";
        _flexPlaceholderLabel.Font = new Font("Segoe UI", 8F, FontStyle.Italic);
        _flexPlaceholderLabel.AutoSize = true;
        _flexPlaceholderLabel.Location = new Point(12, 114);
        _flexPlaceholderLabel.Name = "_flexPlaceholderLabel";

        _flexGroup.Controls.AddRange(new Control[] {
            _flexEnableCheck, _flexRadioList, _flexPlaceholderLabel
        });

        // Set tooltip on the FlexRadio group
        var toolTip = new ToolTip(components);
        toolTip.SetToolTip(_flexGroup, "FlexRadio VITA-49 discovery relay — enable on both Station and Client to discover remote radios.");

        // ============================================================
        // TOP-LEVEL GROUPBOX: "Remote WinKeyer"
        // Contains: Paddle, Keyer, Sidetone, Input Ports
        // ============================================================
        _remoteWinKeyerGroup = new GroupBox();
        _remoteWinKeyerGroup.Text = "Remote WinKeyer";
        _remoteWinKeyerGroup.Dock = DockStyle.Fill;
        _remoteWinKeyerGroup.Name = "_remoteWinKeyerGroup";
        _remoteWinKeyerGroup.Padding = new Padding(4);

        var topLayout = new TableLayoutPanel();
        topLayout.Dock = DockStyle.Fill;
        topLayout.ColumnCount = 4;
        topLayout.RowCount = 1;
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10F));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        topLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        topLayout.Name = "topLayout";
        topLayout.Controls.Add(_paddleGroup, 0, 0);
        topLayout.Controls.Add(_keyerGroup, 1, 0);
        topLayout.Controls.Add(_sidetoneGroup, 2, 0);
        topLayout.Controls.Add(_portsGroup, 3, 0);

        _remoteWinKeyerGroup.Controls.Add(topLayout);

        // ============================================================
        // TOP-LEVEL GROUPBOX: "Network Control"
        // Contains: Port Forwards, FlexRadio Discovery
        // ============================================================
        _networkControlGroup = new GroupBox();
        _networkControlGroup.Text = "Network Control";
        _networkControlGroup.Dock = DockStyle.Fill;
        _networkControlGroup.Name = "_networkControlGroup";
        _networkControlGroup.Padding = new Padding(8, 4, 8, 4);

        // Connection row: Station Address + Connect button
        var connectionPanel = new Panel();
        connectionPanel.Dock = DockStyle.Top;
        connectionPanel.Height = 32;
        connectionPanel.Name = "connectionPanel";

        _stationAddressCaptionLabel = new Label
        {
            Text = "Station Address:",
            AutoSize = true,
            Location = new Point(0, 7),
            Name = "_stationAddressCaptionLabel"
        };

        _stationAddressTextBox = new TextBox
        {
            Location = new Point(105, 4),
            Size = new Size(200, 23),
            PlaceholderText = "100.x.x.x or hostname",
            Name = "_stationAddressTextBox"
        };

        _connectButton = new Button
        {
            Text = "Pair",
            Location = new Point(315, 3),
            Size = new Size(75, 25),
            UseVisualStyleBackColor = true,
            Name = "_connectButton"
        };

        _stationArmToggle = new CheckBox
        {
            Text = "Station Armed",
            AutoSize = true,
            Location = new Point(400, 6),
            Checked = true,
            Name = "_stationArmToggle"
        };

        _setStationKeyBtn = new Button
        {
            Text = "Set Key",
            Size = new Size(60, 25),
            Location = new Point(530, 3),
            UseVisualStyleBackColor = true,
            Name = "_setStationKeyBtn"
        };
        _setStationKeyBtn.Click += OnSetStationKeyClick;

        connectionPanel.Controls.AddRange(new Control[] {
            _stationAddressCaptionLabel, _stationAddressTextBox, _connectButton, _stationArmToggle, _setStationKeyBtn
        });

        var bottomLayout = new TableLayoutPanel();
        bottomLayout.Dock = DockStyle.Fill;
        bottomLayout.ColumnCount = 2;
        bottomLayout.RowCount = 1;
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        bottomLayout.Name = "bottomLayout";
        bottomLayout.Controls.Add(_forwardGroup, 0, 0);
        bottomLayout.Controls.Add(_flexGroup, 1, 0);

        _networkControlGroup.Controls.Add(bottomLayout);
        _networkControlGroup.Controls.Add(connectionPanel); // Top of group (added after so Dock=Top works)

        // ============================================================
        // MAIN LAYOUT — TableLayoutPanel with 2 rows
        // ============================================================
        _mainLayout = new TableLayoutPanel();
        _mainLayout.Dock = DockStyle.Fill;
        _mainLayout.ColumnCount = 1;
        _mainLayout.RowCount = 2;
        _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
        _mainLayout.Padding = new Padding(6);
        _mainLayout.Name = "_mainLayout";
        _mainLayout.Controls.Add(_remoteWinKeyerGroup, 0, 0);
        _mainLayout.Controls.Add(_networkControlGroup, 0, 1);

        // ============================================================
        // MAIN FORM (13.1, 13.9) — wrapped in TabControl
        // ============================================================

        // Main menu
        _mainMenu = new MenuStrip();
        _mainMenu.Name = "_mainMenu";
        var rwkMenuItem = new ToolStripMenuItem("&File");
        var aboutMenuItem = new ToolStripMenuItem("About RWK");
        aboutMenuItem.Click += (_, _) => { using var dlg = new AboutDialog(); dlg.ShowDialog(this); };
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
        var wizardMenuItem = new ToolStripMenuItem("Port Forward &Wizard...");
        wizardMenuItem.Click += (_, _) => OnWizardClick(null, EventArgs.Empty);
        rwkMenuItem.DropDownItems.Add(wizardMenuItem);
        rwkMenuItem.DropDownItems.Add(new ToolStripSeparator());
        rwkMenuItem.DropDownItems.Add(deleteTsAuthMenuItem);
        rwkMenuItem.DropDownItems.Add(tsAdminMenuItem);
        rwkMenuItem.DropDownItems.Add(new ToolStripSeparator());
        rwkMenuItem.DropDownItems.Add(exitMenuItem);
        _mainMenu.Items.Add(rwkMenuItem);

        // Tab Control wrapping the entire body
        _tabControl = new TabControl();
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Name = "_tabControl";

        // Tab 1: WinKeyer / Forwarding (the existing main layout)
        _mainTab = new TabPage();
        _mainTab.Text = "WinKeyer / Forwarding";
        _mainTab.Name = "_mainTab";
        _mainTab.Controls.Add(_mainLayout);

        // Tab 2: Log
        _logTab = new TabPage();
        _logTab.Text = "Log";
        _logTab.Name = "_logTab";
        _logTab.Padding = new Padding(6);

        // Log level selector
        _logLevelCombo = new ComboBox();
        _logLevelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _logLevelCombo.Items.AddRange(new object[] { "None", "Descriptive", "Debug" });
        _logLevelCombo.SelectedIndex = 1; // Default: Descriptive
        _logLevelCombo.Dock = DockStyle.Top;
        _logLevelCombo.Name = "_logLevelCombo";

        // Log text area
        _logTextBox = new TextBox();
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Font = new Font("Consolas", 8.5F);
        _logTextBox.BackColor = SystemColors.Window;
        _logTextBox.Name = "_logTextBox";
        _logTextBox.WordWrap = false;

        _logTab.Controls.Add(_logTextBox);
        _logTab.Controls.Add(_logLevelCombo); // Added after so Dock=Top renders above Fill

        _tabControl.TabPages.Add(_mainTab);
        _tabControl.TabPages.Add(_logTab);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 600);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "RWK Client";
        MinimumSize = new Size(700, 400);
        WindowState = FormWindowState.Normal;

        // Form icon
        string icoPath = Path.Combine(AppContext.BaseDirectory, "rwk.ico");
        if (File.Exists(icoPath))
            Icon = new Icon(icoPath);

        Controls.Add(_tabControl);
        Controls.Add(_statusStrip);
        Controls.Add(_mainMenu);
        MainMenuStrip = _mainMenu;

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>
    /// Helper to configure a colored-circle indicator label.
    /// </summary>
    private static void ConfigureIndicator(Label label, string text, System.Drawing.Color color, int x, int y, string name)
    {
        label.Text = text;
        label.Font = new Font("Segoe UI", 18F);
        label.ForeColor = color;
        label.AutoSize = true;
        label.Location = new Point(x, y);
        label.Name = name;
    }
}
