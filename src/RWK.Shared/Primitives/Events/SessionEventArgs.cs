namespace RWK.Shared;

/// <summary>
/// Reports that a keying session started or ended, or that its state changed (11.8).
/// </summary>
/// <remarks>
/// The design names this type on <c>ISessionManager.SessionStarted</c> and
/// <c>SessionEnded</c> without giving its members. It is kept self-contained — plain
/// fields rather than a reference to the Station's <c>ActiveSession</c> record — so that
/// an "ended" notification can still describe a session that no longer exists, and so
/// this shared type takes no dependency on a Station-side type.
/// <para>
/// _Requirements: 11.4, 11.5, 11.6, 11.7, 11.8, 13.5_
/// </para>
/// </remarks>
/// <param name="ClientAddress">Tailnet address of the Client peer.</param>
/// <param name="ClientName">Client-supplied display name, shown in the Station UI (13.5).</param>
/// <param name="State">Session state at the moment of the event.</param>
/// <param name="TimestampUtc">When the event occurred, in UTC.</param>
/// <param name="Reason">
/// Optional detail: why authentication was refused (11.5), why a new connection was
/// rejected while a session was active (11.6), or why the session closed — including the
/// owner forcing a disconnect (11.7) or a latching fail-safe.
/// </param>
public record SessionEventArgs(
    string ClientAddress,
    string ClientName,
    SessionState State,
    DateTime TimestampUtc,
    string? Reason = null
);
