namespace RWK.Shared.Discovery;

/// <summary>
/// The emitter's per-radio table after a change, for the Client UI's advertised-radio
/// list (13.18, 13.20).
/// </summary>
/// <param name="Radios">
/// Every tracked radio with its current advertise state, including radios currently
/// withheld. An empty list is the correct content after session loss (15.13).
/// </param>
/// <remarks>
/// The whole table is carried rather than a delta because the UI renders a list and each
/// radio is tracked independently (15.16) — a snapshot cannot show a stale radio that a
/// missed delta would leave behind.
/// <para>
/// _Requirements: 13.18, 13.20, 15.13, 15.16_
/// </para>
/// </remarks>
public record AdvertisedRadiosChangedEventArgs(IReadOnlyList<AdvertisedRadio> Radios);
