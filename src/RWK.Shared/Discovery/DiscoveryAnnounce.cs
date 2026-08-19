using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace RWK.Shared.Discovery;

/// <summary>
/// Carries one captured FlexRadio discovery datagram from Station to Client, verbatim,
/// alongside the metadata the Station already parsed out of it.
/// </summary>
/// <remarks>
/// Design "DiscoveryAnnounce Control Message". Sent on the existing TCP control channel,
/// never on the UDP edge socket: it is low-rate (a handful of messages per radio per
/// minute) and is produced and consumed at normal thread priority, so it cannot contend
/// with edge datagrams or the keying threads (15.18).
/// <para>
/// <see cref="RawPayload"/> travels intact because the <b>Client</b> performs the endpoint
/// rewrite, not the Station (15.4). Its bytes are opaque to this type — nothing here knows
/// the payload layout. The metadata fields are a convenience for logging and for the UI;
/// the Client still re-parses <see cref="RawPayload"/> through
/// <see cref="IDiscoveryPayloadCodec"/> and discards the message if that fails (15.17).
/// </para>
/// <para>
/// Serialization covers the message body only. Message framing and typing on the control
/// channel belong to the channel itself, so this type neither writes nor expects a length
/// prefix or type tag.
/// </para>
/// <para>
/// Note that <see cref="RawPayload"/> is an array, so the compiler-generated record
/// equality compares it by reference. Two announces carrying equal bytes in different
/// arrays are not equal; callers needing value comparison must compare the spans.
/// </para>
/// _Requirements: 15.2, 15.16, 15.17, 15.18_
/// </remarks>
public record DiscoveryAnnounce
{
    /// <summary>Bytes the body occupies excluding the four variable-length fields.</summary>
    /// <remarks>
    /// Three 2-byte string length prefixes, a 4-byte port, an 8-byte timestamp, and a
    /// 4-byte payload length prefix.
    /// </remarks>
    public const int FixedOverheadBytes = 22;

    /// <summary>Cap on the UTF-8 length of <see cref="Serial"/>.</summary>
    public const int MaxSerialBytes = 64;

    /// <summary>Cap on the UTF-8 length of <see cref="Model"/>.</summary>
    public const int MaxModelBytes = 64;

    /// <summary>Cap on the UTF-8 length of <see cref="StationAddress"/>.</summary>
    /// <remarks>Comfortably above the longest textual IPv6 address with a scope id.</remarks>
    public const int MaxStationAddressBytes = 64;

    /// <summary>
    /// Cap on <see cref="RawPayload"/>: the control-channel size cap for this message.
    /// </summary>
    /// <remarks>
    /// A discovery payload is the body of a single UDP datagram, so it sits far below this
    /// bound. The cap exists to keep a malformed or hostile announce from forcing a large
    /// allocation on the Client, not because any payload size is expected. It is a property
    /// of this control message, not of the discovery payload layout.
    /// </remarks>
    public const int MaxRawPayloadBytes = 2048;

    /// <summary>Largest body a valid announce can serialize to.</summary>
    public const int MaxSerializedSize =
        FixedOverheadBytes + MaxSerialBytes + MaxModelBytes + MaxStationAddressBytes + MaxRawPayloadBytes;

    /// <summary>Lowest port number a radio can advertise.</summary>
    private const int MinPort = 1;

    /// <summary>Highest port number a radio can advertise.</summary>
    private const int MaxPort = 65535;

    /// <summary>The radio serial number: the table key on the Client side (15.16).</summary>
    public required string Serial { get; init; }

    /// <summary>Model string as advertised by the radio, for UI display (13.18).</summary>
    public required string Model { get; init; }

    /// <summary>
    /// The address the radio advertised on the Station's local network, in textual form.
    /// </summary>
    public required string StationAddress { get; init; }

    /// <summary>The command port the radio advertised.</summary>
    public required int StationCommandPort { get; init; }

    /// <summary>Station capture time, in Unix milliseconds UTC.</summary>
    public required long CapturedUnixMs { get; init; }

    /// <summary>
    /// The captured datagram, verbatim. The Client rewrites it before broadcasting; the
    /// Station never modifies it (15.2, 15.4).
    /// </summary>
    public required byte[] RawPayload { get; init; }

    /// <summary>Station capture time as a UTC <see cref="DateTime"/>.</summary>
    public DateTime CapturedUtc => DateTimeOffset.FromUnixTimeMilliseconds(CapturedUnixMs).UtcDateTime;

    /// <summary>Bytes this announce occupies when serialized, or 0 if it is not valid.</summary>
    public int SerializedSize
        => TryValidate(out _)
            ? FixedOverheadBytes
              + Encoding.UTF8.GetByteCount(Serial)
              + Encoding.UTF8.GetByteCount(Model)
              + Encoding.UTF8.GetByteCount(StationAddress)
              + RawPayload.Length
            : 0;

