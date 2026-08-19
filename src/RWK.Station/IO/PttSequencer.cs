using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.IO;
using RWK.Shared.Timing;

namespace RWK.Station.IO;

/// <summary>
/// Sequences PTT around key edges: asserts PTT a lead time ahead of key-down, and holds it for a
/// tail time after key-up that each new key-down restarts.
/// </summary>
/// <remarks>
/// Split out of <see cref="StationKeyingOutput"/> so the timing rules of 8.4, 8.5, and 8.6 are
/// pure state plus arithmetic over an <see cref="ISystemClock"/>, testable with a fake clock and a
/// fake keying output and with no serial port involved.
/// <para>
/// <b>No wall-clock timers.</b> The sequencer never starts a <c>Timer</c> or sleeps. Deadlines are
/// clock timestamps; the caller drives progress by calling <see cref="Tick"/>. On the Station the
/// caller is the Edge Replayer's TIME_CRITICAL scheduler loop, which already wakes on absolute QPC
/// deadlines (7.4) — <see cref="NextDeadline"/> gives it the next time this sequencer needs
/// attention so it can fold PTT deadlines into the same wait.
/// </para>
/// <para>
/// <b>Timing model.</b> <see cref="KeyDown"/> at time T with PTT idle asserts PTT at T and returns
/// T + LeadTime as the timestamp at which the key line will assert; the key asserts on the first
/// <see cref="Tick"/> at or after that deadline (8.4, Property 25). While PTT is already asserted a
/// key-down applies immediately, because the lead time has already been served.
/// <see cref="KeyUp"/> keys up at once and arms the tail at now + TailTime (8.5). A key-down inside
/// the tail cancels it, and the following key-up arms a full tail again, so PTT never drops between
/// rapid key cycles (8.6, Property 26). PTT de-asserts only on a <see cref="Tick"/> where the tail
/// has expired, no key-down is pending, and the key is up.
/// </para>
/// <para>
/// <b>Fail-safe.</b> Any exception from the underlying output forces key-up and PTT-off before it
/// propagates, and <see cref="ForceAllUp"/> gives the fail-safe monitor and the SAFE latch a single
/// call that clears pending work and drops both lines (8.7). The SAFE latch lives in the replayer;
/// this class only obeys.
/// </para>
/// <para>
/// _Requirements: 8.4, 8.5, 8.6, 8.7_
/// </para>
/// </remarks>
public sealed class PttSequencer : IDisposable
{
    private readonly object _gate = new();
    private readonly IKeyingOutput _key;
    private readonly IPttOutput? _ptt;
    private readonly ISystemClock _clock;
    private readonly long _leadTicks;
    private readonly long _tailTicks;

    private bool _keyAsserted;
    private bool _pttAsserted;
    private long? _pendingKeyDownAt;
    private long? _tailExpiresAt;

    /// <summary>
    /// Creates a sequencer over the given outputs.
    /// </summary>
    /// <param name="keyOutput">The key line output.</param>
    /// <param name="pttOutput">
    /// The PTT output, or <see langword="null"/> when the PTT line is
    /// <see cref="KeyingLine.None"/> (8.2). With no PTT output there is nothing to lead or hold, so
    /// key edges pass straight through and no lead delay is applied.
    /// </param>
    /// <param name="timing">Lead and tail durations (defaults 15ms and 500ms).</param>
    /// <param name="clock">Timestamp source; a fake clock makes the timing deterministic in tests.</param>
    public PttSequencer(
        IKeyingOutput keyOutput,
        IPttOutput? pttOutput,
        PttTimingConfig timing,
        ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(keyOutput);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(clock);

        _key = keyOutput;
        _ptt = pttOutput;
        _clock = clock;
        _leadTicks = ToTicks(timing.LeadTime, clock.Frequency);
        _tailTicks = ToTicks(timing.TailTime, clock.Frequency);
    }

    /// <summary>
    /// Creates a sequencer for <paramref name="output"/>, wiring PTT only when the output has a PTT
    /// line configured (8.2).
    /// </summary>
    public static PttSequencer Create(
        IStationKeyingOutput output,
        PttTimingConfig timing,
        ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new PttSequencer(
            output,
            output.PttLine == KeyingLine.None ? null : output,
            timing,
            clock);
    }

    /// <summary>Whether a PTT line is configured, and therefore whether lead/tail timing applies.</summary>
    public bool PttEnabled => _ptt is not null;

    /// <summary>Whether PTT is currently asserted.</summary>
    public bool IsPttAsserted { get { lock (_gate) { return _pttAsserted; } } }

    /// <summary>Whether the key line is currently asserted.</summary>
    public bool IsKeyAsserted { get { lock (_gate) { return _keyAsserted; } } }

    /// <summary>Whether a key-down is waiting for the PTT lead time to elapse.</summary>
    public bool IsKeyDownPending { get { lock (_gate) { return _pendingKeyDownAt is not null; } } }

    /// <summary>
    /// The next timestamp at which <see cref="Tick"/> has work to do (a pending key-down or a tail
    /// expiry), or <see langword="null"/> when the sequencer is idle. Lets the replayer's scheduler
    /// include PTT deadlines in its own absolute-deadline wait.
    /// </summary>
    public long? NextDeadline
    {
        get
        {
            lock (_gate)
            {
                if (_pendingKeyDownAt is long pending)
                {
                    return _tailExpiresAt is long tail && tail < pending ? tail : pending;
                }

                return _tailExpiresAt;
            }
        }
    }

