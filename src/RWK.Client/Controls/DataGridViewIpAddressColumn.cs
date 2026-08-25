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
/// A DataGridView column that validates IP address input (both IPv4 and IPv6) with
/// visual feedback. Extends the TextBox column with live validation and red background
/// on invalid entries.
/// </summary>
public sealed class DataGridViewIpAddressColumn : DataGridViewTextBoxColumn
{
    /// <summary>Preset addresses shown in a dropdown when the cell enters edit mode.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Presets { get; set; } = new[] { "127.0.0.1", "0.0.0.0", "::1", "::" };

    public DataGridViewIpAddressColumn()
    {
        CellTemplate = new DataGridViewIpAddressCell();
    }
}

/// <summary>
/// A DataGridView cell that validates its value as an IP address and shows red background
/// when invalid. Supports both IPv4 and IPv6.
/// </summary>
public sealed class DataGridViewIpAddressCell : DataGridViewTextBoxCell
{
    private static readonly Color ErrorBackColor = Color.FromArgb(255, 230, 230);

    protected override void Paint(
        Graphics graphics,
        Rectangle clipBounds,
        Rectangle cellBounds,
        int rowIndex,
        DataGridViewElementStates cellState,
        object? value,
        object? formattedValue,
        string? errorText,
        DataGridViewCellStyle cellStyle,
        DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        // Validate the cell value and adjust background for invalid addresses.
        string? text = formattedValue?.ToString();
        if (!string.IsNullOrWhiteSpace(text) && !IPAddress.TryParse(text, out _))
        {
            cellStyle = cellStyle.Clone();
            cellStyle.BackColor = ErrorBackColor;
            cellStyle.SelectionBackColor = Color.FromArgb(255, 180, 180);
        }

        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState,
            value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
    }

    public override object Clone()
    {
        return base.Clone();
    }
}
