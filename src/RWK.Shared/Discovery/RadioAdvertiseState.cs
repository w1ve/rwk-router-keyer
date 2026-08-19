namespace RWK.Shared.Discovery;

/// <summary>
/// Whether a tracked radio may currently be broadcast on the Client's local network,
/// and if not, why.
/// </summary>
/// <remarks>
/// The withheld reasons are distinct values rather than a single "not advertising" state
/// because the Client UI has to name why a radio is absent from SmartSDR (13.20, 15.11).
/// Collapsing them would leave the operator with a radio that simply never appears and no
/// indication of which companion rule or which enable control is responsible.
/// <para>
/// _Requirements: 15.9, 15.11, 15.14, 15.17_
/// </para>
/// </remarks>
public enum RadioAdvertiseState
{
    /// <summary>
    /// The payload was rewritten to the Client-side endpoint and re-broadcast (15.10).
    /// This is the only state in which a datagram leaves the emitter.
    /// </summary>
    Advertising = 0,

    /// <summary>
    /// No enabled forward rule serves this radio's command channel, so there is no
    /// Client-side endpoint to advertise. The radio is withheld and the missing rule is
    /// reported to the UI (15.11).
    /// </summary>
    WithheldNoCommandRule = 1,

    /// <summary>
    /// The payload could not be parsed, or its address and port fields could not be
    /// located, so the mandatory rewrite failed. The emitter fails closed: nothing is
    /// broadcast and the reason is logged (15.5, 15.17).
    /// </summary>
    WithheldRewriteFailed = 2,

    /// <summary>
    /// An enable control is off — Station-side capture, Client-side re-emission, or the
    /// session itself is not alive — so no radio is advertised (15.8, 15.9).
    /// </summary>
    WithheldDisabled = 3,

    /// <summary>
    /// No report for this radio arrived within the configured expiry interval, so it has
    /// stopped being broadcast and has been removed from the advertised list (15.14).
    /// </summary>
    Expired = 4
}
