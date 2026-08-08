using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Logging;

namespace WinKeyerEmulator.App.Logging;

/// <summary>
/// Implements ILogger by writing formatted log entries to a WinForms TextBox control.
/// Thread-safe: marshals all writes to the UI thread via Control.BeginInvoke.
/// Maintains a maximum line count cap of 10000 lines.
/// </summary>
public sealed class UILogger : ILogger
{
    private readonly TextBox _textBox;
    private readonly LogBuffer _buffer;

    /// <summary>
    /// Creates a new UILogger that writes to the specified TextBox.
    /// </summary>
    /// <param name="textBox">The target TextBox control (must be Multiline, ReadOnly).</param>
    /// <param name="maxLines">Maximum number of lines to retain.</param>
    public UILogger(TextBox textBox, int maxLines = 10000)
    {
        _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
        _buffer = new LogBuffer(maxLines);
    }

    /// <inheritdoc/>
    public void Log(string message, LogSeverity severity, string? source = null)
    {
        _buffer.Append(message, severity, source);

        try
        {
            if (_textBox.IsHandleCreated && !_textBox.IsDisposed && !_textBox.Disposing)
            {
                _textBox.BeginInvoke(() =>
                {
                    try
                    {
                        if (!_textBox.IsDisposed)
                            UpdateTextBox();
                    }
                    catch { }
                });
            }
        }
        catch { }
    }

    private void UpdateTextBox()
    {
        _textBox.Text = _buffer.GetText();

        // Auto-scroll to bottom
        _textBox.SelectionStart = _textBox.Text.Length;
        _textBox.ScrollToCaret();
    }
}
