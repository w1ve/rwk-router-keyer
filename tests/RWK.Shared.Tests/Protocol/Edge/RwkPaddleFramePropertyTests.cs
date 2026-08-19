/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Buffers.Binary;
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared.Protocol.Edge;

namespace RWK.Shared.Tests.Protocol.Edge;

/// <summary>
/// Property-based tests for the RWK-PADDLE frame codec.
///
/// Property 16: RWK-PADDLE Frame Structure — for any epoch, edge count 1-4, and arbitrary
/// edge field values, the serialized frame has the layout specified by Requirements 6.2 and
/// 6.3 and round-trips to an equivalent frame with every field preserved.
///
/// Property 17: Edge Frame Redundancy — for any frame carrying 2-4 edges, the redundancy
/// block round-trips intact with all edges in order and their sequence numbers preserved,
/// so a receiver can heal a lost datagram from a later frame's redundant copies.
///
/// **Validates: Requirements 6.1, 6.2, 6.3, 6.4**
///
/// Wire layout under test (little-endian throughout): a 4-byte header of Epoch (u16) and
/// EdgeCount (u16), followed by EdgeCount entries of 12 bytes each — Sequence (u32),
/// TimestampMs (u32), State (u8), Flags (u8), Reserved (u16). The 12-byte entry size comes
/// from Requirement 6.3's field widths, which are authoritative over the stale "8 bytes
/// each" inline comment in design.md.
/// </summary>
public class RwkPaddleFramePropertyTests
{
    private const int HeaderSize = 4;
    private const int EntrySize = 12;

    // ---------------------------------------------------------------- generators

    /// <summary>Full u16 range, biased toward the 0 and ushort.MaxValue boundaries.</summary>
    private static Gen<ushort> UInt16Gen =>
        from pick in Gen.Choose(0, 5)
        from value in Gen.Choose(0, ushort.MaxValue)
        select pick switch
        {
            0 => (ushort)0,
            1 => ushort.MaxValue,
            _ => (ushort)value,
        };

    /// <summary>
    /// Full u32 range, biased toward the 0 / 1 / uint.MaxValue boundaries. Built from two
    /// 16-bit draws so the whole range is reachable, not just small FsCheck-sized ints.
    /// </summary>
    private static Gen<uint> UInt32Gen =>
        from pick in Gen.Choose(0, 7)
        from high in Gen.Choose(0, ushort.MaxValue)
        from low in Gen.Choose(0, ushort.MaxValue)
        select pick switch
        {
            0 => 0u,
            1 => uint.MaxValue,
            2 => 1u,
            3 => uint.MaxValue - 1u,
            4 => (uint)low,
            _ => ((uint)high << 16) | (uint)low,
        };

    /// <summary>Every byte value, biased toward the key-up / key-down states and 0xFF.</summary>
    private static Gen<byte> ByteGen =>
        from pick in Gen.Choose(0, 5)
        from value in Gen.Choose(0, byte.MaxValue)
        select pick switch
        {
            0 => EdgeEntry.StateKeyUp,
            1 => EdgeEntry.StateKeyDown,
            2 => byte.MaxValue,
            _ => (byte)value,
        };

    /// <summary>An edge entry with arbitrary values in every field, boundaries included.</summary>
    private static Gen<EdgeEntry> EdgeEntryGen =>
        from sequence in UInt32Gen
        from timestampMs in UInt32Gen
        from state in ByteGen
        from flags in ByteGen
        from reserved in UInt16Gen
        select new EdgeEntry(sequence, timestampMs, state, flags, reserved);

    /// <summary>An edge array whose length is drawn from [minCount, maxCount].</summary>
    private static Gen<EdgeEntry[]> EdgesGen(int minCount, int maxCount) =>
        from count in Gen.Choose(minCount, maxCount)
        from e0 in EdgeEntryGen
        from e1 in EdgeEntryGen
        from e2 in EdgeEntryGen
        from e3 in EdgeEntryGen
        select new[] { e0, e1, e2, e3 }[..count];

    /// <summary>
    /// Byte buffers aimed at TryRead: random noise, deliberately truncated buffers, and
    /// buffers whose declared edge count is out of range. Most are malformed; a few parse.
    /// </summary>
    private static Gen<byte[]> CandidateBufferGen =>
        from length in Gen.Choose(0, RwkPaddleFrame.MaxFrameSize + EntrySize)
        from bytes in Gen.ListOf(ByteGen)
        from declaredCount in Gen.Choose(0, ushort.MaxValue)
        from overwriteCount in Gen.Choose(0, 1)
        select BuildCandidate(length, bytes.ToArray(), (ushort)declaredCount, overwriteCount == 1);

    private static byte[] BuildCandidate(int length, byte[] noise, ushort declaredCount, bool overwriteCount)
    {
        byte[] buffer = new byte[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i] = noise.Length == 0 ? (byte)i : noise[i % noise.Length];
        }

