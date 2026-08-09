using System.Diagnostics;
using WinKeyerEmulator.Core.Protocol;

namespace WinKeyerEmulator.Core.Keying;

/// <summary>
/// Keying mode for the soft keyer.
/// </summary>
public enum SoftKeyerMode
{
    /// <summary>Iambic Mode B - squeeze inserts opposite element after current completes.</summary>
    IambicB = 0,
    /// <summary>Iambic Mode A - releasing during element stops immediately after current.</summary>
    IambicA = 1,
    /// <summary>Ultimatic - last paddle pressed wins when both held.</summary>
    Ultimatic = 2,
    /// <summary>Bug/Cootie - dit paddle makes stream of dits, dah paddle is manual.</summary>
    Bug = 3,
}

/// <summary>
/// Software-based iambic keyer that decodes paddle input into ASCII characters.
/// Used for testing the client without physical WinKeyer hardware.
/// 
/// Runs its own timing thread to generate dit/dah elements at the configured WPM.
/// Decoded characters are emitted via the CharacterDecoded event.
/// </summary>
public sealed class SoftKeyer : IDisposable
{
    // Paddle state (set by UI thread via properties)
    private volatile bool _ditPressed;
    private volatile bool _dahPressed;
    private volatile int _wpm = 25;
    private volatile bool _running;
    private volatile SoftKeyerMode _mode = SoftKeyerMode.IambicB;

    // Threading
    private Thread? _keyerThread;
    private readonly ManualResetEventSlim _stopEvent = new(false);

    // Iambic state machine
    private bool _ditMemory;   // Dit paddle was tapped during dah
    private bool _dahMemory;   // Dah paddle was tapped during dit
    private char _lastElement; // '.' or '-' for alternation logic

    // Pattern buffer for decoding
    private readonly List<char> _patternBuffer = new();
    private long _lastElementEndTicks;
    private bool _wordSpaceSent;

    // Inverted Morse table for decoding pattern → character
    private static readonly Dictionary<string, char> PatternToChar;

    // Timing
    private readonly Stopwatch _clock = new();

    /// <summary>
    /// Raised when a complete character has been decoded from paddle input.
    /// </summary>
    public event EventHandler<char>? CharacterDecoded;

    /// <summary>
    /// Raised when an element (dit or dah) starts, for UI feedback.
    /// The bool is true for dit, false for dah.
    /// </summary>
    public event EventHandler<bool>? ElementStarted;

    /// <summary>
    /// Raised when an element ends, for UI feedback.
    /// </summary>
    public event EventHandler? ElementEnded;

