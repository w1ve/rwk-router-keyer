using System.Collections.Concurrent;
using RWK.Shared.Protocol;
using RWK.Shared.Timing;

namespace RWK.Shared.Keying;

/// <summary>
/// Turns paddle contacts, queued host text, and immediate key commands into a single
/// stream of timed edges, one unit of work per <see cref="Pump"/> call.
/// </summary>
/// <remarks>
/// This is the half of the SoftKeyer core that has no threading in it. The RWK v1
/// <c>SoftKeyer</c> owned its own thread and slept inside its element loop, which made its
/// behavior a function of wall-clock scheduling; here the caller owns the thread and the
/// clock, so the same paddle sequence produces the same edges every time.
/// <para>
/// Arbitration order within one <see cref="Pump"/> call, highest first:
/// </para>
/// <list type="number">
///   <item><description>A pending <see cref="AbortAndClear"/>.</description></item>
///   <item><description>An immediate key command from the host (2.4).</description></item>
///   <item><description>Straight-key contact passthrough, in <see cref="KeyerMode.Straight"/> (3.6).</description></item>
///   <item><description>A paddle-generated element (3.1-3.5).</description></item>
///   <item><description>One character of queued host text (2.3).</description></item>
/// </list>
/// <para>
/// Paddle outranking host text is requirement 3.7. It is enforced twice: the paddle is
/// examined before the host queue on entry, and a host character already in flight is
/// abandoned the moment a contact closes — with a key-up edge emitted at the abort point,
/// never left keyed.
/// </para>
/// <para>
/// Thread safety: one consumer thread may call <see cref="Pump"/> while other threads call
/// <see cref="SetPaddleState"/>, <see cref="EnqueueText"/>, <see cref="SetKeyImmediate"/>,
/// and <see cref="AbortAndClear"/>. Two threads must not call <see cref="Pump"/>
/// concurrently.
/// </para>
/// _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 3.10_
/// </remarks>
public sealed class KeyerElementPump
{
    private static readonly Func<bool> NeverStop = static () => false;

    private readonly ISystemClock _clock;
    private readonly KeyerWait _wait;
    private readonly KeyerElementEngine _engine = new();
    private readonly ConcurrentQueue<char> _hostText = new();

    private volatile int _speedWpm = 25;
    private volatile int _weight = KeyerElementTiming.DefaultWeight;
    private volatile bool _paddleReverse;
    private volatile bool _abortRequested;
    private volatile bool _immediateRequested;

    private long _paddleTimestamp;

    // Consumer-thread state: only Pump and its helpers touch these.
    private bool _keyDown;
    private bool _immediateHeld;
    private bool _straightHeld;

    /// <summary>
    /// Raised for every key-state transition, with the timestamp taken at the transition (3.8).
    /// </summary>
    /// <remarks>
    /// Raised on the thread that called <see cref="Pump"/> — the keyer thread in
    /// production — so handlers must be short. The sidetone engine and the edge frame
    /// builder both hang off this event.
    /// </remarks>
    public event EventHandler<EdgeEvent>? EdgeGenerated;

    /// <summary>
    /// Raised when a character of host text finishes sending, for WK2 echo (2.5).
    /// </summary>
    /// <remarks>
    /// Not raised for a character abandoned by paddle break-in: it did not complete.
    /// </remarks>
    public event EventHandler<char>? CharacterCompleted;

    /// <summary>
    /// Creates a pump driven by the given clock, using the production wait strategy.
    /// </summary>
    /// <param name="clock">Timing source; <see cref="StopwatchClock"/> in production.</param>
    public KeyerElementPump(ISystemClock clock)
        : this(clock, null)
    {
    }

