namespace RWK.Shared.Keying;

/// <summary>
/// The unified keyer core: paddle contacts and WinKeyer host commands in, one timed edge
/// stream out (design Component 3).
/// </summary>
/// <remarks>
/// Declared in RWK.Shared rather than alongside its Client implementation for the same
/// reason as <see cref="RWK.Shared.IO.IPaddleInputPoller"/>: the WinKeyer protocol host, the
/// sidetone engine, the edge frame builder, and the UI all talk to the keyer, and none of
/// them should need a reference to the concrete implementation to do so.
/// <para>
/// Implementations own a timing thread. <see cref="EdgeGenerated"/> and
/// <see cref="CharacterCompleted"/> are raised on that thread, so handlers must be short
/// and must not block; marshal to the UI thread if you need to touch controls.
/// </para>
/// _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 3.10_
/// </remarks>
public interface ISoftWinKeyerCore : IDisposable
{
    /// <summary>
    /// Raised for every key-state transition, carrying a QPC timestamp, the new state, and
    /// the input path that caused it (3.8).
    /// </summary>
    event EventHandler<EdgeEvent>? EdgeGenerated;

    /// <summary>
    /// Raised when a character of host text has finished sending, for WK2 echo (2.5).
    /// </summary>
    event EventHandler<char>? CharacterCompleted;

    /// <summary>
    /// Starts the keyer timing thread. Idempotent.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the keyer timing thread, releasing the key. Idempotent.
    /// </summary>
    void Stop();

    /// <summary>Gets whether the timing thread is running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Applies a debounced paddle state with the timestamp captured at detection (1.5).
    /// </summary>
    /// <param name="dit">Dit contact closed.</param>
    /// <param name="dah">Dah contact closed.</param>
    /// <param name="straight">Straight-key contact closed.</param>
    /// <param name="qpcTimestamp">QPC timestamp captured by the poller (1.3).</param>
    void SetPaddleState(bool dit, bool dah, bool straight, long qpcTimestamp);

    /// <summary>
    /// Queues text for Morse encoding on the host path (2.3).
    /// </summary>
    /// <param name="text">Text to send; characters with no Morse pattern are dropped.</param>
    void EnqueueText(string text);

    /// <summary>
    /// Applies a WinKeyer immediate key-down or key-up command (2.4).
    /// </summary>
    /// <param name="down">True to key down, false to release.</param>
    void SetKeyImmediate(bool down);

    /// <summary>
    /// Abandons in-flight keying, discards queued text, and releases the key.
    /// </summary>
    void AbortAndClear();

    /// <summary>Gets or sets the speed in words per minute; clamped to 5-60 (3.10).</summary>
    int SpeedWpm { get; set; }

    /// <summary>Gets or sets the weight percentage; clamped to 25-75, default 50 (3.9).</summary>
    int Weight { get; set; }

    /// <summary>Gets or sets whether the dit and dah contacts are swapped.</summary>
    bool PaddleReverse { get; set; }

    /// <summary>Gets or sets the keyer mode (3.1).</summary>
    KeyerMode Mode { get; set; }
}
