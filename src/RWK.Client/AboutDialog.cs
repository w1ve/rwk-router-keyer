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

/// <summary>
/// About dialog for the RWK Client application.
/// </summary>
internal sealed class AboutDialog : Form
{
    public AboutDialog()
    {
        Text = "About RWK";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(420, 480);
        ShowInTaskbar = false;

        // Splash image at the top
        var pictureBox = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 200,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = SystemColors.Window
        };

        try
        {
            string splashPath = Path.Combine(AppContext.BaseDirectory, "splash.png");
            if (File.Exists(splashPath))
                pictureBox.Image = Image.FromFile(splashPath);
        }
        catch { /* No splash image — leave blank */ }

        // Text panel
        var textPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 10, 20, 10)
        };

        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 10F),
            Text = $"RWK Router/Keyer Version {MainForm.AppVersion}\n\n" +
                   "Copyright (c) 2026 by Gerry Hull, W1VE\n\n" +
                   "Released under the MIT License.\n\n" +
                   "Free and open-source software.\n\n" +
                   "Source at https://github.com/w1ve/rwk"
        };

        textPanel.Controls.Add(label);

        // OK button at the bottom
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30),
            UseVisualStyleBackColor = true
        };
        okButton.Location = new Point((buttonPanel.Width - okButton.Width) / 2, 8);
        okButton.Anchor = AnchorStyles.Top;

        buttonPanel.Controls.Add(okButton);
        AcceptButton = okButton;

        Controls.Add(textPanel);
        Controls.Add(pictureBox);
        Controls.Add(buttonPanel);
    }
}
