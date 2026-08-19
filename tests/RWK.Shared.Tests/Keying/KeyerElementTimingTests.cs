using RWK.Shared.Keying;
using RWK.Shared.Timing;
using Xunit;

namespace RWK.Shared.Tests.Keying;

/// <summary>
/// Unit tests for element and gap durations (Requirements 3.9, 3.10).
/// </summary>
public class KeyerElementTimingTests
{
    private const long Freq = 10_000_000L; // 10 MHz, matching FakeClock and typical QPC.

    private static long Ms(long ticks) => ticks * 1000 / Freq;

    /// <summary>
    /// Dit duration is 1200/WPM milliseconds (3.10).
    /// </summary>
    [Theory]
    [InlineData(5, 240)]
    [InlineData(20, 60)]
    [InlineData(25, 48)]
    [InlineData(40, 30)]
    [InlineData(60, 20)]
    public void DitDuration_Is1200OverWpmMilliseconds(int wpm, long expectedMs)
    {
        var timing = KeyerElementTiming.FromSpeed(wpm, KeyerElementTiming.DefaultWeight, Freq);

        Assert.Equal(expectedMs, Ms(timing.DitTicks));
    }

    [Fact]
    public void DahDuration_IsThreeDits()
    {
        var timing = KeyerElementTiming.FromSpeed(25, KeyerElementTiming.DefaultWeight, Freq);

        Assert.Equal(3 * timing.DitTicks, timing.DahTicks);
    }

    [Fact]
    public void DefaultWeight_MakesElementAndGapEqual()
    {
        var timing = KeyerElementTiming.FromSpeed(25, KeyerElementTiming.DefaultWeight, Freq);

        Assert.Equal(timing.DitTicks, timing.GapTicks);
    }

    /// <summary>
    /// Speed is clamped to 5-60 WPM (3.10). Out-of-range values produce the boundary timing
    /// rather than an exception, because speed arrives from a WK2 register and from a UI
    /// control, neither of which should be able to break keying.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void SpeedBelowRange_ClampsToFiveWpm(int wpm)
    {
        var clamped = KeyerElementTiming.FromSpeed(wpm, 50, Freq);
        var atMin = KeyerElementTiming.FromSpeed(KeyerElementTiming.MinWpm, 50, Freq);

        Assert.Equal(atMin, clamped);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(200)]
    public void SpeedAboveRange_ClampsToSixtyWpm(int wpm)
    {
        var clamped = KeyerElementTiming.FromSpeed(wpm, 50, Freq);
        var atMax = KeyerElementTiming.FromSpeed(KeyerElementTiming.MaxWpm, 50, Freq);

        Assert.Equal(atMax, clamped);
    }

    /// <summary>
    /// Weight is clamped to 25-75% (3.9).
    /// </summary>
    [Theory]
    [InlineData(0, KeyerElementTiming.MinWeight)]
    [InlineData(10, KeyerElementTiming.MinWeight)]
    [InlineData(90, KeyerElementTiming.MaxWeight)]
    [InlineData(100, KeyerElementTiming.MaxWeight)]
    public void WeightOutsideRange_Clamps(int weight, int expectedEquivalent)
    {
        var clamped = KeyerElementTiming.FromSpeed(25, weight, Freq);
        var expected = KeyerElementTiming.FromSpeed(25, expectedEquivalent, Freq);

        Assert.Equal(expected, clamped);
    }

    /// <summary>
    /// Weight shifts duration between element and gap while the element-plus-gap cycle stays
    /// at two base dits, so raising weight makes keying heavier without making it slower (3.9).
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    public void Weight_PreservesElementPlusGapCycle(int weight)
    {
        var reference = KeyerElementTiming.FromSpeed(25, 50, Freq);
        var timing = KeyerElementTiming.FromSpeed(25, weight, Freq);

        Assert.Equal(2 * reference.DitTicks, timing.DitTicks + timing.GapTicks);
    }

    [Fact]
    public void HeavyWeight_LengthensElementAndShortensGap()
    {
        var standard = KeyerElementTiming.FromSpeed(25, 50, Freq);
        var heavy = KeyerElementTiming.FromSpeed(25, 75, Freq);

        Assert.True(heavy.DitTicks > standard.DitTicks);
        Assert.True(heavy.GapTicks < standard.GapTicks);
    }

    [Fact]
    public void BaseDit_IsWeightIndependent()
    {
        var light = KeyerElementTiming.FromSpeed(25, 25, Freq);
        var standard = KeyerElementTiming.FromSpeed(25, 50, Freq);
        var heavy = KeyerElementTiming.FromSpeed(25, 75, Freq);

        Assert.Equal(standard.BaseDitTicks, light.BaseDitTicks);
        Assert.Equal(standard.BaseDitTicks, heavy.BaseDitTicks);
        Assert.Equal(7 * standard.BaseDitTicks, heavy.WordGapTicks);
    }

    /// <summary>
    /// Paddle element timing matches what <see cref="EdgeScheduleBuilder"/> produces for the
    /// host path, so the same character has the same timing from either source (3.9).
    /// </summary>
    [Theory]
    [InlineData(25, 50)]
    [InlineData(40, 75)]
    [InlineData(12, 25)]
    public void ElementTiming_MatchesHostPathScheduleBuilder(int wpm, int weight)
    {
        var timing = KeyerElementTiming.FromSpeed(wpm, weight, Freq);

        // "E" is a single dit: edges are [key-down at 0, key-up at dit].
        long[] dit = EdgeScheduleBuilder.Build("E", wpm, Freq, weight);
        Assert.Equal(timing.DitTicks, dit[1] - dit[0]);

        // "T" is a single dah.
        long[] dah = EdgeScheduleBuilder.Build("T", wpm, Freq, weight);
        Assert.Equal(timing.DahTicks, dah[1] - dah[0]);

        // "I" is two dits: the gap between them is the intra-character gap.
        long[] twoDits = EdgeScheduleBuilder.Build("I", wpm, Freq, weight);
        Assert.Equal(timing.GapTicks, twoDits[2] - twoDits[1]);
    }

    [Fact]
    public void NonPositiveFrequency_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyerElementTiming.FromSpeed(25, 50, 0));
    }

    [Fact]
    public void TicksFor_None_IsZero()
    {
        var timing = KeyerElementTiming.FromSpeed(25, 50, Freq);

        Assert.Equal(0, timing.TicksFor(KeyerElement.None));
        Assert.Equal(timing.DitTicks, timing.TicksFor(KeyerElement.Dit));
        Assert.Equal(timing.DahTicks, timing.TicksFor(KeyerElement.Dah));
    }
}
