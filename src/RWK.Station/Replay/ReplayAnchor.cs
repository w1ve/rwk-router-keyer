using RWK.Shared.Protocol.Edge;

namespace RWK.Station.Replay;

/// <summary>
/// Turns an edge's session-relative timestamp into an absolute QPC deadline, anchoring the first
/// edge of a burst at arrival + D and every later edge at anchor + relative timestamp
/// (7.2, 7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the anchor is.</b> <see cref="AnchorQpc"/> is the QPC value that edge timestamp 0 of the
/// current burst maps to, so a deadline is a single multiply-add:
/// <c>deadline = AnchorQpc + (TimestampMs x freq / 1000)</c>, exactly the expression in design
/// Function 2. It is established from the burst's first edge as
/// <c>arrival + D - (firstTimestampMs x freq / 1000)</c>, which makes that first edge replay at
/// <c>arrival + D</c> as 7.2 requires while keeping 7.3's form for the rest.
/// </para>
/// <para>
/// <b>Why not simply arrival + D per edge.</b> Adding D to each edge's own arrival would import
/// that datagram's network jitter into the keying, which is the thing the buffer exists to remove.
/// Anchoring once per burst means every edge in the burst inherits the Client's spacing and only
/// the burst as a whole is offset.
/// </para>
/// <para>
/// <b>Spec reconciliation.</b> The design's main-loop pseudocode writes
/// <c>anchorQpc &lt;- NOW + JitterDelay</c> and then <c>anchor + TimestampMs</c>. Taken literally
/// those two lines schedule the first edge at <c>arrival + D + TimestampMs</c>, which drifts
/// further into the future the longer a session has been running, because
/// <see cref="EdgeEntry.TimestampMs"/> counts from session start (Requirement 6.3) and not from
/// burst start. Subtracting the first edge's timestamp when the anchor is established is the
/// reading that satisfies 7.2 and 7.3 together, and it is what the wording "anchor + relative
/// timestamp" means.
/// </para>
/// <para>
/// <b>Idle reset (7.2).</b> A new anchor is established when no edge has arrived for
/// <see cref="IdleReset"/> (default 2s), measured between arrivals. Re-anchoring is what stops
/// clock drift between Client and Station accumulating across a whole operating session. Note that
/// an anchor reset must never touch <see cref="EdgeSequenceTracker"/>: re-anchoring is a timing
/// decision, while <see cref="EdgeSequenceTracker.BeginSession"/> discards the verified sequence
/// baseline and would let a key-down behind a gap through as a fresh baseline instead of raising
/// F5 (9.5).
/// </para>
/// <para>
/// Not thread-safe: one anchor belongs to one replay thread, like the sequence tracker.
/// </para>
/// <para>
/// _Requirements: 7.2, 7.3_
/// </para>
/// </remarks>
public sealed class ReplayAnchor
{
    /// <summary>Idle period after which the next edge re-anchors the burst (7.2).</summary>
    public static readonly TimeSpan DefaultIdleReset = TimeSpan.FromSeconds(2);

    private readonly long _frequency;
    private readonly long _idleResetTicks;

    private bool _anchored;
    private long _anchorQpc;
    private long _lastArrivalQpc;

    /// <summary>
    /// Creates an anchor for a clock running at <paramref name="frequency"/> ticks per second.
    /// </summary>
    /// <param name="frequency">Clock frequency, normally <c>ISystemClock.Frequency</c>.</param>
    /// <param name="idleReset">
    /// Idle period that forces re-anchoring; defaults to <see cref="DefaultIdleReset"/>. Values at
    /// or below zero are treated as the default rather than as "re-anchor every edge", which would
    /// reintroduce per-datagram jitter.
    /// </param>
    public ReplayAnchor(long frequency, TimeSpan? idleReset = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        _frequency = frequency;
        TimeSpan reset = idleReset is { } value && value > TimeSpan.Zero ? value : DefaultIdleReset;
        _idleResetTicks = TicksForMilliseconds((long)reset.TotalMilliseconds, frequency);
    }

    /// <summary>Whether an anchor is currently established.</summary>
    public bool IsAnchored => _anchored;

