/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace WinKeyerEmulator.Core.Logging;

/// <summary>
/// A thread-safe log buffer that maintains a capped number of log lines.
/// This class encapsulates the line-limiting logic so it can be tested
/// independently of UI concerns (WinForms Control.BeginInvoke).
/// </summary>
public sealed class LogBuffer
{
    private readonly List<string> _lines = new();
    private readonly object _lock = new();
    private readonly int _maxLines;

    /// <summary>
    /// Creates a new LogBuffer with the specified maximum line count.
    /// </summary>
    /// <param name="maxLines">Maximum number of lines to retain. Must be > 0.</param>
    public LogBuffer(int maxLines = 10000)
    {
        if (maxLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLines), "Max lines must be positive.");

        _maxLines = maxLines;
    }

    /// <summary>
    /// Gets the current number of lines in the buffer.
    /// </summary>
    public int LineCount
    {
        get
        {
            lock (_lock)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>
    /// Gets the maximum number of lines this buffer will retain.
    /// </summary>
    public int MaxLines => _maxLines;

    /// <summary>
    /// Appends a formatted log line to the buffer.
    /// If the buffer exceeds the maximum line count, the oldest lines are trimmed.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="severity">The severity level.</param>
    /// <param name="source">Optional source identifier.</param>
    public void Append(string message, LogSeverity severity, string? source = null)
    {
        string line = FormatLine(message, severity, source);

        lock (_lock)
        {
            _lines.Add(line);
            TrimIfNeeded();
        }
    }

    /// <summary>
    /// Gets all current lines as a single string separated by newlines.
    /// </summary>
    public string GetText()
    {
        lock (_lock)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }

    /// <summary>
    /// Gets a snapshot of all current lines.
    /// </summary>
    public string[] GetLines()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }

    /// <summary>
    /// Clears all log lines.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }

    private void TrimIfNeeded()
    {
        if (_lines.Count > _maxLines)
        {
            int excess = _lines.Count - _maxLines;
            _lines.RemoveRange(0, excess);
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
