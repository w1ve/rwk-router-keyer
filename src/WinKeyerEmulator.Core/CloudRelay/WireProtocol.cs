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

namespace WinKeyerEmulator.Core.CloudRelay;

/// <summary>
/// WRS Wire Protocol frame flags.
/// </summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0x00,
    AckRequired = 0x01,
    Retransmit = 0x02,
    Heartbeat = 0x04,
    Control = 0x08,
    SessionOpen = 0x10,
    SessionClose = 0x20,
    Resync = 0x40,
    Reserved = 0x80,
}

/// <summary>
/// A parsed WRS wire protocol frame.
/// </summary>
public readonly record struct WireFrame(
    byte Version,
    FrameFlags Flags,
    uint SessionId,
    uint SequenceNumber,
    byte[] Payload);

/// <summary>
/// WRS Wire Protocol serializer/deserializer.
/// Binary frame format matching the Cloudflare relay's TypeScript implementation.
/// </summary>
public static class WireProtocol
{
    /// <summary>Magic bytes "WK" (0x57 0x4B).</summary>
    public const ushort Magic = 0x574B;

    /// <summary>Minimum frame size: 2 magic + 1 ver + 1 flags + 4 sessId + 4 seq + 2 payLen + 4 crc = 18.</summary>
    public const int MinFrameSize = 18;

    /// <summary>Header size before payload: 2 + 1 + 1 + 4 + 4 + 2 = 14 bytes.</summary>
    private const int HeaderSize = 14;

    /// <summary>
    /// Serializes a WireFrame into a byte array ready for WebSocket transmission.
    /// </summary>
    public static byte[] Serialize(in WireFrame frame)
    {
        int payloadLength = frame.Payload?.Length ?? 0;
        int totalLength = HeaderSize + payloadLength + 4; // +4 for CRC32
        var buffer = new byte[totalLength];

        // Magic (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), Magic);
        // Version
        buffer[2] = frame.Version;
        // Flags
        buffer[3] = (byte)frame.Flags;
        // Session ID (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), frame.SessionId);
        // Sequence Number (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), frame.SequenceNumber);
        // Payload Length (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(12), (ushort)payloadLength);
        // Payload
        if (payloadLength > 0)
            frame.Payload!.CopyTo(buffer.AsSpan(HeaderSize));

        // CRC32 over all preceding bytes
        uint crc = ComputeCrc32(buffer.AsSpan(0, HeaderSize + payloadLength));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(HeaderSize + payloadLength), crc);

        return buffer;
    }

    /// <summary>
    /// Attempts to deserialize a byte array into a WireFrame.
    /// Returns true if successful, false if the frame is invalid.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out WireFrame frame, out string? error)
    {
        frame = default;
        error = null;

        if (data.Length < MinFrameSize)
        {
            error = "Frame too short";
            return false;
        }

        // Validate magic
        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (magic != Magic)
        {
            error = "Invalid frame header: bad magic bytes";
            return false;
        }

        byte version = data[2];
        var flags = (FrameFlags)data[3];
        uint sessionId = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
        uint sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
        ushort payloadLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(12));

        int expectedLength = HeaderSize + payloadLength + 4;
        if (data.Length < expectedLength)
        {
            error = "Frame too short for declared payload length";
            return false;
        }

        // Extract payload
        byte[] payload = data.Slice(HeaderSize, payloadLength).ToArray();

        // Validate CRC32
        uint declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(HeaderSize + payloadLength));
        uint computedCrc = ComputeCrc32(data.Slice(0, HeaderSize + payloadLength));
        if (declaredCrc != computedCrc)
        {
            error = "Data corruption: CRC32 mismatch";
            return false;
        }

        frame = new WireFrame(version, flags, sessionId, sequenceNumber, payload);
        return true;
    }

    /// <summary>
    /// Computes CRC32 using the standard polynomial (0xEDB88320 reflected).
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
        }
        return crc ^ 0xFFFFFFFF;
    }
}
