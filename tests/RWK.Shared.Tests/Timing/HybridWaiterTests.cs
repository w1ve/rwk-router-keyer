using RWK.Shared.Tests.TestDoubles;
using RWK.Shared.Timing;
using Xunit;

namespace RWK.Shared.Tests.Timing;

/// <summary>
/// Ported from tests/WinKeyerEmulator.Core.Tests/Timing/HybridWaiterTests.cs to prove the
/// RWK.Shared copy of HybridWaiter is behavior-preserving.
/// </summary>
public class HybridWaiterTests
{
    [Fact]
    public void WaitUntil_WithFakeClock_ReturnsAfterTargetIsReached()
    {
        // Arrange: start at 0, target at 100_000 ticks, advance 20_000 per call
        // The clock auto-advances so HybridWaiter will eventually see timestamps >= target.
        var clock = new FakeClock(initialTimestamp: 0, autoAdvanceStep: 20_000);
        long target = 100_000;

        // Act: should not hang because FakeClock advances past target
        HybridWaiter.WaitUntil(target, clock);

        // Assert: GetTimestamp was called multiple times and the final value is past target
        Assert.True(clock.CallCount > 1, "Expected multiple GetTimestamp calls during wait.");
        // After the last call inside WaitUntil, CurrentTimestamp should be >= target
        Assert.True(clock.CurrentTimestamp >= target,
            $"Expected clock to advance past target {target}, but was {clock.CurrentTimestamp}.");
    }

    [Fact]
    public void WaitUntil_TargetAlreadyPassed_ReturnsImmediately()
    {
        // Arrange: clock is already past target
        var clock = new FakeClock(initialTimestamp: 200_000, autoAdvanceStep: 1000);
        long target = 100_000;

        // Act
        HybridWaiter.WaitUntil(target, clock);

        // Assert: should return after minimal calls (first check sees we're past)
        Assert.True(clock.CallCount <= 2,
            "Expected immediate return when target already passed.");
    }

    [Fact]
    public void WaitUntil_SmallRemainingTime_SkipsSleepPhase()
    {
        // Arrange: remaining time is less than spin threshold (~1.5ms = 15000 ticks at 10MHz)
        // So it should go straight to spin phase.
        var clock = new FakeClock(initialTimestamp: 90_000, autoAdvanceStep: 5_000);
        long target = 100_000; // Only 10_000 ticks away (< 15_000 threshold)

        // Act
        HybridWaiter.WaitUntil(target, clock);

        // Assert: returned successfully
        Assert.True(clock.CurrentTimestamp >= target);
    }
}
