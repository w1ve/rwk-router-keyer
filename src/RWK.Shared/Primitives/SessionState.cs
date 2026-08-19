namespace RWK.Shared;

/// <summary>
/// Lifecycle state of a keying session as tracked by the Station session manager (11.8).
/// </summary>
/// <remarks>
/// _Requirements: 11.8, 11.4, 11.5, 13.5_
/// </remarks>
public enum SessionState
{
    /// <summary>
    /// Control connection accepted, HMAC challenge sent, awaiting the response. No keying
    /// is accepted in this state; the connection closes if the response is invalid or the
    /// 10-second window expires (11.2, 11.3, 11.5).
    /// </summary>
    Authenticating = 0,

    /// <summary>Authenticated and keying (11.4).</summary>
    Active = 1,

    /// <summary>
    /// Session retained but keying is impaired by a fail-safe that does not require manual
    /// Re-Arm — F1 or F9. Returns to <see cref="Active"/> when valid edges resume (9.1, 9.9, 9.12).
    /// </summary>
    Degraded = 2,

    /// <summary>
    /// Session ended: by the operator, by the Station owner forcing a disconnect (11.7), or
    /// by a latching fail-safe such as F2 (9.2).
    /// </summary>
    Closed = 3
}
