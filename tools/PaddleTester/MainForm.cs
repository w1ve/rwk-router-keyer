/*
 * RWK Paddle Tester — Mouse-driven paddle simulator over serial port.
 *
 * Captures mouse globally (entire Windows surface) using low-level hooks.
 * Left button = Dit (drives RTS → CTS on other end)
 * Right button = Dah (drives DTR → DSR on other end)
 * Both buttons = iambic (both lines asserted)
 * ESC = stop/release capture
 *
 * Pin mapping (matches PaddleInputPoller expectations):
 *   This app asserts RTS  → appears as CTS on the other end → Dit
 *   This app asserts DTR  → appears as DSR on the other end → Dah
 *
 * Copyright (c) 2026 Gerry Hull, W1VE — MIT License
 */
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace PaddleTester;

public sealed class MainForm : Form
{
    private ComboBox _portCombo = null!;
    private Button _openButton = null!;
    private Button _captureButton = null!;
    private Label _statusLabel = null!;
    private Label _ditIndicator = null!;
    private Label _dahIndicator = null!;
    private Label _captureStatusLabel = null!;

    private SerialPort? _port;
    private bool _ditActive;
    private bool _dahActive;
    private bool _capturing;

    // Low-level mouse hook
    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _hookProc;

    public MainForm()
    {
        InitializeLayout();
        PopulatePorts();
        KeyPreview = true;
        KeyDown += OnKeyDown;
    }

