/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Auth;

/// <summary>
/// The steps of the Tailscale Authentication Wizard.
/// </summary>
public enum AuthWizardStep
{
    /// <summary>Step 1: Welcome — explains what Tailscale is and why auth is needed.</summary>
    Welcome,

    /// <summary>Step 2: Browser OAuth — user clicks to open browser, wizard polls for completion.</summary>
    BrowserAuth,

    /// <summary>Step 3: Verify — checks if the node actually connected after auth.</summary>
    Verifying,

    /// <summary>Step 4: Authorization Required — device not approved, guide user to admin console.</summary>
    AuthorizationRequired,

    /// <summary>Step 5: Success — connected, show details and key expiry warning.</summary>
    Success
}

/// <summary>
/// Testable state machine for the Tailscale Auth Wizard. Drives step transitions
/// based on provider state without touching any UI.
/// </summary>
/// <remarks>
/// The wizard UI (<see cref="TailscaleAuthWizard"/>) delegates all transition decisions
/// to this class. Tests exercise transitions by providing a mock
/// <see cref="ITailscaleAuthProvider"/>.
/// </remarks>
public sealed class AuthWizardStateMachine
{
    private readonly ITailscaleAuthProvider _provider;

    /// <summary>Current step the wizard is on.</summary>
    public AuthWizardStep CurrentStep { get; private set; } = AuthWizardStep.Welcome;

    /// <summary>Diagnostic message for display — set on transitions.</summary>
    public string StatusMessage { get; private set; } = "";

    /// <summary>Whether the wizard has completed successfully.</summary>
    public bool IsComplete => CurrentStep == AuthWizardStep.Success;

    /// <summary>Number of poll attempts made during verification.</summary>
    public int VerifyAttempts { get; private set; }

    /// <summary>Maximum verification attempts before giving up (30 × 2s = 60s).</summary>
    public const int MaxVerifyAttempts = 30;

    public AuthWizardStateMachine(ITailscaleAuthProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Advances to the BrowserAuth step. Called when the user clicks "Continue" from Welcome.
    /// </summary>
    public void StartBrowserAuth()
    {
        CurrentStep = AuthWizardStep.BrowserAuth;
        StatusMessage = "Waiting for browser login...";
        VerifyAttempts = 0;
    }

    /// <summary>
    /// Evaluates the current provider state and transitions steps as needed.
    /// This is the single event-driven entry point: the wizard subscribes to the
    /// host-owned poller's <see cref="ITailscaleAuthProvider.StateChanged"/> event and
    /// feeds each reported state here, keeping the host poller the sole source of truth
    /// (Requirement 2.2). There is no self-poll driver.
    /// </summary>
    /// <param name="state">The current Tailscale state.</param>
    /// <returns>True if a step transition occurred.</returns>
    public bool EvaluateState(TailscaleState state)
    {
        var previousStep = CurrentStep;

        switch (CurrentStep)
        {
            case AuthWizardStep.BrowserAuth:
                if (state == TailscaleState.Connected)
                {
                    CurrentStep = AuthWizardStep.Success;
                    StatusMessage = "Connected to tailnet.";
                }
                else if (state == TailscaleState.Connecting)
                {
                    // Auth completed, now connecting — move to verify
                    CurrentStep = AuthWizardStep.Verifying;
                    StatusMessage = "Authentication received, connecting to tailnet...";
                    VerifyAttempts = 0;
                }
                else if (state == TailscaleState.NeedsAuth)
                {
                    StatusMessage = _provider.AuthUrl is not null
                        ? "Waiting for browser login..."
                        : "Waiting for authentication...";
                }
                break;

            case AuthWizardStep.Verifying:
                VerifyAttempts++;
                if (state == TailscaleState.Connected)
                {
                    CurrentStep = AuthWizardStep.Success;
                    StatusMessage = "Connected to tailnet.";
                }
                else if (state == TailscaleState.NeedsAuth)
                {
                    // Reverted to NeedsAuth — device not approved
                    CurrentStep = AuthWizardStep.AuthorizationRequired;
                    StatusMessage = "Device is not yet authorized on your tailnet.";
                }
                else if (VerifyAttempts >= MaxVerifyAttempts)
                {
                    // Timeout — likely stuck in Connecting or Fault
                    CurrentStep = AuthWizardStep.AuthorizationRequired;
                    StatusMessage = state == TailscaleState.Fault
                        ? "Connection failed. Check your internet connection and firewall."
                        : "Timed out waiting for connection. The device may need manual approval.";
                }
                else
                {
                    StatusMessage = $"Connecting... ({VerifyAttempts}/{MaxVerifyAttempts})";
                }
                break;

            case AuthWizardStep.AuthorizationRequired:
                // User may have approved the device in the admin console — keep polling
                if (state == TailscaleState.Connected)
                {
                    CurrentStep = AuthWizardStep.Success;
                    StatusMessage = "Device approved! Connected to tailnet.";
                }
                else if (state == TailscaleState.Connecting)
                {
                    CurrentStep = AuthWizardStep.Verifying;
                    StatusMessage = "Authorization received, connecting...";
                    VerifyAttempts = 0;
                }
                break;

            case AuthWizardStep.Success:
                // Terminal state — no further transitions
                break;

            case AuthWizardStep.Welcome:
                // User hasn't started yet — check if already connected
                if (state == TailscaleState.Connected)
                {
                    CurrentStep = AuthWizardStep.Success;
                    StatusMessage = "Already connected to tailnet.";
                }
                break;
        }

        return CurrentStep != previousStep;
    }

    /// <summary>
    /// Submits a pre-auth key and transitions to Verifying.
    /// Called when the user pastes an auth key in Step 4.
    /// </summary>
    /// <param name="authKey">The pre-auth key to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Null on success, or an error message on failure.</returns>
    public async Task<string?> SubmitAuthKeyAsync(string authKey, CancellationToken ct = default)
    {
        try
        {
            await _provider.SubmitAuthKeyAsync(authKey, ct).ConfigureAwait(false);
            CurrentStep = AuthWizardStep.Verifying;
            StatusMessage = "Auth key submitted, connecting...";
            VerifyAttempts = 0;
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Auth key submission failed: {ex.Message}";
            return ex.Message;
        }
    }

    /// <summary>
    /// Resets the state machine back to BrowserAuth for a retry.
    /// </summary>
    public void RetryAuth()
    {
        CurrentStep = AuthWizardStep.BrowserAuth;
        StatusMessage = "Retrying authentication...";
        VerifyAttempts = 0;
    }
}
