using RWK.Shared.Discovery;
using Xunit;

namespace RWK.Shared.Tests.Discovery;

/// <summary>
/// Covers the DiscoveryAnnounce validation rules and its control-channel body
/// serialization (task 3.3). The payload itself is opaque here — these tests use arbitrary
/// bytes, since nothing outside the codec knows the discovery payload layout.
/// </summary>
public class DiscoveryAnnounceTests
{
    private static DiscoveryAnnounce Valid(byte[]? payload = null) => new()
    {
        Serial = "1234-5678-9012",
        Model = "FLEX-6400",
        StationAddress = "192.168.5.20",
        StationCommandPort = 12345,
        CapturedUnixMs = 1_700_000_000_000,
        RawPayload = payload ?? new byte[] { 0x01, 0x02, 0x03, 0xFF, 0x00, 0x7F },
    };

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        DiscoveryAnnounce original = Valid();
        byte[] buffer = new byte[original.SerializedSize];

        Assert.True(original.TryWrite(buffer, out int written));
        Assert.Equal(buffer.Length, written);

        Assert.True(DiscoveryAnnounce.TryRead(buffer, out DiscoveryAnnounce? read, out int consumed, out string? reason));
        Assert.Null(reason);
        Assert.Equal(written, consumed);
        Assert.NotNull(read);
        Assert.Equal(original.Serial, read!.Serial);
        Assert.Equal(original.Model, read.Model);
        Assert.Equal(original.StationAddress, read.StationAddress);
        Assert.Equal(original.StationCommandPort, read.StationCommandPort);
        Assert.Equal(original.CapturedUnixMs, read.CapturedUnixMs);
        Assert.Equal(original.RawPayload, read.RawPayload);
    }

    [Fact]
    public void Round_trip_preserves_non_ascii_and_maximum_size_payload()
    {
        byte[] payload = new byte[DiscoveryAnnounce.MaxRawPayloadBytes];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        DiscoveryAnnounce original = Valid(payload) with { Model = "FLEX-6600Ω" };
        byte[] buffer = new byte[original.SerializedSize];

        Assert.True(original.TryWrite(buffer, out _));
        Assert.True(DiscoveryAnnounce.TryRead(buffer, out DiscoveryAnnounce? read, out _, out _));
        Assert.Equal("FLEX-6600Ω", read!.Model);
        Assert.Equal(payload, read.RawPayload);
    }

    [Fact]
    public void Empty_serial_is_rejected_because_it_is_the_table_key()
    {
        Assert.False((Valid() with { Serial = "" }).TryValidate(out string? reason));
        Assert.Contains("Serial", reason);
    }

    [Fact]
    public void Empty_raw_payload_is_rejected()
    {
        Assert.False((Valid() with { RawPayload = Array.Empty<byte>() }).TryValidate(out string? reason));
        Assert.Contains("RawPayload", reason);
    }

    [Fact]
    public void Raw_payload_above_the_control_channel_cap_is_rejected()
    {
        DiscoveryAnnounce announce = Valid(new byte[DiscoveryAnnounce.MaxRawPayloadBytes + 1]);

        Assert.False(announce.TryValidate(out string? reason));
        Assert.Contains("cap", reason);
        Assert.Equal(0, announce.SerializedSize);
    }

    [Fact]
    public void Station_address_that_is_not_an_ip_address_is_rejected()
    {
        Assert.False((Valid() with { StationAddress = "flex-6400.local" }).TryValidate(out string? reason));
        Assert.Contains("does not parse", reason);
    }

    [Fact]
    public void Port_outside_the_valid_range_is_rejected()
    {
        Assert.False((Valid() with { StationCommandPort = 0 }).TryValidate(out _));
        Assert.False((Valid() with { StationCommandPort = 70_000 }).TryValidate(out _));
    }

    [Fact]
    public void An_invalid_announce_never_reaches_the_wire()
    {
        DiscoveryAnnounce invalid = Valid() with { Serial = "" };
        byte[] buffer = new byte[DiscoveryAnnounce.MaxSerializedSize];

        Assert.False(invalid.TryWrite(buffer, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryWrite_fails_without_writing_when_the_buffer_is_too_small()
    {
        DiscoveryAnnounce announce = Valid();
        byte[] buffer = new byte[announce.SerializedSize - 1];

        Assert.False(announce.TryWrite(buffer, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryRead_rejects_a_truncated_body_at_every_length()
    {
        DiscoveryAnnounce announce = Valid();
        byte[] buffer = new byte[announce.SerializedSize];
        Assert.True(announce.TryWrite(buffer, out int written));

        for (int length = 0; length < written; length++)
        {
            Assert.False(
                DiscoveryAnnounce.TryRead(buffer.AsSpan(0, length), out DiscoveryAnnounce? read, out int consumed, out string? reason),
                $"a {length}-byte body must not parse");
            Assert.Null(read);
            Assert.Equal(0, consumed);
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    [Fact]
    public void TryRead_ignores_trailing_bytes_beyond_the_body()
    {
        DiscoveryAnnounce announce = Valid();
        byte[] buffer = new byte[announce.SerializedSize + 16];
        Assert.True(announce.TryWrite(buffer, out int written));

        Assert.True(DiscoveryAnnounce.TryRead(buffer, out DiscoveryAnnounce? read, out int consumed, out _));
        Assert.Equal(written, consumed);
        Assert.Equal(announce.Serial, read!.Serial);
    }

    [Fact]
    public void TryRead_rejects_a_declared_payload_length_above_the_cap_without_allocating_it()
    {
        // Serial, model, and address length prefixes of zero, then port, timestamp, and an
        // absurd payload length. A hostile announce must be refused on the declared length.
        byte[] body = new byte[DiscoveryAnnounce.FixedOverheadBytes];
        BitConverter.TryWriteBytes(body.AsSpan(6), 12345);            // port
        BitConverter.TryWriteBytes(body.AsSpan(10), 0L);             // captured time
        BitConverter.TryWriteBytes(body.AsSpan(18), int.MaxValue);   // payload length

        Assert.False(DiscoveryAnnounce.TryRead(body, out DiscoveryAnnounce? read, out _, out string? reason));
        Assert.Null(read);
        Assert.Contains("RawPayload length", reason);
    }

    [Fact]
    public void FromCapture_copies_the_payload_so_buffer_reuse_cannot_alter_it()
    {
        byte[] receiveBuffer = { 0xAA, 0xBB, 0xCC };
        DiscoveredRadio radio = new(
            "1234-5678-9012",
            "FLEX-6400",
            System.Net.IPAddress.Parse("192.168.5.20"),
            12345,
            DateTime.UtcNow,
            AdvertisedLocalEndpoint: null);

        DiscoveryAnnounce announce = DiscoveryAnnounce.FromCapture(radio, receiveBuffer, DateTime.UtcNow);
        receiveBuffer[0] = 0x00;

        Assert.True(announce.TryValidate(out _));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, announce.RawPayload);
        Assert.Equal(radio.Serial, announce.Serial);
        Assert.Equal("192.168.5.20", announce.StationAddress);
    }
}