    /// <summary>
    /// Builds an announce from a captured datagram and the metadata parsed from it.
    /// </summary>
    /// <param name="radio">The radio parsed by <see cref="IDiscoveryPayloadCodec.TryParse"/>.</param>
    /// <param name="rawPayload">The captured datagram, copied so later reuse of the receive buffer cannot alter it.</param>
    /// <param name="capturedUtc">When the Station received the datagram.</param>
    /// <remarks>
    /// The result is not guaranteed valid — a radio parsed from a hostile payload could
    /// carry an over-long serial. Callers send only announces that pass
    /// <see cref="TryValidate"/>, which <see cref="TryWrite"/> enforces anyway.
    /// </remarks>
    public static DiscoveryAnnounce FromCapture(DiscoveredRadio radio, ReadOnlySpan<byte> rawPayload, DateTime capturedUtc)
    {
        ArgumentNullException.ThrowIfNull(radio);

        return new DiscoveryAnnounce
        {
            Serial = radio.Serial,
            Model = radio.Model,
            StationAddress = radio.StationAddress.ToString(),
            StationCommandPort = radio.StationCommandPort,
            CapturedUnixMs = new DateTimeOffset(capturedUtc.ToUniversalTime()).ToUnixTimeMilliseconds(),
            RawPayload = rawPayload.ToArray(),
        };
    }

