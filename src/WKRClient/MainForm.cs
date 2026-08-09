using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using WinKeyerEmulator.Core.CloudRelay;

namespace WKRClient;

public partial class MainForm : Form
{
    private SerialPort? _winKeyerPort;
    private UdpClient? _udpClient;
    private IPEndPoint? _serverEndpoint;
    private CloudRelayTransport? _relayTransport;
    private Thread? _readThread;
    private volatile bool _running;
    private Task? _udpReceiveTask;
    private CancellationTokenSource? _cts;
    private readonly List<byte> _keyBuffer = new();
    private System.Threading.Timer? _keyFlushTimer;
    private byte _lastPotWpm;           // For filtering spurious pot changes
    private DateTime _lastPotTime;       // For debouncing rapid pot changes
    private ClientSettings _settings = new(); // Current settings for paddle config

    public MainForm()
    {
        InitializeComponent();
        RefreshPorts();
        _settings = ClientSettings.Load();
        ApplySettings(_settings);
        txtSendText.KeyPress += TxtSendText_KeyPress;
    }

    private void RefreshPorts()
    {
        cboWinKeyerPort.Items.Clear();
        var ports = SerialPort.GetPortNames()
            .OrderBy(p => int.TryParse(p.AsSpan(3), out int n) ? n : int.MaxValue)
            .ToArray();
        foreach (var p in ports)
            cboWinKeyerPort.Items.Add(p);
        if (cboWinKeyerPort.Items.Count > 0)
            cboWinKeyerPort.SelectedIndex = 0;
    }

    private void ApplySettings(ClientSettings s)
    {
        if (s.WinKeyerPort != null && cboWinKeyerPort.Items.Contains(s.WinKeyerPort))
            cboWinKeyerPort.SelectedItem = s.WinKeyerPort;
        cboTransport.SelectedIndex = s.Transport == "CloudRelay" ? 1 : 0;
        txtServerAddress.Text = s.ServerAddress;
        nudServerPort.Value = s.ServerPort;
        txtPairingToken.Text = s.PairingToken ?? "";
        cboKeyMode.SelectedIndex = (int)s.KeyMode;
        chkPaddleSwap.Checked = s.PaddleSwap;
        chkAutospace.Checked = s.Autospace;
        UpdateTransportUI();
    }

    private ClientSettings GatherSettings() => new()
    {
        WinKeyerPort = cboWinKeyerPort.SelectedItem?.ToString(),
        Transport = cboTransport.SelectedIndex == 1 ? "CloudRelay" : "UDP",
        ServerAddress = txtServerAddress.Text.Trim(),
        ServerPort = (int)nudServerPort.Value,
        PairingToken = txtPairingToken.Text.Trim(),
        KeyMode = (KeyMode)cboKeyMode.SelectedIndex,
        PaddleSwap = chkPaddleSwap.Checked,
        Autospace = chkAutospace.Checked,
    };

