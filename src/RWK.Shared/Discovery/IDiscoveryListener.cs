namespace RWK.Shared.Discovery;

/// <summary>
/// Station-side capture of FlexRadio discovery broadcasts on the Station's local network.
/// </summary>
/// <remarks>
/// Design Component 10. Implementations bind a UDP socket on the Station LAN, parse each
/// datagram through <see cref="IDiscoveryPayloadCodec"/> only far enough to extract identity
/// and the advertised endpoint, and raise <see cref="DiscoveryCaptured"/> with the verbatim
/// payload alongside that metadata. The Station forwards it to the Client as a
/// <see cref="DiscoveryAnnounce"/> on the TCP control channel (15.2); it never rewrites the
/// payload, because the rewrite target is a Client-side endpoint.
/// <para>
/// Datagrams the codec cannot parse are discarded with a log entry naming the reason, so a
/// malformed broadcast from an unrelated device on the Station LAN does not propagate
/// (15.17).
/// </para>
/// <para>
/// The socket receive loop and all dispatch run at <b>normal thread priority</b> so the
/// TIME_CRITICAL edge replay thread and the watchdog always preempt discovery work (15.18).
/// </para>
/// _Requirements: 15.1, 15.2, 15.6, 15.7, 15.17, 15.18_
/// </remarks>
public interface IDiscoveryListener : IDisposable
{
    /// <summary>
    /// Raised for each datagram that parses, carrying the parsed radio and the verbatim
    /// payload.
    /// </summary>
    event EventHandler<DiscoveryCapturedEventArgs>? DiscoveryCaptured;

    /// <summary>
    /// Binds the discovery socket.
    /// </summary>
    /// <param name="config">Port, bind address, and address-reuse setting.</param>
    /// <remarks>
    /// Called only while the Station-side capture control is on (15.7). The listener is
    /// never started by default (15.6).
    /// </remarks>
    void Start(DiscoveryListenerConfig config);

    /// <summary>
    /// Stops capturing and releases the socket, as required when the capture control is
    /// turned off (15.7).
    /// </summary>
    void Stop();

    /// <summary>Gets whether the socket is currently bound and capturing.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the radios seen so far, keyed by serial in the implementation's table (15.16).
    /// </summary>
    IReadOnlyList<DiscoveredRadio> KnownRadios { get; }
}
