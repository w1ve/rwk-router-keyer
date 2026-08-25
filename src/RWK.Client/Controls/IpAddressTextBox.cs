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

namespace RWK.Client.Controls;

/// <summary>
/// A TextBox that live-validates IPv4 and IPv6 address input with a v4/v6 mode toggle.
/// Shows invalid state visually (red background + tooltip with reason).
/// Exposes <see cref="IsValid"/> and <see cref="TryGetAddress"/> for callers.
/// </summary>
/// <remarks>
/// The toggle switch on the right side of the control selects between IPv4-only, IPv6-only,
/// or Both modes. When in a restricted mode, addresses of the wrong family are rejected with
/// a clear error message.
/// </remarks>
public sealed class IpAddressTextBox : UserControl
{
    private readonly TextBox _textBox;
    private readonly ComboBox _modeCombo;
    private readonly ToolTip _toolTip;

    private static readonly Color ErrorBackColor = Color.FromArgb(255, 230, 230);
    private static readonly Color NormalBackColor = SystemColors.Window;

    private IpAddressMode _mode = IpAddressMode.Both;
    private IPAddress? _parsedAddress;
    private bool _isValid;
    private string? _errorMessage;

    /// <summary>Fired when the validation state changes.</summary>
    public event EventHandler? ValidationChanged;

    /// <summary>Whether the current text is a valid IP address for the selected mode.</summary>
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
            Revalidate();
        }
    }

    /// <summary>Gets or sets the IP address text.</summary>
    [Browsable(true)]
    public override string Text
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }

    /// <summary>Gets or sets placeholder text shown when the control is empty.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _textBox.PlaceholderText;
        set => _textBox.PlaceholderText = value;
    }

    /// <summary>
    /// Attempts to get the parsed IP address.
    /// </summary>
    /// <param name="address">The parsed address if valid.</param>
    /// <returns>True if valid.</returns>
    public bool TryGetAddress(out IPAddress? address)
    {
        address = _parsedAddress;
        return _isValid;
    }

    public IpAddressTextBox()
    {
        _toolTip = new ToolTip { InitialDelay = 200, ReshowDelay = 100 };

        _modeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "v4/v6", "v4", "v6" },
            SelectedIndex = 0,
            Width = 48,
            Dock = DockStyle.Right,
            Font = new Font("Segoe UI", 7.5F),
        };
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            _mode = (IpAddressMode)_modeCombo.SelectedIndex;
            Revalidate();
        };

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
        };
        _textBox.TextChanged += (_, _) => Revalidate();

        // Layout: textbox fills space, mode combo on the right
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = NormalBackColor;
        Height = _textBox.PreferredHeight + 2;
        Controls.Add(_textBox);
        Controls.Add(_modeCombo);

        // Propagate focus
        GotFocus += (_, _) => _textBox.Focus();
    }

    /// <summary>
    /// Sets the text without triggering validation events (for initialization).
    /// </summary>
    public void SetTextQuiet(string text)
    {
        _textBox.TextChanged -= OnTextChanged;
        _textBox.Text = text;
        _textBox.TextChanged += OnTextChanged;
        Revalidate();
    }

    private void OnTextChanged(object? sender, EventArgs e) => Revalidate();

    private void Revalidate()
    {
        var result = IpAddressValidator.Validate(_textBox.Text, _mode);
        bool wasValid = _isValid;
        _isValid = result.IsValid;
        _parsedAddress = result.Address;
        _errorMessage = result.ErrorMessage;

        // Visual feedback
        if (string.IsNullOrWhiteSpace(_textBox.Text))
        {
            // Empty = neutral (no error shown)
            _textBox.BackColor = NormalBackColor;
            BackColor = NormalBackColor;
            _toolTip.SetToolTip(_textBox, "");
        }
        else if (_isValid)
        {
            _textBox.BackColor = NormalBackColor;
            BackColor = NormalBackColor;
            _toolTip.SetToolTip(_textBox, IpAddressValidator.Describe(result.Address!));
        }
        else
        {
            _textBox.BackColor = ErrorBackColor;
            BackColor = ErrorBackColor;
            _toolTip.SetToolTip(_textBox, _errorMessage ?? "Invalid address");
        }

        if (wasValid != _isValid)
            ValidationChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
