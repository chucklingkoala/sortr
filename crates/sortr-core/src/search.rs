//! Type-to-search over targets, used when assigning a target by hand.

use crate::normalize::{fold_case, tokenize};
use crate::target::Target;

/// Indices of the targets whose name contains `query`, ignoring case. Separators and accents are also ignored, so
/// "breakingbad" and "brea bad" both find "Breaking Bad". Names that start with the query come first; otherwise the
/// input order (alphabetical from [`crate::scan::load_targets`]) is kept. An empty query returns every target.
pub fn filter(targets: &[Target], query: Option<&str>) -> Vec<usize> {
    let trimmed = query.unwrap_or("").trim();
    if trimmed.is_empty() {
        return (0..targets.len()).collect();
    }

    let folded = fold_case(trimmed);
    let compact = tokenize(trimmed).concat();

    let mut prefix_matches = Vec::new();
    let mut other_matches = Vec::new();
    for (index, target) in targets.iter().enumerate() {
        let name = fold_case(&target.name);
        if name.starts_with(&folded) || (!compact.is_empty() && target.compact_key.starts_with(&compact)) {
            prefix_matches.push(index);
        } else if name.contains(&folded) || (!compact.is_empty() && target.compact_key.contains(&compact)) {
            other_matches.push(index);
        }
    }
    prefix_matches.extend(other_matches);
    prefix_matches
}

#[cfg(test)]
mod tests {
    use super::*;

    fn targets() -> Vec<Target> {
        ["Acme Corp", "Breaking Bad", "Breathe", "Élite", "Jane Doe", "The Office"]
            .iter()
            .map(|n| Target::new(*n, format!(r"C:\Targets\{n}")))
            .collect()
    }

    fn search(query: Option<&str>) -> Vec<String> {
        let t = targets();
        filter(&t, query).into_iter().map(|i| t[i].name.clone()).collect()
    }

    #[test]
    fn empty_query_returns_all_targets() {
        let all: Vec<String> = targets().into_iter().map(|t| t.name).collect();
        for query in [None, Some(""), Some("   ")] {
            assert_eq!(search(query), all);
        }
    }

    #[test]
    fn substring_is_case_insensitive() {
        for query in ["bad", "BAD", "king b"] {
            assert_eq!(search(Some(query)), ["Breaking Bad"], "{query}");
        }
    }

    #[test]
    fn separators_and_accents_are_ignored() {
        for (query, expected) in
            [("breakingbad", "Breaking Bad"), ("breaking.bad", "Breaking Bad"), ("jane_doe", "Jane Doe"), ("elite", "Élite")]
        {
            assert_eq!(search(Some(query)), [expected], "{query}");
        }
    }

    #[test]
    fn prefix_matches_come_first() {
        assert_eq!(search(Some("the")), ["The Office", "Breathe"]);
    }

    #[test]
    fn no_match_returns_empty() {
        assert!(search(Some("zebra")).is_empty());
    }
}