    static SoftKeyer()
    {
        // Build inverted Morse table at startup
        PatternToChar = new Dictionary<string, char>();
        foreach (char c in MorseTable.SupportedCharacters)
        {
            if (MorseTable.TryGetPattern(c, out var pattern))
            {
                PatternToChar[pattern] = c;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the dit paddle is currently pressed.
    /// </summary>
    public bool DitPressed
    {
        get => _ditPressed;
        set
        {
            bool wasPressed = _ditPressed;
            _ditPressed = value;
            // Record memory if pressed during element generation
            if (value && !wasPressed && _running)
            {
                _ditMemory = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the dah paddle is currently pressed.
    /// </summary>
    public bool DahPressed
    {
        get => _dahPressed;
        set
        {
            bool wasPressed = _dahPressed;
            _dahPressed = value;
            // Record memory if pressed during element generation
            if (value && !wasPressed && _running)
            {
                _dahMemory = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the keying speed in words per minute (5-60 WPM).
    /// </summary>
    public int Wpm
    {
        get => _wpm;
        set => _wpm = Math.Clamp(value, 5, 60);
    }

    /// <summary>
    /// Gets or sets the keying mode.
    /// </summary>
    public SoftKeyerMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>
    /// Gets whether the keyer is currently running.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Starts the keyer timing thread.
    /// </summary>
    public void Start()
    {
        if (_running) return;

        _running = true;
        _stopEvent.Reset();
        _clock.Restart();
        _lastElementEndTicks = 0;
        _patternBuffer.Clear();
        _ditMemory = false;
        _dahMemory = false;
        _lastElement = '\0';
        _wordSpaceSent = false;

        _keyerThread = new Thread(KeyerLoop)
        {
            IsBackground = true,
            Name = "SoftKeyer",
            Priority = ThreadPriority.AboveNormal
        };
        _keyerThread.Start();
    }

    /// <summary>
    /// Stops the keyer timing thread.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _stopEvent.Set();
        _keyerThread?.Join(500);
        _keyerThread = null;

        // Flush any pending pattern
        FlushPattern();
    }

    /// <summary>
    /// Main keyer loop - generates elements based on paddle state.
    /// </summary>
    private void KeyerLoop()
    {
        while (_running)
        {
            int ditMs = 1200 / _wpm;

            // Check for character/word gap when idle
            if (!_ditPressed && !_dahPressed && !_ditMemory && !_dahMemory)
            {
                if (_patternBuffer.Count > 0)
                {
                    long idleTicks = _clock.ElapsedTicks - _lastElementEndTicks;
                    double idleMs = idleTicks * 1000.0 / Stopwatch.Frequency;

                    // Letter gap threshold: ~2.5 dit times (be generous)
                    if (idleMs > ditMs * 2.5)
                    {
                        FlushPattern();
                    }
                }
                else if (!_wordSpaceSent && _lastElementEndTicks > 0)
                {
                    // Check for word space (7 dit times since last character)
                    long idleTicks = _clock.ElapsedTicks - _lastElementEndTicks;
                    double idleMs = idleTicks * 1000.0 / Stopwatch.Frequency;

                    if (idleMs > ditMs * 7)
                    {
                        CharacterDecoded?.Invoke(this, ' ');
                        _wordSpaceSent = true;
                    }
                }

                // Small sleep when idle to avoid spinning
                if (_stopEvent.Wait(5)) break;
                continue;
            }

            // Determine which element to generate
            char element = DetermineNextElement();
            if (element == '\0')
            {
                if (_stopEvent.Wait(1)) break;
                continue;
            }

            // Clear the memory for the element we're about to send
            if (element == '.')
                _ditMemory = false;
            else
                _dahMemory = false;

            // Generate the element
            int elementMs = element == '.' ? ditMs : ditMs * 3;

            ElementStarted?.Invoke(this, element == '.');
            _patternBuffer.Add(element);
            _lastElement = element;
            _wordSpaceSent = false;

            // Wait for element duration
            if (!WaitMs(elementMs)) break;

            ElementEnded?.Invoke(this, EventArgs.Empty);

            // Inter-element gap (1 dit)
            _lastElementEndTicks = _clock.ElapsedTicks;
            if (!WaitMs(ditMs)) break;
        }
    }

    /// <summary>
    /// Determines the next element to send based on paddle state and mode.
    /// </summary>
    private char DetermineNextElement()
    {
        bool dit = _ditPressed || _ditMemory;
        bool dah = _dahPressed || _dahMemory;

        if (!dit && !dah)
            return '\0';

        switch (_mode)
        {
            case SoftKeyerMode.IambicB:
            case SoftKeyerMode.IambicA:
                // Iambic: alternate when both pressed, otherwise send what's pressed
                if (dit && dah)
                {
                    // Alternate: if last was dit, send dah, and vice versa
                    return _lastElement == '.' ? '-' : '.';
                }
                return dit ? '.' : '-';

            case SoftKeyerMode.Ultimatic:
                // Ultimatic: most recently pressed paddle wins
                if (dit && dah)
                {
                    // In ultimatic, we track which was pressed last via memory
                    // If both held from start, dit wins
                    if (_ditMemory && !_dahMemory) return '.';
                    if (_dahMemory && !_ditMemory) return '-';
                    return _lastElement == '.' ? '.' : '-'; // Continue last
                }
                return dit ? '.' : '-';

            case SoftKeyerMode.Bug:
                // Bug: dit paddle auto-repeats, dah is manual (one dah per press)
                if (dit)
                    return '.';
                if (dah)
                {
                    // Only send dah if memory set (was just pressed)
                    if (_dahMemory)
                        return '-';
                    // If held but no memory, they already got their dah
                    return '\0';
                }
                return '\0';

            default:
                return dit ? '.' : '-';
        }
    }

    /// <summary>
    /// Waits for the specified milliseconds, checking for stop.
    /// </summary>
    private bool WaitMs(int ms)
    {
        return !_stopEvent.Wait(ms);
    }

    /// <summary>
    /// Decodes the pattern buffer and emits the character.
    /// </summary>
    private void FlushPattern()
    {
        if (_patternBuffer.Count == 0) return;

        string pattern = new string(_patternBuffer.ToArray());
        _patternBuffer.Clear();

        if (PatternToChar.TryGetValue(pattern, out char c))
        {
            CharacterDecoded?.Invoke(this, c);
        }
        else
        {
            // Unknown pattern - emit error character
            CharacterDecoded?.Invoke(this, '?');
        }
    }

    public void Dispose()
    {
        Stop();
        _stopEvent.Dispose();
    }
}
