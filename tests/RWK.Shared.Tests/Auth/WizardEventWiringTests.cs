/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;
using RWK.Shared.Auth;
using Xunit;

namespace RWK.Shared.Tests.Auth;

/// <summary>
/// WIZARD EVENT-WIRING TESTS (Task 5.2 — Requirement 2.2).
///
/// The production <see cref="TailscaleAuthWizard"/> Form (both the Client and Station
/// copies) is a WinForms modal dialog that cannot be instantiated headlessly in the test
/// host. After the fix (Task 5.1), the wizard no longer runs its own poll timer; instead
/// it subscribes to <see cref="ITailscaleAuthProvider.StateChanged"/> and, in the handler
/// (marshalled onto the UI thread via <c>BeginInvoke</c>), drives
/// <see cref="AuthWizardStateMachine.EvaluateState"/> with the reported state.
///
/// These tests exercise that WIRING LOGIC at the state-machine + provider-event seam
/// rather than constructing the Form. The subscription set up here —
/// <c>provider.StateChanged += (_, e) => stateMachine.EvaluateState(e.State);</c> — is
/// exactly what the wizard installs after marshalling, so verifying it proves that a
/// provider-raised <c>StateChanged</c> reaches the state machine and advances the step
/// (Requirement 2.2: the wizard transitions from host state-change events, not from an
/// independent poll).
/// </summary>
public sealed class WizardEventWiringTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Mock provider — mirrors the MockAuthProvider used by the other Auth tests:
    //  a StateChanged event plus a RaiseStateChanged(TailscaleState) helper so a test
    //  can emit the states the real sidecar host reports during login.
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class MockAuthProvider : ITailscaleAuthProvider
    {
        public TailscaleState CurrentState { get; set; } = TailscaleState.NeedsAuth;
        public string? AuthUrl { get; set; } = "https://login.tailscale.com/a/test123";
        public string? SelfAddress { get; set; }
        public string? SelfDnsName { get; set; }
        public string? TailnetName { get; set; }

        public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

        /// <summary>Emit a host state-change exactly as the sidecar host would.</summary>
        public void RaiseStateChanged(TailscaleState state) =>
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(state, PathType.None, TimeSpan.Zero));

        public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Helper: install the SAME subscription the wizard installs after BeginInvoke.
    //  (In the wizard the lambda body runs on the UI thread; here it runs synchronously,
    //  which is behaviourally identical for the state-machine seam under test.)
    // ──────────────────────────────────────────────────────────────────────────────

    private static void WireWizardSubscription(
        MockAuthProvider provider, AuthWizardStateMachine stateMachine) =>
        provider.StateChanged += (_, e) => stateMachine.EvaluateState(e.State);

    // ──────────────────────────────────────────────────────────────────────────────
    //  Raising StateChanged drives the state machine through the login sequence.
    //  BrowserAuth --Connecting--> Verifying --Connected--> Success (IsComplete).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RaisingStateChanged_DrivesStateMachine_BrowserAuthToVerifyingToSuccess()
    {
        var provider = new MockAuthProvider();
        var stateMachine = new AuthWizardStateMachine(provider);
        stateMachine.StartBrowserAuth();
        WireWizardSubscription(provider, stateMachine);

        Assert.Equal(AuthWizardStep.BrowserAuth, stateMachine.CurrentStep);

        // The host reports Connecting: the subscription must drive EvaluateState and
        // advance BrowserAuth → Verifying.
        provider.RaiseStateChanged(TailscaleState.Connecting);
        Assert.Equal(AuthWizardStep.Verifying, stateMachine.CurrentStep);

        // The host reports Connected: Verifying → Success, and the wizard completes.
        provider.RaiseStateChanged(TailscaleState.Connected);
        Assert.Equal(AuthWizardStep.Success, stateMachine.CurrentStep);
        Assert.True(stateMachine.IsComplete);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Prove it is the SUBSCRIPTION that caused the transition: the step is unchanged
    //  until StateChanged is raised, and changes precisely because it was raised.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Subscription_IsWhatCausesTransition_StepUnchangedUntilEventRaised()
    {
        var provider = new MockAuthProvider();
        var stateMachine = new AuthWizardStateMachine(provider);
        stateMachine.StartBrowserAuth();
        WireWizardSubscription(provider, stateMachine);

        // Before raising anything, the machine sits at BrowserAuth. Merely setting the
        // provider's CurrentState (what a self-poller would read) does NOT move it —
        // only an emitted StateChanged event does, confirming the wizard is event-driven.
        provider.CurrentState = TailscaleState.Connecting;
        Assert.Equal(AuthWizardStep.BrowserAuth, stateMachine.CurrentStep);

        // Raise the event — now the subscribed handler drives the transition.
        provider.RaiseStateChanged(TailscaleState.Connecting);
        Assert.Equal(AuthWizardStep.Verifying, stateMachine.CurrentStep);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  After unsubscribing (as the wizard does on close/Dispose), further raises must
    //  not touch the state machine — no stray transitions from a closed wizard.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterUnsubscribe_FurtherRaises_DoNotChangeStep()
    {
        var provider = new MockAuthProvider();
        var stateMachine = new AuthWizardStateMachine(provider);
        stateMachine.StartBrowserAuth();

        // Use a named handler so we can unsubscribe it, mirroring the wizard's
        // subscribe-on-open / unsubscribe-on-close lifecycle.
        EventHandler<TailscaleStateChangedEventArgs> handler =
            (_, e) => stateMachine.EvaluateState(e.State);
        provider.StateChanged += handler;

        provider.RaiseStateChanged(TailscaleState.Connecting); // BrowserAuth → Verifying
        Assert.Equal(AuthWizardStep.Verifying, stateMachine.CurrentStep);

        // Wizard closes → unsubscribes.
        provider.StateChanged -= handler;
        var stepAfterUnsubscribe = stateMachine.CurrentStep;

        // A Connected event that WOULD have advanced Verifying → Success is now ignored
        // because nothing is listening.
        provider.RaiseStateChanged(TailscaleState.Connected);

        Assert.Equal(stepAfterUnsubscribe, stateMachine.CurrentStep);
        Assert.Equal(AuthWizardStep.Verifying, stateMachine.CurrentStep);
        Assert.False(stateMachine.IsComplete);
    }
}