    /// <summary>
    /// Checks the message against the validation rules for this control message.
    /// </summary>
    /// <param name="failureReason">
    /// The first rule violated, phrased for a log entry; <c>null</c> when the message is valid.
    /// </param>
    /// <returns><c>true</c> when every rule holds.</returns>
    /// <remarks>
    /// The rules are: <see cref="Serial"/> non-empty, because it keys the Client's radio
    /// table (15.16); <see cref="RawPayload"/> non-empty and within
    /// <see cref="MaxRawPayloadBytes"/>; <see cref="StationAddress"/> parses as an IP
    /// address; <see cref="StationCommandPort"/> within the port range; and each string
    /// within its UTF-8 cap. Whether <see cref="RawPayload"/> is a <i>usable</i> discovery
    /// payload is a separate question, answered on the Client by
    /// <see cref="IDiscoveryPayloadCodec.TryParse"/>, which discards and logs what it cannot
    /// parse (15.17).
    /// <para>Never throws.</para>
    /// </remarks>
    public bool TryValidate(out string? failureReason)
    {
        if (string.IsNullOrEmpty(Serial))
        {
            failureReason = "Serial is empty; it is the Client-side radio table key.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(Serial) > MaxSerialBytes)
        {
            failureReason = $"Serial exceeds {MaxSerialBytes} UTF-8 bytes.";
            return false;
        }

        if (Model is null)
        {
            failureReason = "Model is null.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(Model) > MaxModelBytes)
        {
            failureReason = $"Model exceeds {MaxModelBytes} UTF-8 bytes.";
            return false;
        }

        if (string.IsNullOrEmpty(StationAddress))
        {
            failureReason = "StationAddress is empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(StationAddress) > MaxStationAddressBytes)
        {
            failureReason = $"StationAddress exceeds {MaxStationAddressBytes} UTF-8 bytes.";
            return false;
        }

        if (!IPAddress.TryParse(StationAddress, out _))
        {
            failureReason = $"StationAddress '{StationAddress}' does not parse as an IP address.";
            return false;
        }

        if (StationCommandPort is < MinPort or > MaxPort)
        {
            failureReason = $"StationCommandPort {StationCommandPort} is outside {MinPort}..{MaxPort}.";
            return false;
        }

        if (CapturedUnixMs < 0)
        {
            failureReason = $"CapturedUnixMs {CapturedUnixMs} is negative.";
            return false;
        }

        if (RawPayload is null || RawPayload.Length == 0)
        {
            failureReason = "RawPayload is empty; there is nothing for the Client to rewrite.";
            return false;
        }

        if (RawPayload.Length > MaxRawPayloadBytes)
        {
            failureReason =
                $"RawPayload is {RawPayload.Length} bytes, above the {MaxRawPayloadBytes}-byte control-channel cap.";
            return false;
        }

        failureReason = null;
        return true;
    }

    /// <summary>
    /// Gets <see cref="StationAddress"/> as an <see cref="IPAddress"/> without throwing.
    /// </summary>
    /// <param name="address">The parsed address, or <c>null</c> when the text does not parse.</param>
    /// <returns><c>true</c> when the text parses.</returns>
    public bool TryGetStationAddress(out IPAddress? address)
        => IPAddress.TryParse(StationAddress, out address);

    /// <summary>
    /// Serializes the message body into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Buffer to write into; must hold at least <see cref="SerializedSize"/> bytes.</param>
    /// <param name="bytesWritten">Bytes written on success, 0 on failure.</param>
    /// <returns>
    /// <c>false</c> when the message fails <see cref="TryValidate"/> or the buffer is too
    /// small, so an invalid announce can never reach the wire.
    /// </returns>
    /// <remarks>
    /// Layout, little-endian: serial length (<c>ushort</c>) and UTF-8 bytes, model length
    /// and bytes, station address length and bytes, command port (<c>int32</c>), capture
    /// time (<c>int64</c>), raw payload length (<c>int32</c>) and bytes. Never throws.
    /// </remarks>
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        if (!TryValidate(out _))
        {
            return false;
        }

        int required = SerializedSize;
        if (destination.Length < required)
        {
            return false;
        }

        int offset = 0;
        if (!TryWriteString(destination, ref offset, Serial)
            || !TryWriteString(destination, ref offset, Model)
            || !TryWriteString(destination, ref offset, StationAddress))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], StationCommandPort);
        offset += sizeof(int);

        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], CapturedUnixMs);
        offset += sizeof(long);

        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], RawPayload.Length);
        offset += sizeof(int);

        RawPayload.CopyTo(destination[offset..]);
        offset += RawPayload.Length;

        bytesWritten = offset;
        return true;
    }

    /// <summary>
    /// Parses a message body from <paramref name="source"/>, validating it before returning it.
    /// </summary>
    /// <param name="source">Body bytes taken from one control-channel message. Trailing bytes are ignored.</param>
    /// <param name="announce">The parsed, valid message on success; <c>null</c> on failure.</param>
    /// <param name="bytesConsumed">Bytes consumed on success, 0 on failure.</param>
    /// <param name="failureReason">Reason for rejection, suitable for a log entry; <c>null</c> on success.</param>
    /// <returns><c>true</c> only when the body is well formed and passes <see cref="TryValidate"/>.</returns>
    /// <remarks>
    /// Never throws, for any input. Length prefixes are checked against both the remaining
    /// buffer and the field caps before anything is allocated, so a malformed announce
    /// cannot force a large allocation.
    /// </remarks>
    public static bool TryRead(
        ReadOnlySpan<byte> source,
        out DiscoveryAnnounce? announce,
        out int bytesConsumed,
        out string? failureReason)
    {
        announce = null;
        bytesConsumed = 0;

        int offset = 0;
        if (!TryReadString(source, ref offset, MaxSerialBytes, nameof(Serial), out string serial, out failureReason)
            || !TryReadString(source, ref offset, MaxModelBytes, nameof(Model), out string model, out failureReason)
            || !TryReadString(source, ref offset, MaxStationAddressBytes, nameof(StationAddress), out string stationAddress, out failureReason))
        {
            return false;
        }

        if (source.Length - offset < sizeof(int) + sizeof(long) + sizeof(int))
        {
            failureReason = "Message body is truncated before the port, timestamp, and payload length fields.";
            return false;
        }

        int commandPort = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += sizeof(int);

        long capturedUnixMs = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += sizeof(int);

        if (payloadLength < 0 || payloadLength > MaxRawPayloadBytes)
        {
            failureReason = $"RawPayload length {payloadLength} is outside 0..{MaxRawPayloadBytes}.";
            return false;
        }

        if (source.Length - offset < payloadLength)
        {
            failureReason = $"Message body is truncated: {payloadLength} payload bytes declared, {source.Length - offset} available.";
            return false;
        }

        DiscoveryAnnounce candidate = new()
        {
            Serial = serial,
            Model = model,
            StationAddress = stationAddress,
            StationCommandPort = commandPort,
            CapturedUnixMs = capturedUnixMs,
            RawPayload = source.Slice(offset, payloadLength).ToArray(),
        };
        offset += payloadLength;

        if (!candidate.TryValidate(out failureReason))
        {
            return false;
        }

        announce = candidate;
        bytesConsumed = offset;
        return true;
    }

    private static bool TryWriteString(Span<byte> destination, ref int offset, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (destination.Length - offset < sizeof(ushort) + byteCount)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)byteCount);
        offset += sizeof(ushort);

        _ = Encoding.UTF8.GetBytes(value, destination[offset..]);
        offset += byteCount;
        return true;
    }

    private static bool TryReadString(
        ReadOnlySpan<byte> source,
        ref int offset,
        int maxBytes,
        string fieldName,
        out string value,
        out string? failureReason)
    {
        value = string.Empty;

        if (source.Length - offset < sizeof(ushort))
        {
            failureReason = $"Message body is truncated before the {fieldName} length prefix.";
            return false;
        }

        int byteCount = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += sizeof(ushort);

        if (byteCount > maxBytes)
        {
            failureReason = $"{fieldName} declares {byteCount} bytes, above its {maxBytes}-byte cap.";
            return false;
        }

        if (source.Length - offset < byteCount)
        {
            failureReason = $"Message body is truncated: {fieldName} declares {byteCount} bytes, {source.Length - offset} available.";
            return false;
        }

        value = Encoding.UTF8.GetString(source.Slice(offset, byteCount));
        offset += byteCount;
        failureReason = null;
        return true;
    }
}
