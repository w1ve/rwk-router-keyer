using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Logging;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Logging;

/// <summary>
/// Example-based tests for LogBuffer.
/// </summary>
public class LogBufferTests
{
    /// <summary>
    /// Test 14.6: Log entries include timestamp and severity.
    /// </summary>
    [Fact]
    public void Append_IncludesTimestampAndSeverity()
    {
        var buffer = new LogBuffer();

        buffer.Append("Test message", LogSeverity.Info, "TestSource");

        var lines = buffer.GetLines();
        Assert.Single(lines);

        string line = lines[0];
        // Verify timestamp format [HH:mm:ss.fff]
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", line);
        // Verify severity is present
        Assert.Contains("[INFO]", line);
        // Verify source is present
        Assert.Contains("[TestSource]", line);
        // Verify message is present
        Assert.Contains("Test message", line);
    }

    [Fact]
    public void Append_WarningSeverity_ShowsWARN()
    {
        var buffer = new LogBuffer();

        buffer.Append("Warning message", LogSeverity.Warning);

        var lines = buffer.GetLines();
        Assert.Single(lines);
        Assert.Contains("[WARN]", lines[0]);
    }

    [Fact]
    public void Append_ErrorSeverity_ShowsERROR()
    {
        var buffer = new LogBuffer();

        buffer.Append("Error message", LogSeverity.Error);

        var lines = buffer.GetLines();
        Assert.Single(lines);
        Assert.Contains("[ERROR]", lines[0]);
    }

    [Fact]
    public void Append_NoSource_OmitsSourceBrackets()
    {
        var buffer = new LogBuffer();

        buffer.Append("No source", LogSeverity.Info);

        var lines = buffer.GetLines();
        string line = lines[0];
        // Should have format: [timestamp] [INFO] message (no third bracket group for source)
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[INFO\] No source$", line);
    }

    [Fact]
    public void LineCount_AfterExceedingCap_TrimsOldestLines()
    {
        var buffer = new LogBuffer(maxLines: 5);

        for (int i = 0; i < 8; i++)
        {
            buffer.Append($"Line {i}", LogSeverity.Info);
        }

        Assert.Equal(5, buffer.LineCount);
        // Oldest 3 lines (0, 1, 2) should be trimmed
        var lines = buffer.GetLines();
        Assert.Contains("Line 3", lines[0]);
        Assert.Contains("Line 7", lines[4]);
    }

    [Fact]
    public void Clear_RemovesAllLines()
    {
        var buffer = new LogBuffer();
        buffer.Append("Line 1", LogSeverity.Info);
        buffer.Append("Line 2", LogSeverity.Info);

        buffer.Clear();

        Assert.Equal(0, buffer.LineCount);
    }

    [Fact]
    public void Constructor_InvalidMaxLines_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(-1));
    }
}
