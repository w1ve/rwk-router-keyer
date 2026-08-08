using WinKeyerEmulator.Core.IO;
using WinKeyerEmulator.Core.Protocol;
using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.Core;

/// <summary>
/// Orchestrates the WinKeyer protocol parsing and timing engine execution.
/// This is the central coordination class that wires protocol commands to keying output.
/// </summary>
public class KeyerCore : IDisposable
{
    private readonly IKeyingOutput _keyingOutput;
    private readonly TimingEngine _timingEngine;
    private readonly ILogger _logger;
    private readonly WinKeyerProtocol _protocol;
    private readonly System.Threading.Timer _flushTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a new KeyerCore with the specified dependencies.
    /// </summary>
    /// <param name="keyingOutput">The keying output interface for DTR/RTS toggling.</param>
    /// <param name="timingEngine">The timing engine for scheduling Morse edges.</param>
    /// <param name="logger">Logger for operational events.</param>
    public KeyerCore(IKeyingOutput keyingOutput, TimingEngine timingEngine, ILogger logger)
    {
        _keyingOutput = keyingOutput ?? throw new ArgumentNullException(nameof(keyingOutput));
        _timingEngine = timingEngine ?? throw new ArgumentNullException(nameof(timingEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _protocol = new WinKeyerProtocol(logger);
        _protocol.TextReceived += OnTextReceived;
        _protocol.BufferCleared += OnBufferCleared;
        _flushTimer = new System.Threading.Timer(FlushBuffer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Raised when asynchronous response bytes need to be sent to the host
    /// (character echoes, status changes). The AppController wires this to
    /// the appropriate ICommandSink.
    /// </summary>
    public event EventHandler<byte[]>? ResponseAvailable;

    /// <summary>
    /// Gets the current protocol state for inspection and testing.
    /// </summary>
    public ProtocolState State => _protocol.State;

    /// <summary>
    /// Processes incoming command bytes through the WinKeyer protocol state machine.
    /// Text characters received in host mode are routed to the TimingEngine.
    /// </summary>
    /// <param name="data">The raw command bytes to process.</param>
    /// <returns>All response bytes produced (concatenated), or null if no responses were generated.</returns>
    public byte[]? ProcessCommand(ReadOnlySpan<byte> data)
    {
        List<byte>? allResponses = null;
        foreach (byte b in data)
        {
            var response = _protocol.ProcessByte(b);
            if (response != null)
            {
                allResponses ??= new List<byte>();
                allResponses.AddRange(response);
            }
        }
        return allResponses?.ToArray();
    }

    /// <summary>
    /// Cancels the current transmission and clears the text buffer.
    /// </summary>
    public void AbortMessage()
    {
        _timingEngine.AbortCurrent();
        _protocol.State.TextBuffer.Clear();
        _protocol.State.BufferState = BufferState.Idle;
        _logger.Log("Message aborted", LogSeverity.Info, "KeyerCore");
    }

    /// <summary>
    /// Handles text characters received from the protocol layer.
    /// Starts/resets a short timer to batch characters before flushing to the timing engine.
    /// </summary>
    private void OnTextReceived(object? sender, char c)
    {
        // Reset the flush timer - we'll drain the buffer after a short pause
        // to allow multi-character sequences to accumulate
        _flushTimer.Change(50, Timeout.Infinite);
    }

    /// <summary>
    /// Timer callback that drains the text buffer and sends it to the timing engine.
    /// </summary>
    private void FlushBuffer(object? state)
    {
        if (_disposed) return;

        try
        {
            var buffer = _protocol.State.TextBuffer;
            if (buffer.Count > 0)
            {
                var text = new string(buffer.ToArray());
                buffer.Clear();
                _protocol.State.BufferState = BufferState.Idle;

                _logger.Log($"Keying: \"{text}\" at {_protocol.State.CurrentWpm} WPM", LogSeverity.Info, "KeyerCore");
                _timingEngine.EnqueueMessage(text, _protocol.State.CurrentWpm);

                // Echo each character back to the host (WinKeyer protocol requirement)
                var echoBytes = new List<byte>();
                foreach (char ch in text)
                {
                    echoBytes.Add((byte)ch);
                }
                // Append idle status byte (0xC0 = idle, buffer empty)
                echoBytes.Add(0xC0);

                ResponseAvailable?.Invoke(this, echoBytes.ToArray());
            }
        }
        catch
        {
            // Swallow exceptions during shutdown
        }
    }

    /// <summary>
    /// Handles buffer clear from the protocol layer — aborts current keying.
    /// </summary>
    private void OnBufferCleared(object? sender, EventArgs e)
    {
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite); // Cancel pending flush
        _timingEngine.AbortCurrent();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _flushTimer.Dispose();
        _protocol.TextReceived -= OnTextReceived;
        _protocol.BufferCleared -= OnBufferCleared;
    }
}
