using FsCheck;
using FsCheck.Xunit;
using WinKeyerEmulator.Core.CloudRelay;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.CloudRelay;

/// <summary>
/// Property-based tests for wire protocol round-trip correctness.
/// </summary>
public class WireProtocolPropertyTests
{
    /// <summary>
    /// Generates arbitrary WireFrame values for property tests.
    /// Payload limited to 1000 bytes to keep tests fast.
    /// </summary>
    public static Arbitrary<WireFrame> ArbitraryWireFrame()
    {
        var gen = from version in Gen.Constant((byte)1)
                  from flags in Gen.Elements(
                      FrameFlags.None,
                      FrameFlags.Heartbeat,
                      FrameFlags.Control,
                      FrameFlags.SessionOpen,
                      FrameFlags.SessionClose)
                  from sessionId in Arb.Generate<uint>()
                  from seqNum in Arb.Generate<uint>()
                  from payloadLen in Gen.Choose(0, 200)
                  from payload in Gen.ArrayOf(payloadLen, Arb.Generate<byte>())
                  select new WireFrame(version, flags, sessionId, seqNum, payload);

        return Arb.From(gen);
    }

    [Property(Arbitrary = new[] { typeof(WireProtocolPropertyTests) })]
    public void RoundTrip_AnyValidFrame_DeserializesIdentically(WireFrame frame)
    {
        var serialized = WireProtocol.Serialize(frame);
        var success = WireProtocol.TryDeserialize(serialized, out var result, out var error);

        Assert.True(success, $"Deserialization failed: {error}");
        Assert.Equal(frame.Version, result.Version);
        Assert.Equal(frame.Flags, result.Flags);
        Assert.Equal(frame.SessionId, result.SessionId);
        Assert.Equal(frame.SequenceNumber, result.SequenceNumber);
        Assert.Equal(frame.Payload, result.Payload);
    }

    [Property(Arbitrary = new[] { typeof(WireProtocolPropertyTests) })]
    public void Serialize_FrameLength_EqualsHeaderPlusPayloadPlusCrc(WireFrame frame)
    {
        var serialized = WireProtocol.Serialize(frame);
        int expectedLength = 14 + (frame.Payload?.Length ?? 0) + 4;
        Assert.Equal(expectedLength, serialized.Length);
    }

    [Property(Arbitrary = new[] { typeof(WireProtocolPropertyTests) })]
    public void SingleBitFlip_AnyPosition_FailsDeserialization(WireFrame frame)
    {
        var serialized = WireProtocol.Serialize(frame);

        // Skip magic bytes (position 0,1) since those produce "bad magic" not CRC error
        // We want to verify CRC catches corruption in the data portion
        if (serialized.Length <= 4) return; // Too short to corrupt meaningfully

        // Flip a bit in a random position after the magic (positions 2 through end)
        var position = System.Random.Shared.Next(2, serialized.Length);
        var bitPosition = System.Random.Shared.Next(0, 8);
        var corrupted = (byte[])serialized.Clone();
        corrupted[position] ^= (byte)(1 << bitPosition);

        var success = WireProtocol.TryDeserialize(corrupted, out _, out _);
        Assert.False(success);
    }
}
