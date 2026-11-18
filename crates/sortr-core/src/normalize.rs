//! Normalizes names so that "Breaking.Bad", "breaking_bad", "BREAKING BAD" and "Bréaking-Bad" all reduce to the same
//! tokens: `["breaking", "bad"]`.
//!
//! The rules follow Sortr 1.x (.NET) exactly so that existing folders match the same way:
//! - accents are removed by decomposing (NFD), dropping non-spacing marks (category Mn only) and recomposing (NFC);
//! - characters are lowercased one at a time with the simple mapping (no context-dependent final sigma);
//! - token characters are letters (Lu, Ll, Lt, Lm, Lo) and decimal digits (Nd). Everything else separates, including
//!   `²` (No) and `Ⅻ` (Nl), which Rust's `char::is_alphanumeric` would keep.

use std::cmp::Ordering;

use icu_normalizer::{ComposingNormalizerBorrowed, DecomposingNormalizerBorrowed};
use icu_properties::CodePointMapData;
use icu_properties::props::GeneralCategory;

fn category(c: char) -> GeneralCategory {
    CodePointMapData::<GeneralCategory>::new().get(c)
}

/// Removes accents: "Bréaking" → "Breaking", "Cœur-Brisé" → "Cœur-Brise".
pub fn remove_diacritics(text: &str) -> String {
    let decomposed = DecomposingNormalizerBorrowed::new_nfd().normalize(text);
    let stripped: String = decomposed.chars().filter(|&c| category(c) != GeneralCategory::NonspacingMark).collect();
    ComposingNormalizerBorrowed::new_nfc().normalize(&stripped).into_owned()
}

fn is_letter_or_digit(c: char) -> bool {
    matches!(
        category(c),
        GeneralCategory::UppercaseLetter
            | GeneralCategory::LowercaseLetter
            | GeneralCategory::TitlecaseLetter
            | GeneralCategory::ModifierLetter
            | GeneralCategory::OtherLetter
            | GeneralCategory::DecimalNumber
    )
}

/// One-to-one lowercase mapping; characters whose lowercase is several characters are kept as they are.
fn simple_lower(c: char) -> char {
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

/// One-to-one uppercase mapping (so `ß` stays `ß` rather than becoming `SS`).
fn simple_upper(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// Lowercased, accent-free tokens of letters and digits, split on any other character.
pub fn tokenize(text: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut current = String::new();
    for c in remove_diacritics(text).chars().map(simple_lower) {
        if is_letter_or_digit(c) {
            current.push(c);
        } else if !current.is_empty() {
            tokens.push(std::mem::take(&mut current));
        }
    }
    if !current.is_empty() {
        tokens.push(current);
    }
    tokens
}

/// Case-folds for ordinal, case-insensitive comparison (.NET's `OrdinalIgnoreCase`).
pub fn fold_case(text: &str) -> String {
    text.chars().map(simple_upper).collect()
}

/// Ordinal, case-insensitive ordering, as used for sorting files and targets.
pub fn cmp_ignore_case(a: &str, b: &str) -> Ordering {
    a.chars().map(simple_upper).cmp(b.chars().map(simple_upper))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tokens(text: &str) -> Vec<String> {
        tokenize(text)
    }

    #[test]
    fn tokenize_normalizes_case_separators_and_accents() {
        assert_eq!(tokens("Breaking.Bad"), ["breaking", "bad"]);
        assert_eq!(tokens("breaking_bad"), ["breaking", "bad"]);
        assert_eq!(tokens("  BREAKING -- Bad "), ["breaking", "bad"]);
        assert_eq!(tokens("Élite"), ["elite"]);
        assert_eq!(tokens("Cœur-Brisé"), ["cœur", "brise"]);
    }

    #[test]
    fn only_letters_and_decimal_digits_are_token_characters() {
        // ² is "other number" and Ⅻ is "letter number": .NET's IsLetterOrDigit treats both as separators.
        assert_eq!(tokens("Ab²Cd"), ["ab", "cd"]);
        assert_eq!(tokens("Henry Ⅻ"), ["henry"]);
        assert_eq!(tokens("Room 101"), ["room", "101"]);
    }

    #[test]
    fn lowercasing_is_one_to_one() {
        // İ decomposes to I + combining dot, so it becomes a plain i.
        assert_eq!(tokens("İstanbul"), ["istanbul"]);
        // No final-sigma rule: Σ always lowercases to σ.
        assert_eq!(tokens("ΟΔΟΣ"), ["οδοσ"]);
    }

    #[test]
    fn empty_and_separator_only_text_has_no_tokens() {
        assert!(tokens("").is_empty());
        assert!(tokens(" ._-()[] ").is_empty());
    }

    #[test]
    fn case_insensitive_ordering() {
        assert_eq!(cmp_ignore_case("acme", "ACME"), Ordering::Equal);
        assert_eq!(cmp_ignore_case("apple", "Banana"), Ordering::Less);
        assert_eq!(fold_case("Straße"), "STRAßE");
    }
}
