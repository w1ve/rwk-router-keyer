using RWK.Shared;
using RWK.Shared.Keying;
using RWK.Shared.Tests.TestDoubles;
using Xunit;

namespace RWK.Shared.Tests.Keying;

/// <summary>
/// Unit tests for the caller-driven keyer pump (Requirements 3.1-3.10).
/// </summary>
/// <remarks>
/// These are the RWK v1 <c>SoftKeyerTests</c> carried forward. The v1 tests started a
/// background timing thread, slept, and asserted on decoded ASCII; they were races. Here the
/// test is the timing thread: it calls <see cref="KeyerElementPump.Pump"/> itself, and a
/// <see cref="VirtualKeyerWait"/> advances a <see cref="FakeClock"/> instead of anything
/// sleeping. Edge counts and element durations are therefore exact rather than probable.
/// <para>
/// The v1 decode assertions ("a dit tap decodes as E") have no equivalent: v2 emits edges
/// rather than characters, so what used to be checked through a Morse decoder is now checked
/// directly on the edge stream.
/// </para>
/// </remarks>
public class KeyerElementPumpTests
{
    private const int Wpm = 25;
    private const long Freq = 10_000_000L;
    private const long Dit = Freq * 1200 / (Wpm * 1000); // 480,000 ticks = 48ms
    private const long Dah = 3 * Dit;

    private static (KeyerElementPump Pump, FakeClock Clock, VirtualKeyerWait Waiter, List<EdgeEvent> Edges) NewPump(
        KeyerMode mode = KeyerMode.IambicB)
    {
        var clock = new FakeClock();
        var waiter = new VirtualKeyerWait(clock);
        var pump = new KeyerElementPump(clock, waiter.Wait) { SpeedWpm = Wpm, Mode = mode };

        var edges = new List<EdgeEvent>();
        pump.EdgeGenerated += (_, e) => edges.Add(e);

        return (pump, clock, waiter, edges);
    }

    private static void AssertAlternatingFromKeyDown(List<EdgeEvent> edges)
    {
        for (int i = 0; i < edges.Count; i++)
            Assert.Equal(i % 2 == 0, edges[i].KeyDown);
    }

    // ── Configuration ────────────────────────────────────────────────────────

    [Fact]
    public void Speed_ClampsToSupportedRange()
    {
        var (pump, _, _, _) = NewPump();

        pump.SpeedWpm = 100;
        Assert.Equal(60, pump.SpeedWpm);

        pump.SpeedWpm = 1;
        Assert.Equal(5, pump.SpeedWpm);

        pump.SpeedWpm = 25;
        Assert.Equal(25, pump.SpeedWpm);
    }

    [Fact]
    public void Weight_ClampsToSupportedRangeAndDefaultsToFifty()
    {
        var (pump, _, _, _) = NewPump();

        Assert.Equal(50, pump.Weight);

        pump.Weight = 100;
        Assert.Equal(75, pump.Weight);

        pump.Weight = 0;
        Assert.Equal(25, pump.Weight);
    }

    [Fact]
    public void Mode_RoundTrips()
    {
        var (pump, _, _, _) = NewPump();

        pump.Mode = KeyerMode.IambicA;
        Assert.Equal(KeyerMode.IambicA, pump.Mode);

        pump.Mode = KeyerMode.Bug;
        Assert.Equal(KeyerMode.Bug, pump.Mode);

        pump.Mode = KeyerMode.Straight;
        Assert.Equal(KeyerMode.Straight, pump.Mode);
    }

    [Fact]
    public void IdlePaddles_PumpDoesNothing()
    {
        var (pump, clock, _, edges) = NewPump();

        Assert.Equal(PumpAction.Idle, pump.Pump());
        Assert.Empty(edges);
        Assert.Equal(0, clock.CurrentTimestamp);
        Assert.False(pump.IsKeyDown);
    }

    // ── Paddle path ──────────────────────────────────────────────────────────

    /// <summary>
    /// One dit tap produces exactly one key-down/key-up pair of dit length. This is the v1
    /// "decodes E from a single dit" test with the decoder removed: what it was really
    /// asserting is that a single tap yields a single element.
    /// </summary>
    [Fact]
    public void SingleDitTap_ProducesOneDitLongKeyDown()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetPaddleState(dit: true, dah: false, straight: false);
        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        pump.SetPaddleState(dit: false, dah: false, straight: false);

        Assert.Equal(PumpAction.Idle, pump.Pump());

        Assert.Equal(2, edges.Count);
        AssertAlternatingFromKeyDown(edges);
        Assert.Equal(Dit, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
        Assert.All(edges, e => Assert.Equal(EdgeSource.Paddle, e.Source));
        Assert.False(pump.IsKeyDown);
    }

