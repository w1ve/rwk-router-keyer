namespace WinKeyerEmulator.Core.Protocol;

/// <summary>
/// Tracks the current state of the text buffer.
/// </summary>
public enum BufferState
{
    /// <summary>Buffer is empty, no transmission in progress.</summary>
    Idle,

    /// <summary>Characters are being transmitted.</summary>
    Sending
}

/// <summary>
/// Holds the current WinKeyer protocol state including host mode, speed, and buffer status.
/// </summary>
public class ProtocolState
{
    /// <summary>
    /// Whether the host has opened a session (Admin Open received).
    /// Defaults to true so the emulator works immediately if the host
    /// connects mid-session (e.g., after emulator restart).
    /// </summary>
    public bool HostMode { get; set; } = true;

    /// <summary>
    /// Current keying speed in words per minute. Default is 15 WPM.
    /// </summary>
    public int CurrentWpm { get; set; } = 15;

    /// <summary>
    /// Current state of the text buffer (idle or sending).
    /// </summary>
    public BufferState BufferState { get; set; } = BufferState.Idle;

    /// <summary>
    /// Queue of characters pending transmission.
    /// </summary>
    public Queue<char> TextBuffer { get; } = new();

    /// <summary>
    /// Resets the state to defaults (as if freshly constructed).
    /// </summary>
    public void Reset()
    {
        HostMode = true;
        CurrentWpm = 15;
        BufferState = BufferState.Idle;
        TextBuffer.Clear();
    }
}
