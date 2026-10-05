//! The ITU Morse pattern table.
//!
//! Port of `RWK.Shared.Protocol.MorseTable`. Patterns use `.` for dit and `-` for
//! dah; the host text path in [`crate::engine::keying`] turns these into timed
//! elements. Advancing/backing up is the caller's concern — this is a pure lookup.

/// ITU Morse pattern lookup for the printable ASCII set the WinKeyer host path accepts.
pub struct MorseTable;

impl MorseTable {
    /// Returns the pattern for `ch`, or [`None`] when the character has no Morse
    /// representation. Case-insensitive for letters.
    #[must_use]
    pub fn pattern(ch: char) -> Option<&'static str> {
        let up = ch.to_ascii_uppercase();
        Some(match up {
            'A' => ".-",
            'B' => "-...",
            'C' => "-.-.",
            'D' => "-..",
            'E' => ".",
            'F' => "..-.",
            'G' => "--.",
            'H' => "....",
            'I' => "..",
            'J' => ".---",
            'K' => "-.-",
            'L' => ".-..",
            'M' => "--",
            'N' => "-.",
            'O' => "---",
            'P' => ".--.",
            'Q' => "--.-",
            'R' => ".-.",
            'S' => "...",
            'T' => "-",
            'U' => "..-",
            'V' => "...-",
            'W' => ".--",
            'X' => "-..-",
            'Y' => "-.--",
            'Z' => "--..",
            '0' => "-----",
            '1' => ".----",
            '2' => "..---",
            '3' => "...--",
            '4' => "....-",
            '5' => ".....",
            '6' => "-....",
            '7' => "--...",
            '8' => "---..",
            '9' => "----.",
            '.' => ".-.-.-",
            ',' => "--..--",
            '?' => "..--..",
            '\'' => ".----.",
            '!' => "-.-.--",
            '/' => "-..-.",
            '(' => "-.--.",
            ')' => "-.--.-",
            '&' => ".-...",
            ':' => "---...",
            ';' => "-.-.-.",
            '=' => "-...-",
            '+' => ".-.-.",
            '-' => "-....-",
            '_' => "..--.-",
            '"' => ".-..-.",
            '$' => "...-..-",
            '@' => ".--.-.",
            // A space is handled by the scheduler as a word gap, not a pattern.
            _ => return None,
        })
    }

    /// True when `ch` is a Morse word separator.
    #[must_use]
    pub fn is_word_separator(ch: char) -> bool {
        ch == ' '
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_letters_match_itu() {
        assert_eq!(MorseTable::pattern('c'), Some("-.-."));
        assert_eq!(MorseTable::pattern('K'), Some("-.-"));
        assert_eq!(MorseTable::pattern('5'), Some("....."));
        assert_eq!(MorseTable::pattern('?'), Some("..--.."));
    }

    #[test]
    fn unknown_characters_have_no_pattern() {
        assert_eq!(MorseTable::pattern('%'), None);
        assert_eq!(MorseTable::pattern('\n'), None);
    }

    #[test]
    fn space_is_a_word_separator() {
        assert!(MorseTable::is_word_separator(' '));
        assert!(!MorseTable::is_word_separator('a'));
    }

    #[test]
    fn every_pattern_is_dit_or_dah_only() {
        for ch in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,?'!/()&:;=+-_\"$@".chars() {
            let pattern = MorseTable::pattern(ch).unwrap_or_else(|| panic!("missing {ch}"));
            assert!(
                pattern.chars().all(|c| c == '.' || c == '-'),
                "{ch} pattern {pattern} has a non-element character"
            );
        }
    }
}
