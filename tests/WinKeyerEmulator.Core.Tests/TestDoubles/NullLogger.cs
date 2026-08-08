using WinKeyerEmulator.Core;

namespace WinKeyerEmulator.Core.Tests.TestDoubles;

/// <summary>
/// A no-op logger for use in tests where logging output is not relevant.
/// </summary>
public class NullLogger : ILogger
{
    public List<(string Message, LogSeverity Severity, string? Source)> Entries { get; } = new();

    public void Log(string message, LogSeverity severity, string? source = null)
    {
        Entries.Add((message, severity, source));
    }
}