    /// <summary>
    /// The QPC value that edge timestamp 0 maps to. Meaningless while <see cref="IsAnchored"/> is
    /// false.
    /// </summary>
    public long AnchorQpc => _anchorQpc;

    /// <summary>Arrival timestamp of the most recent edge passed to <see cref="Schedule"/>.</summary>
    public long LastArrivalQpc => _lastArrivalQpc;

    /// <summary>Idle period that forces re-anchoring, in clock ticks.</summary>
    public long IdleResetTicks => _idleResetTicks;

    /// <summary>Idle period that forces re-anchoring (7.2).</summary>
    public TimeSpan IdleReset => TimeSpan.FromSeconds((double)_idleResetTicks / _frequency);

    /// <summary>How many times an anchor has been established, including the first.</summary>
    public long AnchorCount { get; private set; }

    /// <summary>
    /// Whether an edge arriving at <paramref name="arrivalQpc"/> would establish a new anchor:
    /// either none exists yet, or the gap since the last arrival has reached
    /// <see cref="IdleReset"/> (7.2).
    /// </summary>
    public bool WouldReanchor(long arrivalQpc)
        => !_anchored || (arrivalQpc - _lastArrivalQpc) >= _idleResetTicks;

    /// <summary>
    /// Returns the absolute QPC deadline at which <paramref name="edge"/> should be replayed,
    /// establishing a new anchor first if the burst has restarted (7.2, 7.3).
    /// </summary>
    /// <param name="arrivalQpc">QPC timestamp at which the edge's datagram arrived.</param>
    /// <param name="edge">The edge to schedule.</param>
    /// <param name="delayTicks">The jitter buffer delay D, in clock ticks.</param>
    /// <param name="reanchored">True when this call established a new anchor.</param>
    public long Schedule(long arrivalQpc, in EdgeEntry edge, long delayTicks, out bool reanchored)
        => Schedule(arrivalQpc, edge.TimestampMs, delayTicks, out reanchored);

    /// <summary>
    /// Timestamp-only overload of <see cref="Schedule(long, in EdgeEntry, long, out bool)"/>.
    /// </summary>
    public long Schedule(long arrivalQpc, uint timestampMs, long delayTicks, out bool reanchored)
    {
        if (delayTicks < 0)
        {
            delayTicks = 0;
        }

        long relative = TicksForMilliseconds(timestampMs, _frequency);

        reanchored = WouldReanchor(arrivalQpc);
        if (reanchored)
        {
            // Anchor so that this edge lands exactly at arrival + D (7.2).
            _anchorQpc = arrivalQpc + delayTicks - relative;
            _anchored = true;
            AnchorCount++;
        }

        _lastArrivalQpc = arrivalQpc;
        return _anchorQpc + relative;
    }

    /// <summary>
    /// Drops the anchor, so the next edge anchors afresh. Used on session establishment,
    /// reconnect, and after a fail-safe has forced key-up, where continuing to schedule against an
    /// anchor from before the interruption would be meaningless.
    /// </summary>
    public void Reset()
    {
        _anchored = false;
        _anchorQpc = 0;
        _lastArrivalQpc = 0;
    }

    /// <summary>
    /// Converts a session-relative millisecond timestamp to clock ticks:
    /// <c>ms x frequency / 1000</c>, evaluated so that a full 32-bit millisecond value cannot
    /// overflow.
    /// </summary>
    public static long TicksForMilliseconds(long milliseconds, long frequency)
    {
        if (milliseconds <= 0 || frequency <= 0)
        {
            return 0;
        }

        // Split the multiply so ms up to uint.MaxValue stays inside long for any sane frequency.
        long whole = milliseconds / 1000L * frequency;
        long fraction = milliseconds % 1000L * frequency / 1000L;
        return whole + fraction;
    }

    /// <summary>Converts clock ticks to milliseconds as a double, for telemetry and logging.</summary>
    public static double MillisecondsForTicks(long ticks, long frequency)
        => frequency <= 0 ? 0 : ticks * 1000.0 / frequency;
}
