using RWK.Shared;
using RWK.Shared.Config;

namespace RWK.Station.Replay;

/// <summary>
/// Chooses the playout delay D the Edge Replayer adds to an arriving edge before replaying it:
/// a per-path-type band, optionally adapted from measured RTT and jitter (7.1, 7.6, 7.7).
/// </summary>
/// <remarks>
/// Deliberately a separate component from <see cref="EdgeReplayer"/> and free of any thread,
/// socket, or clock, so the delay arithmetic can be tested directly.
/// <para>
/// <b>Bands (7.1).</b> Direct: 30-150ms, default 60ms. DERP-class: 100-500ms, default 200ms. The
/// configured <see cref="JitterBufferConfig.DirectDelay"/> and
/// <see cref="JitterBufferConfig.DerpDelay"/> are clamped into their band, so a configuration
/// outside the range cannot produce an out-of-range delay.
/// </para>
/// <para>
/// <b>The profile is an input, not an assumption.</b> <see cref="Profile"/> comes from the
/// sidecar's <c>edge.jitterProfile</c> declaration (Component 13, ADR 0001).
/// <see cref="EdgeJitterProfile.DerpClassOnly"/> forces the DERP-class band at all times, even
/// while <see cref="Path"/> is <see cref="PathType.Direct"/>. A path of
/// <see cref="PathType.None"/> — nothing established yet — also uses the DERP-class band, because
/// the shorter band is only justified by an observed direct path.
/// </para>
/// <para>
/// <b>Adaptation (7.6, 7.7).</b> <c>rtt_ewma</c> uses alpha 0.2, <c>jitter_ewma</c> alpha 0.1 over
/// the absolute deviation of each RTT sample from the RTT EWMA, and
/// <c>delay = base + (2 x jitter_ewma)</c> clamped to the band. Before the first sample the base
/// delay is used unchanged. Task 11.3 refines where the samples come from; this type only owns the
/// formula, so the sampling seam is the single <see cref="ObserveRtt"/> call.
/// </para>
/// <para>
/// <b>Late-edge storm auto-bump (7.6).</b> When more than 3 late edges arrive within a 10-second
/// sliding window, the buffer auto-bumps the delay by one step (10ms) while in adaptive mode.
/// This compensates for transient path degradation without waiting for RTT samples to drift
/// the EWMA upward.
/// </para>
/// <para>
/// <b>Path-type transitions.</b> When the path type changes (Direct→DERP or DERP→Direct), the new
/// delay is deferred until the next idle anchor reset. This ensures that a band switch never
/// stretches or compresses edges mid-word (mid-burst). Call <see cref="ApplyPendingPathChange"/>
/// from the replayer at each anchor establishment to commit the deferred path.
/// </para>
/// <para>
/// <b>Threading.</b> Mutations take a short lock and cache the resulting delay; the replay thread
/// reads the cached value with no lock, so nothing on the keying path can block on the UI thread
/// changing configuration.
/// </para>
/// <para>
/// _Requirements: 7.1, 7.6, 7.7_
/// </para>
/// </remarks>
public sealed class JitterBuffer
{
    /// <summary>Shortest delay permitted on a direct path (7.1).</summary>
    public static readonly TimeSpan DirectMinDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>Longest delay permitted on a direct path (7.1).</summary>
    public static readonly TimeSpan DirectMaxDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Shortest delay permitted on a DERP-class path (7.1).</summary>
    public static readonly TimeSpan DerpMinDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest delay permitted on a DERP-class path (7.1).</summary>
    public static readonly TimeSpan DerpMaxDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>EWMA smoothing factor for RTT samples (7.6).</summary>
    public const double RttAlpha = 0.2;

    /// <summary>EWMA smoothing factor for jitter samples (7.6).</summary>
    public const double JitterAlpha = 0.1;

    /// <summary>Multiplier applied to the jitter EWMA by the adaptive formula (7.7).</summary>
    public const double JitterMultiplier = 2.0;

    /// <summary>Number of late edges within the storm window that triggers an auto-bump.</summary>
    public const int LateEdgeStormThreshold = 3;

    /// <summary>Sliding window duration for late-edge storm detection.</summary>
    public static readonly TimeSpan LateEdgeStormWindow = TimeSpan.FromSeconds(10);

    /// <summary>The amount (in milliseconds) that a single auto-bump step adds to the delay.</summary>
    public const double AutoBumpStepMs = 10.0;

    private readonly object _gate = new();

