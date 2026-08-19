namespace RWK.Shared.Protocol.Edge;

/// <summary>
/// Validates received edges against the current session: epoch match (Requirement 6.5),
/// sequence ordering with duplicate rejection (6.6), and timestamp monotonicity (6.7).
/// </summary>
/// <remarks>
/// <para>
/// This type classifies edges and nothing more. It holds the "what happened" half of the
/// Edge_Replayer's inbound path so that the replayer maps an
/// <see cref="EdgeValidationResult"/> onto a response — schedule, discard, force key-up,
/// latch SAFE — without re-deriving the rules. Jitter buffering, anchoring, and scheduling
/// live elsewhere; so do the fail-safe responses themselves.
/// </para>
/// <para>
/// Keying path discipline, matching <see cref="RwkPaddleFrame"/> and <see cref="EdgeEntry"/>:
/// no allocation on any validation call, and no exception for any input. Every rejection is
/// reported through the returned result.
/// </para>
/// <para>
/// Not thread-safe by design. One instance belongs to one replay thread; sharing it across
/// threads would need a lock, and a lock does not belong on this path.
/// </para>
/// <para><b>Redundancy and gaps.</b> Requirement 6.4 puts the current edge plus up to three
/// previous edges in every frame, so most arriving edges are copies of edges already applied
/// and are discarded as duplicates. That same redundancy is what heals loss:
/// <see cref="TryValidateFrame"/> walks a frame's edges in ascending sequence order, so an
/// edge missing from an earlier datagram is applied from its redundant copy before the newer
/// edge is examined, and no gap is ever observed. A gap therefore only surfaces once
/// redundancy has failed to heal it — the case Requirement 9.5 is about.</para>
/// <para><b>Never guess a key-down.</b> Across an unhealed gap the tracker will apply a
/// key-up but not a key-down: see <see cref="CanInferStateAcrossGap"/>.</para>
/// _Requirements: 6.5, 6.6, 6.7_
/// </remarks>
public sealed class EdgeSequenceTracker
{
    private bool _hasApplied;
    private uint _lastSequence;
    private uint _lastTimestampMs;
    private bool _lastKeyDown;

    /// <summary>Creates a tracker for session <paramref name="epoch"/> with no edges applied.</summary>
    public EdgeSequenceTracker(ushort epoch) => Epoch = epoch;

    /// <summary>
    /// The session epoch this tracker accepts. Frames carrying any other epoch are
    /// <see cref="EdgeValidationOutcome.EpochMismatch"/> (Requirement 6.5).
    /// </summary>
    public ushort Epoch { get; private set; }

    /// <summary>True once an edge has been applied in the current session.</summary>
    public bool HasApplied => _hasApplied;

    /// <summary>Sequence of the last applied edge; 0 before any edge is applied.</summary>
    public uint LastSequence => _lastSequence;

    /// <summary>Timestamp of the last applied edge in milliseconds; 0 before any edge is applied.</summary>
    public uint LastTimestampMs => _lastTimestampMs;

    /// <summary>
    /// Key state of the last applied edge; false (key up) before any edge is applied, which
    /// is also the safe assumption for a session that has produced nothing yet.
    /// </summary>
    public bool LastKeyDown => _lastKeyDown;

    /// <summary>
    /// The epoch that follows <paramref name="epoch"/>, wrapping past
    /// <see cref="ushort.MaxValue"/> back to 0.
    /// </summary>
    /// <remarks>
    /// Epoch is a 2-byte field (Requirement 6.2) and increments on every reconnect, so it
    /// eventually rolls over. Rollover is harmless: the epoch is only ever compared for
    /// equality against the session's current value, never ordered. Reusing an epoch number
    /// after 65536 reconnects is safe because a frame from that long-dead session cannot
    /// still be in flight.
    /// </remarks>
    public static ushort NextEpoch(ushort epoch) => (ushort)(epoch + 1);

    /// <summary>
    /// Rebinds the tracker to <paramref name="epoch"/> and clears all sequence and timestamp
    /// state, so the next edge establishes a fresh baseline.
    /// </summary>
    /// <remarks>
    /// Called on session establishment and on reconnect. Sequence and timestamp are only
    /// ever compared within one epoch, so they must not survive across one.
    /// <para>
    /// This also discards the verified baseline, so the next edge — key-down included — is
    /// applied unconditionally. Call it only for a genuine session establishment or
    /// reconnect. Calling it mid-stream would let a key-down that sits behind a gap be
    /// applied as a fresh baseline instead of raising
    /// <see cref="FailSafeCondition.F5"/>, because the tracker cannot distinguish the two.
    /// </para>
    /// </remarks>
    public void BeginSession(ushort epoch)
    {
        Epoch = epoch;
        _hasApplied = false;
        _lastSequence = 0;
        _lastTimestampMs = 0;
        _lastKeyDown = false;
    }

    /// <summary>
    /// Whether the key state across an unhealed sequence gap can be established safely.
    /// </summary>
    /// <param name="edge">The edge that arrived after the gap.</param>
    /// <returns>
    /// True when <paramref name="edge"/> is a key-up, false when it is a key-down.
    /// </returns>
    /// <remarks>
    /// A key-up resolves the key to a state that is safe no matter what the missing edges
    /// carried: the transmitter ends up unkeyed. The missed transitions cost timing — a
    /// dropped element — but they cannot leave the radio keyed.
    /// <para>
    /// A key-down cannot be inferred. Applying it means keying the transmitter while
    /// transitions are unaccounted for, which is guessing a key-down. Requirement 9.5 wants
    /// exactly that case forced to key-up with the SAFE latch set, so it is reported as an
    /// uninferable gap.
    /// </para>
    /// <para>
    /// A pure function of the arriving edge, so the replayer and its tests can ask the
    /// question without owning a tracker.
    /// </para>
    /// </remarks>
    public static bool CanInferStateAcrossGap(in EdgeEntry edge) => !edge.KeyDown;

