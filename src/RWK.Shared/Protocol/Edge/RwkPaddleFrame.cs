using System.Buffers.Binary;

namespace RWK.Shared.Protocol.Edge;

/// <summary>
/// The RWK-PADDLE binary frame carried in one UDP datagram (Requirements 6.1, 6.2).
/// A frame holds the current edge plus up to three previous edges for redundancy (6.4).
/// </summary>
/// <remarks>
/// <para>Wire layout, little-endian:</para>
/// <list type="table">
///   <item><description>0..1 <see cref="Epoch"/>     — ushort, 2 bytes</description></item>
///   <item><description>2..3 <see cref="EdgeCount"/> — ushort, 2 bytes</description></item>
///   <item><description>4..  <see cref="EdgeCount"/> × <see cref="EdgeEntry.Size"/> byte entries</description></item>
/// </list>
/// <para>
/// The struct is entirely value-typed with the edges stored inline, so constructing,
/// serializing, and parsing a frame allocates nothing. This type sits on the keying
/// path, so no member allocates and no member throws on malformed input — parse
/// failures are reported through <c>Try*</c> return values.
/// </para>
/// <para>
/// Entry size is 12 bytes, not the 8 mentioned by an inline comment in design.md.
/// See the remarks on <see cref="EdgeEntry"/> for why that discrepancy resolves to 12.
/// </para>
/// <para>
/// This type is the codec only. Epoch matching, sequence ordering, duplicate rejection,
/// and gap detection are the Edge_Replayer's job (Requirements 6.5–6.7) and live outside
/// this type.
/// </para>
/// </remarks>
public readonly struct RwkPaddleFrame
{
    /// <summary>Size in bytes of the frame header (Epoch + EdgeCount).</summary>
    public const int HeaderSize = 4;

    /// <summary>Size in bytes of one edge entry.</summary>
    public const int EdgeEntrySize = EdgeEntry.Size;

    /// <summary>Smallest legal edge count: a frame always carries at least the current edge.</summary>
    public const int MinEdgeCount = 1;

    /// <summary>Largest legal edge count: current edge plus 3 redundant copies (Requirement 6.4).</summary>
    public const int MaxEdgeCount = 4;

    /// <summary>Smallest legal serialized frame size, in bytes.</summary>
    public const int MinFrameSize = HeaderSize + (MinEdgeCount * EdgeEntrySize);

    /// <summary>Largest legal serialized frame size, in bytes.</summary>
    public const int MaxFrameSize = HeaderSize + (MaxEdgeCount * EdgeEntrySize);

    private const int EpochOffset = 0;
    private const int EdgeCountOffset = 2;

    // Edges are held in fixed fields rather than an array so the frame stays a pure
    // value type: no allocation when building or parsing on the keying path.
    private readonly EdgeEntry _edge0;
    private readonly EdgeEntry _edge1;
    private readonly EdgeEntry _edge2;
    private readonly EdgeEntry _edge3;

    private RwkPaddleFrame(ushort epoch, ReadOnlySpan<EdgeEntry> edges)
    {
        Epoch = epoch;
        EdgeCount = (ushort)edges.Length;

        _edge0 = edges[0];
        _edge1 = edges.Length > 1 ? edges[1] : default;
        _edge2 = edges.Length > 2 ? edges[2] : default;
        _edge3 = edges.Length > 3 ? edges[3] : default;
    }

    /// <summary>Session epoch; increments on reconnect so stale frames can be detected.</summary>
    public ushort Epoch { get; }

    /// <summary>Number of edge entries carried by this frame, always 1..<see cref="MaxEdgeCount"/>.</summary>
    public ushort EdgeCount { get; }

    /// <summary>Size in bytes this frame occupies when serialized.</summary>
    public int SerializedSize => FrameSize(EdgeCount);

    /// <summary>Serialized size of a frame carrying <paramref name="edgeCount"/> entries.</summary>
    public static int FrameSize(int edgeCount) => HeaderSize + (edgeCount * EdgeEntrySize);

    /// <summary>Gets the edge at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative or not less than <see cref="EdgeCount"/>.
    /// </exception>
    public EdgeEntry this[int index]
    {
        get
        {
            if (!TryGetEdge(index, out EdgeEntry edge))
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Frame carries {EdgeCount} edges.");
            }

            return edge;
        }
    }

    /// <summary>Gets the edge at <paramref name="index"/> without throwing.</summary>
    /// <returns>True when <paramref name="index"/> is within <see cref="EdgeCount"/>.</returns>
    public bool TryGetEdge(int index, out EdgeEntry edge)
    {
        if ((uint)index >= EdgeCount)
        {
            edge = default;
            return false;
        }

        edge = index switch
        {
            0 => _edge0,
            1 => _edge1,
            2 => _edge2,
            _ => _edge3,
        };
        return true;
    }

    /// <summary>Copies the frame's edges into <paramref name="destination"/>.</summary>
    /// <param name="destination">Buffer receiving the edges; must hold at least <see cref="EdgeCount"/> items.</param>
    /// <param name="edgesCopied">Number of edges copied on success, 0 on failure.</param>
    /// <returns>True on success; false if <paramref name="destination"/> is too small.</returns>
    public bool TryCopyEdgesTo(Span<EdgeEntry> destination, out int edgesCopied)
    {
        if (destination.Length < EdgeCount)
        {
            edgesCopied = 0;
            return false;
        }

        for (int i = 0; i < EdgeCount; i++)
        {
            _ = TryGetEdge(i, out EdgeEntry edge);
            destination[i] = edge;
        }

        edgesCopied = EdgeCount;
        return true;
    }

    /// <summary>Allocation-free enumerator over the frame's edges.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Builds a frame from <paramref name="edges"/> without allocating.
    /// </summary>
    /// <param name="epoch">Session epoch.</param>
    /// <param name="edges">1..<see cref="MaxEdgeCount"/> edges, current edge first.</param>
    /// <param name="frame">The frame on success, default on failure.</param>
    /// <returns>False when the edge count is 0 or exceeds <see cref="MaxEdgeCount"/>.</returns>
    public static bool TryCreate(ushort epoch, ReadOnlySpan<EdgeEntry> edges, out RwkPaddleFrame frame)
    {
        if (edges.Length is < MinEdgeCount or > MaxEdgeCount)
        {
            frame = default;
            return false;
        }

        frame = new RwkPaddleFrame(epoch, edges);
        return true;
    }

    /// <summary>Builds a frame from <paramref name="edges"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="edges"/> is empty or longer than <see cref="MaxEdgeCount"/>.
    /// </exception>
    public static RwkPaddleFrame Create(ushort epoch, ReadOnlySpan<EdgeEntry> edges)
    {
        if (!TryCreate(epoch, edges, out RwkPaddleFrame frame))
        {
            throw new ArgumentException(
                $"An edge frame carries {MinEdgeCount}..{MaxEdgeCount} edges, got {edges.Length}.",
                nameof(edges));
        }

        return frame;
    }

    /// <summary>
    /// Serializes this frame into <paramref name="destination"/>. Never throws and never allocates.
    /// </summary>
    /// <param name="destination">Buffer to write into; must hold at least <see cref="SerializedSize"/> bytes.</param>
    /// <param name="bytesWritten">Bytes written on success, 0 on failure.</param>
    /// <returns>
    /// True on success; false if <paramref name="destination"/> is too small or the frame
    /// carries an illegal edge count (a default-initialized frame).
    /// </returns>
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        if (EdgeCount is < MinEdgeCount or > MaxEdgeCount)
        {
            return false;
        }

        int required = SerializedSize;
        if (destination.Length < required)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[EpochOffset..], Epoch);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[EdgeCountOffset..], EdgeCount);

        int offset = HeaderSize;
        for (int i = 0; i < EdgeCount; i++)
        {
            _ = TryGetEdge(i, out EdgeEntry edge);
            if (!edge.TryWrite(destination[offset..], out int entryBytes))
            {
                bytesWritten = 0;
                return false;
            }

            offset += entryBytes;
        }

        bytesWritten = offset;
        return true;
    }

    /// <summary>
    /// Parses a frame from <paramref name="source"/>. Never throws and never allocates.
    /// </summary>
    /// <param name="source">Received datagram bytes. Trailing bytes beyond the frame are ignored.</param>
    /// <param name="frame">Parsed frame on success, default on failure.</param>
    /// <param name="bytesConsumed">Bytes consumed on success, 0 on failure.</param>
    /// <returns>
    /// False when the buffer is too small for the header, the declared edge count is 0 or
    /// greater than <see cref="MaxEdgeCount"/>, or the buffer is too small for the declared
    /// edge count.
    /// </returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out RwkPaddleFrame frame, out int bytesConsumed)
    {
        frame = default;
        bytesConsumed = 0;

        if (source.Length < HeaderSize)
        {
            return false;
        }

        ushort epoch = BinaryPrimitives.ReadUInt16LittleEndian(source[EpochOffset..]);
        ushort edgeCount = BinaryPrimitives.ReadUInt16LittleEndian(source[EdgeCountOffset..]);

        if (edgeCount is < MinEdgeCount or > MaxEdgeCount)
        {
            return false;
        }

        int required = FrameSize(edgeCount);
        if (source.Length < required)
        {
            return false;
        }

        Span<EdgeEntry> edges = stackalloc EdgeEntry[MaxEdgeCount];
        int offset = HeaderSize;
        for (int i = 0; i < edgeCount; i++)
        {
            if (!EdgeEntry.TryRead(source[offset..], out EdgeEntry edge, out int entryBytes))
            {
                return false;
            }

            edges[i] = edge;
            offset += entryBytes;
        }

        frame = new RwkPaddleFrame(epoch, edges[..edgeCount]);
        bytesConsumed = offset;
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"RwkPaddleFrame(epoch={Epoch}, edges={EdgeCount})";

    /// <summary>Struct enumerator over a frame's edges; iterating allocates nothing.</summary>
    public struct Enumerator
    {
        private readonly RwkPaddleFrame _frame;
        private int _index;

        internal Enumerator(RwkPaddleFrame frame)
        {
            _frame = frame;
            _index = -1;
            Current = default;
        }

        /// <summary>The edge at the current position.</summary>
        public EdgeEntry Current { get; private set; }

        /// <summary>Advances to the next edge.</summary>
        public bool MoveNext()
        {
            int next = _index + 1;
            if (!_frame.TryGetEdge(next, out EdgeEntry edge))
            {
                return false;
            }

            _index = next;
            Current = edge;
            return true;
        }
    }
}
