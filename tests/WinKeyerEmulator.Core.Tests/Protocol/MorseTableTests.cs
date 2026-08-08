using FsCheck;
using FsCheck.Xunit;
using WinKeyerEmulator.Core.Protocol;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Protocol;

/// <summary>
/// Property-based and example-based tests for the MorseTable class.
/// </summary>
public class MorseTableTests
{
    /// <summary>
    /// Property test: all entries in the Morse table contain only '.' and '-' characters.
    /// **Validates: Requirements 1.6**
    /// </summary>
    [Fact]
    public void AllEntries_ContainOnly_DitAndDah()
    {
        foreach (char c in MorseTable.SupportedCharacters)
        {
            Assert.True(MorseTable.TryGetPattern(c, out string pattern));
            Assert.All(pattern, ch => Assert.True(ch == '.' || ch == '-',
                $"Character '{c}' has pattern '{pattern}' containing invalid char '{ch}'"));
        }
    }

    /// <summary>
    /// Property test (FsCheck): for any supported character drawn from the table,
    /// its pattern contains only '.' and '-' characters.
    /// **Validates: Requirements 1.6**
    /// </summary>
    [Property]
    public Property AllPatterns_ContainOnly_DitAndDah_Property()
    {
        var supportedChars = MorseTable.SupportedCharacters.ToArray();
        var gen = Gen.Elements(supportedChars);

        return Prop.ForAll(gen.ToArbitrary(), c =>
        {
            MorseTable.TryGetPattern(c, out string pattern);
            return pattern.All(ch => ch == '.' || ch == '-');
        });
    }

    /// <summary>
    /// Property test: all supported WinKeyer characters have entries in the table.
    /// The WinKeyer protocol supports A-Z, 0-9, and standard punctuation.
    /// **Validates: Requirements 1.6**
    /// </summary>
    [Fact]
    public void AllSupportedWinKeyerCharacters_HaveEntries()
    {
        // All 26 letters
        for (char c = 'A'; c <= 'Z'; c++)
        {
            Assert.True(MorseTable.TryGetPattern(c, out _), $"Missing entry for letter '{c}'");
        }

        // All 10 digits
        for (char c = '0'; c <= '9'; c++)
        {
            Assert.True(MorseTable.TryGetPattern(c, out _), $"Missing entry for digit '{c}'");
        }

        // Standard punctuation
        char[] punctuation = { '.', ',', '?', '/', '-', '=', '\'', '!', '(', ')', '&', ':', ';', '+', '_', '"', '@', '$' };
        foreach (char c in punctuation)
        {
            Assert.True(MorseTable.TryGetPattern(c, out _), $"Missing entry for punctuation '{c}'");
        }
    }

    /// <summary>
    /// Property test (FsCheck): for any character from the supported WinKeyer character set,
    /// the table has a valid entry.
    /// **Validates: Requirements 1.6**
    /// </summary>
    [Property]
    public Property AllSupportedWinKeyerCharacters_HaveEntries_Property()
    {
        // Build the full set of WinKeyer supported characters
        var winKeyerChars = new List<char>();
        for (char c = 'A'; c <= 'Z'; c++) winKeyerChars.Add(c);
        for (char c = '0'; c <= '9'; c++) winKeyerChars.Add(c);
        winKeyerChars.AddRange(new[] { '.', ',', '?', '/', '-', '=', '\'', '!', '(', ')', '&', ':', ';', '+', '_', '"', '@', '$' });

        var gen = Gen.Elements(winKeyerChars.ToArray());

        return Prop.ForAll(gen.ToArbitrary(), c =>
        {
            return MorseTable.TryGetPattern(c, out string pattern)
                   && !string.IsNullOrEmpty(pattern);
        });
    }

    /// <summary>
    /// Example test: known Morse patterns for individual characters.
    /// </summary>
    [Theory]
    [InlineData('E', ".")]
    [InlineData('T', "-")]
    [InlineData('A', ".-")]
    [InlineData('S', "...")]
    [InlineData('O', "---")]
    [InlineData('H', "....")]
    [InlineData('5', ".....")]
    [InlineData('0', "-----")]
    [InlineData('?', "..--..")]
    public void KnownCharacters_HaveCorrectPatterns(char character, string expectedPattern)
    {
        Assert.True(MorseTable.TryGetPattern(character, out string pattern));
        Assert.Equal(expectedPattern, pattern);
    }

    /// <summary>
    /// Example test: SOS pattern is correctly formed from S, O, S characters.
    /// </summary>
    [Fact]
    public void SOS_Pattern_IsCorrect()
    {
        Assert.True(MorseTable.TryGetPattern('S', out string sPattern));
        Assert.True(MorseTable.TryGetPattern('O', out string oPattern));

        string sosPattern = sPattern + oPattern + sPattern;
        Assert.Equal("...---...", sosPattern);
    }

    /// <summary>
    /// Example test: case-insensitive lookup works for letters.
    /// </summary>
    [Theory]
    [InlineData('a', ".-")]
    [InlineData('e', ".")]
    [InlineData('t', "-")]
    [InlineData('z', "--..")]
    public void LowercaseLetters_ResolveCorrectly(char character, string expectedPattern)
    {
        Assert.True(MorseTable.TryGetPattern(character, out string pattern));
        Assert.Equal(expectedPattern, pattern);
    }

    /// <summary>
    /// Example test: unsupported characters return false.
    /// </summary>
    [Theory]
    [InlineData(' ')]
    [InlineData('#')]
    [InlineData('%')]
    [InlineData('^')]
    public void UnsupportedCharacters_ReturnFalse(char character)
    {
        Assert.False(MorseTable.TryGetPattern(character, out _));
    }

    /// <summary>
    /// Example test: prosigns have correct patterns.
    /// </summary>
    [Theory]
    [InlineData("AR", ".-.-.")]
    [InlineData("BT", "-...-")]
    [InlineData("SK", "...-.-")]
    public void Prosigns_HaveCorrectPatterns(string prosign, string expectedPattern)
    {
        Assert.True(MorseTable.TryGetProsign(prosign, out string pattern));
        Assert.Equal(expectedPattern, pattern);
    }

    /// <summary>
    /// Example test: prosign lookup is case-insensitive.
    /// </summary>
    [Theory]
    [InlineData("ar", ".-.-.")]
    [InlineData("bt", "-...-")]
    [InlineData("sk", "...-.-")]
    public void Prosigns_CaseInsensitiveLookup(string prosign, string expectedPattern)
    {
        Assert.True(MorseTable.TryGetProsign(prosign, out string pattern));
        Assert.Equal(expectedPattern, pattern);
    }

    /// <summary>
    /// Property test: all prosign patterns contain only '.' and '-' characters.
    /// **Validates: Requirements 1.6**
    /// </summary>
    [Fact]
    public void AllProsigns_ContainOnly_DitAndDah()
    {
        foreach (string prosign in MorseTable.SupportedProsigns)
        {
            Assert.True(MorseTable.TryGetProsign(prosign, out string pattern));
            Assert.All(pattern, ch => Assert.True(ch == '.' || ch == '-',
                $"Prosign '{prosign}' has pattern '{pattern}' containing invalid char '{ch}'"));
        }
    }

    /// <summary>
    /// All patterns have at least one element (non-empty).
    /// </summary>
    [Fact]
    public void AllPatterns_AreNonEmpty()
    {
        foreach (char c in MorseTable.SupportedCharacters)
        {
            Assert.True(MorseTable.TryGetPattern(c, out string pattern));
            Assert.NotEmpty(pattern);
        }
    }
}
