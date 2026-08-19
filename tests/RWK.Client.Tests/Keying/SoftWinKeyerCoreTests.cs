using RWK.Client.Keying;
using RWK.Shared;
using RWK.Shared.Keying;
using Xunit;

namespace RWK.Client.Tests.Keying;

/// <summary>
/// Unit tests for the Client keyer's thread lifecycle and settings surface
/// (Requirements 3.1, 3.8, 3.9, 3.10, 14.6).
/// </summary>
/// <remarks>
/// Only the part that has to involve a real thread is tested here. What the keyer decides to
/// key is decided by <c>KeyerElementPump</c> and is covered deterministically in
/// RWK.Shared.Tests against a fake clock; repeating those assertions through a live thread
/// would only reintroduce the wall-clock races that the RWK v1 <c>SoftKeyer</c> tests had.
/// <para>
/// The live-thread assertions here are deliberately shape-based — "at least one edge
/// arrived", "the key ended up released" — never counts or durations.
/// </para>
/// </remarks>
public class SoftWinKeyerCoreTests
{
    /// <summary>Generous ceiling for a 60 WPM element (dit is 20ms) to arrive.</summary>
    private static readonly TimeSpan EdgeTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void StartsAndStops_WithoutError()
    {
        using var keyer = new SoftWinKeyerCore();

        Assert.False(keyer.IsRunning);

        keyer.Start();
        Assert.True(keyer.IsRunning);

        keyer.Stop();
        Assert.False(keyer.IsRunning);
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        using var keyer = new SoftWinKeyerCore();

        keyer.Start();
        keyer.Start();

        Assert.True(keyer.IsRunning);
        keyer.Stop();
    }

    [Fact]
    public void Stop_WithoutStart_DoesNothing()
    {
        using var keyer = new SoftWinKeyerCore();

        keyer.Stop();

        Assert.False(keyer.IsRunning);
    }

    [Fact]
    public void Dispose_StopsTheThread()
    {
        var keyer = new SoftWinKeyerCore();
        keyer.Start();

        keyer.Dispose();

        Assert.False(keyer.IsRunning);
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var keyer = new SoftWinKeyerCore();
        keyer.Dispose();

        Assert.Throws<ObjectDisposedException>(keyer.Start);
    }

    [Fact]
    public void Speed_ClampsToSupportedRange()
    {
        using var keyer = new SoftWinKeyerCore();

        keyer.SpeedWpm = 100;
        Assert.Equal(60, keyer.SpeedWpm);

        keyer.SpeedWpm = 1;
        Assert.Equal(5, keyer.SpeedWpm);

        keyer.SpeedWpm = 25;
        Assert.Equal(25, keyer.SpeedWpm);
    }

    [Fact]
    public void Weight_ClampsToSupportedRangeAndDefaultsToFifty()
    {
        using var keyer = new SoftWinKeyerCore();

        Assert.Equal(50, keyer.Weight);

        keyer.Weight = 90;
        Assert.Equal(75, keyer.Weight);

        keyer.Weight = 5;
        Assert.Equal(25, keyer.Weight);
    }

    [Fact]
    public void Mode_RoundTripsAllFiveModes()
    {
        using var keyer = new SoftWinKeyerCore();

        Assert.Equal(KeyerMode.IambicB, keyer.Mode);

        foreach (KeyerMode mode in Enum.GetValues<KeyerMode>())
        {
            keyer.Mode = mode;
            Assert.Equal(mode, keyer.Mode);
        }
    }

    /// <summary>
    /// A held paddle contact on a running keyer produces edges, and stopping leaves the key
    /// released (3.8).
    /// </summary>
    [Fact]
    public void HeldPaddle_ProducesEdgesAndStopLeavesKeyUp()
    {
        using var keyer = new SoftWinKeyerCore { SpeedWpm = 60 };

        var edges = new List<EdgeEvent>();
        using var firstEdge = new ManualResetEventSlim(false);

        keyer.EdgeGenerated += (_, edge) =>
        {
            lock (edges)
                edges.Add(edge);
            firstEdge.Set();
        };

        keyer.Start();
        keyer.SetPaddleState(dit: true, dah: false, straight: false, qpcTimestamp: 0);

        Assert.True(firstEdge.Wait(EdgeTimeout), "No edge was generated for a held dit contact.");

        keyer.SetPaddleState(dit: false, dah: false, straight: false, qpcTimestamp: 0);
        keyer.Stop();

        lock (edges)
        {
            Assert.NotEmpty(edges);
            Assert.True(edges[0].KeyDown);
            Assert.Equal(EdgeSource.Paddle, edges[0].Source);

            // Whatever the element count turned out to be, the key must not be left asserted.
            Assert.False(edges[^1].KeyDown);
        }

        Assert.False(keyer.Pump.IsKeyDown);
        Assert.Null(keyer.Fault);
    }

    /// <summary>
    /// Host text sent through the running keyer completes and echoes the character (2.5).
    /// </summary>
    [Fact]
    public void HostText_CompletesAndEchoesCharacter()
    {
        using var keyer = new SoftWinKeyerCore { SpeedWpm = 60 };

        using var completed = new ManualResetEventSlim(false);
        char echoed = '\0';

        keyer.CharacterCompleted += (_, c) =>
        {
            echoed = c;
            completed.Set();
        };

        keyer.Start();
        keyer.EnqueueText("E");

        Assert.True(completed.Wait(EdgeTimeout), "Host text never completed.");
        Assert.Equal('E', echoed);

        keyer.Stop();
        Assert.False(keyer.Pump.IsKeyDown);
    }

    /// <summary>
    /// An immediate key-down held across a stop still ends with the key released (2.4, and the
    /// key-up-on-any-failure policy).
    /// </summary>
    [Fact]
    public void StopWhileImmediateKeyDown_ReleasesKey()
    {
        using var keyer = new SoftWinKeyerCore();

        using var keyedDown = new ManualResetEventSlim(false);
        var edges = new List<EdgeEvent>();

        keyer.EdgeGenerated += (_, edge) =>
        {
            lock (edges)
                edges.Add(edge);

            if (edge.KeyDown)
                keyedDown.Set();
        };

        keyer.Start();
        keyer.SetKeyImmediate(true);

        Assert.True(keyedDown.Wait(EdgeTimeout), "Immediate key-down never keyed.");

        keyer.Stop();

        lock (edges)
            Assert.False(edges[^1].KeyDown);

        Assert.False(keyer.Pump.IsKeyDown);
    }
}
