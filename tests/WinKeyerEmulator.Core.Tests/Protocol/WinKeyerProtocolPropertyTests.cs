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
using WinKeyerEmulator.Core.Protocol;
using WinKeyerEmulator.Core.Tests.TestDoubles;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Protocol;

/// <summary>
/// Property-based tests for WinKeyerProtocol state machine.
/// </summary>
public class WinKeyerProtocolPropertyTests
{
    private readonly NullLogger _logger = new();

    private WinKeyerProtocol CreateProtocol() => new(_logger);

    private void AdminOpen(WinKeyerProtocol protocol)
    {
        protocol.ProcessByte(CommandDefinitions.AdminCmd);
        protocol.ProcessByte(CommandDefinitions.AdminOpen);
    }

    private void AdminClose(WinKeyerProtocol protocol)
    {
        protocol.ProcessByte(CommandDefinitions.AdminCmd);
        protocol.ProcessByte(CommandDefinitions.AdminClose);
    }

    /// <summary>
    /// Generates a random valid host-mode operation: speed commands, text bytes, or clear buffer.
    /// </summary>
    private static Gen<byte[]> GenValidOperation()
    {
        var speedCmd = Gen.Choose(CommandDefinitions.MinWpm, CommandDefinitions.MaxWpm)
            .Select(wpm => new byte[] { CommandDefinitions.SpeedCmd, (byte)wpm });

        var textByte = Gen.Choose(CommandDefinitions.PrintableAsciiStart, CommandDefinitions.PrintableAsciiEnd)
            .Select(b => new byte[] { (byte)b });

        var clearBuffer = Gen.Constant(new byte[] { CommandDefinitions.ClearBufferCmd });

        return Gen.OneOf(speedCmd, textByte, clearBuffer);
    }

    /// <summary>
    /// **Validates: Requirements 1.3, 1.4**
    ///
    /// Property 6.10: Admin Open followed by any valid operations followed by Admin Close
    /// returns to idle state (HostMode=false, empty buffer, default speed).
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ValidOperationsArbitrary) })]
    public void AdminOpenThenOperationsThenClose_ReturnsToIdleState(ValidOperations ops)
    {
        var protocol = CreateProtocol();

        // Open host mode
        AdminOpen(protocol);
        Assert.True(protocol.State.HostMode);

        // Execute random valid operations
        foreach (var op in ops.Operations)
        {
            foreach (var b in op)
            {
                protocol.ProcessByte(b);
            }
        }

        // Close host mode
        AdminClose(protocol);

        // Assert state is back to idle
        Assert.False(protocol.State.HostMode);
        Assert.Empty(protocol.State.TextBuffer);
        Assert.Equal(BufferState.Idle, protocol.State.BufferState);
        Assert.Equal(CommandDefinitions.DefaultWpm, protocol.State.CurrentWpm);
    }

    /// <summary>
    /// **Validates: Requirements 1.5**
    ///
    /// Property 6.11: Setting the same speed twice produces the same state as setting it once (idempotence).
    /// </summary>
    [Property]
    public Property SpeedSetTwice_IsSameAsOnce()
    {
        return Prop.ForAll(
            Arb.From(Gen.Choose(CommandDefinitions.MinWpm, CommandDefinitions.MaxWpm)),
            wpm =>
            {
                // Protocol set once
                var protocol1 = CreateProtocol();
                AdminOpen(protocol1);
                protocol1.ProcessByte(CommandDefinitions.SpeedCmd);
                protocol1.ProcessByte((byte)wpm);

                // Protocol set twice
                var protocol2 = CreateProtocol();
                AdminOpen(protocol2);
                protocol2.ProcessByte(CommandDefinitions.SpeedCmd);
                protocol2.ProcessByte((byte)wpm);
                protocol2.ProcessByte(CommandDefinitions.SpeedCmd);
                protocol2.ProcessByte((byte)wpm);

                // States should be identical
                return (protocol1.State.CurrentWpm == protocol2.State.CurrentWpm)
                    .Label("CurrentWpm matches")
                    .And(() => protocol1.State.HostMode == protocol2.State.HostMode)
                    .Label("HostMode matches")
                    .And(() => protocol1.State.BufferState == protocol2.State.BufferState)
                    .Label("BufferState matches")
                    .And(() => protocol1.State.TextBuffer.Count == protocol2.State.TextBuffer.Count)
                    .Label("TextBuffer count matches");
            });
    }

    /// <summary>
    /// **Validates: Requirements 1.7**
    ///
    /// Property 6.12: Invalid command bytes in non-host mode do not change protocol state.
    /// In non-host mode, anything other than Admin Open (0x00 followed by 0x02) should not change state.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(InvalidByteArbitrary) })]
    public void InvalidBytesInNonHostMode_DoNotChangeState(InvalidByte invalidByte)
    {
        var protocol = CreateProtocol();
        
        // HostMode defaults to true, so close first to get to non-host mode
        AdminClose(protocol);
        Assert.False(protocol.State.HostMode);

        // Record state before
        bool hostModeBefore = protocol.State.HostMode;
        int wpmBefore = protocol.State.CurrentWpm;
        var bufferStateBefore = protocol.State.BufferState;
        int bufferCountBefore = protocol.State.TextBuffer.Count;

        // Process the invalid byte
        protocol.ProcessByte(invalidByte.Value);

        // State should be unchanged
        Assert.Equal(hostModeBefore, protocol.State.HostMode);
        Assert.Equal(wpmBefore, protocol.State.CurrentWpm);
        Assert.Equal(bufferStateBefore, protocol.State.BufferState);
        Assert.Equal(bufferCountBefore, protocol.State.TextBuffer.Count);
    }
}

