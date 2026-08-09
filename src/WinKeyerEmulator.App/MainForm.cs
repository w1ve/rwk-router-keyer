using WinKeyerEmulator.App.Controllers;
using WinKeyerEmulator.App.Logging;
using WinKeyerEmulator.App.Services;
using WinKeyerEmulator.App.Settings;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.CloudRelay;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App;

public partial class MainForm : Form
{
    private readonly PortMonitor _portMonitor;
    private readonly AppController _appController;
    private UILogger? _logger;

    public MainForm()
    {
        InitializeComponent();

        // Create logger backed by the log TextBox
        _logger = new UILogger(txtLog);

        // Create controller
        _appController = new AppController(_logger);
        _appController.Stopped += OnAppControllerStopped;
        _appController.RelayStatusChanged += OnRelayStatusChanged;

        // Set up port monitor
        _portMonitor = new PortMonitor();
        _portMonitor.PortsChanged += OnPortsChanged;

        // Populate initial port lists
        RefreshPortLists();

        // Load and apply saved settings
        var settings = AppSettings.Load();
        ApplySettings(settings);

        // Start monitoring for port changes
        _portMonitor.Start();
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (cboKeyingPort.SelectedItem is null)
        {
            MessageBox.Show("Please select a Keying Port.", "Configuration Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var transport = cboTransport.SelectedIndex == 1 ? TransportMode.CloudRelay : TransportMode.Udp;

        if (transport == TransportMode.CloudRelay)
        {
            if (string.IsNullOrWhiteSpace(txtPairingToken.Text) || !TokenGenerator.IsValid(txtPairingToken.Text.Trim()))
            {
                MessageBox.Show("Please enter or generate a valid 64-character pairing token.",
                    "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        GatherSettings().Save();

        var config = new AppConfig
        {
            KeyingPortName = cboKeyingPort.SelectedItem.ToString()!,
            KeyingLine = rdoDTR.Checked ? KeyingLine.DTR : KeyingLine.RTS,
            CommandPortName = GetSelectedCommandPort(),
            Transport = transport,
            UdpAddress = txtUdpAddress.Text.Trim(),
            UdpPort = (int)nudUdpPort.Value,
            PairingToken = txtPairingToken.Text.Trim(),
        };

        try
        {
            _appController.Start(config);
            SetRunningState(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Start Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        try
        {
            _logger?.Log("Stop button clicked", LogSeverity.Info, "UI");
            _appController.Stop();
            _logger?.Log("Stop completed", LogSeverity.Info, "UI");
            SetRunningState(false);
            lblRelayStatus.Text = "";
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error during stop: {ex.Message}", LogSeverity.Error, "UI");
            SetRunningState(false);
        }
    }

    private void ChkLogRawData_CheckedChanged(object? sender, EventArgs e)
    {
        _appController.LogRawData = chkLogRawData.Checked;
    }

    private void CboTransport_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateTransportUI();
    }

    private void BtnGenerateToken_Click(object? sender, EventArgs e)
    {
        txtPairingToken.Text = TokenGenerator.Generate();
        btnCopyToken.Enabled = true;
    }

    private void BtnCopyToken_Click(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(txtPairingToken.Text))
        {
            Clipboard.SetText(txtPairingToken.Text);
        }
    }

    private void UpdateTransportUI()
    {
        bool isRelay = cboTransport.SelectedIndex == 1;

        // UDP controls
        lblUdpAddress.Visible = !isRelay;
        txtUdpAddress.Visible = !isRelay;
        lblUdpPort.Visible = !isRelay;
        nudUdpPort.Visible = !isRelay;

        // Relay controls
        lblPairingToken.Visible = isRelay;
        txtPairingToken.Visible = isRelay;
        btnGenerateToken.Visible = isRelay;
        btnCopyToken.Visible = isRelay;
        btnCopyToken.Enabled = isRelay && !string.IsNullOrWhiteSpace(txtPairingToken.Text);
    }

    private void OnRelayStatusChanged(object? sender, RelayStatusEventArgs e)
    {
        try
        {
            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                BeginInvoke(() => OnRelayStatusChanged(sender, e));
                return;
            }

            lblRelayStatus.Text = e.Status switch
            {
                RelayStatus.Connecting => "⟳ Connecting...",
                RelayStatus.Connected => "◉ Connected",
                RelayStatus.Paired => "✓ Paired",
                RelayStatus.Reconnecting => "⟳ Reconnecting...",
                RelayStatus.Error => "✗ Error",
                _ => "",
            };

            lblRelayStatus.ForeColor = e.Status switch
            {
                RelayStatus.Paired => System.Drawing.Color.Green,
                RelayStatus.Connected => System.Drawing.Color.DarkOrange,
                RelayStatus.Connecting or RelayStatus.Reconnecting => System.Drawing.Color.Gray,
                RelayStatus.Error => System.Drawing.Color.Red,
                _ => System.Drawing.Color.Gray,
            };
        }
        catch { }
    }

    private void OnAppControllerStopped(object? sender, EventArgs e)
    {
        try
        {
            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                BeginInvoke(() => SetRunningState(false));
                return;
            }

            SetRunningState(false);
        }
        catch { }
    }

    private void OnPortsChanged(object? sender, string[] ports)
    {
        try
        {
            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                BeginInvoke(() => OnPortsChanged(sender, ports));
                return;
            }
        }
        catch { return; }

        // Check if an active port was removed
        if (_appController.IsRunning)
        {
            string keyingPort = cboKeyingPort.SelectedItem?.ToString() ?? "";
            string? commandPort = GetSelectedCommandPort();

            bool keyingPortGone = !string.IsNullOrEmpty(keyingPort) && !ports.Contains(keyingPort);
            bool commandPortGone = !string.IsNullOrEmpty(commandPort) && !ports.Contains(commandPort);

            if (keyingPortGone || commandPortGone)
            {
                _appController.Stop();
                string disconnectedPort = keyingPortGone ? keyingPort : commandPort!;
                MessageBox.Show($"Port {disconnectedPort} was disconnected. Emulator stopped.",
                    "Port Disconnected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        UpdatePortDropdowns(ports);
    }

    private void RefreshPortLists()
    {
        var ports = _portMonitor.GetAvailablePorts();
        UpdatePortDropdowns(ports);
    }

    private void UpdatePortDropdowns(string[] ports)
    {
        // Preserve current selections
        string? selectedKeying = cboKeyingPort.SelectedItem?.ToString();
        string? selectedCommand = cboCommandPort.SelectedItem?.ToString();

        cboKeyingPort.Items.Clear();
        cboCommandPort.Items.Clear();

        // Command port always has a "None" option
        cboCommandPort.Items.Add("None");

        foreach (var port in ports.OrderBy(p =>
        {
            if (p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(p.AsSpan(3), out int num))
                return num;
            return int.MaxValue;
        }))
        {
            cboKeyingPort.Items.Add(port);
            cboCommandPort.Items.Add(port);
        }

        // Restore selections if still available
        if (selectedKeying is not null && cboKeyingPort.Items.Contains(selectedKeying))
            cboKeyingPort.SelectedItem = selectedKeying;
        else if (cboKeyingPort.Items.Count > 0)
            cboKeyingPort.SelectedIndex = 0;

        if (selectedCommand is not null && cboCommandPort.Items.Contains(selectedCommand))
            cboCommandPort.SelectedItem = selectedCommand;
        else
            cboCommandPort.SelectedIndex = 0; // "None"
    }

    private void SetRunningState(bool running)
    {
        btnStart.Visible = !running;
        btnStop.Visible = running;

        // Disable/enable configuration controls
        cboKeyingPort.Enabled = !running;
        rdoDTR.Enabled = !running;
        rdoRTS.Enabled = !running;
        cboCommandPort.Enabled = !running;
        cboTransport.Enabled = !running;
        txtUdpAddress.Enabled = !running;
        nudUdpPort.Enabled = !running;
        txtPairingToken.Enabled = !running;
        btnGenerateToken.Enabled = !running;
        btnCopyToken.Enabled = !running && !string.IsNullOrWhiteSpace(txtPairingToken.Text);
    }

    private string? GetSelectedCommandPort()
    {
        var selected = cboCommandPort.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selected) || selected == "None")
            return null;
        return selected;
    }

    private void ApplySettings(AppSettings settings)
    {
        if (settings.KeyingPortName != null && cboKeyingPort.Items.Contains(settings.KeyingPortName))
            cboKeyingPort.SelectedItem = settings.KeyingPortName;

        rdoDTR.Checked = settings.KeyingLine != "RTS";
        rdoRTS.Checked = settings.KeyingLine == "RTS";

        if (settings.CommandPortName != null && cboCommandPort.Items.Contains(settings.CommandPortName))
            cboCommandPort.SelectedItem = settings.CommandPortName;

        cboTransport.SelectedIndex = settings.Transport == "CloudRelay" ? 1 : 0;

        txtUdpAddress.Text = settings.UdpAddress;
        nudUdpPort.Value = settings.UdpPort;
        txtPairingToken.Text = settings.PairingToken ?? "";
        chkLogRawData.Checked = settings.LogRawData;

        UpdateTransportUI();
    }

    private AppSettings GatherSettings()
    {
        return new AppSettings
        {
            KeyingPortName = cboKeyingPort.SelectedItem?.ToString(),
            KeyingLine = rdoRTS.Checked ? "RTS" : "DTR",
            CommandPortName = GetSelectedCommandPort(),
            Transport = cboTransport.SelectedIndex == 1 ? "CloudRelay" : "UDP",
            UdpAddress = txtUdpAddress.Text.Trim(),
            UdpPort = (int)nudUdpPort.Value,
            PairingToken = txtPairingToken.Text.Trim(),
            LogRawData = chkLogRawData.Checked
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        GatherSettings().Save();

        if (_appController.IsRunning)
        {
            _appController.Stop();
        }

        _appController.Stopped -= OnAppControllerStopped;
        _appController.RelayStatusChanged -= OnRelayStatusChanged;
        _portMonitor.PortsChanged -= OnPortsChanged;
        _portMonitor.Dispose();

        base.OnFormClosed(e);
    }
}