    private JitterBufferConfig _config;
    private EdgeJitterProfile _profile;
    private PathType _path;
    private PathType? _pendingPath;
    private double _rttEwmaMs;
    private double _jitterEwmaMs;
    private bool _hasSamples;
    private double _autoBumpMs;

    // Late-edge storm tracking: circular buffer of timestamps (in TimeSpan ticks from DateTime)
    // for the last LateEdgeStormThreshold + some extra entries to track the sliding window.
    private readonly Queue<long> _lateEdgeTimestamps = new();

    // Cached delay in TimeSpan ticks so the replay thread reads it without taking _gate.
    private long _delayTicks;

    /// <summary>
    /// Creates a jitter buffer.
    /// </summary>
    /// <param name="config">Base delays and adaptive mode. Defaults to <see cref="JitterBufferConfig.Default"/>.</param>
    /// <param name="profile">
    /// The sidecar's declared profile. Defaults to <see cref="EdgeJitterProfile.DerpClassOnly"/>:
    /// until the declaration has been read, the conservative band applies.
    /// </param>
    /// <param name="path">The current path type. Defaults to <see cref="PathType.None"/>.</param>
    public JitterBuffer(
        JitterBufferConfig? config = null,
        EdgeJitterProfile profile = EdgeJitterProfile.DerpClassOnly,
        PathType path = PathType.None)
    {
        _config = config ?? JitterBufferConfig.Default;
        _profile = profile;
        _path = path;
        RecomputeUnlocked();
    }

    /// <summary>Base delays and adaptive mode (7.1). Setting this recomputes the delay.</summary>
    public JitterBufferConfig Config
    {
        get { lock (_gate) { return _config; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _config = value;
                RecomputeUnlocked();
            }
        }
    }

    /// <summary>
    /// The profile declared by the sidecar. Setting this recomputes the delay, so a mid-session
    /// change to <see cref="EdgeJitterProfile.DerpClassOnly"/> widens the buffer immediately.
    /// </summary>
    public EdgeJitterProfile Profile
    {
        get { lock (_gate) { return _profile; } }
        set
        {
            lock (_gate)
            {
                _profile = value;
                RecomputeUnlocked();
            }
        }
    }

    /// <summary>The current network path type (5.3). Setting this recomputes the delay.</summary>
    /// <remarks>
    /// When the path type changes (Direct→DERP or vice versa), the new band is deferred until
    /// <see cref="ApplyPendingPathChange"/> is called (at the next idle anchor reset). This
    /// ensures the delay band never switches mid-word, which would stretch or compress edges
    /// within a burst.
    /// </remarks>
    public PathType Path
    {
        get { lock (_gate) { return _path; } }
        set
        {
            lock (_gate)
            {
                if (value == _path)
                {
                    _pendingPath = null;
                    return;
                }

                _pendingPath = value;
                // Don't recompute immediately; the new band applies at the next anchor reset.
            }
        }
    }

    /// <summary>
    /// Whether a path change is pending, waiting for the next idle anchor reset to apply.
    /// </summary>
    public bool HasPendingPathChange { get { lock (_gate) { return _pendingPath.HasValue; } } }

    /// <summary>
    /// The path type that will be applied at the next anchor reset, or null if no change is pending.
    /// </summary>
    public PathType? PendingPath { get { lock (_gate) { return _pendingPath; } } }

    /// <summary>
    /// Commits a deferred path-type change. Call this at anchor establishment (idle reset).
    /// Returns true if a path change was applied.
    /// </summary>
    /// <remarks>
    /// The design guarantees the band switch happens at the beginning of a new burst (after ≥2s
    /// idle), never mid-word.
    /// </remarks>
    public bool ApplyPendingPathChange()
    {
        lock (_gate)
        {
            if (!_pendingPath.HasValue)
            {
                return false;
            }

            _path = _pendingPath.Value;
            _pendingPath = null;
            RecomputeUnlocked();
            return true;
        }
    }

    /// <summary>
    /// Forces the path immediately without deferral. Used during session establishment or
    /// when there is no active burst to protect.
    /// </summary>
    public void SetPathImmediate(PathType path)
    {
        lock (_gate)
        {
            _path = path;
            _pendingPath = null;
            RecomputeUnlocked();
        }
    }

    /// <summary>Whether at least one RTT sample has been observed.</summary>
    public bool HasSamples { get { lock (_gate) { return _hasSamples; } } }

