using System.Diagnostics;
using RWK.Shared;
using RWK.Shared.IO;

namespace RWK.Station.Tests.TestDoubles;

/// <summary>
/// One recorded key or PTT transition with the elapsed time at which it happened.
/// </summary>
public readonly record struct LineTransition(string Line, bool Asserted, double ElapsedMs);

/// <summary>
/// An <see cref="IKeyingOutput"/> plus <see cref="IPttOutput"/> that records transitions with
/// timestamps instead of touching a serial port.
/// </summary>
public sealed class RecordingKeyingOutput : IKeyingOutput, IPttOutput
{
    private readonly List<LineTransition> _transitions = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    /// <summary>Every transition recorded so far, oldest first.</summary>
    public IReadOnlyList<LineTransition> Transitions
    {
        get { lock (_gate) { return _transitions.ToArray(); } }
    }

    /// <summary>Transitions of the key line only.</summary>
    public IReadOnlyList<LineTransition> KeyTransitions
        => Transitions.Where(t => t.Line == "KEY").ToArray();

    /// <summary>Transitions of the PTT line only.</summary>
    public IReadOnlyList<LineTransition> PttTransitions
        => Transitions.Where(t => t.Line == "PTT").ToArray();

    /// <summary>Whether the key line is currently asserted.</summary>
    public bool IsKeyDown { get; private set; }

    /// <summary>Whether the PTT line is currently asserted.</summary>
    public bool IsPttOn { get; private set; }

    /// <inheritdoc/>
    public bool IsOpen { get; private set; } = true;

    /// <summary>Restarts the elapsed-time reference and clears recorded transitions.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _transitions.Clear();
            _clock.Restart();
        }
    }

    /// <inheritdoc/>
    public void Open(string portName, KeyingLine line) => IsOpen = true;

    /// <inheritdoc/>
    public void Close() => IsOpen = false;

    /// <inheritdoc/>
    public void KeyDown() => Record("KEY", asserted: true);

    /// <inheritdoc/>
    public void KeyUp() => Record("KEY", asserted: false);

    /// <inheritdoc/>
    public void PttDown() => Record("PTT", asserted: true);

    /// <inheritdoc/>
    public void PttUp() => Record("PTT", asserted: false);

    /// <inheritdoc/>
    public void Dispose() => IsOpen = false;

    private void Record(string line, bool asserted)
    {
        lock (_gate)
        {
            // Record only genuine transitions. The sequencer's fail-safe paths de-assert
            // defensively, and those repeats would swamp the timing assertions. Both lines start
            // de-asserted, matching StationKeyingOutput.Open.
            bool current = line == "KEY" ? IsKeyDown : IsPttOn;
            if (current == asserted)
            {
                return;
            }

            if (line == "KEY")
            {
                IsKeyDown = asserted;
            }
            else
            {
                IsPttOn = asserted;
            }

            _transitions.Add(new LineTransition(line, asserted, _clock.Elapsed.TotalMilliseconds));
        }
    }
}
