namespace RWK.Shared.Keying;

/// <summary>
/// Element and gap durations, in clock ticks, for one speed/weight setting (3.9, 3.10).
/// </summary>
/// <remarks>
/// The paddle path cannot use <see cref="Timing.EdgeScheduleBuilder"/> — that builds a
/// schedule for a known string, whereas paddle elements are decided one at a time as
/// contacts change — so this type carries the same weight arithmetic for a single
/// element. The formula is deliberately identical to
/// <see cref="Timing.EdgeScheduleBuilder.Build(string, int, long, int)"/> so that a
/// character sent from the host path and the same character sent on the paddles have
/// identical timing, which is what 3.9 ("consistently across all sources") requires.
/// <para>
/// Weight shifts duration between element and gap while holding the element+gap cycle
/// at two base dits: at 50% both are one base dit, at 75% the element is 1.5 base dits
/// and the gap 0.5.
/// </para>
/// _Requirements: 3.9, 3.10_
/// </remarks>
/// <param name="DitTicks">Key-down duration of a dit.</param>
/// <param name="DahTicks">Key-down duration of a dah (three times the dit).</param>
/// <param name="GapTicks">Key-up duration following every element.</param>
public readonly record struct KeyerElementTiming(long DitTicks, long DahTicks, long GapTicks)
{
    /// <summary>Lowest supported speed in words per minute (3.10).</summary>
    public const int MinWpm = 5;

    /// <summary>Highest supported speed in words per minute (3.10).</summary>
    public const int MaxWpm = 60;

    /// <summary>Lightest supported weight, as a percentage (3.9).</summary>
    public const int MinWeight = 25;

    /// <summary>Heaviest supported weight, as a percentage (3.9).</summary>
    public const int MaxWeight = 75;

    /// <summary>Default weight, as a percentage: even element and gap (3.9).</summary>
    public const int DefaultWeight = 50;

    /// <summary>
    /// Computes element timing for a speed and weight against a clock's tick frequency.
    /// </summary>
    /// <param name="wpm">Speed in words per minute; clamped to 5-60 (3.10).</param>
    /// <param name="weight">Weight percentage; clamped to 25-75 (3.9).</param>
    /// <param name="tickFrequency">Ticks per second of the timing source.</param>
    /// <returns>Durations in ticks of the same source.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tickFrequency"/> is not positive.</exception>
    public static KeyerElementTiming FromSpeed(int wpm, int weight, long tickFrequency)
    {
        if (tickFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickFrequency), tickFrequency, "Tick frequency must be positive.");

        wpm = Math.Clamp(wpm, MinWpm, MaxWpm);
        weight = Math.Clamp(weight, MinWeight, MaxWeight);

        // dit = 1200 / wpm milliseconds (3.10), expressed in ticks.
        long baseDit = tickFrequency * 1200L / (wpm * 1000L);

        double weightFactor = weight / 50.0;
        double gapFactor = (100 - weight) / 50.0;

        long dit = (long)(baseDit * weightFactor);
        return new KeyerElementTiming(dit, 3 * dit, (long)(baseDit * gapFactor));
    }

    /// <summary>
    /// Gets the unweighted dit duration, recovered from the weighted element and gap.
    /// </summary>
    /// <remarks>
    /// Weight moves duration between the element and the gap while holding the cycle at
    /// two base dits, so the base dit is always half of element + gap regardless of
    /// weight. Recovering it here avoids carrying a fourth field that could disagree
    /// with the other three.
    /// </remarks>
    public long BaseDitTicks => (DitTicks + GapTicks) / 2;

    /// <summary>
    /// Gets the key-up duration between two characters, over and above the gap that
    /// already follows the last element of the preceding character.
    /// </summary>
    /// <remarks>
    /// Three gap units, matching
    /// <see cref="Timing.EdgeScheduleBuilder"/>'s inter-character gap, so a string sent
    /// one character at a time by the pump is spaced identically to the same string
    /// scheduled in one pass (3.9).
    /// </remarks>
    public long InterCharacterGapTicks => 3 * GapTicks;

    /// <summary>
    /// Gets the key-up duration of a space character: seven unweighted dits.
    /// </summary>
    /// <remarks>
    /// Weight-independent, matching <see cref="Timing.EdgeScheduleBuilder"/>: weight
    /// shapes elements, not word spacing.
    /// </remarks>
    public long WordGapTicks => 7 * BaseDitTicks;

    /// <summary>
    /// Gets the key-down duration for an element.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>Duration in ticks; zero for <see cref="KeyerElement.None"/>.</returns>
    public long TicksFor(KeyerElement element) => element switch
    {
        KeyerElement.Dit => DitTicks,
        KeyerElement.Dah => DahTicks,
        _ => 0
    };
}
