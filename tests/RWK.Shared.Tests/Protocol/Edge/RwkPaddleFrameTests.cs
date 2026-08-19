/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Protocol.Edge;
using Xunit;

namespace RWK.Shared.Tests.Protocol.Edge;

/// <summary>
/// Unit tests for the RWK-PADDLE frame codec (Requirements 6.1, 6.2, 6.3).
/// </summary>
public class RwkPaddleFrameTests
{
    [Fact]
    public void SizeConstants_MatchWireLayout()
    {
        Assert.Equal(4, RwkPaddleFrame.HeaderSize);
        Assert.Equal(12, EdgeEntry.Size);
        Assert.Equal(16, RwkPaddleFrame.MinFrameSize);
        Assert.Equal(52, RwkPaddleFrame.MaxFrameSize);
    }

    [Fact]
    public void TryWrite_ProducesLittleEndianHeaderAndEntry()
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            0x0201,
            new[] { new EdgeEntry(0x0A0B0C0D, 0x01020304, EdgeEntry.StateKeyDown, 0x7F, 0xBEEF) });

        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));
        Assert.Equal(16, written);

        // Epoch, EdgeCount
        Assert.Equal(new byte[] { 0x01, 0x02, 0x01, 0x00 }, buffer[..4].ToArray());
        // Sequence, TimestampMs, State, Flags, Reserved
        Assert.Equal(new byte[] { 0x0D, 0x0C, 0x0B, 0x0A }, buffer[4..8].ToArray());
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, buffer[8..12].ToArray());
        Assert.Equal(EdgeEntry.StateKeyDown, buffer[12]);
        Assert.Equal(0x7F, buffer[13]);
        Assert.Equal(new byte[] { 0xEF, 0xBE }, buffer[14..16].ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RoundTrip_PreservesEpochAndAllEdges(int edgeCount)
    {
        EdgeEntry[] edges = new EdgeEntry[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            edges[i] = new EdgeEntry((uint)(100 + i), (uint)(1000 + (i * 7)), (byte)(i % 2), (byte)i, (ushort)(i * 3));
        }

        RwkPaddleFrame frame = RwkPaddleFrame.Create(4242, edges);

        Span<byte> buffer = stackalloc byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));
        Assert.Equal(RwkPaddleFrame.FrameSize(edgeCount), written);

        Assert.True(RwkPaddleFrame.TryRead(buffer[..written], out RwkPaddleFrame parsed, out int consumed));
        Assert.Equal(written, consumed);
        Assert.Equal(4242, parsed.Epoch);
        Assert.Equal(edgeCount, parsed.EdgeCount);

        for (int i = 0; i < edgeCount; i++)
        {
            Assert.Equal(edges[i], parsed[i]);
        }
    }

    [Fact]
    public void TryWrite_FailsWhenDestinationTooSmall()
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(1, new[] { EdgeEntry.KeyDownAt(1, 0) });

        Span<byte> tooSmall = stackalloc byte[RwkPaddleFrame.MinFrameSize - 1];
        Assert.False(frame.TryWrite(tooSmall, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryCreate_RejectsIllegalEdgeCounts()
    {
        Assert.False(RwkPaddleFrame.TryCreate(1, ReadOnlySpan<EdgeEntry>.Empty, out _));
        Assert.False(RwkPaddleFrame.TryCreate(1, new EdgeEntry[RwkPaddleFrame.MaxEdgeCount + 1], out _));
        Assert.True(RwkPaddleFrame.TryCreate(1, new EdgeEntry[RwkPaddleFrame.MaxEdgeCount], out _));
    }

    [Fact]
    public void TryRead_FailsWhenBufferShorterThanHeader()
    {
        Assert.False(RwkPaddleFrame.TryRead(new byte[RwkPaddleFrame.HeaderSize - 1], out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(ushort.MaxValue)]
    public void TryRead_RejectsIllegalDeclaredEdgeCount(int declaredCount)
    {
        byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize];
        buffer[2] = (byte)(declaredCount & 0xFF);
        buffer[3] = (byte)((declaredCount >> 8) & 0xFF);

        Assert.False(RwkPaddleFrame.TryRead(buffer, out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryRead_FailsWhenBufferTooSmallForDeclaredEdgeCount()
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(
            7,
            new[] { EdgeEntry.KeyDownAt(1, 0), EdgeEntry.KeyUpAt(2, 10) });

        byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));

        // Declares 2 edges but only one edge worth of payload is present.
        Assert.False(RwkPaddleFrame.TryRead(buffer.AsSpan(0, written - 1), out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryRead_IgnoresTrailingBytes()
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(9, new[] { EdgeEntry.KeyDownAt(5, 250) });

        byte[] buffer = new byte[RwkPaddleFrame.MaxFrameSize];
        Assert.True(frame.TryWrite(buffer, out int written));

        Assert.True(RwkPaddleFrame.TryRead(buffer, out RwkPaddleFrame parsed, out int consumed));
        Assert.Equal(written, consumed);
        Assert.Equal(1, parsed.EdgeCount);
        Assert.True(parsed[0].KeyDown);
    }

    [Fact]
    public void Enumerator_YieldsExactlyEdgeCountEdges()
    {
        EdgeEntry[] edges = { EdgeEntry.KeyDownAt(1, 0), EdgeEntry.KeyUpAt(2, 20), EdgeEntry.KeyDownAt(3, 40) };
        RwkPaddleFrame frame = RwkPaddleFrame.Create(3, edges);

        int seen = 0;
        foreach (EdgeEntry edge in frame)
        {
            Assert.Equal(edges[seen], edge);
            seen++;
        }

        Assert.Equal(edges.Length, seen);
    }

    [Fact]
    public void TryGetEdge_FailsOutsideEdgeCount()
    {
        RwkPaddleFrame frame = RwkPaddleFrame.Create(1, new[] { EdgeEntry.KeyDownAt(1, 0) });

        Assert.False(frame.TryGetEdge(-1, out _));
        Assert.False(frame.TryGetEdge(1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame[1]);
    }

    [Fact]
    public void DefaultFrame_DoesNotSerialize()
    {
        RwkPaddleFrame empty = default;
        Assert.False(empty.TryWrite(new byte[RwkPaddleFrame.MaxFrameSize], out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void EdgeEntry_RoundTripsIndependently()
    {
        EdgeEntry entry = new(uint.MaxValue, uint.MaxValue, EdgeEntry.StateKeyUp, byte.MaxValue, ushort.MaxValue);

        Span<byte> buffer = stackalloc byte[EdgeEntry.Size];
        Assert.True(entry.TryWrite(buffer, out int written));
        Assert.Equal(EdgeEntry.Size, written);

        Assert.True(EdgeEntry.TryRead(buffer, out EdgeEntry parsed, out int consumed));
        Assert.Equal(EdgeEntry.Size, consumed);
        Assert.Equal(entry, parsed);
        Assert.False(parsed.KeyDown);
    }

    [Fact]
    public void EdgeEntry_TryReadFailsWhenSourceTooShort()
    {
        Assert.False(EdgeEntry.TryRead(new byte[EdgeEntry.Size - 1], out _, out int consumed));
        Assert.Equal(0, consumed);
    }
}
