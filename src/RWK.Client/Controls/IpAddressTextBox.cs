/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace RWK.Client.Controls;

/// <summary>
/// A structured IP address input control with dotted-octet notation for IPv4
/// (four octet fields separated by dots) and colon-notation for IPv6 (eight
/// hextet fields separated by colons, with :: shorthand support).
/// A dropdown on the right selects between v4/v6/both modes.
/// </summary>
public sealed class IpAddressTextBox : UserControl
{
    private readonly TextBox[] _v4Octets = new TextBox[4];
    private readonly Label[] _v4Dots = new Label[3];
    private readonly TextBox _v6TextBox;
    private readonly ComboBox _modeCombo;
    private readonly Panel _v4Panel;
    private readonly Panel _v6Panel;
    private readonly ToolTip _toolTip;

    private static readonly Color ErrorBackColor = Color.FromArgb(255, 230, 230);
    private static readonly Color NormalBackColor = SystemColors.Window;

    private IpAddressMode _mode = IpAddressMode.Both;
    private IPAddress? _parsedAddress;
    private bool _isValid;
    private bool _suppressEvents;

    /// <summary>Fired when the validation state or address changes.</summary>
    public event EventHandler? ValidationChanged;

    /// <summary>Whether the current input is a valid IP address for the selected mode.</summary>
    [Browsable(false)]
    public bool IsValid => _isValid;

    /// <summary>The parsed IP address, or null if invalid.</summary>
    [Browsable(false)]
    public IPAddress? Address => _parsedAddress;

