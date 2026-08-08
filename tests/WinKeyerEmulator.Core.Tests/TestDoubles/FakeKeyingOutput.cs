using WinKeyerEmulator.Core.IO;
using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.Core.Tests.TestDoubles;

/// <summary>
/// Type of keying event recorded by FakeKeyingOutput.
/// </summary>
public enum KeyingEventType
{
    KeyDown,
    KeyUp
}

/// <summary>
/// A single recorded keying event with its type and timestamp.
/// </summary>
public record KeyingEvent(KeyingEventType Type, long Timestamp);

/// <summary>
/// Test double for <see cref="IKeyingOutput"/> that records all KeyDown/KeyUp calls
/// with timestamps from a provided clock for assertion in tests.
/// </summary>
public sealed class FakeKeyingOutput : IKeyingOutput
{
    private readonly ISystemClock? _clock;
    private readonly List<KeyingEvent> _events = new();

    /// <summary>
    /// Gets the list of recorded keying events in order.
    /// </summary>
    public IReadOnlyList<KeyingEvent> Events => _events;

    /// <summary>
    /// Gets whether the output is currently open.
    /// </summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Creates a FakeKeyingOutput that records timestamps from the provided clock.
    /// </summary>
    /// <param name="clock">Optional clock for recording timestamps. If null, timestamps are 0.</param>
    public FakeKeyingOutput(ISystemClock? clock = null)
    {
        _clock = clock;
    }

    /// <inheritdoc/>
    public void Open(string portName, KeyingLine line)
    {
        IsOpen = true;
    }

    /// <inheritdoc/>
    public void Close()
    {
        IsOpen = false;
    }

    /// <inheritdoc/>
    public void KeyDown()
    {
        long timestamp = _clock?.GetTimestamp() ?? 0;
        _events.Add(new KeyingEvent(KeyingEventType.KeyDown, timestamp));
    }

    /// <inheritdoc/>
    public void KeyUp()
    {
        long timestamp = _clock?.GetTimestamp() ?? 0;
        _events.Add(new KeyingEvent(KeyingEventType.KeyUp, timestamp));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
    }

    /// <summary>
    /// Clears all recorded events.
    /// </summary>
    public void Clear()
    {
        _events.Clear();
    }
}
