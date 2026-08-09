using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Logging;

namespace WinKeyerEmulator.App.Logging;

/// <summary>
/// Implements ILogger by writing formatted log entries to a WinForms TextBox control.
/// Thread-safe: marshals all writes to the UI thread via Control.BeginInvoke.
/// Uses AppendText for efficiency - avoids full text rebuild on each log.
/// Trims old lines when approaching capacity.
/// </summary>
public sealed class UILogger : ILogger
{
    private readonly TextBox _textBox;
    private readonly int _maxLines;
    private int _lineCount;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new UILogger that writes to the specified TextBox.
    /// </summary>
    /// <param name="textBox">The target TextBox control (must be Multiline, ReadOnly).</param>
    /// <param name="maxLines">Maximum number of lines to retain.</param>
    public UILogger(TextBox textBox, int maxLines = 10000)
    {
        _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
        _maxLines = maxLines;
        _lineCount = 0;
    }

    /// <inheritdoc/>
    public void Log(string message, LogSeverity severity, string? source = null)
    {
        string line = FormatLine(message, severity, source);

        try
        {
            if (_textBox.IsHandleCreated && !_textBox.IsDisposed && !_textBox.Disposing)
            {
                _textBox.BeginInvoke(() =>
                {
                    try
                    {
                        if (!_textBox.IsDisposed)
                            AppendLine(line);
                    }
                    catch { }
                });
            }
        }
        catch { }
    }

    private void AppendLine(string line)
    {
        lock (_lock)
        {
            _lineCount++;

            // Trim old lines if we're at capacity (do it in batches for efficiency)
            if (_lineCount > _maxLines)
            {
                TrimOldLines();
            }

            // Use AppendText which is O(1) instead of setting Text which is O(n)
            _textBox.AppendText(line + Environment.NewLine);
        }
    }

    private void TrimOldLines()
    {
        // Remove oldest 20% of lines to avoid frequent trimming
        int linesToRemove = _maxLines / 5;
        
        string text = _textBox.Text;
        int removeUpTo = 0;
        int linesFound = 0;
        
        for (int i = 0; i < text.Length && linesFound < linesToRemove; i++)
        {
            if (text[i] == '\n')
            {
                linesFound++;
                removeUpTo = i + 1;
            }
        }

        if (removeUpTo > 0 && removeUpTo < text.Length)
        {
            _textBox.Text = text.Substring(removeUpTo);
            _lineCount -= linesFound;
        }
    }

    private static string FormatLine(string message, LogSeverity severity, string? source)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string severityStr = severity switch
        {
            LogSeverity.Info => "INFO",
            LogSeverity.Warning => "WARN",
            LogSeverity.Error => "ERROR",
            _ => "INFO"
        };

        if (string.IsNullOrEmpty(source))
            return $"[{timestamp}] [{severityStr}] {message}";
        else
            return $"[{timestamp}] [{severityStr}] [{source}] {message}";
    }
}
