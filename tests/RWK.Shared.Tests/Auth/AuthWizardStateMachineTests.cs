/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Auth;
using Xunit;

namespace RWK.Shared.Tests.Auth;

/// <summary>
/// Unit tests for the Tailscale Auth Wizard state machine. Verifies step transitions
/// based on provider state without any WinForms UI dependency.
/// </summary>
public sealed class AuthWizardStateMachineTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Mock provider
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class MockAuthProvider : ITailscaleAuthProvider
    {
        public TailscaleState CurrentState { get; set; } = TailscaleState.NeedsAuth;
        public string? AuthUrl { get; set; } = "https://login.tailscale.com/a/test123";
        public string? SelfAddress { get; set; }
        public string? SelfDnsName { get; set; }
        public string? TailnetName { get; set; }

        public bool SubmitKeyCalled { get; private set; }
        public string? SubmittedKey { get; private set; }
        public bool SubmitKeyThrows { get; set; }

        public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
        {
            SubmitKeyCalled = true;
            SubmittedKey = authKey;
            if (SubmitKeyThrows)
                throw new InvalidOperationException("Key rejected by sidecar.");
            return Task.CompletedTask;
        }

        public Task<TailscaleState> PollStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentState);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Initial state
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InitialStep_IsWelcome()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);

        Assert.Equal(AuthWizardStep.Welcome, sm.CurrentStep);
        Assert.False(sm.IsComplete);
    }

    [Fact]
    public void Welcome_AlreadyConnected_JumpsToSuccess()
    {
        var provider = new MockAuthProvider { CurrentState = TailscaleState.Connected };
        var sm = new AuthWizardStateMachine(provider);

        bool transitioned = sm.EvaluateState(TailscaleState.Connected);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
        Assert.True(sm.IsComplete);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Welcome → BrowserAuth
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartBrowserAuth_MovesToBrowserAuthStep()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);

        sm.StartBrowserAuth();

        Assert.Equal(AuthWizardStep.BrowserAuth, sm.CurrentStep);
        Assert.Equal(0, sm.VerifyAttempts);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  BrowserAuth transitions
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BrowserAuth_StaysInBrowserAuth_WhileNeedsAuth()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        bool transitioned = sm.EvaluateState(TailscaleState.NeedsAuth);

        Assert.False(transitioned);
        Assert.Equal(AuthWizardStep.BrowserAuth, sm.CurrentStep);
    }

    [Fact]
    public void BrowserAuth_MovesToVerifying_OnConnecting()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        bool transitioned = sm.EvaluateState(TailscaleState.Connecting);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Verifying, sm.CurrentStep);
    }

    [Fact]
    public void BrowserAuth_MovesToSuccess_OnConnected()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        bool transitioned = sm.EvaluateState(TailscaleState.Connected);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
        Assert.True(sm.IsComplete);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Verifying transitions
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Verifying_MovesToSuccess_OnConnected()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting); // → Verifying

        bool transitioned = sm.EvaluateState(TailscaleState.Connected);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
    }

    [Fact]
    public void Verifying_MovesToAuthorizationRequired_OnNeedsAuth()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting); // → Verifying

        bool transitioned = sm.EvaluateState(TailscaleState.NeedsAuth);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.AuthorizationRequired, sm.CurrentStep);
    }

    [Fact]
    public void Verifying_TimesOut_AfterMaxAttempts()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting); // → Verifying

        // Simulate polling at Connecting state for MaxVerifyAttempts
        for (int i = 0; i < AuthWizardStateMachine.MaxVerifyAttempts - 1; i++)
        {
            sm.EvaluateState(TailscaleState.Connecting);
        }
        Assert.Equal(AuthWizardStep.Verifying, sm.CurrentStep);

        // One more pushes it over
        sm.EvaluateState(TailscaleState.Connecting);
        Assert.Equal(AuthWizardStep.AuthorizationRequired, sm.CurrentStep);
    }

    [Fact]
    public void Verifying_IncrementsAttemptCounter()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting); // → Verifying

        Assert.Equal(0, sm.VerifyAttempts);
        sm.EvaluateState(TailscaleState.Connecting);
        Assert.Equal(1, sm.VerifyAttempts);
        sm.EvaluateState(TailscaleState.Connecting);
        Assert.Equal(2, sm.VerifyAttempts);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  AuthorizationRequired transitions
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorizationRequired_MovesToSuccess_WhenApproved()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting);
        sm.EvaluateState(TailscaleState.NeedsAuth); // → AuthorizationRequired

        bool transitioned = sm.EvaluateState(TailscaleState.Connected);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
    }

    [Fact]
    public void AuthorizationRequired_MovesToVerifying_OnConnecting()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting);
        sm.EvaluateState(TailscaleState.NeedsAuth); // → AuthorizationRequired

        bool transitioned = sm.EvaluateState(TailscaleState.Connecting);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Verifying, sm.CurrentStep);
        Assert.Equal(0, sm.VerifyAttempts); // Reset on re-entry
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Auth key submission
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAuthKey_Success_MovesToVerifying()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting);
        sm.EvaluateState(TailscaleState.NeedsAuth); // → AuthorizationRequired

        string? error = await sm.SubmitAuthKeyAsync("tskey-auth-test123");

        Assert.Null(error);
        Assert.Equal(AuthWizardStep.Verifying, sm.CurrentStep);
        Assert.True(provider.SubmitKeyCalled);
        Assert.Equal("tskey-auth-test123", provider.SubmittedKey);
    }

    [Fact]
    public async Task SubmitAuthKey_Failure_ReturnsErrorMessage()
    {
        var provider = new MockAuthProvider { SubmitKeyThrows = true };
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting);
        sm.EvaluateState(TailscaleState.NeedsAuth); // → AuthorizationRequired

        string? error = await sm.SubmitAuthKeyAsync("tskey-auth-bad");

        Assert.NotNull(error);
        Assert.Contains("rejected", error);
        // Should stay on AuthorizationRequired — not transition
        Assert.Equal(AuthWizardStep.AuthorizationRequired, sm.CurrentStep);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Retry
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RetryAuth_ResetsToLBrowserAuth()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting);
        sm.EvaluateState(TailscaleState.NeedsAuth); // → AuthorizationRequired

        sm.RetryAuth();

        Assert.Equal(AuthWizardStep.BrowserAuth, sm.CurrentStep);
        Assert.Equal(0, sm.VerifyAttempts);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  PollAndTransitionAsync
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAndTransition_ReadsProviderState()
    {
        var provider = new MockAuthProvider { CurrentState = TailscaleState.NeedsAuth };
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        // Provider is still NeedsAuth — no transition
        bool transitioned = await sm.PollAndTransitionAsync();
        Assert.False(transitioned);

        // Now provider reports Connected
        provider.CurrentState = TailscaleState.Connected;
        transitioned = await sm.PollAndTransitionAsync();
        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Success is terminal
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Success_IsTerminal_NoFurtherTransitions()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connected); // → Success

        // Even if state changes, success is terminal
        bool transitioned = sm.EvaluateState(TailscaleState.NeedsAuth);
        Assert.False(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
    }
}
