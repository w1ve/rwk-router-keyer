using System.Buffers.Binary;

namespace RWK.Shared.Protocol.Edge;

/// <summary>
/// A single timestamped key-state transition inside an RWK-PADDLE frame (Requirement 6.3).
/// </summary>
/// <remarks>
/// <para>Wire layout, little-endian, 12 bytes total:</para>
/// <list type="table">
///   <item><description>0..3  <see cref="Sequence"/>    — uint, 4 bytes</description></item>
///   <item><description>4..7  <see cref="TimestampMs"/> — uint, 4 bytes</description></item>
///   <item><description>8     <see cref="State"/>       — byte, 1 byte</description></item>
///   <item><description>9     <see cref="Flags"/>       — byte, 1 byte</description></item>
///   <item><description>10..11 <see cref="Reserved"/>   — ushort, 2 bytes</description></item>
/// </list>
/// <para>
/// Spec discrepancy, resolved deliberately: design.md annotates the edges array with the
/// comment "8 bytes each", but the field list in that same block — and Requirement 6.3 —
/// both enumerate 4 + 4 + 1 + 1 + 2 = 12 bytes. The explicit per-field widths win, because
/// two independent places agree on them while only an inline comment says 8. Entry size is
/// therefore <see cref="Size"/> = 12. Do not "fix" this back to 8 without also changing
/// the field widths in Requirement 6.3.
/// </para>
/// <para>
/// Encoding is explicitly little-endian via <see cref="BinaryPrimitives"/> so the wire
/// format never depends on host endianness.
/// </para>
/// </remarks>
public readonly struct EdgeEntry : IEquatable<EdgeEntry>
{
    /// <summary>Size in bytes of one serialized edge entry.</summary>
    public const int Size = 12;

    /// <summary><see cref="State"/> value meaning key up.</summary>
    public const byte StateKeyUp = 0;

    /// <summary><see cref="State"/> value meaning key down.</summary>
    public const byte StateKeyDown = 1;

    // Field offsets within a serialized entry.
    private const int SequenceOffset = 0;
    private const int TimestampOffset = 4;
    private const int StateOffset = 8;
    private const int FlagsOffset = 9;
    private const int ReservedOffset = 10;

    /// <summary>Creates an edge entry.</summary>
    /// <param name="sequence">Monotonic sequence number.</param>
    /// <param name="timestampMs">Monotonic milliseconds since session start.</param>
    /// <param name="state">0 = key up, 1 = key down.</param>
    /// <param name="flags">Reserved flag bits (PTT and future use).</param>
    /// <param name="reserved">Padding / future use.</param>
    public EdgeEntry(uint sequence, uint timestampMs, byte state, byte flags = 0, ushort reserved = 0)
    {
        Sequence = sequence;
        TimestampMs = timestampMs;
        State = state;
        Flags = flags;
        Reserved = reserved;
    }

    /// <summary>Monotonic sequence number assigned by the Client.</summary>
    public uint Sequence { get; }

    /// <summary>Monotonic milliseconds since session start.</summary>
    public uint TimestampMs { get; }

    /// <summary>Key state: 0 = key up, 1 = key down.</summary>
    public byte State { get; }

    /// <summary>Reserved flag bits (PTT, etc.).</summary>
    public byte Flags { get; }

    /// <summary>Padding / future use.</summary>
    public ushort Reserved { get; }

    /// <summary>True when <see cref="State"/> is non-zero (key down).</summary>
    public bool KeyDown => State != StateKeyUp;

    /// <summary>Creates a key-down entry.</summary>
    public static EdgeEntry KeyDownAt(uint sequence, uint timestampMs, byte flags = 0)
        => new(sequence, timestampMs, StateKeyDown, flags);

    /// <summary>Creates a key-up entry.</summary>
    public static EdgeEntry KeyUpAt(uint sequence, uint timestampMs, byte flags = 0)
        => new(sequence, timestampMs, StateKeyUp, flags);

    /// <summary>
    /// Serializes this entry into <paramref name="destination"/>. Never throws and never allocates.
    /// </summary>
    /// <param name="destination">Buffer to write into; must be at least <see cref="Size"/> bytes.</param>
    /// <param name="bytesWritten">Bytes written on success, 0 on failure.</param>
    /// <returns>True on success; false if <paramref name="destination"/> is too small.</returns>
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[SequenceOffset..], Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TimestampOffset..], TimestampMs);
        destination[StateOffset] = State;
        destination[FlagsOffset] = Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ReservedOffset..], Reserved);

        bytesWritten = Size;
        return true;
    }

    /// <summary>
    /// Deserializes one entry from <paramref name="source"/>. Never throws and never allocates.
    /// </summary>
    /// <param name="source">Buffer positioned at the start of an entry.</param>
    /// <param name="entry">Parsed entry on success, default on failure.</param>
    /// <param name="bytesConsumed">Bytes consumed on success, 0 on failure.</param>
    /// <returns>True on success; false if <paramref name="source"/> is shorter than <see cref="Size"/>.</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out EdgeEntry entry, out int bytesConsumed)
    {
        if (source.Length < Size)
        {
            entry = default;
            bytesConsumed = 0;
            return false;
        }

        entry = new EdgeEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(source[SequenceOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[TimestampOffset..]),
            source[StateOffset],
            source[FlagsOffset],
            BinaryPrimitives.ReadUInt16LittleEndian(source[ReservedOffset..]));

        bytesConsumed = Size;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(EdgeEntry other)
        => Sequence == other.Sequence
        && TimestampMs == other.TimestampMs
        && State == other.State
        && Flags == other.Flags
        && Reserved == other.Reserved;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EdgeEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Sequence, TimestampMs, State, Flags, Reserved);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EdgeEntry left, EdgeEntry right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EdgeEntry left, EdgeEntry right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"Edge(seq={Sequence}, t={TimestampMs}ms, state={(KeyDown ? "down" : "up")}, flags=0x{Flags:X2})";
}
