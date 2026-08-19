namespace RWK.Shared.Protocol;

/// <summary>
/// Static lookup table mapping characters to their Morse code patterns (dit/dah strings).
/// Supports all 26 letters, 10 digits, standard punctuation, and prosigns.
/// </summary>
/// <remarks>
/// Behavior-preserving copy of WinKeyerEmulator.Core.Protocol.MorseTable (RWK v1).
/// Required by <see cref="RWK.Shared.Timing.EdgeScheduleBuilder"/>.
/// </remarks>
public static class MorseTable
{
    private static readonly Dictionary<char, string> Patterns = new()
    {
        // Letters
        ['A'] = ".-",
        ['B'] = "-...",
        ['C'] = "-.-.",
        ['D'] = "-..",
        ['E'] = ".",
        ['F'] = "..-.",
        ['G'] = "--.",
        ['H'] = "....",
        ['I'] = "..",
        ['J'] = ".---",
        ['K'] = "-.-",
        ['L'] = ".-..",
        ['M'] = "--",
        ['N'] = "-.",
        ['O'] = "---",
        ['P'] = ".--.",
        ['Q'] = "--.-",
        ['R'] = ".-.",
        ['S'] = "...",
        ['T'] = "-",
        ['U'] = "..-",
        ['V'] = "...-",
        ['W'] = ".--",
        ['X'] = "-..-",
        ['Y'] = "-.--",
        ['Z'] = "--..",

        // Digits
        ['0'] = "-----",
        ['1'] = ".----",
        ['2'] = "..---",
        ['3'] = "...--",
        ['4'] = "....-",
        ['5'] = ".....",
        ['6'] = "-....",
        ['7'] = "--...",
        ['8'] = "---..",
        ['9'] = "----.",

        // Punctuation
        ['.'] = ".-.-.-",
        [','] = "--..--",
        ['?'] = "..--..",
        ['/'] = "-..-.",
        ['-'] = "-....-",
        ['='] = "-...-",
        ['\''] = ".----.",
        ['!'] = "-.-.--",
        ['('] = "-.--.",
        [')'] = "-.--.-",
        ['&'] = ".-...",
        [':'] = "---...",
        [';'] = "-.-.-.",
        ['+'] = ".-.-.",
        ['_'] = "..--.-",
        ['"'] = ".-..-.",
        ['@'] = ".--.-.",
        ['$'] = "...-..-",
    };

    /// <summary>
    /// Prosign patterns keyed by their multi-character abbreviation.
    /// Prosigns are transmitted as a single combined pattern without inter-character gaps.
    /// </summary>
    private static readonly Dictionary<string, string> Prosigns = new(StringComparer.Ordinal)
    {
        ["AR"] = ".-.-.",
        ["BT"] = "-...-",
        ["SK"] = "...-.-",
    };

    /// <summary>
    /// Gets the set of all single characters that have Morse patterns.
    /// </summary>
    public static IReadOnlyCollection<char> SupportedCharacters => Patterns.Keys;

    /// <summary>
    /// Gets the set of all prosign abbreviations.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedProsigns => Prosigns.Keys;

    /// <summary>
    /// Attempts to get the Morse pattern for a single character.
    /// Characters are matched case-insensitively for letters.
    /// </summary>
    /// <param name="c">The character to look up.</param>
    /// <param name="pattern">The dit/dah pattern if found.</param>
    /// <returns>True if the character has a Morse pattern; false otherwise.</returns>
    public static bool TryGetPattern(char c, out string pattern)
    {
        char upper = char.ToUpperInvariant(c);
        return Patterns.TryGetValue(upper, out pattern!);
    }

    /// <summary>
    /// Attempts to get the Morse pattern for a prosign.
    /// </summary>
    /// <param name="prosign">The prosign abbreviation (e.g., "AR", "BT", "SK").</param>
    /// <param name="pattern">The dit/dah pattern if found.</param>
    /// <returns>True if the prosign is recognized; false otherwise.</returns>
    public static bool TryGetProsign(string prosign, out string pattern)
    {
        return Prosigns.TryGetValue(prosign.ToUpperInvariant(), out pattern!);
    }
}
