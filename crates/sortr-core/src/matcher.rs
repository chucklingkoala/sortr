use serde::Serialize;

use crate::normalize::{cmp_ignore_case, tokenize};
use crate::target::Target;

/// Minimum compact-key length (in characters) before separator-free (loose) matching is allowed.
pub const MIN_LOOSE_KEY_LENGTH: usize = 6;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize)]
pub enum MatchConfidence {
    /// Name found as whole tokens, e.g. "breaking.bad", "Bad_Breaking".
    Strong,
    /// Name found only with separators ignored, e.g. "breakingbad", "webripBreakingBad".
    Loose,
}

/// One target a file name mentions.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MatchCandidate {
    /// Index into the matcher's target list.
    pub target: usize,
    pub confidence: MatchConfidence,
    /// Start of the match within the file's compact (separator-free) name, in characters.
    pub start: usize,
    /// Length of the match within the compact name, in characters.
    pub length: usize,
}

impl MatchCandidate {
    pub fn end(&self) -> usize {
        self.start + self.length
    }
}

pub struct NameMatcher<'a> {
    targets: &'a [Target],
}

impl<'a> NameMatcher<'a> {
    pub fn new(targets: &'a [Target]) -> Self {
        NameMatcher { targets }
    }

    /// Returns candidates best-first: strong before loose, then longest match, then by name.
    pub fn match_name(&self, file_name: &str) -> Vec<MatchCandidate> {
        let (stem, _) = crate::split_extension(file_name);
        let tokens = tokenize(stem);
        if tokens.is_empty() {
            return Vec::new();
        }

        let lengths: Vec<usize> = tokens.iter().map(|t| t.chars().count()).collect();
        let mut offsets = vec![0; tokens.len()];
        for i in 1..tokens.len() {
            offsets[i] = offsets[i - 1] + lengths[i - 1];
        }
        let compact = tokens.concat();

        let candidates: Vec<MatchCandidate> = self
            .targets
            .iter()
            .enumerate()
            .filter_map(|(index, target)| {
                match_strong(index, target, &tokens, &offsets, &lengths).or_else(|| match_loose(index, target, &compact))
            })
            .collect();

        // Drop matches fully contained in a longer match ("Doctor Who" inside "Doctor Who Confidential").
        let mut kept: Vec<MatchCandidate> = candidates
            .iter()
            .filter(|c| !candidates.iter().any(|o| o.length > c.length && o.start <= c.start && o.end() >= c.end()))
            .cloned()
            .collect();
        kept.sort_by(|a, b| {
            a.confidence
                .cmp(&b.confidence)
                .then(b.length.cmp(&a.length))
                .then_with(|| cmp_ignore_case(&self.targets[a.target].name, &self.targets[b.target].name))
        });
        kept
    }
}

fn match_strong(
    index: usize,
    target: &Target,
    tokens: &[String],
    offsets: &[usize],
    lengths: &[usize],
) -> Option<MatchCandidate> {
    let name = &target.tokens;
    let n = name.len();
    if n == 0 || n > tokens.len() {
        return None;
    }
    (0..=tokens.len() - n).find_map(|i| {
        let run = &tokens[i..i + n];
        let forward = run.iter().eq(name.iter());
        let reversed = n > 1 && run.iter().eq(name.iter().rev());
        (forward || reversed).then(|| {
            let start = offsets[i];
            let end = offsets[i + n - 1] + lengths[i + n - 1];
            MatchCandidate { target: index, confidence: MatchConfidence::Strong, start, length: end - start }
        })
    })
}

