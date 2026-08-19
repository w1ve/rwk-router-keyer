/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using WinKeyerEmulator.Core.Protocol;
using Xunit;

namespace WinKeyerEmulator.Integration.Tests;

/// <summary>
/// Integration tests that validate WinKeyer protocol behavior over UDP.
/// Each test creates a UdpTestServer (KeyerCore + UDP listener on dynamic port)
/// and a UdpTestClient that sends command datagrams and asserts on responses.
/// </summary>
public class UdpProtocolTests : IDisposable
{
    private readonly UdpTestServer _server;
    private readonly UdpTestClient _client;

    public UdpProtocolTests()
    {
        _server = new UdpTestServer();
        _client = new UdpTestClient(_server.Port);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    /// <summary>
    /// 18.3: Admin Open command (0x00 0x02) returns the correct version byte (23).
    /// </summary>
    [Fact]
    public async Task AdminOpen_ReturnsCorrectVersionByte()
    {
        // Send Admin Open: 0x00 (admin prefix) + 0x02 (open sub-command)
        var response = await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);

        Assert.NotNull(response);
        // Response is version byte + idle status (0xC0)
        Assert.True(response.Length >= 1);
        Assert.Equal(CommandDefinitions.WinKeyerVersion, response[0]);
    }

    /// <summary>
    /// 18.4: Admin Close after Open returns to idle (no further responses to text).
    /// </summary>
    [Fact]
    public async Task AdminClose_AfterOpen_ReturnsToIdle_NoResponseToText()
    {
        // Open host mode
        await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);

        // Close host mode - no response expected
        await _client.SendAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminClose);

        // Give server time to process the close command
        await Task.Delay(50);

        // Send a text character - should NOT produce a response since we're not in host mode
        await _client.SendAsync(0x41); // 'A'

        var noResponse = await _client.ExpectNoResponseAsync();
        Assert.True(noResponse, "Expected no response after Admin Close, but got one");

        // Verify state is back to idle
        Assert.False(_server.Core.State.HostMode);
    }

    /// <summary>
    /// 18.5: Speed Set to 20 WPM followed by text buffer produces status byte responses.
    /// </summary>
    [Fact]
    public async Task SpeedSet_FollowedByText_ProducesEchoResponse()
    {
        // Open host mode
        await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);

        // Set speed to 20 WPM - no response expected
        await _client.SendAsync(CommandDefinitions.SpeedCmd, 20);
        await Task.Delay(50);

        // Send a text character 'H' (0x48) - no immediate response, but async echo will come
        await _client.SendAsync(0x48);

        // Wait for the async echo response (character echo + idle status)
        var response = await _client.ReceiveAsync();
        Assert.NotNull(response);
        // Should contain the echoed character 'H' and idle status 0xC0
        Assert.Contains((byte)'H', response);

        // Verify speed was set correctly
        Assert.Equal(20, _server.Core.State.CurrentWpm);
    }

    /// <summary>
    /// 18.6: Clear Buffer command stops pending text transmission.
    /// </summary>
    [Fact]
    public async Task ClearBuffer_StopsPendingTextTransmission()
    {
        // Open host mode
        await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);

        // Send text to fill buffer - this triggers async echo
        await _client.SendAsync(0x43); // 'C'
        await Task.Delay(100); // Wait for echo

        // Drain any pending responses
        await _client.ReceiveAsync();

        // Send Clear Buffer command (0x0A) - no response expected
        await _client.SendAsync(CommandDefinitions.ClearBufferCmd);
        await Task.Delay(50);

        // Verify buffer is now idle
        Assert.Equal(BufferState.Idle, _server.Core.State.BufferState);
        Assert.Empty(_server.Core.State.TextBuffer);
    }

    /// <summary>
    /// 18.7: Multiple commands in sequence maintain correct protocol state.
    /// </summary>
    [Fact]
    public async Task MultipleCommands_InSequence_MaintainCorrectState()
    {
        // 1. Open host mode
        var openResponse = await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);
        Assert.NotNull(openResponse);
        Assert.Equal(CommandDefinitions.WinKeyerVersion, openResponse[0]);
        Assert.True(_server.Core.State.HostMode);

        // 2. Set speed to 25 WPM
        await _client.SendAsync(CommandDefinitions.SpeedCmd, 25);
        await Task.Delay(50);
        Assert.Equal(25, _server.Core.State.CurrentWpm);

        // 3. Send text 'T' - async echo
        await _client.SendAsync(0x54); // 'T'
        await Task.Delay(100);
        // Drain echo
        await _client.ReceiveAsync();

        // 4. Change speed to 30 WPM
        await _client.SendAsync(CommandDefinitions.SpeedCmd, 30);
        await Task.Delay(50);
        Assert.Equal(30, _server.Core.State.CurrentWpm);

        // 5. Send another text character - async echo
        await _client.SendAsync(0x45); // 'E'
        await Task.Delay(100);
        await _client.ReceiveAsync();

        // 6. Clear buffer
        await _client.SendAsync(CommandDefinitions.ClearBufferCmd);
        await Task.Delay(50);
        Assert.Equal(BufferState.Idle, _server.Core.State.BufferState);

        // 7. Close host mode
        await _client.SendAsync(CommandDefinitions.AdminCmd, CommandDefinitions.AdminClose);
        await Task.Delay(50);
        Assert.False(_server.Core.State.HostMode);
        Assert.Equal(CommandDefinitions.DefaultWpm, _server.Core.State.CurrentWpm);
    }

    /// <summary>
    /// 18.8: Invalid command bytes are ignored without breaking session.
    /// </summary>
    [Fact]
    public async Task InvalidCommandBytes_AreIgnored_WithoutBreakingSession()
    {
        // Open host mode
        var openResponse = await _client.SendAndReceiveAsync(
            CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen);
        Assert.NotNull(openResponse);

        // Send invalid/unrecognized bytes (0x7F is beyond printable ASCII, 0xFF is invalid)
        await _client.SendAsync(0x7F);
        await Task.Delay(50);

        await _client.SendAsync(0xFF);
        await Task.Delay(50);

        // Verify session is still alive - host mode still active
        Assert.True(_server.Core.State.HostMode);

        // Verify the protocol still responds correctly to valid commands
        // Set speed - should work fine
        await _client.SendAsync(CommandDefinitions.SpeedCmd, 22);
        await Task.Delay(50);
        Assert.Equal(22, _server.Core.State.CurrentWpm);

        // Send valid text - async echo response
        await _client.SendAsync(0x41); // 'A'
        await Task.Delay(100);
        var response = await _client.ReceiveAsync();
        Assert.NotNull(response);
        Assert.Contains((byte)'A', response);

        // Session remains functional
        Assert.True(_server.Core.State.HostMode);
    }
}
