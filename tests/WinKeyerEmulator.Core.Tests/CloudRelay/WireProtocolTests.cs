using WinKeyerEmulator.Core.CloudRelay;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.CloudRelay;

/// <summary>
/// Tests for the WRS wire protocol serializer/deserializer.
/// </summary>
public class WireProtocolTests
{
    // ===== Round-trip tests =====

    [Fact]
    public void RoundTrip_EmptyPayload()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 1, Array.Empty<byte>());

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(frame.Version, result.Version);
        Assert.Equal(frame.Flags, result.Flags);
        Assert.Equal(frame.SessionId, result.SessionId);
        Assert.Equal(frame.SequenceNumber, result.SequenceNumber);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void RoundTrip_DataPayload()
    {
        var payload = new byte[] { 0x48, 0x45, 0x4C, 0x4C, 0x4F }; // "HELLO"
        var frame = new WireFrame(1, FrameFlags.None, 42, 100, payload);

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(1, result.Version);
        Assert.Equal(FrameFlags.None, result.Flags);
        Assert.Equal(42u, result.SessionId);
        Assert.Equal(100u, result.SequenceNumber);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void RoundTrip_HeartbeatFrame()
    {
        var frame = new WireFrame(1, FrameFlags.Heartbeat, 0, 0, Array.Empty<byte>());

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(FrameFlags.Heartbeat, result.Flags);
    }

    [Fact]
    public void RoundTrip_ControlFrame()
    {
        var payload = new byte[] { 0x03 }; // PAIRED notification
        var frame = new WireFrame(1, FrameFlags.Control, 0, 0, payload);

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(FrameFlags.Control, result.Flags);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void RoundTrip_SessionOpenFrame()
    {
        var frame = new WireFrame(1, FrameFlags.SessionOpen, 0, 0, Array.Empty<byte>());

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(FrameFlags.SessionOpen, result.Flags);
    }

    [Fact]
    public void RoundTrip_SessionCloseFrame()
    {
        var payload = new byte[] { 0x01 }; // reason: grace period expired
        var frame = new WireFrame(1, FrameFlags.SessionClose, 0, 0, payload);

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(FrameFlags.SessionClose, result.Flags);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void RoundTrip_MaxSequenceNumber()
    {
        var frame = new WireFrame(1, FrameFlags.None, uint.MaxValue, uint.MaxValue, new byte[] { 0xFF });

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(uint.MaxValue, result.SessionId);
        Assert.Equal(uint.MaxValue, result.SequenceNumber);
    }

    [Fact]
    public void RoundTrip_LargePayload()
    {
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        var frame = new WireFrame(1, FrameFlags.None, 0, 1, payload);

        var bytes = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(bytes, out var result, out var error);

        Assert.True(success, error);
        Assert.Equal(payload, result.Payload);
    }

    // ===== Serialization format tests =====

    [Fact]
    public void Serialize_ProducesCorrectMagicBytes()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, Array.Empty<byte>());
        var bytes = WireProtocol.Serialize(frame);

        // Magic "WK" = 0x57 0x4B
        Assert.Equal(0x57, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }

    [Fact]
    public void Serialize_EmptyPayload_ProducesMinimumFrameSize()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, Array.Empty<byte>());
        var bytes = WireProtocol.Serialize(frame);

        Assert.Equal(WireProtocol.MinFrameSize, bytes.Length);
    }

    [Fact]
    public void Serialize_PayloadLength_IncludedInFrameSize()
    {
        var payload = new byte[10];
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, payload);
        var bytes = WireProtocol.Serialize(frame);

        Assert.Equal(WireProtocol.MinFrameSize + 10, bytes.Length);
    }

    // ===== Deserialization error cases =====

    [Fact]
    public void Deserialize_TooShort_ReturnsFalse()
    {
        var data = new byte[5]; // Way too short

        var success = WireProtocol.TryDeserialize(data, out _, out var error);

        Assert.False(success);
        Assert.Contains("too short", error);
    }

    [Fact]
    public void Deserialize_BadMagic_ReturnsFalse()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, Array.Empty<byte>());
        var bytes = WireProtocol.Serialize(frame);
        bytes[0] = 0x00; // corrupt magic

        var success = WireProtocol.TryDeserialize(bytes, out _, out var error);

        Assert.False(success);
        Assert.Contains("magic", error);
    }

    [Fact]
    public void Deserialize_CorruptedCrc_ReturnsFalse()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, new byte[] { 0x01, 0x02, 0x03 });
        var bytes = WireProtocol.Serialize(frame);
        // Corrupt the CRC (last 4 bytes)
        bytes[^1] ^= 0xFF;

        var success = WireProtocol.TryDeserialize(bytes, out _, out var error);

        Assert.False(success);
        Assert.Contains("CRC32", error);
    }

    [Fact]
    public void Deserialize_CorruptedPayload_ReturnsFalse()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, new byte[] { 0x01, 0x02, 0x03 });
        var bytes = WireProtocol.Serialize(frame);
        // Corrupt a payload byte
        bytes[14] ^= 0xFF;

        var success = WireProtocol.TryDeserialize(bytes, out _, out var error);

        Assert.False(success);
        Assert.Contains("CRC32", error);
    }

    [Fact]
    public void Deserialize_TruncatedPayload_ReturnsFalse()
    {
        var frame = new WireFrame(1, FrameFlags.None, 0, 0, new byte[100]);
        var bytes = WireProtocol.Serialize(frame);
        // Truncate: keep header but cut the payload short
        var truncated = bytes[..30];

        var success = WireProtocol.TryDeserialize(truncated, out _, out var error);

        Assert.False(success);
        Assert.Contains("too short", error);
    }

    // ===== CRC32 known-value tests =====

    [Fact]
    public void ComputeCrc32_EmptyInput_ReturnsKnownValue()
    {
        // CRC32 of empty byte array should be 0x00000000
        var crc = WireProtocol.ComputeCrc32(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0x00000000u, crc);
    }

    [Fact]
    public void ComputeCrc32_KnownInput_ReturnsExpectedValue()
    {
        // CRC32 of "123456789" = 0xCBF43926 (standard test vector)
        var data = "123456789"u8.ToArray();
        var crc = WireProtocol.ComputeCrc32(data);
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void ComputeCrc32_SingleByte_Deterministic()
    {
        var a = WireProtocol.ComputeCrc32(new byte[] { 0x42 });
        var b = WireProtocol.ComputeCrc32(new byte[] { 0x42 });
        Assert.Equal(a, b);
    }

    // ===== Cross-compatibility with TypeScript implementation =====

    [Fact]
    public void Serialize_MatchesExpectedBigEndianLayout()
    {
        // Verify the binary layout matches what the TypeScript relay expects
        var frame = new WireFrame(1, FrameFlags.Heartbeat, 0, 0, Array.Empty<byte>());
        var bytes = WireProtocol.Serialize(frame);

        Assert.Equal(0x57, bytes[0]); // Magic high
        Assert.Equal(0x4B, bytes[1]); // Magic low
        Assert.Equal(0x01, bytes[2]); // Version
        Assert.Equal(0x04, bytes[3]); // Flags = Heartbeat
        // SessionId = 0 (4 bytes, big-endian)
        Assert.Equal(0x00, bytes[4]);
        Assert.Equal(0x00, bytes[5]);
        Assert.Equal(0x00, bytes[6]);
        Assert.Equal(0x00, bytes[7]);
        // SequenceNumber = 0 (4 bytes, big-endian)
        Assert.Equal(0x00, bytes[8]);
        Assert.Equal(0x00, bytes[9]);
        Assert.Equal(0x00, bytes[10]);
        Assert.Equal(0x00, bytes[11]);
        // PayloadLength = 0 (2 bytes, big-endian)
        Assert.Equal(0x00, bytes[12]);
        Assert.Equal(0x00, bytes[13]);
        // CRC32 (4 bytes) at offset 14
        Assert.Equal(18, bytes.Length);
    }
}
