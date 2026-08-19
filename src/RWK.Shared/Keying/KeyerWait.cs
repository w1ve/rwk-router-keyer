namespace RWK.Shared.Keying;

/// <summary>
/// Blocks the calling thread until <paramref name="targetTimestamp"/> is reached on the
/// keyer's clock, returning early if <paramref name="shouldAbort"/> becomes true.
/// </summary>
/// <remarks>
/// This exists so that element timing is a parameter of the keyer rather than something
/// baked into it. Production passes
/// <see cref="Timing.HybridWaiter.WaitUntil(long, Timing.ISystemClock, Func{bool}?)"/>,
/// which sleeps coarsely and then spins for sub-millisecond precision. Tests pass a
/// waiter that advances a fake clock straight to the target, which is what makes element
/// counts and durations assertable instead of dependent on scheduler luck — the defect
/// that made the RWK v1 <c>SoftKeyer</c> tests race.
/// <para>
/// An implementation must leave the clock at or beyond <paramref name="targetTimestamp"/>
/// on a normal return, and short of it when it aborts early: the caller distinguishes the
/// two cases by reading the clock, not by a return value.
/// </para>
/// _Requirements: 3.10, 14.2_
/// </remarks>
/// <param name="targetTimestamp">Absolute timestamp, in clock ticks, to wait until.</param>
/// <param name="shouldAbort">Polled during the wait; true means return without waiting further.</param>
public delegate void KeyerWait(long targetTimestamp, Func<bool> shouldAbort);
