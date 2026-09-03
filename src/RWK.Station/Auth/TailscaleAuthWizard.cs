/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
// This file is functionally identical to the Client's TailscaleAuthWizard.
// Both use the shared AuthWizardStateMachine from RWK.Shared.Auth.
// Kept as a separate file per-project because WinForms Form subclasses
// cannot live in the net9.0 RWK.Shared library (no WinForms reference).

using System.Diagnostics;
using RWK.Shared;
using RWK.Shared.Auth;

namespace RWK.Station.Auth;

/// <summary>
/// Tailscale Authentication Wizard for the Station application.
/// Identical behavior to the Client version — guides the user through
/// browser OAuth, verifies connection, handles authorization failures,
/// and warns about key expiry.
/// </summary>
/// <remarks>
/// The wizard never runs its own poll timer. It subscribes to
/// <see cref="ITailscaleAuthProvider.StateChanged"/> (forwarded from the host-owned
/// single poller) and drives all transitions through <see cref="AuthWizardStateMachine"/>,
/// keeping the host the single source of truth (Requirements 2.1, 2.2). State-change events
/// raised on the poll thread are marshalled onto the UI thread with <c>BeginInvoke</c>
/// (Requirement 2.6).
/// </remarks>
public sealed class TailscaleAuthWizard : Form
{
    private readonly AuthWizardStateMachine _stateMachine;
    private readonly ITailscaleAuthProvider _provider;
    private bool _seeded;

    private Label _titleLabel = null!;
    private TextBox _contentBox = null!;
    private Label _statusLabel = null!;
    private Button _primaryButton = null!;
    private Button _secondaryButton = null!;
    private Button _closeButton = null!;
    private TextBox? _authKeyBox;
    private ProgressBar _progressBar = null!;

    public bool AuthSucceeded => _stateMachine.IsComplete;

    public TailscaleAuthWizard(ITailscaleAuthProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _stateMachine = new AuthWizardStateMachine(provider);
        InitializeLayout();
        ShowStep(_stateMachine.CurrentStep);
    }

    private void InitializeLayout()
    {
        Text = "RWK Tailscale Authentication";
        Size = new Size(560, 420);
        MinimumSize = new Size(500, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _titleLabel = new Label
        {
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Location = new Point(20, 16),
            Size = new Size(510, 30),
            AutoSize = false
        };
        Controls.Add(_titleLabel);

        _contentBox = new TextBox
        {
            Location = new Point(20, 52),
            Size = new Size(510, 210),
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Font = new Font("Segoe UI", 9.5f),
            TabStop = false
        };
        Controls.Add(_contentBox);

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 270),
            Size = new Size(510, 8),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Visible = false
        };
        Controls.Add(_progressBar);

        _statusLabel = new Label
        {
            Location = new Point(20, 285),
            Size = new Size(510, 22),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            ForeColor = SystemColors.GrayText
        };
        Controls.Add(_statusLabel);

        _authKeyBox = new TextBox
        {
            Location = new Point(20, 310),
            Size = new Size(380, 24),
            PlaceholderText = "Paste pre-auth key here (tskey-auth-...)",
            Visible = false
        };
        Controls.Add(_authKeyBox);

        _primaryButton = new Button
        {
            Size = new Size(130, 32),
            Location = new Point(20, 345),
            UseVisualStyleBackColor = true
        };
        _primaryButton.Click += OnPrimaryClick;
        Controls.Add(_primaryButton);

        _secondaryButton = new Button
        {
            Size = new Size(150, 32),
            Location = new Point(160, 345),
            UseVisualStyleBackColor = true,
            Visible = false
        };
        _secondaryButton.Click += OnSecondaryClick;
        Controls.Add(_secondaryButton);