    /// <summary>Current RTT EWMA in milliseconds; 0 before the first sample (7.6).</summary>
    public double RttEwmaMs { get { lock (_gate) { return _rttEwmaMs; } } }

    /// <summary>Current jitter EWMA in milliseconds; 0 before the second sample (7.6).</summary>
    public double JitterEwmaMs { get { lock (_gate) { return _jitterEwmaMs; } } }

    /// <summary>
    /// The delay D currently applied to a newly anchored burst. Read lock-free, so the replay
    /// thread can call it while scheduling.
    /// </summary>
    public TimeSpan CurrentDelay => TimeSpan.FromTicks(Volatile.Read(ref _delayTicks));

    /// <summary>
    /// <see cref="CurrentDelay"/> expressed in ticks of a clock running at
    /// <paramref name="frequency"/> ticks per second, which is the form the QPC scheduler needs.
    /// </summary>
    public long CurrentDelayIn(long frequency)
    {
        if (frequency <= 0)
        {
            return 0;
        }

        long ticks = Volatile.Read(ref _delayTicks);
        return ticks / TimeSpan.TicksPerSecond * frequency
             + ticks % TimeSpan.TicksPerSecond * frequency / TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// Feeds one round-trip-time measurement into the EWMAs and recomputes the delay (7.6).
    /// Negative samples are ignored.
    /// </summary>
    /// <remarks>
    /// The jitter sample is the absolute deviation of this RTT from the RTT EWMA as it stood
    /// before this sample, which is the usual construction and needs no sample history. Task 11.3
    /// owns where the samples come from.
    /// </remarks>
    public void ObserveRtt(TimeSpan rtt)
    {
        if (rtt < TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            double sampleMs = rtt.TotalMilliseconds;

            if (!_hasSamples)
            {
                // Seed rather than smooth from zero: smoothing from zero would understate the
                // delay for the first several samples, which is the unsafe direction.
                _rttEwmaMs = sampleMs;
                _jitterEwmaMs = 0;
                _hasSamples = true;
            }
            else
            {
                double deviationMs = Math.Abs(sampleMs - _rttEwmaMs);
                _rttEwmaMs = (RttAlpha * sampleMs) + ((1.0 - RttAlpha) * _rttEwmaMs);
                _jitterEwmaMs = (JitterAlpha * deviationMs) + ((1.0 - JitterAlpha) * _jitterEwmaMs);
            }

            RecomputeUnlocked();
        }
    }

    /// <summary>
    /// Discards the RTT and jitter history, so the delay returns to the base for the current band.
    /// Called on session establishment or reconnect: samples from a finished session say nothing
    /// about a new one.
    /// </summary>
    public void ResetSamples()
    {
        lock (_gate)
        {
            _rttEwmaMs = 0;
            _jitterEwmaMs = 0;
            _hasSamples = false;
            _autoBumpMs = 0;
            _lateEdgeTimestamps.Clear();
            RecomputeUnlocked();
        }
    }

    /// <summary>
    /// Reports a late edge event. When more than <see cref="LateEdgeStormThreshold"/> late edges
    /// occur within <see cref="LateEdgeStormWindow"/>, the delay is auto-bumped one step
    /// (when in adaptive mode). Returns true if a bump was applied.
    /// </summary>
    /// <param name="utcNowTicks">The current UTC time in <see cref="DateTime.Ticks"/>, for testability.</param>
    public bool ReportLateEdge(long utcNowTicks)
    {
        lock (_gate)
        {
            if (!_config.AdaptiveMode)
            {
                return false;
            }

            // Add this late edge timestamp.
            _lateEdgeTimestamps.Enqueue(utcNowTicks);

            // Evict entries older than the storm window.
            long windowStart = utcNowTicks - LateEdgeStormWindow.Ticks;
            while (_lateEdgeTimestamps.Count > 0 && _lateEdgeTimestamps.Peek() < windowStart)
            {
                _lateEdgeTimestamps.Dequeue();
            }

            // Check if we've exceeded the threshold.
            if (_lateEdgeTimestamps.Count > LateEdgeStormThreshold)
            {
                _autoBumpMs += AutoBumpStepMs;

                // Clamp auto-bump so total delay can't exceed the max of the current band.
                TimeSpan maxDelay = MaxDelayFor(_path, _profile);
                TimeSpan baseDelay = BaseDelayFor(_config, _path, _profile);
                double maxBump = maxDelay.TotalMilliseconds - baseDelay.TotalMilliseconds;
                if (_autoBumpMs > maxBump)
                {
                    _autoBumpMs = maxBump;
                }

                // Clear the window after a bump so we don't immediately trigger again.
                _lateEdgeTimestamps.Clear();

                RecomputeUnlocked();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Reports a late edge event using the current system time.
    /// Convenience overload that calls <see cref="ReportLateEdge(long)"/> with
    /// <see cref="DateTime.UtcNow"/> ticks.
    /// </summary>
    public bool ReportLateEdge() => ReportLateEdge(DateTime.UtcNow.Ticks);

    /// <summary>Current auto-bump offset in milliseconds from late-edge storm detection.</summary>
    public double AutoBumpMs { get { lock (_gate) { return _autoBumpMs; } } }

    /// <summary>
    /// Whether the DERP-class band applies for <paramref name="path"/> under
    /// <paramref name="profile"/>: on a relayed path, on an unknown path, or whenever the profile
    /// is <see cref="EdgeJitterProfile.DerpClassOnly"/>.
    /// </summary>
    public static bool UsesDerpBand(PathType path, EdgeJitterProfile profile)
        => profile == EdgeJitterProfile.DerpClassOnly || path != PathType.Direct;

    /// <summary>Shortest delay permitted for <paramref name="path"/> under <paramref name="profile"/>.</summary>
    public static TimeSpan MinDelayFor(PathType path, EdgeJitterProfile profile)
        => UsesDerpBand(path, profile) ? DerpMinDelay : DirectMinDelay;

    /// <summary>Longest delay permitted for <paramref name="path"/> under <paramref name="profile"/>.</summary>
    public static TimeSpan MaxDelayFor(PathType path, EdgeJitterProfile profile)
        => UsesDerpBand(path, profile) ? DerpMaxDelay : DirectMaxDelay;

    /// <summary>
    /// The configured base delay for <paramref name="path"/> under <paramref name="profile"/>,
    /// clamped into its band.
    /// </summary>
    public static TimeSpan BaseDelayFor(JitterBufferConfig config, PathType path, EdgeJitterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(config);

        bool derp = UsesDerpBand(path, profile);
        TimeSpan configured = derp ? config.DerpDelay : config.DirectDelay;
        return Clamp(configured, MinDelayFor(path, profile), MaxDelayFor(path, profile));
    }

    /// <summary>
    /// The delay the adaptive formula yields for the given inputs (7.7): the clamped base delay
    /// when adaptive mode is off or no samples exist, otherwise
    /// <c>base + (2 x jitterEwmaMs)</c> clamped to the band.
    /// </summary>
    public static TimeSpan DelayFor(
        JitterBufferConfig config,
        PathType path,
        EdgeJitterProfile profile,
        bool hasSamples,
        double jitterEwmaMs,
        double autoBumpMs = 0)
    {
        TimeSpan baseDelay = BaseDelayFor(config, path, profile);

        if (!config.AdaptiveMode || !hasSamples || double.IsNaN(jitterEwmaMs) || jitterEwmaMs < 0)
        {
            // Even in non-adaptive mode, an auto-bump applies if we're in adaptive mode
            // (this branch fires when adaptive is off OR there are no samples yet).
            if (config.AdaptiveMode && autoBumpMs > 0)
            {
                double bumpedMs = baseDelay.TotalMilliseconds + autoBumpMs;
                TimeSpan max = MaxDelayFor(path, profile);
                TimeSpan bumped = bumpedMs >= max.TotalMilliseconds
                    ? max
                    : TimeSpan.FromMilliseconds(bumpedMs);
                return Clamp(bumped, MinDelayFor(path, profile), max);
            }

            return baseDelay;
        }

        double adaptiveMs = baseDelay.TotalMilliseconds + (JitterMultiplier * jitterEwmaMs) + autoBumpMs;
        TimeSpan maxDelay = MaxDelayFor(path, profile);

        // Guard the conversion: a runaway jitter EWMA must clamp, not overflow.
        TimeSpan adaptive = adaptiveMs >= maxDelay.TotalMilliseconds
            ? maxDelay
            : TimeSpan.FromMilliseconds(adaptiveMs);

        return Clamp(adaptive, MinDelayFor(path, profile), maxDelay);
    }

    private void RecomputeUnlocked()
        => Volatile.Write(
            ref _delayTicks,
            DelayFor(_config, _path, _profile, _hasSamples, _jitterEwmaMs, _autoBumpMs).Ticks);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