    /// <summary>Gets or sets the address mode (IPv4, IPv6, or Both).</summary>
    [DefaultValue(IpAddressMode.Both)]
    public IpAddressMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _modeCombo.SelectedIndex = (int)value;
            UpdatePanelVisibility();
            Revalidate();
        }
    }

    /// <summary>Gets or sets the IP address text (canonical form).</summary>
    [Browsable(true)]
    public override string Text
    {
        get => GetAddressString();
        set => SetAddressString(value);
    }

    /// <summary>Gets or sets placeholder text (only applies to the v6 free-form box).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _v6TextBox.PlaceholderText;
        set => _v6TextBox.PlaceholderText = value;
    }

    /// <summary>Attempts to get the parsed IP address.</summary>
    public bool TryGetAddress(out IPAddress? address)
    {
        address = _parsedAddress;
        return _isValid;
    }

    public IpAddressTextBox()
    {
        _toolTip = new ToolTip { InitialDelay = 200, ReshowDelay = 100 };

        // Mode dropdown
        _modeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "v4/v6", "v4", "v6" },
            SelectedIndex = 0,
            Width = 50,
            Dock = DockStyle.Right,
            Font = new Font("Segoe UI", 7.5F),
        };
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            _mode = (IpAddressMode)_modeCombo.SelectedIndex;
            UpdatePanelVisibility();
            Revalidate();
        };

        // IPv4 panel: 4 octet text boxes with dots between them
        _v4Panel = new Panel { Dock = DockStyle.Fill, BackColor = NormalBackColor };
        for (int i = 0; i < 4; i++)
        {
            _v4Octets[i] = new TextBox
            {
                Width = 36,
                MaxLength = 3,
                TextAlign = HorizontalAlignment.Center,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F),
                Name = $"_octet{i}"
            };
            _v4Octets[i].TextChanged += (_, _) => Revalidate();
            _v4Octets[i].KeyPress += OnOctetKeyPress;
            _v4Octets[i].KeyDown += OnOctetKeyDown;

            if (i < 3)
            {
                _v4Dots[i] = new Label
                {
                    Text = ".",
                    AutoSize = false,
                    Width = 8,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Consolas", 9F, FontStyle.Bold),
                };
            }
        }

        // Layout the IPv4 panel
        int x = 2;
        for (int i = 0; i < 4; i++)
        {
            _v4Octets[i].Location = new Point(x, 2);
            _v4Panel.Controls.Add(_v4Octets[i]);
            x += _v4Octets[i].Width;
            if (i < 3)
            {
                _v4Dots[i].Location = new Point(x, 2);
                _v4Dots[i].Height = _v4Octets[i].Height;
                _v4Panel.Controls.Add(_v4Dots[i]);
                x += _v4Dots[i].Width;
            }
        }

        // IPv6 panel: free-form text box with colon notation (too many fields for individual boxes)
        _v6Panel = new Panel { Dock = DockStyle.Fill, BackColor = NormalBackColor, Visible = false };
        _v6TextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9F),
            PlaceholderText = "e.g. fd7a:115c:a1e0::1",
        };
        _v6TextBox.TextChanged += (_, _) => Revalidate();
        _v6Panel.Controls.Add(_v6TextBox);

        // Assemble
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = NormalBackColor;
        Height = 23;
        Controls.Add(_v4Panel);
        Controls.Add(_v6Panel);
        Controls.Add(_modeCombo);

        GotFocus += (_, _) =>
        {
            if (_v4Panel.Visible) _v4Octets[0].Focus();
            else _v6TextBox.Focus();
        };
    }

    private void UpdatePanelVisibility()
    {
        switch (_mode)
        {
            case IpAddressMode.IPv4Only:
                _v4Panel.Visible = true;
                _v6Panel.Visible = false;
                break;
            case IpAddressMode.IPv6Only:
                _v4Panel.Visible = false;
                _v6Panel.Visible = true;
                break;
            case IpAddressMode.Both:
            default:
                // Show v4 panel by default; if current text looks like IPv6, show v6
                if (_parsedAddress?.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    _v4Panel.Visible = false;
                    _v6Panel.Visible = true;
                }
                else
                {
                    _v4Panel.Visible = true;
                    _v6Panel.Visible = false;
                }
                break;
        }
    }

    private void OnOctetKeyPress(object? sender, KeyPressEventArgs e)
    {
        // Only allow digits and control characters in octet fields
        if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar) && e.KeyChar != '.')
        {
            e.Handled = true;
            return;
        }

        // Period advances to next octet
        if (e.KeyChar == '.')
        {
            e.Handled = true;
            int idx = Array.IndexOf(_v4Octets, sender);
            if (idx >= 0 && idx < 3)
                _v4Octets[idx + 1].Focus();
        }
    }

    private void OnOctetKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        int idx = Array.IndexOf(_v4Octets, sender);

        // Right arrow at end of field → next octet
        if (e.KeyCode == Keys.Right && tb.SelectionStart == tb.Text.Length && idx < 3)
        {
            _v4Octets[idx + 1].Focus();
            _v4Octets[idx + 1].SelectionStart = 0;
            e.Handled = true;
        }
        // Left arrow at start of field → previous octet
        else if (e.KeyCode == Keys.Left && tb.SelectionStart == 0 && idx > 0)
        {
            _v4Octets[idx - 1].Focus();
            _v4Octets[idx - 1].SelectionStart = _v4Octets[idx - 1].Text.Length;
            e.Handled = true;
        }
        // Tab to next field within the control before leaving
        else if (e.KeyCode == Keys.Tab && !e.Shift && idx < 3)
        {
            _v4Octets[idx + 1].Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private string GetAddressString()
    {
        if (_v4Panel.Visible)
        {
            string[] parts = _v4Octets.Select(o => o.Text.Trim()).ToArray();
            if (parts.All(p => p.Length > 0))
                return string.Join(".", parts);
            return string.Join(".", parts); // partial
        }
        return _v6TextBox.Text.Trim();
    }

    private void SetAddressString(string? value)
    {
        _suppressEvents = true;
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                foreach (var o in _v4Octets) o.Text = "";
                _v6TextBox.Text = "";
                return;
            }

            if (IPAddress.TryParse(value, out IPAddress? addr))
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] bytes = addr.GetAddressBytes();
                    for (int i = 0; i < 4; i++)
                        _v4Octets[i].Text = bytes[i].ToString();
                    _v4Panel.Visible = true;
                    _v6Panel.Visible = false;
                }
                else
                {
                    _v6TextBox.Text = addr.ToString();
                    _v4Panel.Visible = false;
                    _v6Panel.Visible = true;
                }
            }
            else
            {
                // Best effort — put in whichever panel is visible
                if (value.Contains(':'))
                {
                    _v6TextBox.Text = value;
                    _v4Panel.Visible = false;
                    _v6Panel.Visible = true;
                }
                else
                {
                    var parts = value.Split('.');
                    for (int i = 0; i < Math.Min(4, parts.Length); i++)
                        _v4Octets[i].Text = parts[i];
                    _v4Panel.Visible = true;
                    _v6Panel.Visible = false;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
            Revalidate();
        }
    }

    private void Revalidate()
    {
        if (_suppressEvents) return;

        string text = GetAddressString();
        var result = IpAddressValidator.Validate(text, _mode);
        bool wasValid = _isValid;
        _isValid = result.IsValid;
        _parsedAddress = result.Address;

        // Visual feedback
        Color bg = string.IsNullOrWhiteSpace(text) ? NormalBackColor
                 : _isValid ? NormalBackColor
                 : ErrorBackColor;

        BackColor = bg;
        _v4Panel.BackColor = bg;
        _v6Panel.BackColor = bg;
        foreach (var o in _v4Octets) o.BackColor = bg;
        _v6TextBox.BackColor = bg;

        if (_isValid && result.Address != null)
            _toolTip.SetToolTip(this, IpAddressValidator.Describe(result.Address));
        else if (!string.IsNullOrWhiteSpace(text))
            _toolTip.SetToolTip(this, result.ErrorMessage ?? "Invalid");
        else
            _toolTip.SetToolTip(this, "");

        if (wasValid != _isValid || _isValid)
            ValidationChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
