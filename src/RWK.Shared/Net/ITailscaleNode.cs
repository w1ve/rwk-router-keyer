namespace RWK.Shared.Net;

/// <summary>
/// Embedded Tailscale node providing secure mesh connectivity between Client and Station.
/// </summary>
/// <remarks>
/// Design Component 5. Implementations operate in userspace networking mode so no TUN
/// adapter and no administrator privileges are required (5.1).
/// <para>
/// Two transports are exposed: a UDP path for edge protocol datagrams (5.6) and a TCP
/// stream factory for the control channel (5.7). Path type, RTT, and DERP region are
/// surfaced as properties for the UI (5.3, 5.4, 5.5).
/// </para>
/// _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8_
/// </remarks>
public interface ITailscaleNode : IDisposable
{
    /// <summary>
    /// Raised when the node's connection state changes, including transition to
    /// <c>Fault</c> when the network path is lost (5.8).
    /// </summary>
    event EventHandler<TailscaleStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Joins the configured tailnet using a pre-authorization key (5.2).
    /// If null or empty, the node launches without an auth key and waits for
    /// interactive browser login or for an auth key to be submitted later.
    /// </summary>
    /// <param name="authKey">The Tailscale pre-auth key, or null for interactive login.</param>
    Task StartAsync(string? authKey);

    /// <summary>
    /// Leaves the tailnet and releases networking resources.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Gets the current node state.
    /// </summary>
    TailscaleState State { get; }

    /// <summary>
    /// Gets the peer's Tailscale address, or <see langword="null"/> when no peer is known.
    /// </summary>
    string? PeerAddress { get; }

    /// <summary>
    /// Gets this node's own Tailscale IPv4 address, or <see langword="null"/> before joining.
    /// </summary>
    string? SelfAddress { get; }

    /// <summary>
    /// Gets this node's own Tailscale DNS name, or <see langword="null"/> before joining.
    /// </summary>
    string? SelfDnsName { get; }

    /// <summary>
    /// Gets the current connection path type: direct or DERP-relayed (5.3).
    /// </summary>
    PathType CurrentPath { get; }

    /// <summary>
    /// Gets the most recently measured round-trip time to the peer (5.4).
    /// </summary>
    TimeSpan RoundTripTime { get; }

    /// <summary>
    /// Gets the DERP region identifier while relayed, otherwise <see langword="null"/> (5.5).
    /// </summary>
    string? DerpRegion { get; }

    /// <summary>
    /// Sends an edge protocol datagram over the UDP path (5.6).
    /// </summary>
    /// <param name="data">The serialized frame to send.</param>
    /// <returns>The number of bytes sent.</returns>
    Task<int> SendEdgeAsync(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Raised for each edge protocol datagram received on the UDP path (5.6).
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? EdgeReceived;

    /// <summary>
    /// Establishes a TCP stream to the peer for the control channel (5.7).
    /// </summary>
    /// <param name="peerAddress">The peer's Tailscale address.</param>
    /// <param name="port">The control channel port on the peer.</param>
    Task<Stream> ConnectControlAsync(string peerAddress, int port);
}
