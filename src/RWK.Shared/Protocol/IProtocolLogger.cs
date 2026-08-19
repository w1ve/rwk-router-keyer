namespace RWK.Shared.Protocol;

/// <summary>
/// Severity levels for protocol log entries.
/// </summary>
public enum ProtocolLogSeverity
{
    /// <summary>Informational event, normal operation.</summary>
    Info,

    /// <summary>Something unexpected that was handled without loss of function.</summary>
    Warning,

    /// <summary>A failure that prevented an operation from completing.</summary>
    Error
}

/// <summary>
/// Logging abstraction used by the WinKeyer protocol state machine.
/// </summary>
/// <remarks>
/// Behavior-preserving port of <c>WinKeyerEmulator.Core.ILogger</c> (RWK v1). The v1
/// protocol engine logs every command it consumes, and several v1 tests assert on those
/// entries (for example the out-of-range speed warning), so the port keeps the same shape.
/// Renamed to keep the generic name <c>ILogger</c> free in RWK.Shared.
/// </remarks>
public interface IProtocolLogger
{
    /// <summary>
    /// Records a log entry with the specified message and severity.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="severity">The severity level of the entry.</param>
    /// <param name="source">Optional source identifier (for example, component name).</param>
    void Log(string message, ProtocolLogSeverity severity, string? source = null);
}

/// <summary>
/// An <see cref="IProtocolLogger"/> that discards every entry. Used when a caller has no
/// interest in protocol logging (the protocol engine requires a non-null logger).
/// </summary>
public sealed class NullProtocolLogger : IProtocolLogger
{
    /// <summary>Shared instance.</summary>
    public static readonly NullProtocolLogger Instance = new();

    /// <inheritdoc/>
    public void Log(string message, ProtocolLogSeverity severity, string? source = null)
    {
        // Intentionally empty.
    }
}