    /// <summary>
    /// Requests key-down. Asserts PTT if it is not already asserted, and reports when the key line
    /// will follow (8.4).
    /// </summary>
    /// <returns>
    /// The timestamp at which the key line asserts. With PTT already up, or with no PTT line, this
    /// is the current timestamp and the key is already down on return. Otherwise it is
    /// now + LeadTime, and the key asserts on the first <see cref="Tick"/> at or after it.
    /// </returns>
    public long KeyDown()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();

            // A key-down is pending or in effect, so the tail must not expire (8.6).
            _tailExpiresAt = null;

            if (_keyAsserted)
            {
                return now;
            }

            if (_pendingKeyDownAt is long alreadyPending)
            {
                // Idempotent: an already-scheduled key-down keeps its original deadline so a repeat
                // request cannot push the key later.
                ApplyDueKeyDownUnlocked(now);
                return alreadyPending;
            }

            if (_ptt is null)
            {
                // No PTT line: nothing to lead, so the key edge passes straight through (8.2).
                ApplyKeyDownUnlocked();
                return now;
            }

            if (!_pttAsserted)
            {
                IPttOutput ptt = _ptt;
                Protected(() =>
                {
                    ptt.PttDown();
                    _pttAsserted = true;
                });

                _pendingKeyDownAt = now + _leadTicks;
                ApplyDueKeyDownUnlocked(now); // fires immediately when LeadTime is zero
                return _pendingKeyDownAt ?? now;
            }

            // PTT already up: the lead time has been served, key immediately.
            ApplyKeyDownUnlocked();
            return now;
        }
    }

    /// <summary>
    /// Requests key-up. Keys up immediately and starts or restarts the PTT tail timer (8.5).
    /// </summary>
    public void KeyUp()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();

            // Drop a key-down that never reached the wire; PTT is up, so the tail still applies.
            _pendingKeyDownAt = null;

            if (_keyAsserted)
            {
                Protected(() =>
                {
                    _key.KeyUp();
                    _keyAsserted = false;
                });
            }

            if (_ptt is not null && _pttAsserted)
            {
                // Start on the first key-up, restart on every later one: the tail is measured from
                // the most recent key-up, so rapid cycles keep extending it (8.5, 8.6).
                _tailExpiresAt = now + _tailTicks;
            }
        }
    }

    /// <summary>
    /// Advances the sequencer: applies a due key-down and de-asserts PTT once the tail has expired
    /// with no pending key-down (8.6). Cheap and safe to call at any rate; call it at least as often
    /// as <see cref="NextDeadline"/> requires.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();

            ApplyDueKeyDownUnlocked(now);

            if (_tailExpiresAt is not long tailExpiresAt || now < tailExpiresAt)
            {
                return;
            }

            // Only when nothing is keyed and nothing is about to be (8.6).
            if (_keyAsserted || _pendingKeyDownAt is not null)
            {
                return;
            }

            _tailExpiresAt = null;

            if (_ptt is IPttOutput ptt && _pttAsserted)
            {
                Protected(() =>
                {
                    ptt.PttUp();
                    _pttAsserted = false;
                });
            }
        }
    }

    /// <summary>
    /// Fail-safe: cancels pending work and drives key and PTT inactive, key first (8.7). Best
    /// effort — it never throws, and PTT still drops if the key line fails.
    /// </summary>
    public void ForceAllUp()
    {
        lock (_gate)
        {
            ForceAllUpUnlocked();
        }
    }

    /// <summary>
    /// Drops both lines via <see cref="ForceAllUp"/>. Does not dispose the underlying outputs: this
    /// sequencer does not own them, and the keying output performs its own fail-safe disposal.
    /// </summary>
    public void Dispose() => ForceAllUp();

    private void ApplyDueKeyDownUnlocked(long now)
    {
        if (_pendingKeyDownAt is long due && now >= due)
        {
            _pendingKeyDownAt = null;
            ApplyKeyDownUnlocked();
        }
    }

    private void ApplyKeyDownUnlocked()
    {
        Protected(() =>
        {
            _key.KeyDown();
            _keyAsserted = true;
        });
    }

    /// <summary>
    /// Runs an output call so that a failure cannot leave a line asserted: everything is forced
    /// inactive before the exception propagates (8.7).
    /// </summary>
    private void Protected(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            ForceAllUpUnlocked();
            throw;
        }
    }

    private void ForceAllUpUnlocked()
    {
        _pendingKeyDownAt = null;
        _tailExpiresAt = null;

        try
        {
            _key.KeyUp();
        }
        catch
        {
            // Best effort: the keying output has its own fail-safe, including closing the port.
        }
        finally
        {
            _keyAsserted = false;
        }

        try
        {
            _ptt?.PttUp();
        }
        catch
        {
            // Best effort, as above.
        }
        finally
        {
            _pttAsserted = false;
        }
    }

    private static long ToTicks(TimeSpan duration, long frequency)
    {
        if (duration <= TimeSpan.Zero || frequency <= 0)
        {
            return 0;
        }

        return (long)(duration.TotalSeconds * frequency);
    }
}
