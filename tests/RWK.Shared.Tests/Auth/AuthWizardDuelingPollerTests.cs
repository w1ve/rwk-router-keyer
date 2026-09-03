/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Net;
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared;
using RWK.Shared.Auth;
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Auth;

/// <summary>
/// EXPLORATION TEST (Task 1 — Property 1: Bug Condition), retargeted after the fix.
///
/// This test originally surfaced the dueling-poller / interleaving-dependent terminal
/// step defect (Requirements 1.1, 2.1, 2.2): two independent participants — the wizard's
/// own poll timer (<c>PollAndTransitionAsync</c> → <c>SidecarAuthProvider.PollStatusAsync</c>
/// with stale-auth-URL heuristics) and the host-event path — could reach DIFFERENT terminal
/// <see cref="AuthWizardStep"/> values for the SAME host-reported state sequence.
///
/// The fix eliminated the self-poll seam entirely: <c>PollStatusAsync</c> is gone from
/// <see cref="ITailscaleAuthProvider"/>, the provider is a pure read-through snapshot of the
/// host, and <see cref="AuthWizardStateMachine.EvaluateState"/> is the SINGLE event-driven
/// entry point. There is therefore exactly ONE deterministic terminal step for a given
/// host-reported state sequence — the divergence is gone.
///
/// The two drive-path helpers below now BOTH feed <see cref="AuthWizardStateMachine.EvaluateState"/>
/// the states the host actually reports, so they trivially agree. The assertions still assert
/// agreement, and the provider-never-fabricates assertion remains meaningful by reading
/// <see cref="ITailscaleAuthProvider.CurrentState"/> (which must always equal <c>host.State</c>)
/// instead of the removed <c>PollStatusAsync</c>.
/// </summary>
public sealed class AuthWizardDuelingPollerTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Mock host — implements only what SidecarAuthProvider consumes. Its State/AuthUrl
    //  are driven by the test to mimic the real sidecar during interactive login.
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class MockSidecarHost : ITsnetSidecarHost
    {
        public TailscaleState State { get; set; } = TailscaleState.NeedsAuth;
        public string? AuthUrl { get; set; } = "https://login.tailscale.com/a/stale123";
        public string? SelfAddress { get; set; }
        public string? SelfDnsName { get; set; }

        public event EventHandler<string>? AuthUrlAvailable;
        public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

        public void RaiseStateChanged(TailscaleState state)
        {
            State = state;
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
                state, PathType.None, TimeSpan.Zero));
        }

        public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Unused surface for the auth path — not exercised by these tests.
        public string ApiBaseAddress => throw new NotSupportedException();
        public string Token => throw new NotSupportedException();
        public IPEndPoint EdgeLocalEndpoint => throw new NotSupportedException();
        public string EdgeTransport => throw new NotSupportedException();
        public string JitterProfile => throw new NotSupportedException();
        public string? PeerAddress => null;
        public PathType CurrentPath => PathType.None;
        public double RoundTripMs => -1;
        public string? DerpRegion => null;

        public Task StartAsync(string? authKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IPEndPoint> CreateOutboundForwardAsync(string peerAddress, int port, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task RegisterEdgeCallbackAsync(string callbackAddress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SetPeerAsync(string peerAddress, int edgePort = 0, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ClearPeerAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }

        // Silence unused-event warnings; the auth-URL event is not part of this scenario.
        public void UnusedAuthUrlEvent() => AuthUrlAvailable?.Invoke(this, string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Helpers: drive the state machine to its terminal step for a state sequence.
    //  With the self-poll seam removed there is a SINGLE event-driven entry point
    //  (EvaluateState). Both helpers now feed the state machine the states the host
    //  actually reports, so they must reach the same terminal step.
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Host-event path (path b): feed the state machine the states the host ACTUALLY
    /// reported, via <see cref="AuthWizardStateMachine.EvaluateState"/>. Also verifies
    /// the provider never fabricates a state — <see cref="ITailscaleAuthProvider.CurrentState"/>
    /// must equal <c>host.State</c> at every step (the read-through snapshot invariant that
    /// replaced the removed heuristic <c>PollStatusAsync</c>).
    /// </summary>
    private static AuthWizardStep RunHostEventPath(IReadOnlyList<TailscaleState> hostStates)
    {
        var host = new MockSidecarHost();
        var provider = new SidecarAuthProvider(host);
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        foreach (var s in hostStates)
        {
            host.State = s;
            host.AuthUrl = s == TailscaleState.NeedsAuth ? host.AuthUrl : null;

            // The provider is a pure read-through: it must report exactly what the host
            // reports and never fabricate a state the host did not report.
            Assert.Equal(host.State, provider.CurrentState);

            sm.EvaluateState(s);
        }

        return sm.CurrentStep;
    }

    /// <summary>
    /// Former "wizard-timer path" (path a), retargeted. The wizard no longer owns a poll
    /// timer; with the self-poll seam removed the wizard is driven by the SAME single
    /// event-driven entry point. This helper therefore feeds <see cref="AuthWizardStateMachine.EvaluateState"/>
    /// the states the host actually reports — repeating each state <paramref name="pollsPerState"/>
    /// times to mimic the host raising the same state multiple times before it changes.
    /// Because there is now only one poller, this path trivially agrees with the host-event path.
    /// </summary>
    private static AuthWizardStep RunSingleEventPath(
        IReadOnlyList<TailscaleState> hostStates, int pollsPerState)
    {
        var host = new MockSidecarHost();
        var provider = new SidecarAuthProvider(host);
        var sm = new AuthWizardStateMachine(provider);
        sm.StartBrowserAuth();

        foreach (var s in hostStates)
        {
            host.State = s;
            host.AuthUrl = s == TailscaleState.NeedsAuth ? host.AuthUrl : null;
            for (int i = 0; i < pollsPerState; i++)
            {
                // Provider snapshot never diverges from the host.
                Assert.Equal(host.State, provider.CurrentState);
                sm.EvaluateState(s);
            }
        }

        return sm.CurrentStep;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Scoped assertion: the provider is a pure read-through snapshot — CurrentState
    //  always equals host.State and it NEVER fabricates a state the host did not report,
    //  even while the host sits at NeedsAuth with the same stale auth URL across many
    //  observations (the window during which the user completes interactive login).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Provider_NeverFabricatesStateTheHostDidNotReport()
    {
        var host = new MockSidecarHost
        {
            State = TailscaleState.NeedsAuth,
            AuthUrl = "https://login.tailscale.com/a/stale123"
        };
        var provider = new SidecarAuthProvider(host);

        // The host stays at NeedsAuth with the same auth URL across many observations,
        // exactly as it does while the user completes the interactive browser login.
        TailscaleState? fabricated = null;
        for (int i = 0; i < 20; i++)
        {
            var reported = provider.CurrentState;
            if (reported != host.State)
            {
                fabricated = reported;
                break;
            }
        }

        // FIXED behavior: the provider is a pure read-through snapshot — it reports
        // exactly what the host reports and never fabricates a state.
        Assert.True(
            fabricated is null,
            $"Provider fabricated '{fabricated}' while host.State was '{host.State}'. " +
            "The single read-through provider must never diverge from the host.");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Core property: both drive paths reach the SAME terminal step for the SAME
    //  login event sequence. With a single event-driven poller the divergence is gone.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothPollerPaths_ReachSameTerminalStep_ForStaleAuthUrlSequence()
    {
        // Login event sequence as reported by the host: the sidecar sits at NeedsAuth
        // (interactive browser login in progress) and has NOT yet moved on. This is the
        // window during which the user is completing OAuth in the browser — previously the
        // window in which the stale-auth-URL heuristic fabricated a Connecting the host
        // never reported.
        var hostStates = new[]
        {
            TailscaleState.NeedsAuth
        };

        // Repeat the host-reported NeedsAuth many times. Previously the wizard's faster
        // timer polled here and tripped the stale heuristic; now, with a single event-driven
        // path, repeating the SAME host-reported state never advances on a fabricated state.
        // Both paths stay in BrowserAuth and therefore agree.
        const int pollsPerState = 12 + 31;

        var hostEventTerminal = RunHostEventPath(hostStates);
        var singleEventTerminal = RunSingleEventPath(hostStates, pollsPerState);

        Assert.Equal(hostEventTerminal, singleEventTerminal);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Scoped PBT: for any login event sequence, both paths must agree on the terminal
    //  step. With the single event-driven poller they agree for ALL sequences.
    // ──────────────────────────────────────────────────────────────────────────────

    public static Arbitrary<TailscaleState[]> LoginSequences()
    {
        // Sequences over the states that occur during interactive login. These include
        // in-progress logins that have NOT yet reached Connected (the user is still in
        // the browser), which is exactly when the stale-auth-URL heuristic used to fire. A
        // single-poller design must reach the same terminal step for ALL such sequences.
        var authStates = Gen.Elements(
            TailscaleState.NeedsAuth,
            TailscaleState.Connecting,
            TailscaleState.Connected);

        var seq = Gen.NonEmptyListOf(authStates).Select(l => l.ToArray());
        return Arb.From(seq);
    }

    [Property(Arbitrary = new[] { typeof(AuthWizardDuelingPollerTests) }, MaxTest = 200)]
    public Property BothPollerPaths_Agree_ForAnyLoginSequence(TailscaleState[] hostStates)
    {
        var hostEventTerminal = RunHostEventPath(hostStates);
        // Repeat each host-reported state many times. Under the single-poller design there
        // is no second poller and no stale-auth-URL heuristic, so repeating a host-reported
        // state never advances on a state the host did not report — both paths must agree.
        var singleEventTerminal = RunSingleEventPath(hostStates, 44);

        return (hostEventTerminal == singleEventTerminal)
            .Label($"host-event terminal={hostEventTerminal}, single-event terminal={singleEventTerminal} " +
                   $"for [{string.Join(",", hostStates)}]");
    }
}
