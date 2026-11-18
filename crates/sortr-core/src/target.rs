use crate::normalize::tokenize;

/// One folder files can be sorted into. Its folder name is the name Sortr searches for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Target {
    pub name: String,
    pub folder_path: String,
    /// The name's normalized tokens, e.g. `["jane", "doe"]`.
    pub tokens: Vec<String>,
    /// The tokens run together, e.g. `"janedoe"`.
    pub compact_key: String,
    /// The tokens reversed and run together, e.g. `"doejane"`.
    pub reversed_compact_key: String,
    /// Length of `compact_key` in characters.
    pub compact_len: usize,
}

impl Target {
    pub fn new(name: impl Into<String>, folder_path: impl Into<String>) -> Self {
        let name = name.into();
        let tokens = tokenize(&name);
        let compact_key = tokens.concat();
        let reversed_compact_key = tokens.iter().rev().map(String::as_str).collect();
        let compact_len = compact_key.chars().count();
        Target { name, folder_path: folder_path.into(), tokens, compact_key, reversed_compact_key, compact_len }
    }
}
