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
using RWK.Station.Controllers;
using RWK.Station.Tests.TestDoubles;
using Xunit;

namespace RWK.Station.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ControlForwardRegistrar"/>, the fix for the field bug where a single
/// transient sidecar failure at arm time left the Station's control-channel inbound forward
/// unregistered while the Station latched it as registered and showed a plain green "ARMED".
/// </summary>
/// <remarks>
/// Spec: .kiro/specs/station-control-forward-registration
/// _Requirements: 1.1, 1.2, 1.3, 2.1, 2.2, 2.4, 3.1_
/// </remarks>
public class ControlForwardRegistrarTests
{
    private const int Port = 7373;

    // Instant no-op delay so bounded-retry tests never actually sleep.
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay =
        (_, _) => Task.CompletedTask;

    private static ControlForwardRegistrar NewRegistrar(FakeSidecarHost host, int maxAttempts = 5)
        => new(host, Port, Port, diagnostics: null, maxAttempts: maxAttempts,
               retryDelay: TimeSpan.Zero, delay: NoDelay);

    // ──────────────────────────────────────────────────────────────────────────────
    //  Task 1 — Property 1: Bug Condition — Robust Control-Forward Registration
    //
    //  These assertions encode the EXPECTED robust behavior. They FAIL against a
    //  single-attempt/old-policy registrar (see OldPolicy_* below, which reproduces the bug)
    //  and PASS against the fixed registrar. Together they surface the counterexample and
    //  validate the fix.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransientThenSuccess_RegistersAndLatchesOnlyAfterSuccess()
    {
        // Fails the first two attempts (transient), succeeds on the third.
        var host = new FakeSidecarHost { FailFirstAttempts = 2 };
        var registrar = NewRegistrar(host, maxAttempts: 5);

        Assert.False(registrar.IsRegistered); // latch must not be set before any success

        var result = await registrar.TryRegisterAsync();

        Assert.True(result.Registered);
        Assert.True(registrar.IsRegistered);
        Assert.Equal(3, host.CreateInboundForwardCallCount); // retried until success
    }

    [Fact]
    public async Task PersistentFailure_DoesNotLatch_AndSurfacesReason()
    {
        var host = new FakeSidecarHost { AlwaysFail = true };
        var registrar = NewRegistrar(host, maxAttempts: 4);

        var result = await registrar.TryRegisterAsync();

        Assert.False(result.Registered);           // failure surfaced
        Assert.False(registrar.IsRegistered);      // latch NOT set on failure (the bug's core)
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(4, host.CreateInboundForwardCallCount); // bounded retry, not infinite
    }

    [Fact]
    public async Task Idempotent_AfterSuccess_DoesNotPostAgain()
    {
        var host = new FakeSidecarHost(); // succeeds immediately
        var registrar = NewRegistrar(host);

        var first = await registrar.TryRegisterAsync();
        var second = await registrar.TryRegisterAsync();

        Assert.True(first.Registered);
        Assert.True(second.Registered);
        Assert.Equal(1, host.CreateInboundForwardCallCount); // no duplicate forward
    }

    /// <summary>
    /// Reproduces the ORIGINAL bug policy: a single attempt with the latch conceptually set
    /// before the call. This test documents the counterexample — with maxAttempts=1 a transient
    /// failure leaves the forward unregistered after exactly one post — which is precisely the
    /// field failure. The fixed registrar with retry (tests above) resolves it.
    /// </summary>
    [Fact]
    public async Task OldPolicy_SingleAttempt_TransientFailure_LeavesForwardUnregistered()
    {
        var host = new FakeSidecarHost { FailFirstAttempts = 1 };
        var registrar = NewRegistrar(host, maxAttempts: 1); // no retry == old behavior

        var result = await registrar.TryRegisterAsync();

        Assert.False(result.Registered);
        Assert.Equal(1, host.CreateInboundForwardCallCount); // gave up after one attempt
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Task 2 — Property 2: Preservation — First-Attempt Success and Idempotency
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptSuccess_PostsExactlyOnce_AndLatches()
    {
        // Preserves the original happy-path behavior: one post, latched.
        var host = new FakeSidecarHost();
        var registrar = NewRegistrar(host);

        var result = await registrar.TryRegisterAsync();

        Assert.True(result.Registered);
        Assert.True(registrar.IsRegistered);
        Assert.Equal(1, host.CreateInboundForwardCallCount);
    }

    /// <summary>
    /// Property: for any (failCount, maxAttempts), the latch is true iff a post ultimately
    /// succeeded — i.e. iff failCount &lt; maxAttempts. The latch is never set on failure.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property LatchIsTrue_IffPostEventuallySucceeds()
    {
        var gen =
            from maxAttempts in Gen.Choose(1, 6)
            from failCount in Gen.Choose(0, 8)
            select (maxAttempts, failCount);

        return Prop.ForAll(Arb.From(gen), tuple =>
        {
            var (maxAttempts, failCount) = tuple;
            var host = new FakeSidecarHost { FailFirstAttempts = failCount };
            var registrar = NewRegistrar(host, maxAttempts);

            var result = registrar.TryRegisterAsync().GetAwaiter().GetResult();

            bool expectedRegistered = failCount < maxAttempts;
            return result.Registered == expectedRegistered
                && registrar.IsRegistered == expectedRegistered;
        });
    }

    /// <summary>
    /// Property: after a successful registration, any number of extra re-attempts never posts
    /// again (idempotency preserved across configurations).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AfterSuccess_ExtraReattempts_NeverPostAgain()
    {
        var gen =
            from extra in Gen.Choose(1, 5)
            select extra;

        return Prop.ForAll(Arb.From(gen), extra =>
        {
            var host = new FakeSidecarHost(); // succeeds immediately
            var registrar = NewRegistrar(host);

            registrar.TryRegisterAsync().GetAwaiter().GetResult();
            for (int i = 0; i < extra; i++)
                registrar.TryRegisterAsync().GetAwaiter().GetResult();

            return host.CreateInboundForwardCallCount == 1;
        });
    }
}
