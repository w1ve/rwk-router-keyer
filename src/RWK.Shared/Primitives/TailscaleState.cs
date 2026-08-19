namespace RWK.Shared;

/// <summary>
/// Connection state of the embedded Tailscale node (5.8).
/// </summary>
/// <remarks>
/// <see cref="Fault"/> is reported when the network path is lost; on the Station side
/// that condition drives fail-safe F9 (9.9).
/// <para>
/// _Requirements: 5.8, 9.9, 13.1_
/// </para>
/// </remarks>
public enum TailscaleState
{
    /// <summary>Not joined to the tailnet, or stopped.</summary>
    Disconnected = 0,

    /// <summary>Join in progress: authenticating with the tailnet or negotiating a path.</summary>
    Connecting = 1,

    /// <summary>Joined and a path to the peer exists (direct or DERP-relayed).</summary>
    Connected = 2,

    /// <summary>
    /// Path lost or the node failed. Reported via the state-changed event so the
    /// Station can force key-up (9.9).
    /// </summary>
    Fault = 3,

    /// <summary>
    /// The sidecar is waiting for interactive browser login. The <c>authUrl</c> field
    /// in the status document carries the URL the user must visit. Transitions to
    /// <see cref="Connecting"/> or <see cref="Connected"/> once the user completes login.
    /// </summary>
    NeedsAuth = 4
}
