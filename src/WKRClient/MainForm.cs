using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace WKRClient;

public partial class MainForm : Form
{
    private SerialPort? _winKeyerPort;
    private UdpClient? _udpClient;
    private IPEndPoint? _serverEndpoint;
    private Thread? _readThread;
    private volatile bool _running;
    private Task? _udpReceiveTask;
    private CancellationTokenSource? _cts;
    private readonly List<byte> _keyBuffer = new();
    private System.Threading.Timer? _keyFlushTimer;

    public MainForm()
    {
        InitializeComponent();
        RefreshPorts();
        var settings = ClientSettings.Load();
        ApplySettings(settings);
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
        txtServerAddress.Text = s.ServerAddress;
        nudServerPort.Value = s.ServerPort;
    }

    private ClientSettings GatherSettings() => new()
    {
        WinKeyerPort = cboWinKeyerPort.SelectedItem?.ToString(),
        ServerAddress = txtServerAddress.Text.Trim(),
        ServerPort = (int)nudServerPort.Value
    };

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (cboWinKeyerPort.SelectedItem is null)
        {
            MessageBox.Show("Select a WinKeyer COM port.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        GatherSettings().Save();

        try
        {
            _serverEndpoint = new IPEndPoint(IPAddress.Parse(txtServerAddress.Text.Trim()), (int)nudServerPort.Value);
            _udpClient = new UdpClient();
            _running = true;
            _cts = new CancellationTokenSource();
            _keyFlushTimer = new System.Threading.Timer(FlushKeyBuffer, null, Timeout.Infinite, Timeout.Infinite);

            // Start receiving UDP responses from server
            _udpReceiveTask = Task.Run(() => UdpReceiveLoop(_cts.Token));

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

                // Send Admin Open and wait for version response with timeout
                _winKeyerPort.Write(new byte[] { 0x00, 0x02 }, 0, 2);
                Thread.Sleep(500); // Give WinKeyer time to respond

                if (_winKeyerPort.BytesToRead > 0)
                {
                    var resp = new byte[_winKeyerPort.BytesToRead];
                    _winKeyerPort.Read(resp, 0, resp.Length);
                    Log($"WinKeyer found on {portName}, version: {resp[0]}");

                    // Start reading from local WinKeyer and forwarding to server
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

            Log("Started. Server target: " + _serverEndpoint + (_winKeyerPort == null ? " (keyboard only)" : ""));
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
                    var data = new byte[n];
                    Array.Copy(buffer, data, n);
                    // Forward to server over UDP
                    _udpClient?.Send(data, data.Length, _serverEndpoint);
                    Log($"WK→Server: {FormatHex(data)}");
                }
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { break; }
            catch (IOException) { if (_running) { Log("WinKeyer disconnected"); _running = false; } break; }
            catch (InvalidOperationException) { break; }
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
                // Could forward back to WinKeyer or just log
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private void TxtSendText_KeyPress(object? sender, KeyPressEventArgs e)
    {
        // ESC key (0x1B) = immediate abort
        if (e.KeyChar == 0x1B)
        {
            if (_running && _udpClient != null && _serverEndpoint != null)
            {
                // Send Clear Buffer command (0x0A) to abort transmission on server
                _udpClient.Send(new byte[] { 0x0A }, 1, _serverEndpoint);
                Log("ESC → Abort sent to server");
            }
            e.Handled = true;
            return;
        }

        // Send printable ASCII immediately to server as WinKeyer text bytes
        if (_running && _udpClient != null && _serverEndpoint != null)
        {
            char c = e.KeyChar;
            if (c >= 0x20 && c <= 0x7E)
            {
                byte b = (byte)c;
                lock (_keyBuffer)
                {
                    _keyBuffer.Add(b);
                }
                // Reset flush timer — sends after 150ms of typing pause
                _keyFlushTimer?.Change(150, Timeout.Infinite);
                return;
            }
        }
        // Block non-printable chars (except backspace/enter for navigation)
        if (e.KeyChar < 0x20 && e.KeyChar != '\b' && e.KeyChar != '\r')
            e.Handled = true;
    }

    private void FlushKeyBuffer(object? state)
    {
        byte[] data;
        lock (_keyBuffer)
        {
            if (_keyBuffer.Count == 0) return;
            data = _keyBuffer.ToArray();
            _keyBuffer.Clear();
        }

        if (_running && _udpClient != null && _serverEndpoint != null)
        {
            try
            {
                _udpClient.Send(data, data.Length, _serverEndpoint);
                string text = System.Text.Encoding.ASCII.GetString(data);
                Log($"Key→Server: \"{text}\"");
            }
            catch { }
        }
    }

    private void Cleanup()
    {
        _running = false;
        _cts?.Cancel();
        try { _keyFlushTimer?.Dispose(); } catch { }
        _keyFlushTimer = null;
        // Flush any remaining buffered keys
        FlushKeyBuffer(null);
        try { _winKeyerPort?.Close(); } catch { }
        try { _winKeyerPort?.Dispose(); } catch { }
        _winKeyerPort = null;
        try { _udpClient?.Close(); } catch { }
        try { _udpClient?.Dispose(); } catch { }
        _udpClient = null;
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
        txtServerAddress.Enabled = !running;
        nudServerPort.Enabled = !running;
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
