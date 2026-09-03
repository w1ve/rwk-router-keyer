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
/// Unit tests for <see cref="SidecarAuthProvider"/> after the auth redesign
/// (bugfix Requirements 2.1, 2.2). The provider is a pure read-through snapshot over
/// <see cref="ITsnetSidecarHost"/>: every auth-relevant property reads directly from the
/// host, <see cref="SidecarAuthProvider.StateChanged"/> forwards the host's event, and
/// <see cref="SidecarAuthProvider.SubmitAuthKeyAsync"/> routes to the host. No independent
/// polling and no state heuristics remain, so <see cref="SidecarAuthProvider.CurrentState"/>
/// must equal the host's <see cref="ITsnetSidecarHost.State"/> for every possible state.
/// </summary>
public sealed class SidecarAuthProviderTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  Mock host — implements only what SidecarAuthProvider consumes. Everything else
    //  throws NotSupportedException, matching the pattern in AuthWizardDuelingPollerTests.
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class MockSidecarHost : ITsnetSidecarHost
    {
        public TailscaleState State { get; set; } = TailscaleState.NeedsAuth;
        public string? AuthUrl { get; set; }
        public string? SelfAddress { get; set; }
        public string? SelfDnsName { get; set; }

        // Records the last auth key submitted so tests can assert SubmitAuthKeyAsync routing.
        public string? SubmittedAuthKey { get; private set; }
        public int SubmitCallCount { get; private set; }

        public event EventHandler<string>? AuthUrlAvailable;
        public event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

        /// <summary>Raise StateChanged the way the real host does, updating State first.</summary>
        public void RaiseStateChanged(TailscaleState state)
        {
            State = state;
            StateChanged?.Invoke(this, new TailscaleStateChangedEventArgs(
                state, PathType.None, TimeSpan.Zero));
        }

        public Task SubmitAuthKeyAsync(string authKey, CancellationToken cancellationToken = default)
        {
            SubmittedAuthKey = authKey;
            SubmitCallCount++;
            return Task.CompletedTask;
        }

        // Unused surface for the auth path — not exercised by these tests.
        public string ApiBaseAddress => throw new NotSupportedException();
        public string Token => throw new NotSupportedException();
        public IPEndPoint EdgeLocalEndpoint => throw new NotSupportedException();
        public string EdgeTransport => throw new NotSupportedException();
        public string JitterProfile => throw new NotSupportedException();
        public string? PeerAddress => throw new NotSupportedException();
        public PathType CurrentPath => throw new NotSupportedException();
        public double RoundTripMs => throw new NotSupportedException();
        public string? DerpRegion => throw new NotSupportedException();

        public Task StartAsync(string? authKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IPEndPoint> CreateOutboundForwardAsync(string peerAddress, int port, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task RegisterEdgeCallbackAsync(string callbackAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SetPeerAsync(string peerAddress, int edgePort = 0, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ClearPeerAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }

        // Silence unused-event warning; the auth-URL event is not part of these tests.
        public void UnusedAuthUrlEvent() => AuthUrlAvailable?.Invoke(this, string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Read-through properties (Requirement 2.2).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentState_ReadsThroughToHostState()
    {
        var host = new MockSidecarHost { State = TailscaleState.Connecting };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal(TailscaleState.Connecting, provider.CurrentState);

        host.State = TailscaleState.Connected;
        Assert.Equal(TailscaleState.Connected, provider.CurrentState);
    }

    [Fact]
    public void AuthUrl_ReadsThroughToHostAuthUrl()
    {
        var host = new MockSidecarHost { AuthUrl = "https://login.tailscale.com/a/abc123" };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal("https://login.tailscale.com/a/abc123", provider.AuthUrl);

        host.AuthUrl = null;
        Assert.Null(provider.AuthUrl);
    }

    [Fact]
    public void SelfAddress_ReadsThroughToHost()
    {
        var host = new MockSidecarHost { SelfAddress = "100.101.102.103" };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal("100.101.102.103", provider.SelfAddress);

        host.SelfAddress = null;
        Assert.Null(provider.SelfAddress);
    }

    [Fact]
    public void SelfDnsName_ReadsThroughToHost()
    {
        var host = new MockSidecarHost { SelfDnsName = "myhost.tail12345.ts.net" };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal("myhost.tail12345.ts.net", provider.SelfDnsName);

        host.SelfDnsName = null;
        Assert.Null(provider.SelfDnsName);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  TailnetName extraction from SelfDnsName.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TailnetName_ExtractedFromTsNetDnsName()
    {
        var host = new MockSidecarHost { SelfDnsName = "myhost.tail12345.ts.net" };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal("tail12345", provider.TailnetName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("myhost")]                    // no domain
    [InlineData("myhost.example.com")]        // not a ts.net name
    [InlineData("host.tailnet.example.net")]  // ends in .net but not .ts.net
    public void TailnetName_NullForNonTsNetOrNullDnsName(string? dnsName)
    {
        var host = new MockSidecarHost { SelfDnsName = dnsName };
        var provider = new SidecarAuthProvider(host);

        Assert.Null(provider.TailnetName);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  SubmitAuthKeyAsync routes to the host (Requirement 2.2 — no independent path).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAuthKeyAsync_RoutesToHost()
    {
        var host = new MockSidecarHost();
        var provider = new SidecarAuthProvider(host);

        await provider.SubmitAuthKeyAsync("tskey-auth-example");

        Assert.Equal(1, host.SubmitCallCount);
        Assert.Equal("tskey-auth-example", host.SubmittedAuthKey);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  StateChanged forwards the host's event (Requirements 2.1, 2.2).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiresWhenHostRaisesStateChanged()
    {
        var host = new MockSidecarHost { State = TailscaleState.NeedsAuth };
        var provider = new SidecarAuthProvider(host);

        TailscaleState? observed = null;
        void Handler(object? sender, TailscaleStateChangedEventArgs e) => observed = e.State;

        provider.StateChanged += Handler;
        host.RaiseStateChanged(TailscaleState.Connected);

        Assert.Equal(TailscaleState.Connected, observed);

        // Unsubscribing stops forwarding — no further updates after removal.
        provider.StateChanged -= Handler;
        host.RaiseStateChanged(TailscaleState.Fault);

        Assert.Equal(TailscaleState.Connected, observed);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Read-through invariant: for EVERY TailscaleState value, setting host.State makes
    //  provider.CurrentState equal it — no heuristics, no divergence (Requirements 2.1, 2.2).
    // ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TailscaleState.Disconnected)]
    [InlineData(TailscaleState.Connecting)]
    [InlineData(TailscaleState.Connected)]
    [InlineData(TailscaleState.Fault)]
    [InlineData(TailscaleState.NeedsAuth)]
    public void CurrentState_EqualsHostState_ForEveryState(TailscaleState state)
    {
        var host = new MockSidecarHost { State = state };
        var provider = new SidecarAuthProvider(host);

        Assert.Equal(state, provider.CurrentState);
    }

    [Fact]
    public void CurrentState_TracksHostState_AcrossAllEnumValues()
    {
        var host = new MockSidecarHost();
        var provider = new SidecarAuthProvider(host);

        foreach (TailscaleState state in Enum.GetValues<TailscaleState>())
        {
            host.State = state;
            Assert.Equal(state, provider.CurrentState);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  PROPERTY (Task 4.3 — Heuristic-free provider equivalence).
    //
    //  The single-poller invariant expressed as a property: with the stale-auth-URL
    //  heuristics stripped from SidecarAuthProvider (Task 3.2), the provider is a pure
    //  read-through snapshot over ITsnetSidecarHost. For ANY sequence of TailscaleState
    //  values set on the host — in ANY order — the provider's CurrentState must equal
    //  host.State after each set, with no divergence and no heuristic drift. This is the
    //  property form of the example/Theory read-through tests above (Requirements 2.1, 2.2).
    //
    //  EXPECTED OUTCOME: PASS — the provider holds no independent state to diverge.
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Non-empty sequences drawn from every <see cref="TailscaleState"/> value.</summary>
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

    [Property(Arbitrary = new[] { typeof(SidecarAuthProviderTests) }, MaxTest = 500)]
    public Property CurrentState_AlwaysEqualsHostState_ForAnyStateSequence(TailscaleState[] states)
    {
        var host = new MockSidecarHost();
        var provider = new SidecarAuthProvider(host);

        foreach (var state in states)
        {
            host.State = state;

            // Core required assertion: the read-through snapshot never diverges from the
            // host — no heuristics can push CurrentState to a value the host never reported.
            if (provider.CurrentState != state)
            {
                return false.Label(
                    $"Provider CurrentState {provider.CurrentState} != host.State {state}");
            }
        }

        return true.ToProperty();
    }

    /// <summary>Nullable auth-URL-shaped strings: null, empty, and arbitrary strings.</summary>
    public static Arbitrary<string?> AuthUrls()
    {
        var url = Gen.OneOf(
            Gen.Constant<string?>(null),
            Gen.Constant<string?>(string.Empty),
            Arb.Default.String().Generator.Select(s => (string?)s));
        return Arb.From(url);
    }

    [Property(Arbitrary = new[] { typeof(SidecarAuthProviderTests) }, MaxTest = 500)]
    public Property AuthUrl_AlwaysEqualsHostAuthUrl_ForAnyValue(string? authUrl)
    {
        var host = new MockSidecarHost { AuthUrl = authUrl };
        var provider = new SidecarAuthProvider(host);

        // AuthUrl is a pure read-through as well — the provider reflects the host exactly.
        return (provider.AuthUrl == authUrl).ToProperty()
            .Label($"Provider AuthUrl '{provider.AuthUrl}' != host.AuthUrl '{authUrl}'");
    }
}