fn match_loose(index: usize, target: &Target, compact: &str) -> Option<MatchCandidate> {
    // Single-word names and short keys are too prone to false positives inside other words.
    if target.tokens.len() < 2 || target.compact_len < MIN_LOOSE_KEY_LENGTH {
        return None;
    }
    let byte_index = compact.find(&target.compact_key).or_else(|| compact.find(&target.reversed_compact_key))?;
    Some(MatchCandidate {
        target: index,
        confidence: MatchConfidence::Loose,
        start: compact[..byte_index].chars().count(),
        length: target.compact_len,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn targets(names: &[&str]) -> Vec<Target> {
        names.iter().map(|n| Target::new(*n, format!(r"C:\Targets\{n}"))).collect()
    }

    fn match_names(targets: &[Target], file_name: &str) -> Vec<String> {
        NameMatcher::new(targets).match_name(file_name).iter().map(|c| targets[c.target].name.clone()).collect()
    }

    fn single(targets: &[Target], file_name: &str) -> (String, MatchConfidence) {
        let candidates = NameMatcher::new(targets).match_name(file_name);
        assert_eq!(candidates.len(), 1, "{file_name}: {candidates:?}");
        (targets[candidates[0].target].name.clone(), candidates[0].confidence)
    }

    #[test]
    fn separated_names_are_strong_matches() {
        let t = targets(&["Breaking Bad", "The Office"]);
        for file in [
            "breaking.bad.s01e01.720p.mkv",
            "Breaking Bad - S02E03.mkv",
            "[WEB] breaking_bad (2008).mp4",
            "Bad_Breaking.mkv",
            "Bad, Breaking.mp4",
        ] {
            assert_eq!(single(&t, file), ("Breaking Bad".into(), MatchConfidence::Strong), "{file}");
        }
    }

    #[test]
    fn fused_names_are_loose_matches() {
        let t = targets(&["Breaking Bad", "The Office"]);
        for file in ["BreakingBad.mkv", "breakingbad_s01e02.mkv", "webripBREAKINGBAD.mkv", "BadBreaking.mkv"] {
            assert_eq!(single(&t, file), ("Breaking Bad".into(), MatchConfidence::Loose), "{file}");
        }
    }

    #[test]
    fn single_word_name_matches_whole_token_only() {
        let t = targets(&["Friends"]);
        assert_eq!(match_names(&t, "friends_s01e01.mkv"), ["Friends"]);
        assert!(match_names(&t, "boyfriends.mkv").is_empty());
        assert!(match_names(&t, "superfriendsrecap.mkv").is_empty());
    }

    #[test]
    fn short_multi_word_name_does_not_match_loosely() {
        let t = targets(&["Ab Fab"]);
        assert_eq!(match_names(&t, "ab.fab.s01e01.mkv"), ["Ab Fab"]);
        assert!(match_names(&t, "slabfabrication.mkv").is_empty());
    }

    #[test]
    fn two_targets_both_returned() {
        let t = targets(&["Breaking Bad", "The Office", "Doctor Who"]);
        let names = match_names(&t, "Breaking Bad and The Office crossover.mkv");
        assert_eq!(names.len(), 2);
        assert!(names.contains(&"Breaking Bad".to_string()));
        assert!(names.contains(&"The Office".to_string()));
    }

    #[test]
    fn longer_name_shadows_contained_shorter_name() {
        let t = targets(&["Doctor Who", "Doctor Who Confidential"]);
        assert_eq!(match_names(&t, "doctor.who.confidential.s01e01.mkv"), ["Doctor Who Confidential"]);
        assert_eq!(match_names(&t, "doctorwhoconfidential.mkv"), ["Doctor Who Confidential"]);
        assert_eq!(match_names(&t, "doctor.who.s01e01.mkv"), ["Doctor Who"]);
    }

    #[test]
    fn strong_matches_rank_before_loose_matches() {
        let t = targets(&["Breaking Bad", "The Office"]);
        let candidates = NameMatcher::new(&t).match_name("theoffice with breaking.bad.mkv");
        let names: Vec<&str> = candidates.iter().map(|c| t[c.target].name.as_str()).collect();
        let confidences: Vec<MatchConfidence> = candidates.iter().map(|c| c.confidence).collect();
        assert_eq!(names, ["Breaking Bad", "The Office"]);
        assert_eq!(confidences, [MatchConfidence::Strong, MatchConfidence::Loose]);
    }

    #[test]
    fn extension_is_ignored() {
        assert!(match_names(&targets(&["Mp Four"]), "clip.mpfour").is_empty());
    }

    #[test]
    fn dotfile_name_is_all_extension() {
        // As in .NET, ".friends" has no name part, so nothing matches.
        assert!(match_names(&targets(&["Friends"]), ".friends").is_empty());
    }

    #[test]
    fn no_match_returns_empty() {
        assert!(match_names(&targets(&["Breaking Bad"]), "holiday_video.mp4").is_empty());
    }

    #[test]
    fn readme_examples() {
        let t = targets(&["Acme Corp", "Jane Doe", "Mary Sue"]);
        assert_eq!(single(&t, "jane.doe.invoice.2024.pdf"), ("Jane Doe".into(), MatchConfidence::Strong));
        assert_eq!(single(&t, "JaneDoe_contract.docx"), ("Jane Doe".into(), MatchConfidence::Loose));
        assert_eq!(single(&t, "Doe_Jane-headshot.jpg"), ("Jane Doe".into(), MatchConfidence::Strong));
        assert_eq!(single(&t, "acme-corp quarterly report.xlsx"), ("Acme Corp".into(), MatchConfidence::Strong));
        assert_eq!(match_names(&t, "meeting notes - Acme Corp & Jane Doe.txt"), ["Acme Corp", "Jane Doe"]);
        assert!(match_names(&t, "holiday.jpg").is_empty());
    }
}