    /// <summary>
    /// Validates one edge from a frame carrying <paramref name="frameEpoch"/>, applying it
    /// when the result says so. Never throws, never allocates.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TryValidateFrame"/> when a whole frame is in hand: it orders the
    /// frame's redundant copies so they can heal a gap. Passing a frame's edges to this
    /// method in wire order would report gaps that the redundancy block could have filled.
    /// </remarks>
    public EdgeValidationResult Validate(ushort frameEpoch, in EdgeEntry edge)
    {
        // 6.5 — epoch first: an edge from another session says nothing about this one.
        if (frameEpoch != Epoch)
        {
            return EdgeValidationResult.EpochMismatch();
        }

        // The first edge of a session establishes the baseline. Its sequence number is
        // whatever the Client has reached; treating it as a gap against an assumed 0 would
        // latch SAFE on nothing worse than joining a stream that has already been running.
        if (!_hasApplied)
        {
            Apply(edge);
            return EdgeValidationResult.Accepted(edge);
        }

        // 6.6 — already seen. The overwhelmingly common case, thanks to 6.4 redundancy.
        // Sequence is compared as a plain unsigned value, deliberately not as wrapping
        // serial-number arithmetic: at uint.MaxValue the tracker saturates and treats
        // everything after as a duplicate rather than accepting a wrapped low sequence that
        // could equally be a stale replay. A session would need over four billion edges to
        // reach that point; the Client starts a new epoch long before. Discarding is the
        // key-up-safe failure — it can drop keying, never invent it.
        if (edge.Sequence <= _lastSequence)
        {
            return EdgeValidationResult.Duplicate(edge);
        }

        // 6.7 — a new sequence whose timestamp precedes the last applied one. The Client
        // advances sequence and timestamp together, so within an epoch this cannot happen;
        // the stream is corrupt or foreign and nothing in it can be trusted to schedule.
        if (edge.TimestampMs < _lastTimestampMs)
        {
            return EdgeValidationResult.TimestampRegression(edge);
        }

        uint missed = edge.Sequence - _lastSequence - 1;
        if (missed > 0)
        {
            bool canInfer = CanInferStateAcrossGap(edge);
            if (canInfer)
            {
                Apply(edge);
            }

            return EdgeValidationResult.SequenceGap(edge, canInfer, missed);
        }

        Apply(edge);
        return EdgeValidationResult.Accepted(edge);
    }

    /// <summary>
    /// Validates every edge in <paramref name="frame"/> in ascending sequence order, so the
    /// frame's redundant copies (Requirement 6.4) heal gaps left by lost datagrams.
    /// Never throws, never allocates.
    /// </summary>
    /// <param name="frame">The received frame.</param>
    /// <param name="results">
    /// Buffer receiving one result per examined edge, in the order examined. Must hold at
    /// least <see cref="RwkPaddleFrame.EdgeCount"/> items, or 1 for an epoch mismatch.
    /// </param>
    /// <param name="resultCount">Number of results written; 0 on failure.</param>
    /// <returns>False when <paramref name="results"/> is too small; nothing is applied then.</returns>
    /// <remarks>
    /// An epoch mismatch is decided from the frame header and writes a single
    /// <see cref="EdgeValidationOutcome.EpochMismatch"/> result: the whole frame is discarded
    /// without examining any edge, per Requirement 6.5.
    /// <para>
    /// Results are written in ascending sequence order rather than wire order. Wire order
    /// puts the current edge first (6.4); the replayer wants oldest-first to schedule.
    /// </para>
    /// </remarks>
    public bool TryValidateFrame(in RwkPaddleFrame frame, Span<EdgeValidationResult> results, out int resultCount)
    {
        resultCount = 0;

        if (frame.Epoch != Epoch)
        {
            if (results.Length < 1)
            {
                return false;
            }

            results[0] = EdgeValidationResult.EpochMismatch();
            resultCount = 1;
            return true;
        }

        int count = frame.EdgeCount;
        if (results.Length < count)
        {
            return false;
        }

        // Up to four entries on the stack, ordered by insertion sort: no allocation, and
        // at this size nothing beats it.
        Span<EdgeEntry> ordered = stackalloc EdgeEntry[RwkPaddleFrame.MaxEdgeCount];
        if (!frame.TryCopyEdgesTo(ordered, out int copied))
        {
            return false;
        }

        SortBySequenceAscending(ordered[..copied]);

        for (int i = 0; i < copied; i++)
        {
            results[i] = Validate(frame.Epoch, ordered[i]);
        }

        resultCount = copied;
        return true;
    }

    private static void SortBySequenceAscending(Span<EdgeEntry> edges)
    {
        for (int i = 1; i < edges.Length; i++)
        {
            EdgeEntry current = edges[i];
            int j = i - 1;
            while (j >= 0 && edges[j].Sequence > current.Sequence)
            {
                edges[j + 1] = edges[j];
                j--;
            }

            edges[j + 1] = current;
        }
    }

    private void Apply(in EdgeEntry edge)
    {
        _hasApplied = true;
        _lastSequence = edge.Sequence;
        _lastTimestampMs = edge.TimestampMs;
        _lastKeyDown = edge.KeyDown;
    }
}
