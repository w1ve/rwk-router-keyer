using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using WinKeyerEmulator.Core.IO;

namespace WinKeyerEmulator.Core.Timing;

/// <summary>
/// Diagnostic event arguments for timing measurements.
/// </summary>
public class TimingDiagnosticEventArgs : EventArgs
{
    public int ElementIndex { get; init; }
    public double ExpectedMs { get; init; }
    public double ActualMs { get; init; }
    public bool IsDit { get; init; }
}

/// <summary>
/// Manages a dedicated high-priority keying thread that dequeues precomputed
/// edge schedules and executes them via HybridWaiter for sub-millisecond precision.
/// </summary>
public class TimingEngine : IDisposable
{
    private readonly IKeyingOutput _keyingOutput;
    private readonly ISystemClock _clock;
    private Thread? _keyingThread;
    private CancellationTokenSource? _cts;
    private readonly BlockingCollection<long[]> _scheduleQueue;
    private volatile bool _abortCurrent;
    private bool _disposed;
    private long _lastEdgeTimestamp; // When the last schedule finished keying
    private volatile int _lastWpm;   // WPM of last message (for gap calculation) - volatile for cross-thread access
    private volatile int _weight = 50; // CW weight (25-75, default 50)

    /// <summary>
    /// Optional callback invoked when the keying thread starts.
    /// Use this for platform-specific setup like timeBeginPeriod(1).
    /// </summary>
    public Action? OnThreadStart { get; set; }

    /// <summary>
    /// Optional callback invoked when the keying thread stops.
    /// Use this for platform-specific cleanup like timeEndPeriod(1).
    /// </summary>
    public Action? OnThreadStop { get; set; }

    /// <summary>
    /// Event raised for each element with timing diagnostic information.
    /// </summary>
    public event EventHandler<TimingDiagnosticEventArgs>? TimingDiagnostic;

    /// <summary>
    /// Gets or sets the CW weight (25-75, default 50).
    /// 50 = standard timing, higher = heavier (longer elements), lower = lighter (shorter elements).
    /// </summary>
    public int Weight
    {
        get => _weight;
        set => _weight = Math.Clamp(value, 25, 75);
    }

    /// <summary>
    /// Creates a new TimingEngine with the specified keying output and clock.
    /// </summary>
    /// <param name="keyingOutput">The keying output to toggle for Morse edges.</param>
    /// <param name="clock">The system clock for high-resolution timing.</param>
    public TimingEngine(IKeyingOutput keyingOutput, ISystemClock clock)
    {
        _keyingOutput = keyingOutput ?? throw new ArgumentNullException(nameof(keyingOutput));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduleQueue = new BlockingCollection<long[]>(new ConcurrentQueue<long[]>());
    }

    /// <summary>
    /// Builds an edge schedule from text and WPM, then enqueues it for execution.
    /// </summary>
    /// <param name="text">The text to encode as Morse code.</param>
    /// <param name="wpm">Speed in words per minute.</param>
    public void EnqueueMessage(string text, int wpm)
    {
        if (_scheduleQueue.IsAddingCompleted)
            return;

        long[] schedule = EdgeScheduleBuilder.Build(text, wpm, _clock.Frequency, _weight);
        if (schedule.Length > 0)
        {
            _lastWpm = wpm;
            try
            {
                _scheduleQueue.Add(schedule);
            }
            catch (InvalidOperationException)
            {
                // Queue was completed between our check and the Add call
            }
        }
    }

