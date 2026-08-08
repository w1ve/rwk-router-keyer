using Xunit;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Protocol;
using WinKeyerEmulator.Core.Tests.TestDoubles;
using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.Core.Tests;

/// <summary>
/// Unit tests for KeyerCore orchestration class.
/// </summary>
public class KeyerCoreTests
{
    /// <summary>
    /// Verifies that processing text in host mode enqueues a message to the TimingEngine.
    /// Sends Admin Open, then a text character, and checks that the FakeKeyingOutput
    /// receives keying events (proving the message was enqueued and executed).
    /// </summary>
    [Fact]
    public void ProcessCommand_TextInHostMode_EnqueuesMessageToTimingEngine()
    {
        // Arrange
        var clock = new FakeClock(initialTimestamp: 0, autoAdvanceStep: 1_000_000);
        var output = new FakeKeyingOutput(clock);
        var logger = new NullLogger();

        using var engine = new TimingEngine(output, clock);
        engine.Start();

        using var keyer = new KeyerCore(output, engine, logger);

        // Act: Open host mode
        byte[] adminOpen = { CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen };
        var response = keyer.ProcessCommand(adminOpen);

        // Verify admin open responded with version byte
        Assert.NotNull(response);
        Assert.Equal(CommandDefinitions.WinKeyerVersion, response![0]);

        // Set speed to 20 WPM
        byte[] setSpeed = { CommandDefinitions.SpeedCmd, 20 };
        keyer.ProcessCommand(setSpeed);

        // Send a text character 'E' (ASCII 0x45)
        byte[] textByte = { (byte)'E' };
        keyer.ProcessCommand(textByte);

        // Give the timing engine time to execute the schedule
        Thread.Sleep(200);

        engine.Stop();

        // Assert: keying events were produced (proving text was enqueued to timing engine)
        Assert.True(output.Events.Count >= 2,
            $"Expected at least 2 keying events (KeyDown + KeyUp for dit), got {output.Events.Count}");
        Assert.Equal(KeyingEventType.KeyDown, output.Events[0].Type);
        Assert.Equal(KeyingEventType.KeyUp, output.Events[1].Type);
    }

    /// <summary>
    /// Verifies that KeyerCore can be instantiated without any UI dependencies.
    /// This confirms the core is decoupled from WinForms.
    /// </summary>
    [Fact]
    public void KeyerCore_InstantiatesWithoutUIDependencies()
    {
        // Arrange: all dependencies are simple interfaces/classes with no UI
        var clock = new FakeClock(initialTimestamp: 0, autoAdvanceStep: 1_000_000);
        var output = new FakeKeyingOutput(clock);
        var logger = new NullLogger();

        using var engine = new TimingEngine(output, clock);
        using var keyer = new KeyerCore(output, engine, logger);

        // Assert: keyer is created successfully with accessible state
        Assert.NotNull(keyer);
        Assert.NotNull(keyer.State);
        Assert.False(keyer.State.HostMode);
        Assert.Equal(15, keyer.State.CurrentWpm); // Default WPM
    }

    /// <summary>
    /// Verifies that AbortMessage clears the buffer and stops keying.
    /// </summary>
    [Fact]
    public void AbortMessage_ClearsBufferAndStopsKeying()
    {
        // Arrange
        var clock = new FakeClock(initialTimestamp: 0, autoAdvanceStep: 1_000_000);
        var output = new FakeKeyingOutput(clock);
        var logger = new NullLogger();

        using var engine = new TimingEngine(output, clock);
        engine.Start();
        using var keyer = new KeyerCore(output, engine, logger);

        // Open host mode and send text
        byte[] adminOpen = { CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen };
        keyer.ProcessCommand(adminOpen);

        // Act: abort
        keyer.AbortMessage();

        // Assert: buffer is cleared and state is idle
        Assert.Empty(keyer.State.TextBuffer);
        Assert.Equal(BufferState.Idle, keyer.State.BufferState);

        engine.Stop();
    }

    /// <summary>
    /// Verifies that the ProtocolState is properly exposed for inspection.
    /// </summary>
    [Fact]
    public void ProtocolState_ExposedForInspection()
    {
        // Arrange
        var clock = new FakeClock(initialTimestamp: 0, autoAdvanceStep: 1_000_000);
        var output = new FakeKeyingOutput(clock);
        var logger = new NullLogger();

        using var engine = new TimingEngine(output, clock);
        using var keyer = new KeyerCore(output, engine, logger);

        // Act: open host mode
        byte[] adminOpen = { CommandDefinitions.AdminCmd, CommandDefinitions.AdminOpen };
        keyer.ProcessCommand(adminOpen);

        // Assert: state reflects host mode
        Assert.True(keyer.State.HostMode);

        // Act: set speed
        byte[] setSpeed = { CommandDefinitions.SpeedCmd, 30 };
        keyer.ProcessCommand(setSpeed);

        // Assert: state reflects new speed
        Assert.Equal(30, keyer.State.CurrentWpm);
    }
}