    /// <summary>
    /// Creates a pump driven by the given clock and wait strategy.
    /// </summary>
    /// <param name="clock">Timing source.</param>
    /// <param name="wait">
    /// Wait strategy, or <see langword="null"/> for
    /// <see cref="HybridWaiter.WaitUntil(long, ISystemClock, Func{bool}?)"/>. Tests supply
    /// a waiter that advances a fake clock so element timing is deterministic.
    /// </param>
    public KeyerElementPump(ISystemClock clock, KeyerWait? wait)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _wait = wait ?? ((target, abort) => HybridWaiter.WaitUntil(target, _clock, abort));
    }

    /// <summary>
    /// Gets or sets the keying speed in words per minute; clamped to 5-60 (3.10).
    /// </summary>
    public int SpeedWpm
    {
        get => _speedWpm;
        set => _speedWpm = Math.Clamp(value, KeyerElementTiming.MinWpm, KeyerElementTiming.MaxWpm);
    }

    /// <summary>
    /// Gets or sets the weight as a percentage; clamped to 25-75, default 50 (3.9).
    /// </summary>
    public int Weight
    {
        get => _weight;
        set => _weight = Math.Clamp(value, KeyerElementTiming.MinWeight, KeyerElementTiming.MaxWeight);
    }

    /// <summary>
    /// Gets or sets whether the dit and dah contacts are swapped for a left-handed paddle.
    /// </summary>
    /// <remarks>
    /// Applied when <see cref="SetPaddleState"/> is called, so the engine and everything
    /// downstream only ever see logical dit/dah.
    /// </remarks>
    public bool PaddleReverse
    {
        get => _paddleReverse;
        set => _paddleReverse = value;
    }

    /// <summary>
    /// Gets or sets the keyer mode (3.1).
    /// </summary>
    /// <remarks>
    /// Changing the mode clears remembered taps: a tap recorded under the previous mode
    /// was queued against rules that no longer apply.
    /// </remarks>
    public KeyerMode Mode
    {
        get => _engine.Mode;
        set
        {
            if (_engine.Mode == value)
                return;

            _engine.Mode = value;
            _engine.ClearMemory();
        }
    }

    /// <summary>Gets the last key state emitted on <see cref="EdgeGenerated"/>.</summary>
    public bool IsKeyDown => _keyDown;

    /// <summary>Gets whether any host text is waiting to be sent.</summary>
    public bool HasPendingText => !_hostText.IsEmpty;

    /// <summary>
    /// Gets the element decision state machine, for inspection.
    /// </summary>
    public KeyerElementEngine Engine => _engine;

    /// <summary>
    /// Applies a debounced paddle state (1.5), honouring <see cref="PaddleReverse"/>.
    /// </summary>
    /// <param name="dit">Dit contact closed.</param>
    /// <param name="dah">Dah contact closed.</param>
    /// <param name="straight">Straight-key contact closed.</param>
    /// <param name="qpcTimestamp">
    /// QPC timestamp captured by the poller at detection (1.3). Used verbatim for
    /// straight-key passthrough edges, where it is the true contact moment; generated
    /// elements are stamped when the pump emits them instead.
    /// </param>
    public void SetPaddleState(bool dit, bool dah, bool straight, long qpcTimestamp = 0)
    {
        if (_paddleReverse)
            (dit, dah) = (dah, dit);

        Interlocked.Exchange(ref _paddleTimestamp, qpcTimestamp);
        _engine.SetPaddleState(dit, dah, straight);
    }

    /// <summary>
    /// Queues text for Morse encoding on the host path (2.3).
    /// </summary>
    /// <param name="text">Text to send. Characters with no Morse pattern are dropped.</param>
    public void EnqueueText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (char c in text)
            _hostText.Enqueue(c);
    }

    /// <summary>
    /// Requests an immediate key-down or key-up from the host path (2.4).
    /// </summary>
    /// <remarks>
    /// Applied on the next <see cref="Pump"/> call and held until released: while the
    /// immediate key is down nothing else may key, because the host has taken direct
    /// control of the line.
    /// </remarks>
    /// <param name="down">True to key down, false to release.</param>
    public void SetKeyImmediate(bool down) => _immediateRequested = down;

    /// <summary>
    /// Requests that in-flight keying stop, the text queue be discarded, and the key be
    /// released.
    /// </summary>
    /// <remarks>
    /// Serviced by the next <see cref="Pump"/> call, and also honoured mid-element: an
    /// abort raised while the key is down produces a key-up edge at the abort point.
    /// </remarks>
    public void AbortAndClear() => _abortRequested = true;

    /// <summary>
    /// Emits a key-up edge if the key is currently down. Safe to call from the consumer
    /// thread at shutdown.
    /// </summary>
    public void ForceKeyUp() => EnsureKeyUp(EdgeSource.Host);

    /// <summary>
    /// Clears queued text, paddle state, and pending requests, releasing the key.
    /// </summary>
    public void Reset()
    {
        _abortRequested = false;
        _immediateRequested = false;
        _immediateHeld = false;
        _straightHeld = false;
        while (_hostText.TryDequeue(out _)) { }
        _engine.Reset();
        EnsureKeyUp(EdgeSource.Host);
    }

    /// <summary>
    /// Gets the element and gap durations for the current speed and weight (3.9, 3.10).
    /// </summary>
    public KeyerElementTiming CurrentTiming() =>
        KeyerElementTiming.FromSpeed(_speedWpm, _weight, _clock.Frequency);

    /// <summary>
    /// Performs one unit of keying work, blocking for its duration.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="PumpAction.Idle"/> immediately when there is nothing to do; the
    /// caller should wait about a millisecond and call again rather than spin.
    /// </remarks>
    /// <param name="shouldStop">
    /// Polled during waits; true abandons the current element with the key released.
    /// Production passes the keyer thread's cancellation check.
    /// </param>
    /// <returns>What was done.</returns>
    public PumpAction Pump(Func<bool>? shouldStop = null)
    {
        Func<bool> stop = shouldStop ?? NeverStop;

        if (_abortRequested)
        {
            ServiceAbort();
            return PumpAction.Aborted;
        }

        // Immediate key commands outrank everything: the host is driving the line directly.
        bool immediate = _immediateRequested;
        if (immediate != _immediateHeld)
        {
            _immediateHeld = immediate;
            EmitEdge(immediate, EdgeSource.Immediate, null);
            return PumpAction.Immediate;
        }

        if (_immediateHeld)
            return PumpAction.Idle;

        if (Mode == KeyerMode.Straight)
        {
            // Straight key: the contact is the key. No elements are generated (3.6).
            bool contact = _engine.StraightPressed;
            if (contact != _straightHeld)
            {
                _straightHeld = contact;
                long stamp = Interlocked.Read(ref _paddleTimestamp);
                EmitEdge(contact, EdgeSource.Paddle, stamp == 0 ? null : stamp);
                return PumpAction.StraightKey;
            }

            if (contact)
                return PumpAction.Idle;
        }
        else if (_straightHeld)
        {
            // Mode changed out from under a closed straight-key contact. Release the key
            // rather than leave it asserted with nothing now watching the contact.
            _straightHeld = false;
            EnsureKeyUp(EdgeSource.Paddle);
            return PumpAction.StraightKey;
        }

        KeyerElement element = _engine.RequestNextElement();
        if (element != KeyerElement.None)
        {
            SendElement(element, EdgeSource.Paddle, stop);
            return PumpAction.PaddleElement;
        }

        if (_hostText.TryDequeue(out char c))
        {
            SendHostCharacter(c, stop);
            return PumpAction.HostCharacter;
        }

        return PumpAction.Idle;
    }

    /// <summary>
    /// Sends one element: key down, hold for the element, key up, hold for the gap.
    /// </summary>
    private void SendElement(KeyerElement element, EdgeSource source, Func<bool> stop)
    {
        KeyerElementTiming timing = CurrentTiming();
        Func<bool> abort = () => _abortRequested || stop();

        // Checked before the key-down rather than only during the hold, so a stop raised
        // between the caller's own check and this call cannot produce a zero-length blip.
        if (abort())
            return;

        long start = _clock.GetTimestamp();
        EmitEdge(true, source, start);

        long keyUpAt = start + timing.TicksFor(element);
        bool abandoned = WaitUntil(keyUpAt, abort);

        // Unconditional: whether the element ran to length or was cut short, the key comes up.
        EnsureKeyUp(source);

        if (abandoned)
            return;

        WaitUntil(keyUpAt + timing.GapTicks, abort);
    }

    /// <summary>
    /// Sends one character of host text, abandoning it if the paddle breaks in (3.7).
    /// </summary>
    private void SendHostCharacter(char c, Func<bool> stop)
    {
        KeyerElementTiming timing = CurrentTiming();
        Func<bool> abort = () => _abortRequested || stop() || PaddleWantsToBreakIn();

        if (abort())
            return;

        if (c is ' ' or '\t' or '\r' or '\n')
        {
            // Word space: key-up time only, no edges (3.10 spacing, no element).
            if (WaitUntil(_clock.GetTimestamp() + timing.WordGapTicks, abort))
                return;

            CharacterCompleted?.Invoke(this, ' ');
            return;
        }

        // Reuse the host-path schedule builder so a character sent from the host has the
        // same weighted timing as the same character sent on the paddles (3.9).
        long[] schedule = EdgeScheduleBuilder.Build(c.ToString(), _speedWpm, _clock.Frequency, _weight);
        if (schedule.Length == 0)
            return; // No Morse pattern for this character; nothing to key.

        long baseTimestamp = _clock.GetTimestamp();

        for (int i = 0; i < schedule.Length; i++)
        {
            if (WaitUntil(baseTimestamp + schedule[i], abort))
            {
                // Break-in mid-character: guarantee the key-up edge at the abort point.
                EnsureKeyUp(EdgeSource.Host);
                return;
            }

            EmitEdge(i % 2 == 0, EdgeSource.Host, null);
        }

        // Trailing inter-character gap, so the next character is spaced correctly. A
        // break-in here still leaves the key up, which the loop above already ensured.
        if (WaitUntil(_clock.GetTimestamp() + timing.InterCharacterGapTicks, abort))
            return;

        CharacterCompleted?.Invoke(this, char.ToUpperInvariant(c));
    }

    /// <summary>
    /// Whether a paddle contact or a remembered tap should displace host text (3.7).
    /// </summary>
    private bool PaddleWantsToBreakIn()
    {
        if (Mode == KeyerMode.Straight)
            return _engine.StraightPressed;

        // Memory counts as well as contact state: a tap short enough to be released again
        // before the next Pump call still deserves to interrupt the host.
        return _engine.DitPressed || _engine.DahPressed || _engine.DitMemory || _engine.DahMemory;
    }

    /// <summary>
    /// Waits until <paramref name="target"/>, reporting whether it gave up early.
    /// </summary>
    /// <returns>True if the wait was abandoned before reaching the target.</returns>
    private bool WaitUntil(long target, Func<bool> abort)
    {
        _wait(target, abort);

        // Reading the clock rather than re-testing the predicate keeps the answer about
        // what happened, not about what has become true since.
        return _clock.GetTimestamp() < target;
    }

    /// <summary>
    /// Services a pending abort: discard queued work and release the key.
    /// </summary>
    private void ServiceAbort()
    {
        _abortRequested = false;
        _immediateRequested = false;
        _immediateHeld = false;
        _straightHeld = false;

        while (_hostText.TryDequeue(out _)) { }
        _engine.ClearMemory();

        EnsureKeyUp(EdgeSource.Host);
    }

    private void EnsureKeyUp(EdgeSource source) => EmitEdge(false, source, null);

    /// <summary>
    /// Emits an edge if it is a real transition, stamping it at <paramref name="timestamp"/>
    /// or at the current clock reading (3.8).
    /// </summary>
    private void EmitEdge(bool keyDown, EdgeSource source, long? timestamp)
    {
        if (keyDown == _keyDown)
            return;

        _keyDown = keyDown;
        EdgeGenerated?.Invoke(this, new EdgeEvent(timestamp ?? _clock.GetTimestamp(), keyDown, source));
    }
}
