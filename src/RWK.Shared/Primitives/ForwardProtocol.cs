namespace RWK.Shared;

/// <summary>
/// Transport protocol carried by a port forwarding rule (10.1).
/// </summary>
/// <remarks>
/// Values are explicit and MUST NOT be renumbered: the protocol is persisted with
/// each rule and pushed to the Station on session establishment (10.7, 10.8).
/// <para>
/// _Requirements: 10.1_
/// </para>
/// </remarks>
public enum ForwardProtocol
{
    /// <summary>
    /// TCP. Per-rule listener accepting multiple simultaneous connections, with
    /// bidirectional pumping and half-close propagation (10.2, 10.3, 10.4).
    /// </summary>
    Tcp = 0,

    /// <summary>
    /// UDP. NAT-style session table keyed on the source endpoint with a 60-second
    /// idle timeout, preserving datagram boundaries (10.5, 10.6).
    /// </summary>
    Udp = 1
}