        _closeButton = new Button
        {
            Text = "Cancel",
            Size = new Size(80, 32),
            Location = new Point(450, 345),
            UseVisualStyleBackColor = true
        };
        _closeButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_closeButton);

        FormClosing += OnFormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Subscribe to the host-owned poller's state-change events. Subscribing on Load
        // guarantees the form handle exists before any BeginInvoke marshaling occurs.
        _provider.StateChanged += OnProviderStateChanged;

        // One-time seed: a state that already arrived before the wizard opened must not be
        // missed. Evaluate the current host snapshot once and refresh the step if it moved.
        if (!_seeded)
        {
            _seeded = true;
            bool transitioned = _stateMachine.EvaluateState(_provider.CurrentState);
            _statusLabel.Text = _stateMachine.StatusMessage;
            if (transitioned)
            {
                ShowStep(_stateMachine.CurrentStep);
            }
        }
    }

    /// <summary>
    /// Handles host state-change events. Raised on the host poll thread, so marshal to the
    /// UI thread with <c>BeginInvoke</c> (never synchronous <c>Invoke</c>) to avoid blocking
    /// the poll thread during the interactive login window (Requirement 2.6).
    /// </summary>
    private void OnProviderStateChanged(object? sender, TailscaleStateChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => OnProviderStateChanged(sender, e)));
            }
            return;
        }

        bool transitioned = _stateMachine.EvaluateState(e.State);
        _statusLabel.Text = _stateMachine.StatusMessage;
        if (transitioned)
        {
            ShowStep(_stateMachine.CurrentStep);
        }
    }

    private void ShowStep(AuthWizardStep step)
    {
        _progressBar.Visible = false;
        _authKeyBox!.Visible = false;
        _secondaryButton.Visible = false;
        _statusLabel.Text = _stateMachine.StatusMessage;

        switch (step)
        {
            case AuthWizardStep.Welcome:
                _titleLabel.Text = "Tailscale Network Authentication";
                _contentBox.Text =
                    "RWK uses a private Tailscale mesh network to connect your Client " +
                    "and Station PCs. This is YOUR private network \u2014 no data passes through " +
                    "RWK servers or any third party.\r\n\r\n" +
                    "To get started, you need a Tailscale account (free at tailscale.com). " +
                    "You'll sign in via your web browser.\r\n\r\n" +
                    "IMPORTANT: Use an account that has ADMIN access to your Tailnet. " +
                    "If you log in with a non-admin email, the device won't be approved " +
                    "automatically and you'll need to approve it manually in the admin console.\r\n\r\n" +
                    "If you already have a Tailscale account and this device was previously " +
                    "authenticated, click Continue \u2014 it may reconnect without needing to log in again.";
                _primaryButton.Text = "Continue";
                _closeButton.Text = "Cancel";
                break;

            case AuthWizardStep.BrowserAuth:
                _titleLabel.Text = "Sign In via Browser";
                string authUrl = _provider.AuthUrl ?? "https://login.tailscale.com";
                _contentBox.Text =
                    "Click 'Open Browser' to sign in to Tailscale. A browser window will open " +
                    "where you can log in with your Google, Microsoft, GitHub, or email account.\r\n\r\n" +
                    "After you sign in, this wizard will detect the authentication automatically. " +
                    "Do NOT close this window while authenticating.\r\n\r\n" +
                    "If the browser does not open, copy this URL and paste it into your browser:\r\n" +
                    $"{authUrl}";
                _primaryButton.Text = "Open Browser";
                _progressBar.Visible = true;
                _statusLabel.Text = "Waiting for browser login...";
                break;

            case AuthWizardStep.Verifying:
                _titleLabel.Text = "Verifying Connection";
                _contentBox.Text =
                    "Authentication received! Now verifying that this device can reach the tailnet.\r\n\r\n" +
                    "This usually takes a few seconds. If it takes longer than 30 seconds, " +
                    "there may be a firewall or network issue.";
                _primaryButton.Text = "Waiting...";
                _primaryButton.Enabled = false;
                _progressBar.Visible = true;
                break;

            case AuthWizardStep.AuthorizationRequired:
                _titleLabel.Text = "Device Authorization Required";
                _contentBox.Text =
                    "Your browser login completed, but this device is NOT yet authorized " +
                    "on your tailnet. This usually means:\r\n\r\n" +
                    "  \u2022 You logged in with a non-admin email address, OR\r\n" +
                    "  \u2022 Your tailnet requires manual device approval\r\n\r\n" +
                    "To fix this, open the Tailscale admin console and approve this device:\r\n" +
                    "  1. Go to https://login.tailscale.com/admin/machines\r\n" +
                    "  2. Find the new device (it may show as 'Awaiting approval')\r\n" +
                    "  3. Click the \u22ee menu \u2192 Approve\r\n\r\n" +
                    "Alternatively, an admin can generate a pre-auth key:\r\n" +
                    "  Admin Console \u2192 Settings \u2192 Keys \u2192 Generate auth key\r\n" +
                    "Paste the key below and click Submit.";
                _primaryButton.Text = "Open Admin Page";
                _primaryButton.Enabled = true;
                _secondaryButton.Text = "Submit Auth Key";
                _secondaryButton.Visible = true;
                _authKeyBox!.Visible = true;
                _progressBar.Visible = true;
                _statusLabel.Text = "Waiting for device approval...";
                break;

            case AuthWizardStep.Success:
                _titleLabel.Text = "\u2713 Connected Successfully";
                string selfAddr = _provider.SelfAddress ?? "unknown";
                string selfDns = _provider.SelfDnsName ?? "unknown";
                _contentBox.Text =
                    $"Your device is now connected to the Tailscale network!\r\n\r\n" +
                    $"  Tailscale IP:  {selfAddr}\r\n" +
                    $"  Hostname:      {selfDns}\r\n\r\n" +
                    "IMPORTANT \u2014 Key Expiry:\r\n\r\n" +
                    "Tailscale authentication keys expire after 90 days by default. When they " +
                    "expire, RWK will stop connecting until you re-authenticate.\r\n\r\n" +
                    "To prevent this:\r\n" +
                    "  1. Go to https://login.tailscale.com/admin/machines\r\n" +
                    "  2. Find this device \u2192 click the \u22ee menu\r\n" +
                    "  3. Select 'Disable key expiry'\r\n\r\n" +
                    "This is strongly recommended for unattended station PCs.";
                _primaryButton.Text = "Open Admin Page";
                _primaryButton.Enabled = true;
                _secondaryButton.Text = "Done";
                _secondaryButton.Visible = true;
                _closeButton.Text = "Done";
                _progressBar.Visible = false;
                _authKeyBox!.Visible = false;
                _statusLabel.Text = "Authentication complete.";
                break;
        }
    }

    private void OnPrimaryClick(object? sender, EventArgs e)
    {
        switch (_stateMachine.CurrentStep)
        {
            case AuthWizardStep.Welcome:
                _stateMachine.StartBrowserAuth();
                ShowStep(_stateMachine.CurrentStep);
                break;

            case AuthWizardStep.BrowserAuth:
            case AuthWizardStep.AuthorizationRequired:
            case AuthWizardStep.Success:
                string url = _stateMachine.CurrentStep == AuthWizardStep.BrowserAuth
                    ? (_provider.AuthUrl ?? "https://login.tailscale.com")
                    : "https://login.tailscale.com/admin/machines";
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch { _statusLabel.Text = "Could not open browser. Copy the URL manually."; }
                break;
        }
    }

    private void OnSecondaryClick(object? sender, EventArgs e)
    {
        switch (_stateMachine.CurrentStep)
        {
            case AuthWizardStep.AuthorizationRequired:
                string key = _authKeyBox?.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(key))
                {
                    _statusLabel.Text = "Please paste an auth key first.";
                    return;
                }
                _ = SubmitKeyAsync(key);
                break;

            case AuthWizardStep.Success:
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }

    private async Task SubmitKeyAsync(string key)
    {
        _secondaryButton.Enabled = false;
        _statusLabel.Text = "Submitting auth key...";
        string? error = await _stateMachine.SubmitAuthKeyAsync(key, CancellationToken.None);
        if (error is not null)
        {
            _statusLabel.Text = $"Key submission failed: {error}";
            _secondaryButton.Enabled = true;
        }
        else
        {
            ShowStep(_stateMachine.CurrentStep);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e) =>
        _provider.StateChanged -= OnProviderStateChanged;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _provider.StateChanged -= OnProviderStateChanged;
        base.Dispose(disposing);
    }
}
