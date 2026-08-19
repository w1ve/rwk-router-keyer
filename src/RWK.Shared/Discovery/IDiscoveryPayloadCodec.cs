using System.Net;

namespace RWK.Shared.Discovery;

/// <summary>
/// The single abstraction over the FlexRadio discovery payload layout: parses identity and
/// endpoint fields out of a payload, and writes replacement endpoint fields back into one.
/// </summary>
/// <remarks>
/// Design Component 12. The implementation of this interface is the <b>only</b> place in
/// the system that knows the payload layout — field names, ordering, encoding, offsets, and
/// any length prefix or checksum. Nothing else, including this declaration, may encode
/// layout assumptions, so correcting the layout stays a one-file change.
/// <para>
/// The layout is not established fact. The implementation (task 27.2) is gated on a
/// datagram captured from physical hardware, which becomes its test fixture (15.20); until
/// then every offset in it is provisional and marked <c>[VERIFY]</c>.
/// </para>
/// <para>
/// Both methods take arbitrary bytes off the network. Implementations MUST NOT throw for
/// any input, MUST NOT mutate the input, and MUST NOT perform I/O or logging — the caller
/// logs the returned <c>failureReason</c> (15.17).
/// </para>
/// _Requirements: 15.4, 15.5, 15.17, 15.20_
/// </remarks>
public interface IDiscoveryPayloadCodec
{
    /// <summary>
    /// Extracts radio identity and the advertised endpoint from a raw discovery payload.
    /// </summary>
    /// <param name="payload">
    /// Raw datagram bytes, which may be anything: a payload from an unrelated device on
    /// the Station LAN, a truncated datagram, or random bytes.
    /// </param>
    /// <param name="radio">
    /// On success, the parsed radio with <c>AdvertisedLocalEndpoint</c> left <c>null</c> —
    /// substituting the Client-side endpoint is the emitter's job, not the parser's.
    /// Undefined on failure.
    /// </param>
    /// <param name="failureReason">
    /// On failure, a human-readable reason suitable for the log entry required by 15.17.
    /// <c>null</c> on success.
    /// </param>
    /// <returns>
    /// <c>true</c> only when the payload matches the expected layout and carries the
    /// identity and endpoint fields; <c>false</c> for anything else.
    /// </returns>
    /// <remarks>
    /// Never throws and never mutates <paramref name="payload"/>. Parsing goes only as
    /// deep as identity, model, advertised address, and command port; no further
    /// interpretation is attempted.
    /// </remarks>
    bool TryParse(ReadOnlySpan<byte> payload, out DiscoveredRadio radio, out string? failureReason);

    /// <summary>
    /// Produces a copy of <paramref name="payload"/> with the radio address and command
    /// port fields replaced by <paramref name="localEndpoint"/>.
    /// </summary>
    /// <param name="payload">The verbatim payload as captured at the Station.</param>
    /// <param name="localEndpoint">
    /// The bind address and Client port of the <b>enabled</b> forward rule serving this
    /// radio's command channel — the endpoint SmartSDR must be told to connect to (15.4).
    /// </param>
    /// <param name="rewritten">
    /// On success, the rewritten payload. On failure, empty — never a partially rewritten
    /// or unmodified payload.
    /// </param>
    /// <param name="failureReason">
    /// On failure, a human-readable reason for the log entry required by 15.17.
    /// <c>null</c> on success.
    /// </param>
    /// <returns>
    /// <c>true</c> only when both the address field and the port field were located and the
    /// result is structurally valid.
    /// </returns>
    /// <remarks>
    /// On success, every field other than the address and the port is preserved
    /// byte-for-byte in its original position, including fields the codec does not
    /// interpret, so a firmware revision that adds fields does not break brokering. The
    /// result satisfies <see cref="TryParse"/>, and re-parsing it yields the address and
    /// port of <paramref name="localEndpoint"/>; any length prefix or checksum the layout
    /// carries is recomputed so that holds.
    /// <para>
    /// <b>Fails closed.</b> Returns <c>false</c> with <paramref name="rewritten"/> empty
    /// whenever the fields cannot be located, the payload does not parse, or a structurally
    /// valid result cannot be produced. The caller then emits nothing: no code path is
    /// permitted to broadcast an unrewritten payload, because a verbatim payload advertises
    /// a Station-network address SmartSDR cannot reach and the connection attempt fails
    /// (15.5, 15.17).
    /// </para>
    /// <para>
    /// Never throws and never mutates <paramref name="payload"/>.
    /// </para>
    /// </remarks>
    bool TryRewriteEndpoint(
        ReadOnlySpan<byte> payload,
        IPEndPoint localEndpoint,
        out byte[] rewritten,
        out string? failureReason);
}
