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
using System.Diagnostics;

namespace MorseTest;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private AudioOutput? _audioOutput;
    private SerialPinReader? _serialReader;
    private Thread? _pollThread;
    private volatile bool _stopPolling;
    private volatile bool _lastKeyState;

    public MainForm()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
    }

    private void InitializeComponent()
    {
        this.Text = "Morse Test - COM Port CW Keyer";
        this.Size = new Size(450, 400);
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.StartPosition = FormStartPosition.CenterScreen;

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            RowCount = 7,
            ColumnCount = 2
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // COM Port selection
        var lblComPort = new Label { Text = "COM Port:", Anchor = AnchorStyles.Left, AutoSize = true };
        var cmbComPort = new ComboBox 
        { 
            Name = "cmbComPort",
            Dock = DockStyle.Fill, 
            DropDownStyle = ComboBoxStyle.DropDownList 
        };
        cmbComPort.SelectedIndexChanged += CmbComPort_SelectedIndexChanged;

        // Audio device selection
        var lblAudioDevice = new Label { Text = "Audio Device:", Anchor = AnchorStyles.Left, AutoSize = true };
        var cmbAudioDevice = new ComboBox 
        { 
            Name = "cmbAudioDevice",
            Dock = DockStyle.Fill, 
            DropDownStyle = ComboBoxStyle.DropDownList 
        };
        cmbAudioDevice.SelectedIndexChanged += CmbAudioDevice_SelectedIndexChanged;

        // Pin mode selection
        var lblPinMode = new Label { Text = "Monitor Pin:", Anchor = AnchorStyles.Left, AutoSize = true };
        var cmbPinMode = new ComboBox 
        { 
            Name = "cmbPinMode",
            Dock = DockStyle.Fill, 
            DropDownStyle = ComboBoxStyle.DropDownList 
        };
        cmbPinMode.Items.AddRange(new object[] { "CTS (via RTS)", "DSR (via DTR)", "DCD" });
        cmbPinMode.SelectedIndexChanged += CmbPinMode_SelectedIndexChanged;

        // Invert checkbox
        var chkInvert = new CheckBox 
        { 
            Name = "chkInvert",
            Text = "Invert Pin Logic", 
            Anchor = AnchorStyles.Left,
            AutoSize = true
        };
        chkInvert.CheckedChanged += ChkInvert_CheckedChanged;

        // Tone frequency
        var lblToneFreq = new Label { Text = "Tone Freq (Hz):", Anchor = AnchorStyles.Left, AutoSize = true };
        var numToneFreq = new NumericUpDown 
        { 
            Name = "numToneFreq",
            Minimum = 300, 
            Maximum = 1500, 
            Value = 750,
            Increment = 50,
            Dock = DockStyle.Fill
        };
        numToneFreq.ValueChanged += NumToneFreq_ValueChanged;

        // Status panel
        var statusPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblStatus = new Label 
        { 
            Name = "lblStatus",
            Text = "Status: Not Connected",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold)
        };
        statusPanel.Controls.Add(lblStatus);

        // Pin status panel
        var pinStatusPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };

        var lblCTS = new Label { Name = "lblCTS", Text = "CTS: ?", AutoSize = true, Margin = new Padding(5) };
        var lblDSR = new Label { Name = "lblDSR", Text = "DSR: ?", AutoSize = true, Margin = new Padding(5) };
        var lblDCD = new Label { Name = "lblDCD", Text = "DCD: ?", AutoSize = true, Margin = new Padding(5) };
        var lblRing = new Label { Name = "lblRing", Text = "Ring: ?", AutoSize = true, Margin = new Padding(5) };
        pinStatusPanel.Controls.AddRange(new Control[] { lblCTS, lblDSR, lblDCD, lblRing });

        // Buttons panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };

        var btnRefresh = new Button { Text = "Refresh Ports", AutoSize = true };
        btnRefresh.Click += BtnRefresh_Click;

        var btnTestTone = new Button { Text = "Test Tone", AutoSize = true };
        btnTestTone.Click += BtnTestTone_Click;

        var btnStart = new Button { Name = "btnStart", Text = "Start", AutoSize = true };
        btnStart.Click += BtnStart_Click;

        var btnStop = new Button { Name = "btnStop", Text = "Stop", AutoSize = true, Enabled = false };
        btnStop.Click += BtnStop_Click;

        buttonPanel.Controls.AddRange(new Control[] { btnRefresh, btnTestTone, btnStart, btnStop });

        // Add controls to layout
        mainPanel.Controls.Add(lblComPort, 0, 0);
        mainPanel.Controls.Add(cmbComPort, 1, 0);
        mainPanel.Controls.Add(lblAudioDevice, 0, 1);
        mainPanel.Controls.Add(cmbAudioDevice, 1, 1);
        mainPanel.Controls.Add(lblPinMode, 0, 2);
        mainPanel.Controls.Add(cmbPinMode, 1, 2);
        mainPanel.Controls.Add(new Label(), 0, 3); // spacer
        mainPanel.Controls.Add(chkInvert, 1, 3);
        mainPanel.Controls.Add(lblToneFreq, 0, 4);
        mainPanel.Controls.Add(numToneFreq, 1, 4);
        mainPanel.Controls.Add(statusPanel, 0, 5);
        mainPanel.SetColumnSpan(statusPanel, 2);
        mainPanel.Controls.Add(pinStatusPanel, 0, 6);
        mainPanel.SetColumnSpan(pinStatusPanel, 2);
        mainPanel.Controls.Add(buttonPanel, 0, 7);
        mainPanel.SetColumnSpan(buttonPanel, 2);

        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        this.Controls.Add(mainPanel);

        // Initialize controls with saved settings and data
        this.Load += MainForm_Load;
        this.FormClosing += MainForm_FormClosing;
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        RefreshComPorts();
        RefreshAudioDevices();
        ApplySettings();
    }

    private void RefreshComPorts()
    {
        var cmb = Controls.Find("cmbComPort", true).FirstOrDefault() as ComboBox;
        if (cmb == null) return;

        cmb.Items.Clear();
        var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        cmb.Items.AddRange(ports);

        if (!string.IsNullOrEmpty(_settings.SelectedComPort) && cmb.Items.Contains(_settings.SelectedComPort))
        {
            cmb.SelectedItem = _settings.SelectedComPort;
        }
        else if (cmb.Items.Count > 0)
        {
            cmb.SelectedIndex = 0;
        }
    }

    private void RefreshAudioDevices()
    {
        var cmb = Controls.Find("cmbAudioDevice", true).FirstOrDefault() as ComboBox;
        if (cmb == null) return;

        cmb.Items.Clear();
        var devices = AudioOutput.GetOutputDevices();
        foreach (var device in devices)
        {
            cmb.Items.Add(device);
        }

        // Try to select saved device
        if (!string.IsNullOrEmpty(_settings.SelectedAudioDevice))
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i] is AudioDeviceInfo info && info.Id == _settings.SelectedAudioDevice)
                {
                    cmb.SelectedIndex = i;
                    return;
                }
            }
        }

        if (cmb.Items.Count > 0)
            cmb.SelectedIndex = 0;
    }

    private void ApplySettings()
    {
        var cmbPinMode = Controls.Find("cmbPinMode", true).FirstOrDefault() as ComboBox;
        var chkInvert = Controls.Find("chkInvert", true).FirstOrDefault() as CheckBox;
        var numToneFreq = Controls.Find("numToneFreq", true).FirstOrDefault() as NumericUpDown;

        if (cmbPinMode != null)
            cmbPinMode.SelectedIndex = (int)_settings.PinMode;
        if (chkInvert != null)
            chkInvert.Checked = _settings.InvertPin;
        if (numToneFreq != null)
            numToneFreq.Value = _settings.ToneFrequency;
    }

    private void CmbComPort_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is string port)
        {
            _settings.SelectedComPort = port;
            _settings.Save();
        }
    }

    private void CmbAudioDevice_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is AudioDeviceInfo device)
        {
            _settings.SelectedAudioDevice = device.Id;
            _settings.Save();
        }
    }

    private void CmbPinMode_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (sender is ComboBox cmb)
        {
            _settings.PinMode = (PinMonitorMode)cmb.SelectedIndex;
            _settings.Save();
        }
    }

    private void ChkInvert_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is CheckBox chk)
        {
            _settings.InvertPin = chk.Checked;
            _settings.Save();
        }
    }

    private void NumToneFreq_ValueChanged(object? sender, EventArgs e)
    {
        if (sender is NumericUpDown num)
        {
            _settings.ToneFrequency = (int)num.Value;
            _settings.Save();
        }
    }

    private void BtnRefresh_Click(object? sender, EventArgs e)
    {
        RefreshComPorts();
        RefreshAudioDevices();
    }

    private void BtnTestTone_Click(object? sender, EventArgs e)
    {
        try
        {
            var cmbAudioDevice = Controls.Find("cmbAudioDevice", true).FirstOrDefault() as ComboBox;
            var deviceId = (cmbAudioDevice?.SelectedItem as AudioDeviceInfo)?.Id;

            using var testOutput = new AudioOutput(_settings.ToneFrequency, _settings.Volume);
            testOutput.Initialize(deviceId);
            testOutput.PlayTestTone(500);
            Thread.Sleep(600);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error playing test tone: {ex.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        try
        {
            var cmbComPort = Controls.Find("cmbComPort", true).FirstOrDefault() as ComboBox;
            var cmbAudioDevice = Controls.Find("cmbAudioDevice", true).FirstOrDefault() as ComboBox;
            var btnStart = Controls.Find("btnStart", true).FirstOrDefault() as Button;
            var btnStop = Controls.Find("btnStop", true).FirstOrDefault() as Button;
            var lblStatus = Controls.Find("lblStatus", true).FirstOrDefault() as Label;

            if (cmbComPort?.SelectedItem is not string port)
            {
                MessageBox.Show("Please select a COM port.", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var deviceId = (cmbAudioDevice?.SelectedItem as AudioDeviceInfo)?.Id;

            // Initialize audio
            _audioOutput = new AudioOutput(_settings.ToneFrequency, _settings.Volume);
            _audioOutput.Initialize(deviceId);

            // Open serial port
            _serialReader = new SerialPinReader();
            _serialReader.Open(port);

            // Set output pins high so they can be used with loopback
            // When the key grounds the line, we see the transition
            _serialReader.SetDTR(true);
            _serialReader.SetRTS(true);

            // Start high-resolution polling thread
            _stopPolling = false;
            _pollThread = new Thread(PollThreadProc)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "SerialPinPoller"
            };
            _pollThread.Start();

            if (btnStart != null) btnStart.Enabled = false;
            if (btnStop != null) btnStop.Enabled = true;
            if (lblStatus != null) 
            {
                lblStatus.Text = $"Running - {port}";
                lblStatus.ForeColor = Color.Green;
            }
        }
        catch (Exception ex)
        {
            StopKeyer();
            MessageBox.Show($"Error starting: {ex.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        StopKeyer();
    }

    private void StopKeyer()
    {
        _stopPolling = true;
        _pollThread?.Join(200);
        _pollThread = null;

        _audioOutput?.Stop();
        _audioOutput?.Dispose();
        _audioOutput = null;

        _serialReader?.Close();
        _serialReader?.Dispose();
        _serialReader = null;

        _lastKeyState = false;

        var btnStart = Controls.Find("btnStart", true).FirstOrDefault() as Button;
        var btnStop = Controls.Find("btnStop", true).FirstOrDefault() as Button;
        var lblStatus = Controls.Find("lblStatus", true).FirstOrDefault() as Label;

        if (btnStart != null) btnStart.Enabled = true;
        if (btnStop != null) btnStop.Enabled = false;
        if (lblStatus != null)
        {
            lblStatus.Text = "Status: Stopped";
            lblStatus.ForeColor = Color.Black;
        }
    }

    private void PollThreadProc()
    {
        var sw = Stopwatch.StartNew();
        long lastUiUpdate = 0;

        while (!_stopPolling && _serialReader != null && _serialReader.IsOpen && _audioOutput != null)
        {
            try
            {
                var status = _serialReader.GetPinStatus();

                // Get the monitored pin state based on settings
                bool pinState = _settings.PinMode switch
                {
                    PinMonitorMode.CTS => status.CTS,
                    PinMonitorMode.DSR => status.DSR,
                    PinMonitorMode.DCD => status.DCD,
                    _ => status.CTS
                };

                // Apply inversion if configured
                if (_settings.InvertPin)
                    pinState = !pinState;

                // Key state changed?
                if (pinState != _lastKeyState)
                {
                    _lastKeyState = pinState;

                    if (pinState)
                        _audioOutput.KeyDown();
                    else
                        _audioOutput.KeyUp();
                }

                // Update UI at lower rate (every 50ms)
                if (sw.ElapsedMilliseconds - lastUiUpdate > 50)
                {
                    lastUiUpdate = sw.ElapsedMilliseconds;
                    try
                    {
                        this.BeginInvoke(() =>
                        {
                            UpdatePinLabels(status);
                            UpdateStatusLabel(
                                pinState ? "KEY DOWN" : $"Running - {_serialReader?.PortName}",
                                pinState ? Color.Red : Color.Green);
                        });
                    }
                    catch { } // Form might be closing
                }

                // Tight polling - ~1ms using SpinWait for accuracy
                Thread.SpinWait(1000);
                Thread.Sleep(0); // Yield to avoid 100% CPU but keep responsive
            }
            catch
            {
                break;
            }
        }
    }

    private void UpdatePinLabels(ModemPinStatus status)
    {
        var lblCTS = Controls.Find("lblCTS", true).FirstOrDefault() as Label;
        var lblDSR = Controls.Find("lblDSR", true).FirstOrDefault() as Label;
        var lblDCD = Controls.Find("lblDCD", true).FirstOrDefault() as Label;
        var lblRing = Controls.Find("lblRing", true).FirstOrDefault() as Label;

        if (lblCTS != null) lblCTS.Text = $"CTS: {(status.CTS ? "ON" : "OFF")}";
        if (lblDSR != null) lblDSR.Text = $"DSR: {(status.DSR ? "ON" : "OFF")}";
        if (lblDCD != null) lblDCD.Text = $"DCD: {(status.DCD ? "ON" : "OFF")}";
        if (lblRing != null) lblRing.Text = $"Ring: {(status.Ring ? "ON" : "OFF")}";
    }

    private void UpdateStatusLabel(string text, Color color)
    {
        var lblStatus = Controls.Find("lblStatus", true).FirstOrDefault() as Label;
        if (lblStatus != null)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        StopKeyer();
        _settings.Save();
    }
}