        // Half the time, plant a specific edge count in the header so plausible-looking
        // frames (including legal counts with a truncated payload) get exercised too.
        if (overwriteCount && length >= HeaderSize)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), declaredCount);
        }

        return buffer;
    }

    // ---------------------------------------------------- Property 16: structure

    /// <summary>
    /// Property 16: RWK-PADDLE Frame Structure. Any frame built from an arbitrary epoch and
    /// 1-4 arbitrary edges serializes to the specified little-endian layout at the specified
    /// offsets, reports the exact serialized size, and round-trips with every field intact.
    ///
    /// **Validates: Requirements 6.1, 6.2, 6.3**
    /// </summary>
    [Property]
    public Property Property16_FrameStructure_SerializesToLayoutAndRoundTrips()
    {
        var gen = from epoch in UInt16Gen
                  from edges in EdgesGen(RwkPaddleFrame.MinEdgeCount, RwkPaddleFrame.MaxEdgeCount)
                  select (epoch, edges);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (epoch, edges) = input;

            if (!RwkPaddleFrame.TryCreate(epoch, edges, out RwkPaddleFrame frame))
            {
                return false;
            }

            // Buffer is deliberately larger than the frame so we can check that nothing
            // beyond the declared size is touched.
            byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize + EntrySize];
            if (!frame.TryWrite(buffer, out int written))
            {
                return false;
            }

            int expectedSize = HeaderSize + (edges.Length * EntrySize);
            if (written != expectedSize || frame.SerializedSize != expectedSize)
            {
                return false;
            }

            // Header: Epoch u16 at 0, EdgeCount u16 at 2, little-endian (6.2).
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0)) != epoch)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(2)) != edges.Length)
            {
                return false;
            }

            // Entries: 12 bytes each at HeaderSize + i * 12 (6.3).
            for (int i = 0; i < edges.Length; i++)
            {
                int offset = HeaderSize + (i * EntrySize);
                EdgeEntry expected = edges[i];

                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)) != expected.Sequence
                    || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4)) != expected.TimestampMs
                    || buffer[offset + 8] != expected.State
                    || buffer[offset + 9] != expected.Flags
                    || BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 10)) != expected.Reserved)
                {
                    return false;
                }
            }

            // Nothing written past the declared size.
            for (int i = written; i < buffer.Length; i++)
            {
                if (buffer[i] != 0)
                {
                    return false;
                }
            }

            // Round-trip: equivalent frame, every field preserved.
            if (!RwkPaddleFrame.TryRead(buffer.AsSpan(0, written), out RwkPaddleFrame parsed, out int consumed))
            {
                return false;
            }

            if (consumed != written || parsed.Epoch != epoch || parsed.EdgeCount != edges.Length)
            {
                return false;
            }

            for (int i = 0; i < edges.Length; i++)
            {
                if (!parsed.TryGetEdge(i, out EdgeEntry actual) || actual != edges[i])
                {
                    return false;
                }
            }

            // Re-serializing the parsed frame reproduces the same bytes.
            byte[] again = new byte[RwkPaddleFrame.MaxFrameSize];
            return parsed.TryWrite(again, out int rewritten)
                && rewritten == written
                && again.AsSpan(0, written).SequenceEqual(buffer.AsSpan(0, written));
        });
    }

    /// <summary>
    /// Property 16: RWK-PADDLE Frame Structure, read side. TryRead never throws on any byte
    /// buffer, reports 0 bytes consumed when it rejects the input, and only accepts buffers
    /// that genuinely carry the specified structure — a parse that succeeds re-serializes to
    /// exactly the bytes it consumed.
    ///
    /// **Validates: Requirements 6.1, 6.2, 6.3**
    /// </summary>
    [Property]
    public Property Property16_FrameStructure_TryReadRejectsMalformedInputWithoutThrowing()
    {
        return Prop.ForAll(CandidateBufferGen.ToArbitrary(), buffer =>
        {
            bool ok = RwkPaddleFrame.TryRead(buffer, out RwkPaddleFrame frame, out int consumed);

            if (!ok)
            {
                return consumed == 0;
            }

            // A successful parse implies the buffer really did hold a legal frame.
            if (frame.EdgeCount is < RwkPaddleFrame.MinEdgeCount or > RwkPaddleFrame.MaxEdgeCount)
            {
                return false;
            }

            int expectedSize = HeaderSize + (frame.EdgeCount * EntrySize);
            if (consumed != expectedSize || buffer.Length < consumed)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0)) != frame.Epoch)
            {
                return false;
            }

            byte[] round = new byte[RwkPaddleFrame.MaxFrameSize];
            return frame.TryWrite(round, out int written)
                && written == consumed
                && round.AsSpan(0, written).SequenceEqual(buffer.AsSpan(0, consumed));
        });
    }

    // --------------------------------------------------- Property 17: redundancy

    /// <summary>
    /// Property 17: Edge Frame Redundancy. A frame carrying 2-4 edges round-trips with its
    /// whole redundancy block intact: same count, same order, same sequence numbers, same
    /// payload for every entry including the redundant copies.
    ///
    /// **Validates: Requirements 6.2, 6.4**
    /// </summary>
    [Property]
    public Property Property17_EdgeFrameRedundancy_BlockRoundTripsIntact()
    {
        var gen = from epoch in UInt16Gen
                  from edges in EdgesGen(2, RwkPaddleFrame.MaxEdgeCount)
                  select (epoch, edges);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (epoch, edges) = input;

            if (!RwkPaddleFrame.TryCreate(epoch, edges, out RwkPaddleFrame frame))
            {
                return false;
            }

            byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize];
            if (!frame.TryWrite(buffer, out int written))
            {
                return false;
            }

            if (!RwkPaddleFrame.TryRead(buffer.AsSpan(0, written), out RwkPaddleFrame parsed, out _))
            {
                return false;
            }

            if (parsed.EdgeCount != edges.Length)
            {
                return false;
            }

            // Order and sequence numbers survive, entry for entry, via the enumerator too.
            EdgeEntry[] recovered = new EdgeEntry[RwkPaddleFrame.MaxEdgeCount];
            if (!parsed.TryCopyEdgesTo(recovered, out int copied) || copied != edges.Length)
            {
                return false;
            }

            for (int i = 0; i < edges.Length; i++)
            {
                if (recovered[i] != edges[i] || recovered[i].Sequence != edges[i].Sequence)
                {
                    return false;
                }
            }

            int seen = 0;
            foreach (EdgeEntry edge in parsed)
            {
                if (edge != edges[seen])
                {
                    return false;
                }

                seen++;
            }

            return seen == edges.Length;
        });
    }

    /// <summary>
    /// Property 17: Edge Frame Redundancy, receiver consequence. When the Client emits the
    /// current edge plus up to 3 previous edges per frame (6.4), a receiver that loses one
    /// datagram can still recover the lost edge — byte-identically — from the redundant
    /// copies in a later frame, for every edge except one carried only by the final frame.
    ///
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Property]
    public Property Property17_EdgeFrameRedundancy_LostDatagramHealedFromLaterFrame()
    {
        var gen = from epoch in UInt16Gen
                  from edgeCount in Gen.Choose(2, 24)
                  from baseSequence in Gen.Choose(0, ushort.MaxValue)
                  from droppedIndex in Gen.Choose(0, 23)
                  from flags in ByteGen
                  select (epoch, edgeCount, (uint)baseSequence, droppedIndex % edgeCount, flags);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (epoch, edgeCount, baseSequence, droppedIndex, flags) = input;

            // The edge stream the Client generated: monotonic sequence and timestamp,
            // alternating key-down / key-up.
            EdgeEntry[] stream = new EdgeEntry[edgeCount];
            for (int i = 0; i < edgeCount; i++)
            {
                stream[i] = new EdgeEntry(
                    baseSequence + (uint)i,
                    (uint)(i * 37),
                    (byte)(i % 2 == 0 ? EdgeEntry.StateKeyDown : EdgeEntry.StateKeyUp),
                    flags,
                    (ushort)i);
            }

            // Datagram i carries edge i plus up to 3 previous edges (6.4), current first.
            byte[][] datagrams = new byte[edgeCount][];
            for (int i = 0; i < edgeCount; i++)
            {
                int redundant = Math.Min(i, RwkPaddleFrame.MaxEdgeCount - 1);
                EdgeEntry[] edges = new EdgeEntry[redundant + 1];
                for (int k = 0; k <= redundant; k++)
                {
                    edges[k] = stream[i - k];
                }

                if (!RwkPaddleFrame.TryCreate(epoch, edges, out RwkPaddleFrame frame))
                {
                    return false;
                }

                byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize];
                if (!frame.TryWrite(buffer, out int written))
                {
                    return false;
                }

                datagrams[i] = buffer[..written];
            }

            // The network drops datagram droppedIndex. Reassemble from what arrived.
            Dictionary<uint, EdgeEntry> received = new();
            for (int i = 0; i < edgeCount; i++)
            {
                if (i == droppedIndex)
                {
                    continue;
                }

                if (!RwkPaddleFrame.TryRead(datagrams[i], out RwkPaddleFrame frame, out int consumed)
                    || consumed != datagrams[i].Length)
                {
                    return false;
                }

                foreach (EdgeEntry edge in frame)
                {
                    if (received.TryGetValue(edge.Sequence, out EdgeEntry existing) && existing != edge)
                    {
                        // Redundant copies of the same edge must agree.
                        return false;
                    }

                    received[edge.Sequence] = edge;
                }
            }

            // Every edge is recoverable except one that only the dropped final datagram carried.
            for (int i = 0; i < edgeCount; i++)
            {
                bool onlyInDroppedFrame = droppedIndex == edgeCount - 1 && i == edgeCount - 1;

                bool recovered = received.TryGetValue(stream[i].Sequence, out EdgeEntry edge) && edge == stream[i];

                if (recovered == onlyInDroppedFrame)
                {
                    return false;
                }
            }

            return true;
        });
    }
}
