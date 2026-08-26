/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Config;

namespace RWK.Client.Controls;

/// <summary>
/// Modal dialog for importing a Station. The user pastes the Station Info string
/// (copied from the Station's "Copy Station Info to Clipboard" menu item) and
/// provides a friendly name (max 20 characters).
/// </summary>
public sealed class ImportStationDialog : Form
{
    private readonly TextBox _pasteBox;
    private readonly TextBox _nameBox;
    private readonly Label _statusLabel;
    private readonly Button _okBtn;
    private readonly Button _cancelBtn;

    /// <summary>The resulting station entry if dialog was accepted.</summary>
    public StationEntry? Result { get; private set; }

    public ImportStationDialog()
    {
        Text = "Import Station";
        Size = new Size(420, 240);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var pasteLabel = new Label
        {
            Text = "Paste Station Info (from Station menu → Copy Station Info):",
            Location = new Point(12, 14),
            AutoSize = true
        };

        _pasteBox = new TextBox
        {
            Location = new Point(12, 36),
            Size = new Size(380, 23),
            PlaceholderText = "100.x.x.x|ABCD1234"
        };

        var nameLabel = new Label
        {
            Text = "Station Name (max 20 chars):",
            Location = new Point(12, 70),
            AutoSize = true
        };

        _nameBox = new TextBox
        {
            Location = new Point(12, 92),
            Size = new Size(200, 23),
            MaxLength = StationEntry.MaxNameLength,
            PlaceholderText = "e.g. Home Station"
        };

        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(12, 124),
            Size = new Size(380, 20),
            ForeColor = Color.FromArgb(200, 0, 0)
        };

        _okBtn = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(230, 160),
            Size = new Size(75, 28),
            Enabled = false
        };

        _cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(310, 160),
            Size = new Size(75, 28)
        };

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;

        Controls.AddRange(new Control[] { pasteLabel, _pasteBox, nameLabel, _nameBox, _statusLabel, _okBtn, _cancelBtn });

        _pasteBox.TextChanged += (_, _) => Validate();
        _nameBox.TextChanged += (_, _) => Validate();
    }

    private void Validate()
    {
        string name = _nameBox.Text.Trim();
        bool parsed = StationEntry.TryParseClipboard(_pasteBox.Text, out string ip, out string key);

        if (!parsed && _pasteBox.Text.Trim().Length > 0)
        {
            _statusLabel.Text = "Invalid format. Expected: TailscaleIP|PairingKey";
            _okBtn.Enabled = false;
            return;
        }

        if (name.Length == 0)
        {
            _statusLabel.Text = parsed ? $"Parsed: {ip} — enter a name." : "";
            _okBtn.Enabled = false;
            return;
        }

        if (parsed)
        {
            _statusLabel.Text = $"Ready: {name} → {ip}";
            _statusLabel.ForeColor = Color.Green;
            Result = new StationEntry(name, ip, key);
            _okBtn.Enabled = true;
        }
        else
        {
            _statusLabel.Text = "";
            _statusLabel.ForeColor = Color.FromArgb(200, 0, 0);
            _okBtn.Enabled = false;
        }
    }
}