    /// <summary>
    /// One dah tap produces one key-down of three dit lengths (the v1 "decodes T" test).
    /// </summary>
    [Fact]
    public void SingleDahTap_ProducesOneDahLongKeyDown()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetPaddleState(dit: false, dah: true, straight: false);
        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        pump.SetPaddleState(dit: false, dah: false, straight: false);

        Assert.Equal(PumpAction.Idle, pump.Pump());

        Assert.Equal(2, edges.Count);
        Assert.Equal(Dah, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
    }

    /// <summary>
    /// A held contact keeps producing elements, one per pump call, separated by a one-gap
    /// key-up. The v1 test that looked like a decode failure ("E came out as I") was this
    /// behavior arriving on time: a second element from a still-closed contact is correct,
    /// and it is asserted here rather than raced against.
    /// </summary>
    [Fact]
    public void HeldDitContact_ProducesOneElementPerPumpCall()
    {
        var (pump, _, _, edges) = NewPump();
        pump.SetPaddleState(dit: true, dah: false, straight: false);

        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        Assert.Equal(PumpAction.PaddleElement, pump.Pump());

        Assert.Equal(6, edges.Count);
        AssertAlternatingFromKeyDown(edges);

        for (int i = 0; i < edges.Count; i += 2)
        {
            Assert.Equal(Dit, edges[i + 1].QpcTimestamp - edges[i].QpcTimestamp);

            // Inter-element gap: one gap unit at the default weight, which equals a dit.
            if (i + 2 < edges.Count)
                Assert.Equal(Dit, edges[i + 2].QpcTimestamp - edges[i + 1].QpcTimestamp);
        }
    }

