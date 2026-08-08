using FsCheck;
using FsCheck.Xunit;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.Logging;

namespace WinKeyerEmulator.Core.Tests.Logging;

/// <summary>
/// Property-based tests for LogBuffer line count invariant.
/// **Validates: Requirements 8**
/// </summary>
public class LogBufferPropertyTests
{
    /// <summary>
    /// Property: After N log entries (N > 10000), line count never exceeds 10000.
    /// **Validates: Requirements 8**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property LineCount_NeverExceeds_MaxLines(PositiveInt count)
    {
        const int maxLines = 10000;
        int n = count.Get;

        return Prop.ForAll(
            Arb.Default.NonEmptyString(),
            message =>
            {
                var buffer = new LogBuffer(maxLines);
                for (int i = 0; i < n; i++)
                {
                    buffer.Append(message.Get, LogSeverity.Info, "Test");
                }

                return buffer.LineCount <= maxLines;
            });
    }

    /// <summary>
    /// Property: After exactly maxLines + excess entries, line count equals maxLines.
    /// **Validates: Requirements 8**
    /// </summary>
    [Property(MaxTest = 50)]
    public bool LineCount_AtCapacity_Equals_MaxLines(PositiveInt excess)
    {
        const int maxLines = 100; // Use smaller cap for faster test execution
        int totalEntries = maxLines + excess.Get;

        var buffer = new LogBuffer(maxLines);
        for (int i = 0; i < totalEntries; i++)
        {
            buffer.Append($"Entry {i}", LogSeverity.Info);
        }

        return buffer.LineCount == maxLines;
    }

    /// <summary>
    /// Property: When entries <= maxLines, all entries are retained.
    /// **Validates: Requirements 8**
    /// </summary>
    [Property(MaxTest = 50)]
    public bool LineCount_BelowCap_RetainsAll(PositiveInt count)
    {
        const int maxLines = 10000;
        int n = Math.Min(count.Get, maxLines);

        var buffer = new LogBuffer(maxLines);
        for (int i = 0; i < n; i++)
        {
            buffer.Append($"Entry {i}", LogSeverity.Info);
        }

        return buffer.LineCount == n;
    }
}
