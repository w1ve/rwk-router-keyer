using WinKeyerEmulator.Core.Protocol;

namespace WinKeyerEmulator.Core.Timing;

/// <summary>
/// Pure-function schedule builder that converts text and WPM into an array of absolute
/// tick offsets representing key-down/key-up edge transitions.
/// Even-indexed entries are key-down timestamps, odd-indexed entries are key-up timestamps.
/// </summary>
public static class EdgeScheduleBuilder
{
    /// <summary>
    /// Builds an array of absolute tick offsets for all key-down/key-up edges.
    /// Even indices are key-down, odd indices are key-up.
    /// </summary>
    /// <param name="text">The text to encode as Morse code.</param>
    /// <param name="wpm">Speed in words per minute (PARIS standard).</param>
    /// <param name="tickFrequency">Tick frequency of the timing source (ticks per second).</param>
    /// <returns>Array of absolute tick offsets from t=0. Empty array if no valid characters.</returns>
    public static long[] Build(string text, int wpm, long tickFrequency)
    {
        if (string.IsNullOrEmpty(text) || wpm <= 0 || tickFrequency <= 0)
            return Array.Empty<long>();

        long dit = tickFrequency * 1200L / (wpm * 1000L);
        long dah = 3 * dit;
        long intraCharGap = dit;       // Between elements within a character
        long interCharGap = 3 * dit;   // Between characters
        long wordGap = 7 * dit;        // Between words (space character)

        var edges = new List<long>();
        long position = 0;
        bool previousCharEmitted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == ' ')
            {
                // Word gap: advance position, no edges emitted
                if (previousCharEmitted)
                {
                    position += wordGap;
                }
                previousCharEmitted = false;
                continue;
            }

            if (!MorseTable.TryGetPattern(c, out string pattern))
            {
                // Unknown character: skip entirely
                continue;
            }

            // Add inter-character gap before this character (if a previous character was emitted)
            if (previousCharEmitted)
            {
                position += interCharGap;
            }

            // Emit edges for each element in the pattern
            for (int j = 0; j < pattern.Length; j++)
            {
                // Add intra-character gap before this element (not before the first)
                if (j > 0)
                {
                    position += intraCharGap;
                }

                long elementDuration = pattern[j] == '.' ? dit : dah;

                // Key-down edge
                edges.Add(position);
                // Key-up edge
                edges.Add(position + elementDuration);

                position += elementDuration;
            }

            previousCharEmitted = true;
        }

        return edges.ToArray();
    }
}
