using WinKeyerEmulator.Core.CloudRelay;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.App.Controllers;

/// <summary>
/// Transport mode for the remote command source.
/// </summary>
public enum TransportMode
{
    /// <summary>Direct UDP (requires Tailscale or port forwarding).</summary>
    Udp,
    /// <summary>Cloudflare WebSocket relay (works through any NAT).</summary>
    CloudRelay,
}

/// <summary>
/// Configuration for starting the WinKeyer emulator session.
/// </summary>
public class AppConfig
{
    /// <summary>
    /// The serial port name used for keying output (e.g., "COM3"). Required.
    /// </summary>
    public required string KeyingPortName { get; init; }

    /// <summary>
    /// Which control line (DTR or RTS) to use for keying.
    /// </summary>
    public KeyingLine KeyingLine { get; init; } = KeyingLine.DTR;

    /// <summary>
    /// The serial port name used for command input (e.g., "COM4"). Null means no serial command source.
    /// </summary>
    public string? CommandPortName { get; init; }

    /// <summary>
    /// Transport mode for the remote command channel.
    /// </summary>
    public TransportMode Transport { get; init; } = TransportMode.Udp;

    // --- UDP settings ---

    /// <summary>
    /// The IP address to bind the UDP listener to (e.g., "127.0.0.1").
    /// </summary>
    public string UdpAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// The UDP port number to listen on.
    /// </summary>
    public int UdpPort { get; init; } = 7388;

    // --- Cloud Relay settings ---

    /// <summary>
    /// The relay WebSocket URL (e.g., "wss://wrs.w1ve.com/ws").
    /// </summary>
    public string RelayUrl { get; init; } = "wss://wrs.w1ve.com/ws";

    /// <summary>
    /// The 64-character hex pairing token for the cloud relay.
    /// </summary>
    public string? PairingToken { get; init; }
}