    private void InitializeLayout()
    {
        Text = "RWK Paddle Tester";
        Size = new Size(420, 320);
        MinimumSize = new Size(400, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // Port selection
        var portLabel = new Label { Text = "COM Port:", Location = new Point(12, 18), AutoSize = true };
        Controls.Add(portLabel);

        _portCombo = new ComboBox
        {
            Location = new Point(90, 14),
            Size = new Size(120, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        Controls.Add(_portCombo);

        var refreshBtn = new Button
        {
            Text = "Refresh",
            Location = new Point(218, 13),
            Size = new Size(65, 25),
            UseVisualStyleBackColor = true
        };
        refreshBtn.Click += (_, _) => PopulatePorts();
        Controls.Add(refreshBtn);

        _openButton = new Button
        {
            Text = "Open",
            Location = new Point(290, 13),
            Size = new Size(80, 25),
            UseVisualStyleBackColor = true
        };
        _openButton.Click += OnOpenCloseClick;
        Controls.Add(_openButton);

        // Status
        _statusLabel = new Label
        {
            Text = "Port closed",
            Location = new Point(12, 48),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.Gray
        };
        Controls.Add(_statusLabel);

        // Capture button
        _captureButton = new Button
        {
            Text = "Start Capture",
            Location = new Point(12, 75),
            Size = new Size(120, 30),
            UseVisualStyleBackColor = true,
            Enabled = false,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _captureButton.Click += OnCaptureClick;
        Controls.Add(_captureButton);

        _captureStatusLabel = new Label
        {
            Text = "Open a port, then click Start Capture.\n" +
                   "Left mouse = Dit | Right mouse = Dah | Both = Iambic\n" +
                   "Press ESC to stop capture.",
            Location = new Point(12, 112),
            Size = new Size(390, 52),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(_captureStatusLabel);

        // Large indicators
        _ditIndicator = new Label
        {
            Text = "DIT",
            Font = new Font("Segoe UI", 28f, FontStyle.Bold),
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(40, 170),
            Size = new Size(140, 80),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_ditIndicator);

        _dahIndicator = new Label
        {
            Text = "DAH",
            Font = new Font("Segoe UI", 28f, FontStyle.Bold),
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(230, 170),
            Size = new Size(140, 80),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_dahIndicator);

        // Footer
        var footerLabel = new Label
        {
            Text = "Global mouse capture active when capturing. ESC to release.",
            Location = new Point(12, 260),
            Size = new Size(390, 18),
            Font = new Font("Segoe UI", 8f),
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(footerLabel);

        FormClosing += (_, _) => { StopCapture(); ClosePort(); };
    }

    private void PopulatePorts()
    {
        _portCombo.Items.Clear();
        var ports = SerialPort.GetPortNames()
            .OrderBy(p =>
            {
                if (p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(p[3..], out int n))
                    return n;
                return 9999;
            })
            .ToArray();
        _portCombo.Items.AddRange(ports);
        if (ports.Length > 0) _portCombo.SelectedIndex = 0;
    }

    private void OnOpenCloseClick(object? sender, EventArgs e)
    {
        if (_port is not null && _port.IsOpen)
            ClosePort();
        else
            OpenPort();
    }

    private void OpenPort()
    {
        if (_portCombo.SelectedItem is not string portName) return;

        try
        {
            _port = new SerialPort(portName)
            {
                BaudRate = 9600,
                DtrEnable = false,
                RtsEnable = false,
                Handshake = Handshake.None
            };
            _port.Open();
            _port.RtsEnable = false;
            _port.DtrEnable = false;

            _statusLabel.Text = $"Port open: {portName}";
            _statusLabel.ForeColor = Color.Green;
            _openButton.Text = "Close";
            _portCombo.Enabled = false;
            _captureButton.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open {portName}:\n\n{ex.Message}",
                "Port Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClosePort()
    {
        StopCapture();

        if (_port is not null)
        {
            try { _port.RtsEnable = false; _port.DtrEnable = false; _port.Close(); } catch { }
            _port.Dispose();
            _port = null;
        }

        _ditActive = false;
        _dahActive = false;
        UpdateIndicators();

        _statusLabel.Text = "Port closed";
        _statusLabel.ForeColor = Color.Gray;
        _openButton.Text = "Open";
        _portCombo.Enabled = true;
        _captureButton.Enabled = false;
    }

    private void OnCaptureClick(object? sender, EventArgs e)
    {
        if (_capturing)
            StopCapture();
        else
            StartCapture();
    }

    private void StartCapture()
    {
        if (_port is null || !_port.IsOpen || _capturing) return;

        _hookProc = MouseHookCallback;
        _hookId = NativeMethods.SetMouseHook(_hookProc);

        _capturing = true;
        _captureButton.Text = "Stop Capture";
        _captureButton.BackColor = Color.FromArgb(255, 200, 200);
        _captureStatusLabel.Text = "CAPTURING — Left=Dit, Right=Dah, Both=Iambic\nPress ESC to stop.";
        _captureStatusLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _captureStatusLabel.ForeColor = Color.DarkRed;
    }

    private void StopCapture()
    {
        if (!_capturing) return;

        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _hookProc = null;
        _capturing = false;

        // Release lines
        if (_port is not null && _port.IsOpen)
        {
            try { _port.RtsEnable = false; _port.DtrEnable = false; } catch { }
        }
        _ditActive = false;
        _dahActive = false;
        UpdateIndicators();

        _captureButton.Text = "Start Capture";
        _captureButton.BackColor = SystemColors.Control;
        _captureStatusLabel.Text = "Capture stopped. Click Start Capture to resume.";
        _captureStatusLabel.Font = new Font("Segoe UI", 9f);
        _captureStatusLabel.ForeColor = SystemColors.ControlText;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _capturing)
        {
            StopCapture();
            e.Handled = true;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _port is not null && _port.IsOpen)
        {
            int msg = (int)wParam;

            switch (msg)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    _ditActive = true;
                    SetLines();
                    break;
                case NativeMethods.WM_LBUTTONUP:
                    _ditActive = false;
                    SetLines();
                    break;
                case NativeMethods.WM_RBUTTONDOWN:
                    _dahActive = true;
                    SetLines();
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    _dahActive = false;
                    SetLines();
                    break;
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void SetLines()
    {
        try
        {
            _port!.RtsEnable = _ditActive;  // RTS → CTS (Dit)
            _port!.DtrEnable = _dahActive;  // DTR → DSR (Dah)
        }
        catch { }

        // Update UI on the UI thread
        if (InvokeRequired)
            BeginInvoke(UpdateIndicators);
        else
            UpdateIndicators();
    }

    private void UpdateIndicators()
    {
        _ditIndicator.ForeColor = _ditActive ? Color.FromArgb(0, 180, 0) : Color.LightGray;
        _ditIndicator.BackColor = _ditActive ? Color.FromArgb(220, 255, 220) : SystemColors.Control;
        _dahIndicator.ForeColor = _dahActive ? Color.FromArgb(0, 0, 200) : Color.LightGray;
        _dahIndicator.BackColor = _dahActive ? Color.FromArgb(220, 220, 255) : SystemColors.Control;
    }
}

/// <summary>
/// P/Invoke declarations for low-level mouse hook (global mouse capture).
/// </summary>
internal static class NativeMethods
{
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static IntPtr SetMouseHook(LowLevelMouseProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }
}