    private void CboTransport_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateTransportUI();
    }

    private void UpdateTransportUI()
    {
        bool isRelay = cboTransport.SelectedIndex == 1;

        // UDP controls
        lblServerAddress.Visible = !isRelay;
        txtServerAddress.Visible = !isRelay;
        lblServerPort.Visible = !isRelay;
        nudServerPort.Visible = !isRelay;

        // Relay controls
        lblPairingToken.Visible = isRelay;
        txtPairingToken.Visible = isRelay;
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (cboWinKeyerPort.SelectedItem is null)
        {
            MessageBox.Show("Select a WinKeyer COM port.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool isRelay = cboTransport.SelectedIndex == 1;

        if (isRelay)
        {
            if (string.IsNullOrWhiteSpace(txtPairingToken.Text) || !TokenGenerator.IsValid(txtPairingToken.Text.Trim()))
            {
                MessageBox.Show("Please enter the pairing token from the server (64 hex characters).",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        GatherSettings().Save();

        try
        {
            _running = true;
            _cts = new CancellationTokenSource();
            _keyFlushTimer = new System.Threading.Timer(FlushKeyBuffer, null, Timeout.Infinite, Timeout.Infinite);

            if (isRelay)
            {
                // Start Cloud Relay transport
                var relayConfig = new RelayConfig
                {
                    RelayUrl = "wss://wrs.w1ve.com/ws",
                    PairingToken = txtPairingToken.Text.Trim(),
                    EndpointType = RelayEndpointType.RemoteSide,
                };
                _relayTransport = new CloudRelayTransport(relayConfig);
                _relayTransport.StatusChanged += OnRelayStatusChanged;
                _relayTransport.DataReceived += OnRelayDataReceived;
                _relayTransport.Error += (_, msg) => Log($"Relay error: {msg}");
                _relayTransport.Start();
                Log("Cloud Relay started, connecting...");
            }
            else
            {
                // Start UDP transport
                _serverEndpoint = new IPEndPoint(IPAddress.Parse(txtServerAddress.Text.Trim()), (int)nudServerPort.Value);
                _udpClient = new UdpClient();
                _udpReceiveTask = Task.Run(() => UdpReceiveLoop(_cts.Token));
            }

            // Try to open local WinKeyer serial port
            string portName = cboWinKeyerPort.SelectedItem.ToString()!;
            try
            {
                _winKeyerPort = new SerialPort(portName, 1200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 2000
                };
                _winKeyerPort.Open();

                // Send Admin Open (0x00 0x02) and wait for version response
                _winKeyerPort.Write(new byte[] { 0x00, 0x02 }, 0, 2);
                
                // Retry loop for version byte (more robust than fixed sleep)
                int? version = null;
                for (int attempt = 0; attempt < 10 && version == null; attempt++)
                {
                    Thread.Sleep(50);
                    if (_winKeyerPort.BytesToRead > 0)
                    {
                        var resp = new byte[_winKeyerPort.BytesToRead];
                        _winKeyerPort.Read(resp, 0, resp.Length);
                        // Validate plausible version (10-50 covers WK1 through WK3)
                        if (resp[0] >= 10 && resp[0] <= 50)
                            version = resp[0];
                    }
                }

                if (version.HasValue)
                {
                    // Determine generation from version byte
                    string gen = version >= 30 ? "WK3" : version >= 20 ? "WK2" : "WK1";
                    Log($"WinKeyer found on {portName}: {gen} (rev {version})");

                    if (version < 20)
                        Log("WARNING: WK1 detected — mode register layout may differ; paddle echo untested.");

                    // Speed Pot Setup (cmd 0x05): min=5, range=45 (5-50 WPM), step=0
                    _winKeyerPort.Write(new byte[] { 0x05, 0x05, 0x2D, 0x00 }, 0, 4);
                    Thread.Sleep(50);

                    // Mode register (0x0E) — CORRECT command for paddle echo!
                    // Previous code used 0x0D which is Farnsworth (wrong command).
                    // Build mode byte from settings: paddle echo + key mode + swap + autospace
                    _settings = GatherSettings();
                    byte mode = _settings.BuildModeRegister();
                    _winKeyerPort.Write(new byte[] { 0x0E, mode }, 0, 2);
                    Thread.Sleep(50);

                    // Reset debounce so first pot change is always sent
                    _lastPotWpm = 0;
                    _lastPotTime = DateTime.MinValue;

                    string modeDesc = $"{_settings.KeyMode}";
                    if (_settings.PaddleSwap) modeDesc += ", swapped";
                    if (_settings.Autospace) modeDesc += ", autospace";
                    Log($"Paddle echo ON ({modeDesc})");

                    _winKeyerPort.ReadTimeout = 500;
                    _readThread = new Thread(ReadWinKeyerLoop) { IsBackground = true, Name = "WKR-SerialRead" };
                    _readThread.Start();
                }
                else
                {
                    Log($"WARNING: WINKEY NOT FOUND ON {portName}. Restart when Winkey connected.");
                    _winKeyerPort.Close();
                    _winKeyerPort.Dispose();
                    _winKeyerPort = null;
                }
            }
            catch (Exception ex)
            {
                Log($"WARNING: WINKEY NOT FOUND ON {portName}. {ex.Message}. Restart when Winkey connected.");
                try { _winKeyerPort?.Close(); } catch { }
                try { _winKeyerPort?.Dispose(); } catch { }
                _winKeyerPort = null;
            }

            string transportInfo = isRelay ? "Cloud Relay" : $"UDP → {_serverEndpoint}";
            Log($"Started. Transport: {transportInfo}" + (_winKeyerPort == null ? " (keyboard only)" : ""));
            SetRunningState(true);
        }
        catch (Exception ex)
        {
            Log("Start failed: " + ex.Message);
            Cleanup();
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        Log("Stopping...");
        Cleanup();
        Log("Stopped.");
        SetRunningState(false);
        lblRelayStatus.Text = "";
    }

    private void ReadWinKeyerLoop()
    {
        var buffer = new byte[256];
        while (_running)
        {
            try
            {
                if (_winKeyerPort is null || !_winKeyerPort.IsOpen) break;
                int n = _winKeyerPort.Read(buffer, 0, buffer.Length);
                if (n > 0)
                {
                    var commands = new List<byte>(n);
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buffer[i];
                        if (b >= 0xC0)
                        {
                            // Status byte — discard
                        }
                        else if (b >= 0x80)
                        {
                            // Speed pot status: 0x80 | pot_position
                            // WinKeyer is already using this speed locally (0x0D 0x40 mode)
                            // Just forward to server so it matches
                            byte potPos = (byte)(b & 0x3F);
                            byte wpm = (byte)(5 + potPos);

                            // Debounce: only send if WPM actually changed AND
                            // at least 25ms since last change (filter ADC noise)
                            var now = DateTime.UtcNow;
                            bool wpmChanged = wpm != _lastPotWpm;
                            bool enoughTime = (now - _lastPotTime).TotalMilliseconds >= 25;

                            if (wpmChanged && enoughTime)
                            {
                                _lastPotWpm = wpm;
                                _lastPotTime = now;
                                commands.Add(0x02);
                                commands.Add(wpm);
                                Log($"Speed pot → {wpm} WPM");
                            }
                        }
                        else
                        {
                            // Character echo — forward to server
                            commands.Add(b);
                        }
                    }

                    if (commands.Count > 0)
                    {
                        var data = commands.ToArray();
                        SendToServer(data);
                        Log($"WK→Server: {FormatHex(data)}");
                    }
                }
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { break; }
            catch (IOException) { if (_running) { Log("WinKeyer disconnected"); } break; }
            catch (InvalidOperationException) { break; }
            catch (UnauthorizedAccessException) { break; }
        }

        // If the loop exited unexpectedly while running, notify the UI
        if (_running)
        {
            _running = false;
            try
            {
                BeginInvoke(() =>
                {
                    Log("WinKeyer connection lost.");
                    Cleanup();
                    SetRunningState(false);
                    lblRelayStatus.Text = "";
                });
            }
            catch { }
        }
    }

    private void SendToServer(byte[] data)
    {
        if (_relayTransport is not null)
        {
            _relayTransport.SendData(data);
        }
        else if (_udpClient is not null && _serverEndpoint is not null)
        {
            _udpClient.Send(data, data.Length, _serverEndpoint);
        }
    }

    private async Task UdpReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(ct);
                Log($"Server→: {FormatHex(result.Buffer)}");
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private void OnRelayDataReceived(object? sender, byte[] data)
    {
        Log($"Server→: {FormatHex(data)}");
        // Could forward back to WinKeyer for status display if needed
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

            if (e.Message != null)
                Log($"Relay: {e.Message}");
        }
        catch { }
    }

    private void TxtSendText_KeyPress(object? sender, KeyPressEventArgs e)
    {
        // ESC key (0x1B) = immediate abort
        if (e.KeyChar == 0x1B)
        {
            if (_running)
            {
                SendToServer(new byte[] { 0x0A });
                Log("ESC → Abort sent to server");
            }
            e.Handled = true;
            return;
        }

        // Send printable ASCII immediately to server as WinKeyer text bytes
        if (_running)
        {
            char c = e.KeyChar;
            if (c >= 0x20 && c <= 0x7E)
            {
                byte b = (byte)c;
                lock (_keyBuffer)
                {
                    _keyBuffer.Add(b);
                }
                // Reset flush timer — sends after 50ms of typing pause.
                // This batches rapid keystrokes so "CQ" goes as one packet.
                // Paddle characters bypass this entirely (forwarded immediately via WinKeyer echo).
                _keyFlushTimer?.Change(50, Timeout.Infinite);
                return;
            }
        }
        // Block non-printable chars (except backspace/enter for navigation)
        if (e.KeyChar < 0x20 && e.KeyChar != '\b' && e.KeyChar != '\r')
            e.Handled = true;
    }

    private void FlushKeyBuffer(object? state)
    {
        FlushKeyBufferInternal(requireRunning: true);
    }

    /// <summary>
    /// Internal flush that can optionally bypass the _running check (for shutdown).
    /// </summary>
    private void FlushKeyBufferInternal(bool requireRunning)
    {
        byte[] data;
        lock (_keyBuffer)
        {
            if (_keyBuffer.Count == 0) return;
            data = _keyBuffer.ToArray();
            _keyBuffer.Clear();
        }

        if (!requireRunning || _running)
        {
            try
            {
                SendToServer(data);
                string text = System.Text.Encoding.ASCII.GetString(data);
                Log($"Key→Server: \"{text}\"");
            }
            catch { }
        }
    }

    private void Cleanup()
    {
        // Flush any pending keystrokes BEFORE setting _running = false
        try { _keyFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        FlushKeyBufferInternal(requireRunning: false);

        _running = false;
        _cts?.Cancel();
        try { _keyFlushTimer?.Dispose(); } catch { }
        _keyFlushTimer = null;
        try { _winKeyerPort?.Close(); } catch { }
        try { _winKeyerPort?.Dispose(); } catch { }
        _winKeyerPort = null;
        try { _udpClient?.Close(); } catch { }
        try { _udpClient?.Dispose(); } catch { }
        _udpClient = null;
        try
        {
            if (_relayTransport is not null)
            {
                _relayTransport.StatusChanged -= OnRelayStatusChanged;
                _relayTransport.DataReceived -= OnRelayDataReceived;
                _relayTransport.Dispose();
                _relayTransport = null;
            }
        }
        catch { }
        _readThread?.Join(500);
        _readThread = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void SetRunningState(bool running)
    {
        btnStart.Visible = !running;
        btnStop.Visible = running;
        cboWinKeyerPort.Enabled = !running;
        cboTransport.Enabled = !running;
        txtServerAddress.Enabled = !running;
        nudServerPort.Enabled = !running;
        txtPairingToken.Enabled = !running;
        cboKeyMode.Enabled = !running;
        chkPaddleSwap.Enabled = !running;
        chkAutospace.Enabled = !running;
    }

    private void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        if (txtLog.InvokeRequired)
        {
            try { txtLog.BeginInvoke(() => AppendLog(line)); } catch { }
        }
        else
        {
            AppendLog(line);
        }
    }

    private void AppendLog(string line)
    {
        if (txtLog.IsDisposed) return;
        txtLog.AppendText(line + Environment.NewLine);
    }

    private static string FormatHex(byte[] data)
    {
        if (data.Length > 0 && data.All(b => b >= 0x20 && b <= 0x7E))
            return $"\"{System.Text.Encoding.ASCII.GetString(data)}\" ({string.Join(" ", data.Select(b => b.ToString("X2")))})";
        return string.Join(" ", data.Select(b => b.ToString("X2")));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        GatherSettings().Save();
        Cleanup();
        base.OnFormClosed(e);
    }
}
