/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared;
using RWK.Shared.Auth;
using Xunit;

namespace RWK.Shared.Tests.Auth;

/// <summary>
/// PRESERVATION TESTS (Task 2 — Property 2: Preservation).
///
/// Captures the baseline behavior that the tailscale-auth-redesign fix MUST NOT change
/// for non-buggy event sequences (Requirements 3.1, 3.2, 3.5). Following the
/// observation-first methodology, these tests were written against the UNFIXED
/// <see cref="AuthWizardStateMachine.EvaluateState"/> and are EXPECTED TO PASS on the
/// unfixed code — they encode the terminal behavior the fix must preserve.
///
/// Observed on the unfixed state machine:
///   - Authorized reconnect: from <see cref="AuthWizardStep.Welcome"/>, a lone
///     <see cref="TailscaleState.Connected"/> event (no auth URL) drives the machine to
///     <see cref="AuthWizardStep.Success"/> (Requirements 3.1, 3.2).
///   - Genuine fault: a <see cref="TailscaleState.Fault"/> while in
///     <see cref="AuthWizardStep.Verifying"/>, once <see cref="AuthWizardStateMachine.MaxVerifyAttempts"/>
///     is reached, drives to <see cref="AuthWizardStep.AuthorizationRequired"/> with the
///     fault message and never fabricates <see cref="AuthWizardStep.Success"/>
///     (Requirement 3.5).
///
/// The FsCheck property asserts the event-driven state machine only ever advances toward
/// Success along legal edges, and that Success is terminal — this invariant holds
/// identically before and after the fix, because the fix removes the second poller
/// without touching <see cref="AuthWizardStateMachine.EvaluateState"/>'s transition logic.
/// </summary>
public sealed class AuthWizardPreservationTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Mock provider — the state machine consults AuthUrl for status messages only.
    //  For the authorized-reconnect case there is NO auth URL (¬isBugCondition).
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class MockAuthProvider : ITailscaleAuthProvider
    {
        public TailscaleState CurrentState { get; set; } = TailscaleState.NeedsAuth;
        public string? AuthUrl { get; set; }
        public string? SelfAddress { get; set; }
        public string? SelfDnsName { get; set; }
        public string? TailnetName { get; set; }

        public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

        public void RaiseStateChanged(TailscaleState state) =>
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(state, PathType.None, TimeSpan.Zero));

        public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Example: authorized reconnect (Connected from Welcome) → Success.
    //  Non-buggy input (no auth URL, single poller) — must reach Success. (3.1, 3.2)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorizedReconnect_ConnectedFromWelcome_ReachesSuccess()
    {
        // No auth URL present — this is the already-authorized reconnect path, which is
        // NOT a bug condition: a lone Connected event with no interactive login in play.
        var provider = new MockAuthProvider
        {
            CurrentState = TailscaleState.Connected,
            AuthUrl = null
        };
        var sm = new AuthWizardStateMachine(provider);

        // Fresh wizard sits at Welcome; the host reports the node is already Connected.
        Assert.Equal(AuthWizardStep.Welcome, sm.CurrentStep);

        bool transitioned = sm.EvaluateState(TailscaleState.Connected);

        Assert.True(transitioned);
        Assert.Equal(AuthWizardStep.Success, sm.CurrentStep);
        Assert.True(sm.IsComplete);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Example: genuine Fault handling preserved — no spurious Success. (3.5)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenuineFault_DuringVerifying_ReachesAuthorizationRequired_NotSuccess()
    {
        var provider = new MockAuthProvider();
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();
        sm.EvaluateState(TailscaleState.Connecting); // BrowserAuth → Verifying
        Assert.Equal(AuthWizardStep.Verifying, sm.CurrentStep);

        // Feed a genuine backend Fault repeatedly. A Fault is neither Connected nor
        // NeedsAuth, so the machine stays in Verifying incrementing the attempt counter
        // until MaxVerifyAttempts, then surfaces AuthorizationRequired with the fault
        // message. It never fabricates Success.
        for (int i = 0; i < AuthWizardStateMachine.MaxVerifyAttempts; i++)
        {
            Assert.NotEqual(AuthWizardStep.Success, sm.CurrentStep);
            sm.EvaluateState(TailscaleState.Fault);
        }

        Assert.Equal(AuthWizardStep.AuthorizationRequired, sm.CurrentStep);
        Assert.False(sm.IsComplete);
        // The message reflects a genuine fault, not a timeout — a Fault is surfaced, not
        // masked as Success.
        Assert.Contains("Connection failed", sm.StatusMessage);
    }

    [Fact]
    public void Fault_NeverFabricatesSuccess_FromAnyNonSuccessStep()
    {
        // A single Fault event from any pre-terminal step must never jump to Success.
        var steps = new (Action<AuthWizardStateMachine> arrange, AuthWizardStep expectedStart)[]
        {
            (_ => { }, AuthWizardStep.Welcome),
            (sm => sm.StartBrowserAuth(), AuthWizardStep.BrowserAuth),
            (sm => { sm.StartBrowserAuth(); sm.EvaluateState(TailscaleState.Connecting); },
                AuthWizardStep.Verifying),
            (sm => { sm.StartBrowserAuth(); sm.EvaluateState(TailscaleState.Connecting);
                     sm.EvaluateState(TailscaleState.NeedsAuth); },
                AuthWizardStep.AuthorizationRequired),
        };

        foreach (var (arrange, expectedStart) in steps)
        {
            var sm = new AuthWizardStateMachine(new MockAuthProvider());
            arrange(sm);
            Assert.Equal(expectedStart, sm.CurrentStep);

            sm.EvaluateState(TailscaleState.Fault);

            Assert.NotEqual(AuthWizardStep.Success, sm.CurrentStep);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Property (Preservation): for ANY generated sequence of TailscaleState events fed
    //  to EvaluateState, the machine only advances toward Success along legal edges and,
    //  once in Success, never leaves it (Success is terminal). Non-buggy inputs behave
    //  identically to the observed baseline. (3.1, 3.2, 3.5)
    //
    //  EXPECTED OUTCOME on unfixed code: PASS (this captures the baseline to preserve).
    // ──────────────────────────────────────────────────────────────────────────────

    // Legal step edges of EvaluateState — the only transitions the machine may take for
    // a single event. Any transition outside this map (or a regression out of Success)
    // is a violation of the preserved invariant.
    private static readonly IReadOnlyDictionary<AuthWizardStep, AuthWizardStep[]> LegalEdges =
        new Dictionary<AuthWizardStep, AuthWizardStep[]>
        {
            [AuthWizardStep.Welcome] =
                new[] { AuthWizardStep.Welcome, AuthWizardStep.Success },
            [AuthWizardStep.BrowserAuth] =
                new[] { AuthWizardStep.BrowserAuth, AuthWizardStep.Verifying, AuthWizardStep.Success },
            [AuthWizardStep.Verifying] =
                new[] { AuthWizardStep.Verifying, AuthWizardStep.AuthorizationRequired, AuthWizardStep.Success },
            [AuthWizardStep.AuthorizationRequired] =
                new[] { AuthWizardStep.AuthorizationRequired, AuthWizardStep.Verifying, AuthWizardStep.Success },
            [AuthWizardStep.Success] =
                new[] { AuthWizardStep.Success }, // terminal
        };

    public static Arbitrary<TailscaleState[]> StateSequences()
    {
        var anyState = Gen.Elements(
            TailscaleState.Disconnected,
            TailscaleState.Connecting,
            TailscaleState.Connected,
            TailscaleState.Fault,
            TailscaleState.NeedsAuth);

        var seq = Gen.NonEmptyListOf(anyState).Select(l => l.ToArray());
        return Arb.From(seq);
    }

    [Property(Arbitrary = new[] { typeof(AuthWizardPreservationTests) }, MaxTest = 500)]
    public Property EvaluateState_OnlyAdvancesAlongLegalEdges_AndSuccessIsTerminal(
        TailscaleState[] events)
    {
        var sm = new AuthWizardStateMachine(new MockAuthProvider());

        foreach (var ev in events)
        {
            var before = sm.CurrentStep;
            sm.EvaluateState(ev);
            var after = sm.CurrentStep;

            // 1. Every transition must be along a legal edge.
            if (!LegalEdges[before].Contains(after))
            {
                return false.Label(
                    $"Illegal edge {before} --{ev}--> {after}");
            }

            // 2. Once in Success, the machine must never leave it (terminal).
            if (before == AuthWizardStep.Success && after != AuthWizardStep.Success)
            {
                return false.Label(
                    $"Regressed out of Success on {ev} to {after}");
            }
        }

        return true.ToProperty();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Property (Task 4.2 — Event-driven state machine invariant, BrowserAuth-seeded).
    //
    //  The property above starts every run from a fresh machine at Welcome. From Welcome
    //  EvaluateState can only stay at Welcome or jump to Success, so the BrowserAuth /
    //  Verifying / AuthorizationRequired edges of LegalEdges are never taken as a STARTING
    //  point. This property closes that gap: it first calls StartBrowserAuth() to seed the
    //  machine into BrowserAuth (the real post-Continue starting point of an interactive
    //  login), then feeds a generated TailscaleState sequence and asserts the SAME
    //  invariant — the machine advances only along legal edges toward Success, and once in
    //  Success never leaves it. Together the two properties exercise every step in
    //  LegalEdges as a transition source. (Requirements 2.1, 2.2)
    //
    //  EXPECTED OUTCOME: PASS — EvaluateState's transition logic is unchanged by the fix.
    // ──────────────────────────────────────────────────────────────────────────────

    [Property(Arbitrary = new[] { typeof(AuthWizardPreservationTests) }, MaxTest = 500)]
    public Property EvaluateState_FromBrowserAuth_OnlyAdvancesAlongLegalEdges_AndSuccessIsTerminal(
        TailscaleState[] events)
    {
        var sm = new AuthWizardStateMachine(new MockAuthProvider());

        // Seed the machine at BrowserAuth — the starting point once the user has clicked
        // "Continue" and the interactive browser-login/verify edges become reachable.
        sm.StartBrowserAuth();

        foreach (var ev in events)
        {
            var before = sm.CurrentStep;
            sm.EvaluateState(ev);
            var after = sm.CurrentStep;

            // 1. Every transition must be along a legal edge.
            if (!LegalEdges[before].Contains(after))
            {
                return false.Label(
                    $"Illegal edge {before} --{ev}--> {after}");
            }

            // 2. Once in Success, the machine must never leave it (terminal).
            if (before == AuthWizardStep.Success && after != AuthWizardStep.Success)
            {
                return false.Label(
                    $"Regressed out of Success on {ev} to {after}");
            }
        }

        return true.ToProperty();
    }
}
