namespace RWK.Client.Audio;

/// <summary>
/// Raised when the sidetone engine could not open the device named in the saved configuration
/// and opened the system default render endpoint instead.
/// </summary>
/// <remarks>
/// A vanished audio device is a warning, not a failure: the operator must still be able to
/// send. The UI surfaces this non-blocking rather than aborting startup (4.6).
/// <para>
/// _Requirements: 4.6_
/// </para>
/// </remarks>
/// <param name="RequestedDeviceId">The device identifier that was persisted and could not be used.</param>
/// <param name="ActiveDeviceId">The identifier of the default endpoint actually opened.</param>
/// <param name="ActiveDeviceName">Friendly name of the endpoint actually opened.</param>
/// <param name="Reason">Human-readable explanation suitable for a status message or log line.</param>
public sealed record SidetoneDeviceFallbackEventArgs(
    string RequestedDeviceId,
    string ActiveDeviceId,
    string ActiveDeviceName,
    string Reason);