    /// <summary>
    /// Iambic B squeeze alternates dit, dah, dit, dah (3.2).
    /// </summary>
    [Fact]
    public void IambicB_Squeeze_ProducesAlternatingElementDurations()
    {
        var (pump, _, _, edges) = NewPump(KeyerMode.IambicB);
        pump.SetPaddleState(dit: true, dah: true, straight: false);

        pump.Pump();
        pump.Pump();
        pump.Pump();
        pump.Pump();

        Assert.Equal(8, edges.Count);
        Assert.Equal(Dit, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
        Assert.Equal(Dah, edges[3].QpcTimestamp - edges[2].QpcTimestamp);
        Assert.Equal(Dit, edges[5].QpcTimestamp - edges[4].QpcTimestamp);
        Assert.Equal(Dah, edges[7].QpcTimestamp - edges[6].QpcTimestamp);
    }

    /// <summary>
    /// Weight applies to paddle elements as well as host text (3.9).
    /// </summary>
    [Fact]
    public void HeavyWeight_LengthensPaddleElement()
    {
        var (pump, _, _, edges) = NewPump();
        pump.Weight = 75;

        pump.SetPaddleState(dit: true, dah: false, straight: false);
        pump.Pump();

        Assert.Equal(Dit * 3 / 2, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
    }

    /// <summary>
    /// Paddle reverse swaps the contacts before the engine sees them, so a dit contact keys a
    /// dah.
    /// </summary>
    [Fact]
    public void PaddleReverse_SwapsContacts()
    {
        var (pump, _, _, edges) = NewPump();
        pump.PaddleReverse = true;

        pump.SetPaddleState(dit: true, dah: false, straight: false);
        pump.Pump();

        Assert.Equal(Dah, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
    }

    // ── Straight key (3.6) ───────────────────────────────────────────────────

    /// <summary>
    /// Straight mode passes the contact through unchanged: one edge per transition, no
    /// element generation, and the key stays down as long as the contact is closed (3.6).
    /// </summary>
    [Fact]
    public void Straight_PassesContactThroughWithoutGeneratingElements()
    {
        var (pump, _, _, edges) = NewPump(KeyerMode.Straight);

        pump.SetPaddleState(dit: false, dah: false, straight: true, qpcTimestamp: 1234);
        Assert.Equal(PumpAction.StraightKey, pump.Pump());

        // Held closed: no further edges, and nothing else may take the line.
        Assert.Equal(PumpAction.Idle, pump.Pump());
        Assert.Equal(PumpAction.Idle, pump.Pump());
        Assert.True(pump.IsKeyDown);

        pump.SetPaddleState(dit: false, dah: false, straight: false, qpcTimestamp: 5678);
        Assert.Equal(PumpAction.StraightKey, pump.Pump());

        Assert.Equal(2, edges.Count);
        AssertAlternatingFromKeyDown(edges);
        Assert.Equal(EdgeSource.Paddle, edges[0].Source);

        // Straight-key edges carry the poller's detection timestamp, which is the actual
        // contact moment, rather than a timestamp taken when the pump got around to it (1.3).
        Assert.Equal(1234, edges[0].QpcTimestamp);
        Assert.Equal(5678, edges[1].QpcTimestamp);
    }

    [Fact]
    public void Straight_IgnoresPaddleContacts()
    {
        var (pump, _, _, edges) = NewPump(KeyerMode.Straight);

        pump.SetPaddleState(dit: true, dah: true, straight: false);

        Assert.Equal(PumpAction.Idle, pump.Pump());
        Assert.Empty(edges);
    }

    // ── Host text path (2.3, 2.5) ────────────────────────────────────────────

    [Fact]
    public void HostText_SendsOneCharacterPerPumpCall()
    {
        var (pump, _, _, edges) = NewPump();
        var completed = new List<char>();
        pump.CharacterCompleted += (_, c) => completed.Add(c);

        pump.EnqueueText("E");
        Assert.True(pump.HasPendingText);
        Assert.Equal(PumpAction.HostCharacter, pump.Pump());

        Assert.False(pump.HasPendingText);
        Assert.Equal(2, edges.Count);
        Assert.Equal(Dit, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
        Assert.All(edges, e => Assert.Equal(EdgeSource.Host, e.Source));
        Assert.Equal(new[] { 'E' }, completed);
    }

    [Fact]
    public void HostText_MultiElementCharacter_EmitsEdgePerElement()
    {
        var (pump, _, _, edges) = NewPump();

        pump.EnqueueText("A"); // dit dah
        pump.Pump();

        Assert.Equal(4, edges.Count);
        AssertAlternatingFromKeyDown(edges);
        Assert.Equal(Dit, edges[1].QpcTimestamp - edges[0].QpcTimestamp);
        Assert.Equal(Dit, edges[2].QpcTimestamp - edges[1].QpcTimestamp); // intra-character gap
        Assert.Equal(Dah, edges[3].QpcTimestamp - edges[2].QpcTimestamp);
    }

    [Fact]
    public void HostText_Space_ProducesNoEdgesAndCompletes()
    {
        var (pump, clock, _, edges) = NewPump();
        var completed = new List<char>();
        pump.CharacterCompleted += (_, c) => completed.Add(c);

        pump.EnqueueText(" ");
        Assert.Equal(PumpAction.HostCharacter, pump.Pump());

        Assert.Empty(edges);
        Assert.Equal(new[] { ' ' }, completed);
        Assert.Equal(7 * Dit, clock.CurrentTimestamp);
    }

    [Fact]
    public void HostText_UnsupportedCharacter_IsDroppedWithoutKeying()
    {
        var (pump, _, _, edges) = NewPump();
        var completed = new List<char>();
        pump.CharacterCompleted += (_, c) => completed.Add(c);

        pump.EnqueueText("\u00a7"); // No Morse pattern.
        Assert.Equal(PumpAction.HostCharacter, pump.Pump());

        Assert.Empty(edges);
        Assert.Empty(completed);
    }

    [Fact]
    public void HostText_ConsecutiveCharacters_AreSpacedByInterCharacterGap()
    {
        var (pump, _, _, edges) = NewPump();

        pump.EnqueueText("EE");
        pump.Pump();
        pump.Pump();

        Assert.Equal(4, edges.Count);

        // Three gap units between the end of the first character and the start of the second.
        Assert.Equal(3 * Dit, edges[2].QpcTimestamp - edges[1].QpcTimestamp);
    }

    // ── Arbitration (3.7) ────────────────────────────────────────────────────

    /// <summary>
    /// A paddle contact closing during a host character abandons that character and, because
    /// the abort can land while the key is down, emits a key-up edge at the abort point — the
    /// key is never left asserted (3.7).
    /// </summary>
    [Fact]
    public void PaddlePressDuringHostCharacter_AbortsWithKeyUpEdge()
    {
        var (pump, _, waiter, edges) = NewPump();
        var completed = new List<char>();
        pump.CharacterCompleted += (_, c) => completed.Add(c);

        // "O" is three dahs. Wait index 3 is the hold following the second key-down, so the
        // break-in lands with the key asserted.
        waiter.BeforeWait = (index, _) =>
        {
            if (index == 3)
                pump.SetPaddleState(dit: true, dah: false, straight: false);
        };

        pump.EnqueueText("O");
        Assert.Equal(PumpAction.HostCharacter, pump.Pump());

        Assert.Equal(4, edges.Count);
        AssertAlternatingFromKeyDown(edges);
        Assert.False(edges[^1].KeyDown);
        Assert.False(pump.IsKeyDown);
        Assert.Empty(completed);

        // The paddle now owns the line.
        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        Assert.Equal(EdgeSource.Paddle, edges[4].Source);
    }

    /// <summary>
    /// A contact already closed when host text comes up for sending keeps the host off the
    /// line entirely (3.7).
    /// </summary>
    [Fact]
    public void PaddleHeld_HostTextDoesNotKey()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetPaddleState(dit: true, dah: false, straight: false);
        pump.EnqueueText("E");

        Assert.Equal(PumpAction.PaddleElement, pump.Pump());
        Assert.All(edges, e => Assert.Equal(EdgeSource.Paddle, e.Source));
    }

    // ── Immediate key and abort (2.4) ────────────────────────────────────────

    [Fact]
    public void KeyImmediate_KeysDownAndUpOnRequest()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetKeyImmediate(true);
        Assert.Equal(PumpAction.Immediate, pump.Pump());
        Assert.True(pump.IsKeyDown);

        // Held down: nothing else may key while the host drives the line directly.
        pump.SetPaddleState(dit: true, dah: false, straight: false);
        Assert.Equal(PumpAction.Idle, pump.Pump());
        Assert.Single(edges);

        pump.SetKeyImmediate(false);
        Assert.Equal(PumpAction.Immediate, pump.Pump());

        Assert.Equal(2, edges.Count);
        AssertAlternatingFromKeyDown(edges);
        Assert.All(edges, e => Assert.Equal(EdgeSource.Immediate, e.Source));
    }

    [Fact]
    public void AbortAndClear_ReleasesKeyAndDiscardsQueuedText()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetKeyImmediate(true);
        pump.Pump();
        Assert.True(pump.IsKeyDown);

        pump.EnqueueText("CQ TEST");
        pump.AbortAndClear();

        Assert.Equal(PumpAction.Aborted, pump.Pump());
        Assert.False(pump.IsKeyDown);
        Assert.False(pump.HasPendingText);
        Assert.Equal(2, edges.Count);
        Assert.False(edges[1].KeyDown);

        Assert.Equal(PumpAction.Idle, pump.Pump());
    }

    /// <summary>
    /// An abort raised mid-element cuts the element short with a key-up edge rather than
    /// waiting for it to finish.
    /// </summary>
    [Fact]
    public void AbortDuringElement_CutsElementShortWithKeyUp()
    {
        var (pump, clock, waiter, edges) = NewPump();

        waiter.BeforeWait = (index, _) =>
        {
            if (index == 0)
                pump.AbortAndClear();
        };

        pump.SetPaddleState(dit: false, dah: true, straight: false);
        pump.Pump();

        Assert.Equal(2, edges.Count);
        Assert.True(edges[0].KeyDown);
        Assert.False(edges[1].KeyDown);
        Assert.False(pump.IsKeyDown);

        // Cut short: the key-up is earlier than a full dah would have allowed.
        Assert.True(edges[1].QpcTimestamp - edges[0].QpcTimestamp < Dah);
        Assert.Equal(0, clock.CurrentTimestamp);
    }

    [Fact]
    public void ShouldStop_AbandonsElementWithKeyUp()
    {
        var (pump, _, _, edges) = NewPump();
        bool stop = false;

        pump.SetPaddleState(dit: true, dah: false, straight: false);
        pump.Pump(() => stop);
        Assert.Equal(2, edges.Count);

        stop = true;
        pump.Pump(() => stop);

        // No zero-length key-down blip on shutdown: the element is abandoned before it keys.
        Assert.Equal(2, edges.Count);
        Assert.False(edges[^1].KeyDown);
        Assert.False(pump.IsKeyDown);
    }

    [Fact]
    public void ForceKeyUp_ReleasesAnAssertedKey()
    {
        var (pump, _, _, edges) = NewPump();

        pump.SetKeyImmediate(true);
        pump.Pump();

        pump.ForceKeyUp();

        Assert.False(pump.IsKeyDown);
        Assert.Equal(2, edges.Count);
        Assert.False(edges[1].KeyDown);
    }

    [Fact]
    public void ForceKeyUp_WhenAlreadyUp_EmitsNothing()
    {
        var (pump, _, _, edges) = NewPump();

        pump.ForceKeyUp();

        Assert.Empty(edges);
    }

    /// <summary>
    /// Leaving straight mode while the straight contact is closed releases the key: nothing
    /// is watching that contact any more, so it must not stay asserted.
    /// </summary>
    [Fact]
    public void LeavingStraightModeWhileKeyed_ReleasesKey()
    {
        var (pump, _, _, edges) = NewPump(KeyerMode.Straight);

        pump.SetPaddleState(dit: false, dah: false, straight: true);
        pump.Pump();
        Assert.True(pump.IsKeyDown);

        pump.Mode = KeyerMode.IambicB;
        Assert.Equal(PumpAction.StraightKey, pump.Pump());

        Assert.False(pump.IsKeyDown);
        Assert.Equal(2, edges.Count);
        Assert.False(edges[1].KeyDown);
    }
}
