namespace WinKeyerEmulator.Core;

/// <summary>
/// Severity levels for log entries.
/// </summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Abstraction for operational logging within the emulator.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Records a log entry with the specified message and severity.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="severity">The severity level of the entry.</param>
    /// <param name="source">Optional source identifier (e.g., component name).</param>
    void Log(string message, LogSeverity severity, string? source = null);
}
