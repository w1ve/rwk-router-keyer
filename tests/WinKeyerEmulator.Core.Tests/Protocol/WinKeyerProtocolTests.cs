using WinKeyerEmulator.Core.Protocol;
using WinKeyerEmulator.Core.Tests.TestDoubles;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Protocol;

/// <summary>
/// Example-based tests for WinKeyerProtocol.
/// </summary>
public class WinKeyerProtocolTests
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

    // ===== Sub-task 6.13: Admin Open response contains correct version byte =====

    [Fact]
    public void AdminOpen_RespondsWithVersionByte()
    {
        var protocol = CreateProtocol();

        // Send Admin command prefix
        var response1 = protocol.ProcessByte(CommandDefinitions.AdminCmd);
        Assert.Null(response1); // No response for first byte

        // Send Open sub-command
        var response2 = protocol.ProcessByte(CommandDefinitions.AdminOpen);
        Assert.NotNull(response2);
        Assert.Equal(2, response2.Length); // Version byte + idle status
        Assert.Equal(CommandDefinitions.WinKeyerVersion, response2[0]);
        Assert.Equal(0xC0, response2[1]); // Idle status
    }

    [Fact]
    public void AdminOpen_SetsHostModeTrue()
    {
        var protocol = CreateProtocol();
        Assert.False(protocol.State.HostMode);

        AdminOpen(protocol);
        Assert.True(protocol.State.HostMode);
    }

    [Fact]
    public void AdminClose_SetsHostModeFalse()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);
        Assert.True(protocol.State.HostMode);

        AdminClose(protocol);
        Assert.False(protocol.State.HostMode);
    }

    [Fact]
    public void AdminClose_ClearsBuffer()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        // Queue some text
        protocol.ProcessByte((byte)'H');
        protocol.ProcessByte((byte)'I');
        Assert.NotEmpty(protocol.State.TextBuffer);

        AdminClose(protocol);
        Assert.Empty(protocol.State.TextBuffer);
        Assert.Equal(BufferState.Idle, protocol.State.BufferState);
    }

    [Fact]
    public void AdminClose_ResetsSpeed()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        // Set speed
        protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        protocol.ProcessByte(30);
        Assert.Equal(30, protocol.State.CurrentWpm);

        AdminClose(protocol);
        Assert.Equal(CommandDefinitions.DefaultWpm, protocol.State.CurrentWpm);
    }

    // ===== Sub-task 6.14: Speed set to 25 WPM followed by text produces correct status transitions =====

    [Fact]
    public void SpeedSet25_FollowedByText_ProducesCorrectStatusTransitions()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        // Set speed to 25 WPM
        var resp1 = protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        Assert.Null(resp1); // Waiting for speed byte

        var resp2 = protocol.ProcessByte(25);
        Assert.Null(resp2); // Speed set, no response
        Assert.Equal(25, protocol.State.CurrentWpm);

        // Initially idle
        Assert.Equal(BufferState.Idle, protocol.State.BufferState);

        // Send text character - no immediate response (echo comes asynchronously)
        var resp3 = protocol.ProcessByte((byte)'C');
        Assert.Null(resp3); // Text characters don't produce immediate response

        // But state should transition to Sending and TextReceived event fires
        Assert.Equal(BufferState.Sending, protocol.State.BufferState);
    }

    [Fact]
    public void SpeedSet_ValidRange_UpdatesWpm()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        protocol.ProcessByte(20);
        Assert.Equal(20, protocol.State.CurrentWpm);
    }

    [Fact]
    public void SpeedSet_BelowMinimum_RejectedAndLogsWarning()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        protocol.ProcessByte(3); // Below minimum of 5

        Assert.Equal(CommandDefinitions.DefaultWpm, protocol.State.CurrentWpm); // Unchanged
        Assert.Contains(_logger.Entries, e => e.Severity == LogSeverity.Warning && e.Message.Contains("outside valid range"));
    }

    [Fact]
    public void SpeedSet_AboveMaximum_RejectedAndLogsWarning()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        protocol.ProcessByte(50); // Above maximum of 45

        Assert.Equal(CommandDefinitions.DefaultWpm, protocol.State.CurrentWpm); // Unchanged
        Assert.Contains(_logger.Entries, e => e.Severity == LogSeverity.Warning && e.Message.Contains("outside valid range"));
    }

    [Fact]
    public void TextBuffer_QueuesPrintableAscii()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte((byte)'H');
        protocol.ProcessByte((byte)'E');
        protocol.ProcessByte((byte)'L');

        Assert.Equal(3, protocol.State.TextBuffer.Count);
        Assert.Equal('H', protocol.State.TextBuffer.Dequeue());
        Assert.Equal('E', protocol.State.TextBuffer.Dequeue());
        Assert.Equal('L', protocol.State.TextBuffer.Dequeue());
    }

    [Fact]
    public void TextBuffer_RaisesTextReceivedEvent()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        var receivedChars = new List<char>();
        protocol.TextReceived += (_, c) => receivedChars.Add(c);

        protocol.ProcessByte((byte)'A');
        protocol.ProcessByte((byte)'B');

        Assert.Equal(new[] { 'A', 'B' }, receivedChars);
    }

    [Fact]
    public void ClearBuffer_EmptiesTextQueue()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte((byte)'X');
        protocol.ProcessByte((byte)'Y');
        Assert.Equal(2, protocol.State.TextBuffer.Count);

        protocol.ProcessByte(CommandDefinitions.ClearBufferCmd);
        Assert.Empty(protocol.State.TextBuffer);
        Assert.Equal(BufferState.Idle, protocol.State.BufferState);
    }

    [Fact]
    public void NonHostMode_IgnoresNonAdminBytes()
    {
        var protocol = CreateProtocol();
        Assert.False(protocol.State.HostMode);

        // Try speed command - should be ignored
        var resp = protocol.ProcessByte(CommandDefinitions.SpeedCmd);
        Assert.Null(resp);

        // Try text - should be ignored
        resp = protocol.ProcessByte((byte)'A');
        Assert.Null(resp);

        // State should be unchanged
        Assert.False(protocol.State.HostMode);
        Assert.Equal(CommandDefinitions.DefaultWpm, protocol.State.CurrentWpm);
        Assert.Empty(protocol.State.TextBuffer);
    }

    [Fact]
    public void StatusByte_IdleState_HasC0Prefix()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        var status = protocol.GetStatusByte();
        // Idle status = 0xC0 (bits 7:6 set, no other flags)
        Assert.Equal(0xC0, status);
    }

    [Fact]
    public void StatusByte_SendingState_HasBusyBit()
    {
        var protocol = CreateProtocol();
        AdminOpen(protocol);

        protocol.ProcessByte((byte)'T'); // Puts into Sending state

        var status = protocol.GetStatusByte();
        // Should have 0xC0 prefix plus sending/busy bits
        Assert.True((status & 0xC0) == 0xC0, "Status byte must have 0xC0 prefix");
        Assert.True((status & 0x04) != 0, "Should indicate sending");
    }
}
