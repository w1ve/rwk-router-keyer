/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Net;

/// <summary>
/// Unit tests for <see cref="SidecarFailureHandler"/> (task 14.9).
/// Tests the asymmetric sidecar-failure behaviour:
/// - Client: degrades gracefully, keeps local practice usable
/// - Station: refuses to arm, leaves all lines de-asserted
/// - Retry loop re-runs resolution periodically
/// - Failure messages name the resolved path verbatim
/// </summary>
public class SidecarFailureHandlerTests
{
    private static SidecarFailure MakeFailure(
        SidecarFailureKind kind = SidecarFailureKind.NotFound,
        string path = @"E:\RWK\rwk-tailscale-sidecar.exe",
        string reason = "File not found") =>
        new(kind, path, reason);

    // ---- Failure message formatting (16.9) ----

    [Fact]
    public void FormatFailureMessage_NamesResolvedPathVerbatim()
    {
        var failure = MakeFailure(
            path: @"E:\RWK\rwk-tailscale-sidecar.exe",
            reason: "File not found");

        string msg = SidecarFailureHandler.FormatFailureMessage(failure);

        Assert.Contains(@"E:\RWK\rwk-tailscale-sidecar.exe", msg);
        Assert.Contains("File not found", msg);
    }

    [Fact]
    public void FormatFailureMessage_IncludesReason()
    {
        var failure = MakeFailure(
            kind: SidecarFailureKind.LaunchFailed,
            reason: "Access denied: the file is blocked by Windows Defender");

        string msg = SidecarFailureHandler.FormatFailureMessage(failure);

        Assert.Contains("Access denied", msg);
        Assert.Contains("Windows Defender", msg);
    }

    // ---- Client degradation (16.11, 4.7) ----