/// <summary>
/// Wrapper for a sequence of valid host-mode operations for FsCheck.
/// </summary>
public class ValidOperations
{
    public byte[][] Operations { get; }

    public ValidOperations(byte[][] operations)
    {
        Operations = operations;
    }

    public override string ToString()
    {
        return $"ValidOperations[{Operations.Length} ops]";
    }
}

/// <summary>
/// Wrapper for a byte that is not a valid command in non-host mode (anything except 0x00).
/// </summary>
public class InvalidByte
{
    public byte Value { get; }

    public InvalidByte(byte value)
    {
        Value = value;
    }

    public override string ToString() => $"0x{Value:X2}";
}

/// <summary>
/// FsCheck Arbitrary for generating valid host-mode operation sequences.
/// </summary>
public static class ValidOperationsArbitrary
{
    public static Arbitrary<ValidOperations> Arbitrary()
    {
        var genOp = Gen.OneOf(
            // Speed command with valid speed
            Gen.Choose(CommandDefinitions.MinWpm, CommandDefinitions.MaxWpm)
                .Select(wpm => new byte[] { CommandDefinitions.SpeedCmd, (byte)wpm }),
            // Printable text character
            Gen.Choose(CommandDefinitions.PrintableAsciiStart, CommandDefinitions.PrintableAsciiEnd)
                .Select(b => new byte[] { (byte)b }),
            // Clear buffer
            Gen.Constant(new byte[] { CommandDefinitions.ClearBufferCmd })
        );

        var genOps = Gen.ListOf(genOp)
            .Select(ops => new ValidOperations(ops.ToArray()));

        return Arb.From(genOps);
    }
}

/// <summary>
/// FsCheck Arbitrary for generating bytes that are not valid commands in non-host mode.
/// In non-host mode, only 0x00 (Admin prefix) is processed.
/// </summary>
public static class InvalidByteArbitrary
{
    public static Arbitrary<InvalidByte> Arbitrary()
    {
        // Any byte except 0x00 (Admin command prefix) is invalid in non-host mode
        var gen = Gen.Choose(1, 255).Select(b => new InvalidByte((byte)b));
        return Arb.From(gen);
    }
}