    /// <summary>
    /// Starts the dedicated keying thread with ThreadPriority.Highest.
    /// </summary>
    public void Start()
    {
        if (_keyingThread != null && _keyingThread.IsAlive)
            return;

        _cts = new CancellationTokenSource();
        _abortCurrent = false;

        _keyingThread = new Thread(KeyingLoop)
        {
            Name = "TimingEngine-KeyingThread",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        _keyingThread.Start();
    }

    /// <summary>
    /// Stops the keying thread with clean cancellation and waits for it to finish.
    /// </summary>
    public void Stop()
    {
        if (_cts == null || _keyingThread == null)
            return;

        _abortCurrent = true;
        _cts.Cancel();

        try { _scheduleQueue.CompleteAdding(); } catch { }

        // Don't block the UI — give the thread a short time, then abandon it
        if (!_keyingThread.Join(TimeSpan.FromMilliseconds(500)))
        {
            // Thread is stuck in a wait/spin — it's a background thread so it will die with the app
            // or on next iteration when it checks _abortCurrent
        }

        _keyingThread = null;
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Interrupts in-progress keying and clears all queued schedules.
    /// Ensures the key is released and resets timing state.
    /// </summary>
    public void AbortCurrent()
    {
        _abortCurrent = true;
        
        // Drain the schedule queue to prevent queued messages from playing
        while (_scheduleQueue.TryTake(out _)) { }
        
        // Reset timing state so next message doesn't use stale gap calculation
        Interlocked.Exchange(ref _lastEdgeTimestamp, 0);
    }

    /// <summary>
    /// The main keying loop that runs on the dedicated thread.
    /// Sets GCLatencyMode.SustainedLowLatency, dequeues schedules, and executes edges.
    /// </summary>
    private void KeyingLoop()
    {
        // Set GC to low-latency mode for this thread's duration
        var previousMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Invoke platform-specific thread start callback (e.g., timeBeginPeriod)
        OnThreadStart?.Invoke();

        try
        {
            var token = _cts!.Token;

            while (!token.IsCancellationRequested)
            {
                long[]? schedule;
                try
                {
                    schedule = _scheduleQueue.Take(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding was called and queue is empty
                    break;
                }

                _abortCurrent = false;
                ExecuteSchedule(schedule);
            }
        }
        finally
        {
            // Invoke platform-specific thread stop callback (e.g., timeEndPeriod)
            OnThreadStop?.Invoke();

            // Restore GC latency mode
            GCSettings.LatencyMode = previousMode;
        }
    }

    /// <summary>
    /// Executes a single edge schedule, toggling key down/up at the correct timestamps.
    /// </summary>
    private void ExecuteSchedule(long[] schedule)
    {
        if (schedule.Length == 0)
            return;

        long now = _clock.GetTimestamp();

        // If there was a previous message, ensure inter-character gap (3 dit units)
        // before starting this one
        long baseTimestamp = now;
        if (_lastEdgeTimestamp > 0 && _lastWpm > 0)
        {
            long dit = _clock.Frequency * 1200L / (_lastWpm * 1000L);
            long interCharGap = 3 * dit;
            long earliestStart = _lastEdgeTimestamp + interCharGap;
            if (earliestStart > now)
            {
                baseTimestamp = earliestStart;
                // Wait for the gap
                HybridWaiter.WaitUntil(baseTimestamp, _clock, () => _abortCurrent);
                if (_abortCurrent)
                {
                    _keyingOutput.KeyUp();
                    return;
                }
            }
        }

        long keyDownTimestamp = 0;
        
        for (int i = 0; i < schedule.Length; i++)
        {
            if (_abortCurrent)
            {
                _keyingOutput.KeyUp();
                return;
            }

            long targetTimestamp = baseTimestamp + schedule[i];
            HybridWaiter.WaitUntil(targetTimestamp, _clock, () => _abortCurrent);

            if (_abortCurrent)
            {
                _keyingOutput.KeyUp();
                return;
            }

            if (i % 2 == 0)
            {
                // Key down - record when we actually toggled
                _keyingOutput.KeyDown();
                keyDownTimestamp = _clock.GetTimestamp();
            }
            else
            {
                // Key up - measure actual duration
                _keyingOutput.KeyUp();
                long keyUpTimestamp = _clock.GetTimestamp();
                
                // Calculate timing info
                long actualDuration = keyUpTimestamp - keyDownTimestamp;
                double actualMs = actualDuration * 1000.0 / _clock.Frequency;
                long expectedDuration = schedule[i] - schedule[i - 1];
                double expectedMs = expectedDuration * 1000.0 / _clock.Frequency;
                
                // Determine if this was a dit (dit duration = 1200/wpm ms)
                double ditMs = 1200.0 / _lastWpm;
                bool isDit = expectedMs < ditMs * 2; // Dit is ~48ms at 25wpm, dah is ~144ms
                
                // Fire diagnostic event
                TimingDiagnostic?.Invoke(this, new TimingDiagnosticEventArgs
                {
                    ElementIndex = i / 2,
                    ExpectedMs = expectedMs,
                    ActualMs = actualMs,
                    IsDit = isDit
                });
            }
        }

        // Record when this schedule ended
        _lastEdgeTimestamp = _clock.GetTimestamp();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _scheduleQueue.Dispose();
        _disposed = true;
    }
}