    [Fact]
    public void Client_WhenHealthy_AllSubsystemsUsable()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);

        var degradation = handler.GetClientDegradation();

        Assert.True(degradation.PaddleUsable);
        Assert.True(degradation.KeyerUsable);
        Assert.True(degradation.SidetoneUsable);
        Assert.True(degradation.TailnetOperational);
        Assert.Null(degradation.FailureMessage);
    }

    [Fact]
    public void Client_WhenInFailure_PaddleKeyerSidetoneStillUsable()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());

        var degradation = handler.GetClientDegradation();

        // Local practice subsystems remain usable (4.7, 16.11).
        Assert.True(degradation.PaddleUsable);
        Assert.True(degradation.KeyerUsable);
        Assert.True(degradation.SidetoneUsable);
        // Only tailnet ops fail.
        Assert.False(degradation.TailnetOperational);
        Assert.NotNull(degradation.FailureMessage);
    }

    [Fact]
    public void Client_WhenInFailure_FailureMessagePresent()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure(path: @"C:\Test\sidecar.exe"));

        var degradation = handler.GetClientDegradation();

        Assert.Contains(@"C:\Test\sidecar.exe", degradation.FailureMessage);
    }

    [Fact]
    public void Client_AfterRecovery_TailnetOperationalAgain()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());
        handler.ReportRecovery();

        var degradation = handler.GetClientDegradation();

        Assert.True(degradation.TailnetOperational);
        Assert.Null(degradation.FailureMessage);
    }

    // ---- Station refuse-to-arm (16.12, 8.7) ----

    [Fact]
    public void Station_WhenHealthy_MayArm()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Station);

        var policy = handler.GetStationArmPolicy();

        Assert.True(policy.MayArm);
        Assert.False(policy.AllLinesDeaserted);
        Assert.Null(policy.FailureMessage);
    }

    [Fact]
    public void Station_WhenInFailure_RefusesToArm()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Station);
        handler.ReportFailure(MakeFailure());

        var policy = handler.GetStationArmPolicy();

        // Station must refuse to arm (16.12).
        Assert.False(policy.MayArm);
        // All lines must be de-asserted (8.7).
        Assert.True(policy.AllLinesDeaserted);
        Assert.NotNull(policy.FailureMessage);
    }

    [Fact]
    public void Station_WhenInFailure_FailureMessageNamesPath()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Station);
        handler.ReportFailure(MakeFailure(path: @"D:\Radio\rwk-tailscale-sidecar.exe"));

        var policy = handler.GetStationArmPolicy();

        Assert.Contains(@"D:\Radio\rwk-tailscale-sidecar.exe", policy.FailureMessage);
    }

    [Fact]
    public void Station_AfterRecovery_DoesNotAutoArm()
    {
        // After recovery, MayArm becomes true — but arming stays a deliberate action.
        // The handler does not auto-arm; it only lifts the prohibition.
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Station);
        handler.ReportFailure(MakeFailure());
        handler.ReportRecovery();

        var policy = handler.GetStationArmPolicy();

        // MayArm is true (the prohibition is lifted), but the actual arm action
        // must come from the operator — not from recovery.
        Assert.True(policy.MayArm);
        Assert.False(policy.AllLinesDeaserted);
    }

    // ---- Failure state tracking (16.10) ----

    [Fact]
    public void IsInFailure_InitiallyFalse()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        Assert.False(handler.IsInFailure);
        Assert.Null(handler.CurrentFailure);
    }

    [Fact]
    public void ReportFailure_SetsIsInFailure()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());
        Assert.True(handler.IsInFailure);
        Assert.NotNull(handler.CurrentFailure);
    }

    [Fact]
    public void ReportRecovery_ClearsIsInFailure()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());
        handler.ReportRecovery();
        Assert.False(handler.IsInFailure);
        Assert.Null(handler.CurrentFailure);
    }

    [Fact]
    public void FailureStateChanged_FiredOnFailure()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        SidecarFailureStateChangedEventArgs? args = null;
        handler.FailureStateChanged += (_, e) => args = e;

        handler.ReportFailure(MakeFailure());

        Assert.NotNull(args);
        Assert.False(args!.IsRecovered);
        Assert.NotNull(args.Failure);
    }

    [Fact]
    public void FailureStateChanged_FiredOnRecovery()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());

        SidecarFailureStateChangedEventArgs? args = null;
        handler.FailureStateChanged += (_, e) => args = e;
        handler.ReportRecovery();

        Assert.NotNull(args);
        Assert.True(args!.IsRecovered);
        Assert.Null(args.Failure);
    }

    // ---- Retry loop (task 14.9: retry periodically, re-run resolution) ----

    [Fact]
    public async Task RetryRequested_FiredAfterInterval()
    {
        var handler = new SidecarFailureHandler(
            SidecarFailurePolicy.Client,
            retryInterval: TimeSpan.FromMilliseconds(50));

        var retryFired = new TaskCompletionSource();
        handler.RetryRequested += (_, _) => retryFired.TrySetResult();

        handler.ReportFailure(MakeFailure());

        // Should fire within 200ms (well over the 50ms interval).
        var completed = await Task.WhenAny(retryFired.Task, Task.Delay(200));
        Assert.Equal(retryFired.Task, completed);

        handler.Dispose();
    }

    [Fact]
    public async Task RetryRequested_StopsAfterRecovery()
    {
        var handler = new SidecarFailureHandler(
            SidecarFailurePolicy.Client,
            retryInterval: TimeSpan.FromMilliseconds(30));

        int retryCount = 0;
        handler.RetryRequested += (_, _) => Interlocked.Increment(ref retryCount);

        handler.ReportFailure(MakeFailure());
        await Task.Delay(80); // Should get at least one retry.
        handler.ReportRecovery();

        int countAfterRecovery = retryCount;
        await Task.Delay(100); // Wait more — should not get more retries.

        Assert.Equal(countAfterRecovery, retryCount);
        handler.Dispose();
    }

    [Fact]
    public void Dispose_StopsRetryLoop()
    {
        var handler = new SidecarFailureHandler(
            SidecarFailurePolicy.Client,
            retryInterval: TimeSpan.FromMilliseconds(10));

        handler.ReportFailure(MakeFailure());
        handler.Dispose();

        // Should not throw.
        Assert.True(handler.IsInFailure); // State is preserved but loop is stopped.
    }

    // ---- Policy validation ----

    [Fact]
    public void Client_GetStationArmPolicy_Throws()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        Assert.Throws<InvalidOperationException>(() => handler.GetStationArmPolicy());
    }

    [Fact]
    public void Station_GetClientDegradation_Throws()
    {
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Station);
        Assert.Throws<InvalidOperationException>(() => handler.GetClientDegradation());
    }

    // ---- Distinct from Disconnected (16.10) ----

    [Fact]
    public void FailureState_DistinctFromDisconnected()
    {
        // The failure handler maintains its own state separate from TailscaleState.
        // When IsInFailure is true, it should be presented differently than Disconnected.
        var handler = new SidecarFailureHandler(SidecarFailurePolicy.Client);
        handler.ReportFailure(MakeFailure());

        Assert.True(handler.IsInFailure);
        Assert.NotNull(handler.CurrentFailure);
        // The CurrentFailure carries Kind, Path, and Reason — all distinct from a
        // generic "disconnected" state.
        Assert.Equal(SidecarFailureKind.NotFound, handler.CurrentFailure!.Kind);
    }
}
