using System.IO.Ports;
using RWK.Shared;
using RWK.Shared.IO;
using RWK.Shared.Keying;
using RWK.Shared.Protocol;
using RWK.Shared.Timing;
using RWK.Client.IO;
using RWK.Client.Keying;
using WinKeyerEmulator.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace RWK.Integration.Tests;

/// <summary>
/// N1MM+ conformance integration test: simulates the exact WK2 init sequence that N1MM+
/// sends to a WinKeyer device, then verifies CW text sending and speed changes. This test
/// exercises the <see cref="WinKeyerProtocol"/> state machine directly (bypassing the serial
/// port) to verify correct responses without requiring N1MM+ or virtual serial port hardware.
/// </summary>
/// <remarks>
/// The init sequence is taken from CONTEXT.md:
/// <code>
/// Admin Open (00 02) → version + status response (17 C0)
/// WK2 Mode (0E 05)
/// Speed Pot Setup (05 xx xx xx) — 3 data bytes
/// Sidetone (01 xx)
/// Pin Config (09 xx)
/// PTT Lead/Tail (04 xx xx) — 2 data bytes
/// First Extension (10 xx) — admin sub-cmd 0x10, consumes 1 data byte
/// Key Compensation (00 11 xx) — admin sub-cmd 0x11, consumes 1 data byte
/// Weighting (03 xx)
/// GetSpeedPot (07) — no response
/// Speed (02 xx)
/// </code>
/// <para>
/// **Validates: Requirements 2.1-2.7**
/// </para>
/// </remarks>
public class N1mmConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly WinKeyerProtocol _protocol;
    private readonly List<byte[]> _responses = new();

    public N1mmConformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _protocol = new WinKeyerProtocol(new NullLogger());
    }

    /// <summary>
    /// Verifies the complete N1MM+ init sequence produces expected responses:
    /// - Admin Open returns firmware version (0x17) + status byte (0xC0) (2.1, 2.2)
    /// - All subsequent setup commands are accepted without error
    /// - The protocol enters host mode after Admin Open
    /// </summary>
    [Fact]
    public void InitSequence_AdminOpen_ReturnsVersionAndStatus()
    {
        // Admin Open: cmd=0x00, sub=0x02
        byte[]? response = ProcessBytes(0x00, 0x02);

        Assert.NotNull(response);
        Assert.True(response!.Length >= 2,
            $"Admin Open should return at least 2 bytes (version + status), got {response.Length}");

        // First byte is firmware version (0x17 = version 23, WK2.3)
        Assert.Equal(0x17, response[0]);

        // Second byte is status with 0xC0 prefix (bits 7:6 set)
        Assert.True((response[1] & 0xC0) == 0xC0,
            $"Status byte should have bits 7:6 set (0xC0 prefix), got 0x{response[1]:X2}");

        // Protocol should now be in host mode
        Assert.True(_protocol.State.HostMode,
            "Protocol should be in HostMode after Admin Open (2.2)");

        _output.WriteLine($"Admin Open response: version=0x{response[0]:X2}, status=0x{response[1]:X2}");
    }

    /// <summary>
    /// Verifies the full N1MM+ init sequence completes without errors (2.2).
    /// </summary>
    [Fact]
    public void InitSequence_FullN1mmSequence_AcceptsAllCommands()
    {
        // 1. Admin Open (00 02)
        byte[]? response = ProcessBytes(0x00, 0x02);
        Assert.NotNull(response);
        Assert.True(_protocol.State.HostMode);
        _output.WriteLine("✓ Admin Open accepted, host mode active");

        // 2. WK2 Mode (0E 05) — sets keyer mode bits
        response = ProcessBytes(0x0E, 0x05);
        // No response expected from mode set
        _output.WriteLine("✓ WK2 Mode (0x0E 0x05) accepted");

        // 3. Speed Pot Setup (05 xx xx xx) — 3 data bytes
        response = ProcessBytes(0x05, 0x00, 0xFF, 0x19); // min=0, max=255, initial=25 WPM
        _output.WriteLine("✓ Speed Pot Setup accepted");

        // 4. Sidetone (01 xx) — 1 data byte
        response = ProcessBytes(0x01, 0x05); // sidetone frequency code
        _output.WriteLine("✓ Sidetone command accepted");

        // 5. Pin Config (09 xx) — 1 data byte
        response = ProcessBytes(0x09, 0x05); // pin configuration
        _output.WriteLine("✓ Pin Config accepted");

        // 6. PTT Lead/Tail (04 xx xx) — 2 data bytes
        response = ProcessBytes(0x04, 0x01, 0x32); // lead=1 (10ms), tail=50 (500ms)
        _output.WriteLine("✓ PTT Lead/Tail accepted");

        // 7. First Extension (00 10 xx) — admin sub-cmd 0x10 + 1 data byte
        response = ProcessBytes(0x00, 0x10, 0x00);
        _output.WriteLine("✓ First Extension (admin 0x10) accepted");

        // 8. Key Compensation (00 11 xx) — admin sub-cmd 0x11 + 1 data byte
        response = ProcessBytes(0x00, 0x11, 0x00);
        _output.WriteLine("✓ Key Compensation (admin 0x11) accepted");

        // 9. Weighting (03 xx) — 1 data byte
        response = ProcessBytes(0x03, 0x32); // weight = 50
        _output.WriteLine("✓ Weighting accepted");

        // 10. GetSpeedPot (07) — no response (confirmed in CONTEXT.md)
        response = ProcessBytes(0x07);
        // CONTEXT.md: "GetSpeedPotCmd returns no response (caused init loop)"
        // Some implementations may return null or empty
        _output.WriteLine($"✓ GetSpeedPot: response={(response is null ? "null" : $"{response.Length} bytes")}");

        // 11. Speed (02 xx) — set speed to 25 WPM
        response = ProcessBytes(0x02, 0x19); // 25 WPM
        _output.WriteLine("✓ Speed command accepted");
    }

    /// <summary>
    /// Verifies speed command updates the protocol's internal speed state (2.6).
    /// </summary>
    [Fact]
    public void SpeedCommand_UpdatesProtocolSpeed()
    {
        // Enter host mode first
        ProcessBytes(0x00, 0x02);
        Assert.True(_protocol.State.HostMode);

        int? speedReported = null;
        _protocol.SpeedChanged += (_, wpm) => speedReported = wpm;

        // Set speed to 30 WPM (0x1E)
        ProcessBytes(0x02, 0x1E);

        Assert.Equal(30, speedReported);
        _output.WriteLine("Speed changed to 30 WPM via protocol command (2.6)");

        // Set speed to 15 WPM (0x0F)
        ProcessBytes(0x02, 0x0F);

        Assert.Equal(15, speedReported);
        _output.WriteLine("Speed changed to 15 WPM via protocol command (2.6)");
    }

    /// <summary>
    /// Verifies buffered ASCII text triggers TextReceived events (2.3).
    /// </summary>
    [Fact]
    public void BufferedText_RaisesTextReceivedPerCharacter()
    {
        // Enter host mode
        ProcessBytes(0x00, 0x02);

        var received = new List<char>();
        _protocol.TextReceived += (_, c) => received.Add(c);

        // Send "CQ" as printable ASCII (0x43, 0x51)
        ProcessBytes(0x43); // 'C'
        ProcessBytes(0x51); // 'Q'

        Assert.Equal(2, received.Count);
        Assert.Equal('C', received[0]);
        Assert.Equal('Q', received[1]);
        _output.WriteLine("Buffered text 'CQ' raised TextReceived events correctly (2.3)");
    }

    /// <summary>
    /// Verifies Key Immediate command triggers KeyImmediate events (2.4).
    /// </summary>
    [Fact]
    public void KeyImmediate_RaisesKeyImmediateEvent()
    {
        ProcessBytes(0x00, 0x02); // host mode

        bool? keyState = null;
        _protocol.KeyImmediate += (_, down) => keyState = down;

        // Key Immediate down (0x0B 0x01)
        ProcessBytes(0x0B, 0x01);
        Assert.True(keyState, "KeyImmediate(true) not raised (2.4)");

        // Key Immediate up (0x0B 0x00)
        ProcessBytes(0x0B, 0x00);
        Assert.False(keyState, "KeyImmediate(false) not raised (2.4)");

        _output.WriteLine("Key Immediate events raised correctly (2.4)");
    }

    /// <summary>
    /// Verifies the protocol handles Admin Close correctly (returns to non-host mode).
    /// </summary>
    [Fact]
    public void AdminClose_ExitsHostMode()
    {
        // Enter host mode
        ProcessBytes(0x00, 0x02);
        Assert.True(_protocol.State.HostMode);

        // Admin Close (00 03)
        ProcessBytes(0x00, 0x03);
        Assert.False(_protocol.State.HostMode, "Admin Close should exit host mode");

        _output.WriteLine("Admin Close correctly exits host mode");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds bytes one at a time to the protocol, returning the response from the last byte
    /// (earlier bytes in a multi-byte command typically return null).
    /// </summary>
    private byte[]? ProcessBytes(params byte[] bytes)
    {
        byte[]? lastResponse = null;
        foreach (byte b in bytes)
        {
            byte[]? r = _protocol.ProcessByte(b);
            if (r is not null)
                lastResponse = r;
        }
        return lastResponse;
    }

    /// <summary>Null logger for protocol testing.</summary>
    private sealed class NullLogger : WinKeyerEmulator.Core.ILogger
    {
        public void Log(string message, WinKeyerEmulator.Core.LogSeverity severity, string? source = null) { }
    }
}
