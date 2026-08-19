namespace RWK.Shared.Keying;

/// <summary>
/// What one call to <see cref="KeyerElementPump.Pump"/> actually did.
/// </summary>
/// <remarks>
/// The caller needs this to know whether to idle: <see cref="Idle"/> means no input was
/// pending and the pump returned without consuming time, so the keyer thread should wait
/// briefly rather than spin. Every other value means the pump blocked for at least one
/// element's worth of time.
/// <para>
/// The values also name the arbitration outcome, which is why they are reported rather
/// than kept private — a caller (or a test) can see which source won.
/// </para>
/// _Requirements: 3.6, 3.7, 3.8_
/// </remarks>
public enum PumpAction
{
    /// <summary>Nothing was pending; no time was consumed and no edge was emitted.</summary>
    Idle = 0,

    /// <summary>A WinKeyer immediate key-down or key-up was applied (2.4).</summary>
    Immediate = 1,

    /// <summary>A straight-key contact transition was passed through (3.6).</summary>
    StraightKey = 2,

    /// <summary>One paddle-generated element (dit or dah) was sent (3.1-3.5).</summary>
    PaddleElement = 3,

    /// <summary>One character of queued host text was sent, or aborted by paddle break-in (3.7).</summary>
    HostCharacter = 4,

    /// <summary>An <see cref="KeyerElementPump.AbortAndClear"/> request was serviced.</summary>
    Aborted = 5
}
