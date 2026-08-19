namespace RWK.Station.Replay;

/// <summary>
/// A point-in-time snapshot of the Edge Replayer's counters and timing measurements.
/// </summary>
/// <remarks>
/// A <c>readonly record struct</c> so that reading telemetry from the UI thread allocates nothing
/// and cannot tear a reference the replay thread is publishing.
/// <para>
/// Counters are cheap increments on the replay path; the timing extremes exist to make the
/// +/-1ms accuracy target of 7.5 and 14.5 measurable rather than assumed, and to surface late
/// edges (an edge whose deadline had already passed when it was scheduled, replayed immediately).
/// </para>
/// <para>
/// _Requirements: 7.5, 14.5_
/// </para>
/// </remarks>
/// <param name="FramesReceived">Datagrams accepted for processing.</param>
/// <param name="FramesDropped">
/// Datagrams dropped before validation: unparseable, arriving with no session established, arriving
/// while the SAFE latch is set, or arriving faster than the inbound queue could be drained.
/// </param>
/// <param name="EdgesApplied">Edges validated and scheduled.</param>
/// <param name="EdgesReplayed">Edges whose scheduled deadline was reached and keyed out.</param>
/// <param name="DuplicateEdges">Edges discarded as already seen (6.4 redundancy, 6.6).</param>
/// <param name="AnchorCount">Anchors established, including the first (7.2).</param>
/// <param name="LateEdges">
/// Edges whose computed deadline had already passed when they were scheduled. They replay
/// immediately, so lateness shows up here rather than as silently mistimed keying.
/// </param>
/// <param name="MaxLatenessMs">Largest lateness observed at scheduling time, in milliseconds.</param>
/// <param name="MaxReplayErrorMs">
/// Largest difference between when an edge was keyed out and its deadline, in milliseconds. The
/// measurement that 7.5's +/-1ms target is judged against.
/// </param>
/// <param name="PendingOverflows">
/// Times the pending-edge queue was full when an edge needed scheduling. Non-zero means the replay
/// thread was starved badly enough that key state was forced up rather than queued.
/// </param>
/// <param name="CurrentDelay">The jitter buffer delay currently in force (7.1).</param>
/// <param name="RttEwmaMs">Current RTT EWMA in milliseconds (7.6).</param>
/// <param name="JitterEwmaMs">Current jitter EWMA in milliseconds (7.6).</param>
public readonly record struct EdgeReplayerTelemetry(
    long FramesReceived,
    long FramesDropped,
    long EdgesApplied,
    long EdgesReplayed,
    long DuplicateEdges,
    long AnchorCount,
    long LateEdges,
    double MaxLatenessMs,
    double MaxReplayErrorMs,
    long PendingOverflows,
    TimeSpan CurrentDelay,
    double RttEwmaMs,
    double JitterEwmaMs
);
