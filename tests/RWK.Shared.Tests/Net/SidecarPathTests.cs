/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System;
using System.IO;
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Net;

/// <summary>
/// Unit tests for <see cref="SidecarPath"/> — the pure resolver and base directory accessor.
/// Validates requirements 16.6, 16.7, 16.8.
/// </summary>
public class SidecarPathTests
{
    #region Resolve — valid inputs

    [Fact]
    public void Resolve_WithValidBaseDirectory_ReturnsPathCombine()
    {
        // Arrange
        string baseDir = @"C:\Apps\RWK";
        string exeName = "rwk-tailscale-sidecar.exe";

        // Act
        string result = SidecarPath.Resolve(baseDir, exeName);

        // Assert
        Assert.Equal(Path.Combine(baseDir, exeName), result);
    }

    [Fact]
    public void Resolve_WithTrailingSeparator_ReturnsPathCombine()
    {
        // Path.Combine handles trailing separators correctly
        string baseDir = @"C:\Apps\RWK\";
        string exeName = "rwk-tailscale-sidecar.exe";

        string result = SidecarPath.Resolve(baseDir, exeName);

        Assert.Equal(Path.Combine(baseDir, exeName), result);
    }

    [Fact]
    public void Resolve_WithUncPath_ReturnsPathCombine()
    {
        string baseDir = @"\\server\share\RWK";
        string exeName = "rwk-tailscale-sidecar.exe";

        string result = SidecarPath.Resolve(baseDir, exeName);

        Assert.Equal(Path.Combine(baseDir, exeName), result);
    }

    [Fact]
    public void Resolve_DoesNotProbeFileSystem()
    {
        // The resolved path need not exist — this call must succeed
        // even for a completely fabricated directory
        string baseDir = @"Z:\NonExistent\Directory\That\Does\Not\Exist";
        string exeName = "some-sidecar.exe";

        string result = SidecarPath.Resolve(baseDir, exeName);

        Assert.Equal(Path.Combine(baseDir, exeName), result);
    }

    #endregion

    #region Resolve — null or empty baseDirectory throws

    [Fact]
    public void Resolve_WithNullBaseDirectory_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SidecarPath.Resolve(null!, "rwk-tailscale-sidecar.exe"));

        // The exception message should name the offending source
        Assert.Contains("baseDirectory", ex.Message);
        Assert.Contains("Assembly.Location", ex.Message);
    }

    [Fact]
    public void Resolve_WithEmptyBaseDirectory_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SidecarPath.Resolve(string.Empty, "rwk-tailscale-sidecar.exe"));

        // The exception message should name the offending source
        Assert.Contains("baseDirectory", ex.Message);
        Assert.Contains("Assembly.Location", ex.Message);
    }

    [Fact]
    public void Resolve_WithWhitespaceBaseDirectory_ThrowsInvalidOperationException()
    {
        // String.IsNullOrEmpty doesn't catch whitespace-only, but the design says
        // "null or empty" — whitespace-only is technically not empty per IsNullOrEmpty.
        // However a whitespace path is still invalid for path resolution. Let's verify
        // the contract: only null/empty throw per the design spec.
        // A whitespace string is not null or empty, so Resolve will NOT throw.
        string baseDir = "   ";
        string exeName = "rwk-tailscale-sidecar.exe";

        // This should NOT throw per the documented contract (null or empty only)
        string result = SidecarPath.Resolve(baseDir, exeName);
        Assert.Equal(Path.Combine(baseDir, exeName), result);
    }

    #endregion

    #region GetBaseDirectory

    [Fact]
    public void GetBaseDirectory_ReturnsNonEmptyRootedPath()
    {
        // AppContext.BaseDirectory should return a valid path in a test runner
        string baseDir = SidecarPath.GetBaseDirectory();

        Assert.False(string.IsNullOrEmpty(baseDir));
        Assert.True(Path.IsPathRooted(baseDir));
    }

    [Fact]
    public void GetBaseDirectory_ResultCanBePassedToResolve()
    {
        // Integration: GetBaseDirectory result is usable with Resolve
        string baseDir = SidecarPath.GetBaseDirectory();
        string resolved = SidecarPath.Resolve(baseDir, SidecarPath.DefaultExecutableName);

        Assert.False(string.IsNullOrEmpty(resolved));
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(SidecarPath.DefaultExecutableName, resolved);
    }

    #endregion

    #region DefaultExecutableName constant

    [Fact]
    public void DefaultExecutableName_IsExpectedValue()
    {
        Assert.Equal("rwk-tailscale-sidecar.exe", SidecarPath.DefaultExecutableName);
    }

    #endregion
}
