using System.Collections.Concurrent;

namespace RWK.Client;

/// <summary>
/// Log severity levels for the visual log.
/// </summary>
public enum LogLevel
{
    /// <summary>No logging.</summary>
    None = 0,

    /// <summary>
    /// High-level operational messages: connection state changes, speed changes,
    /// arm/disarm, session events. Suitable for operators.
    /// </summary>
    Descriptive = 1,

    /// <summary>
    /// Detailed protocol and internal state messages. Includes everything from
    /// Descriptive plus byte-level protocol traces, timing details, etc.
    /// </summary>
    Debug = 2
}

/// <summary>
/// Thread-safe, non-blocking log service for the Client application.
/// </summary>
/// <remarks>
/// Messages are enqueued to a <see cref="ConcurrentQueue{T}"/> and drained by a
/// dedicated background thread, so callers (including the keyer timing thread) never
/// block on I/O or UI marshaling. The drain thread batches messages and invokes a
/// callback on the UI thread at most every 100ms.
/// <para>
/// The log is capped at <see cref="MaxLines"/> entries. When exceeded, the oldest
/// half is discarded in bulk rather than per-message, to avoid O(N) shifts on every write.
/// </para>
/// </remarks>
public sealed class LogService : IDisposable
{
    /// <summary>Maximum lines retained in the visual log before trimming.</summary>
    public const int MaxLines = 5000;

    /// <summary>How often the drain thread flushes to the UI (milliseconds).</summary>
    private const int DrainIntervalMs = 100;

    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly Thread _drainThread;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _disposed;

    private LogLevel _level = LogLevel.Descriptive;
    private Action<string>? _uiAppend;

    /// <summary>
    /// Gets or sets the current log level filter. Messages below this level are discarded
    /// at enqueue time (zero allocation for filtered messages).
    /// </summary>
    public LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>
    /// Creates and starts the log service. Call <see cref="SetUiCallback"/> once the
    /// form is ready to receive appended text.
    /// </summary>
    public LogService()
    {
        _drainThread = new Thread(DrainLoop)
        {
            Name = "RWK-LogService",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _drainThread.Start();
    }

    /// <summary>
    /// Sets the callback invoked on the UI thread to append formatted log text.
    /// The callback receives a pre-formatted multi-line string (one or more lines
    /// batched together).
    /// </summary>
    public void SetUiCallback(Action<string> appendCallback)
    {
        _uiAppend = appendCallback;
    }

    /// <summary>
    /// Enqueues a Descriptive-level message. No-op if the current level is below Descriptive.
    /// </summary>
    public void Info(string message)
    {
        if (_level < LogLevel.Descriptive) return;
        Enqueue(LogLevel.Descriptive, message);
    }

    /// <summary>
    /// Enqueues a Debug-level message. No-op if the current level is below Debug.
    /// </summary>
    public void Debug(string message)
    {
        if (_level < LogLevel.Debug) return;
        Enqueue(LogLevel.Debug, message);
    }

    /// <summary>
    /// Enqueues a message at the specified level. No-op if filtered out.
    /// </summary>
    public void Log(LogLevel level, string message)
    {
        if (_level < level) return;
        Enqueue(level, message);
    }

    private void Enqueue(LogLevel level, string message)
    {
        _queue.Enqueue(new LogEntry(DateTime.Now, level, message));
        _signal.Set();
    }

    private void DrainLoop()
    {
        var batch = new System.Text.StringBuilder(4096);

        while (!_disposed)
        {
            _signal.WaitOne(DrainIntervalMs);

            if (_queue.IsEmpty)
                continue;

            batch.Clear();
            int count = 0;

            while (_queue.TryDequeue(out LogEntry entry) && count < 200)
            {
                string prefix = entry.Level == LogLevel.Debug ? "DBG" : "INF";
                batch.Append('[').Append(entry.Timestamp.ToString("HH:mm:ss.fff")).Append("] [")
                     .Append(prefix).Append("] ")
                     .AppendLine(entry.Message);
                count++;
            }

            if (count > 0 && _uiAppend is not null)
            {
                string text = batch.ToString();
                try
                {
                    _uiAppend(text);
                }
                catch
                {
                    // UI may be disposed during shutdown — swallow.
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Set(); // Wake the drain thread so it exits.
        _drainThread.Join(500);
        _signal.Dispose();
    }

    private readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Message);
}
