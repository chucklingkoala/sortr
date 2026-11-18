//! Sortr's core: reads file names, works out which target folder each one mentions, moves files and undoes moves.
//! No UI code lives here.

pub mod matcher;
pub mod mover;
pub mod normalize;
pub mod paths;
pub mod preview;
pub mod scan;
pub mod search;
pub mod settings;
pub mod target;
pub mod undo;
mod win;

pub use matcher::{MatchCandidate, MatchConfidence, NameMatcher};
pub use mover::{MoveRequest, MoveResult, get_available_path, move_files};
pub use preview::{PreviewKind, PreviewText};
pub use settings::{AppSettings, SettingsStore};
pub use target::Target;
pub use undo::{MoveRecord, UndoLog, UndoResult};

/// Splits a file name into stem and extension the way .NET's `Path.GetExtension` does: the extension starts at the
/// last dot and includes it, so `.gitignore` is all extension, and a trailing dot (`file.`) is no extension.
pub fn split_extension(file_name: &str) -> (&str, &str) {
    match file_name.rfind('.') {
        Some(i) if i == file_name.len() - 1 => (&file_name[..i], ""),
        Some(i) => (&file_name[..i], &file_name[i..]),
        None => (file_name, ""),
    }
}

/// The last component of a path (after the final `\` or `/`).
pub fn file_name(path: &str) -> &str {
    path.rsplit(['\\', '/']).next().unwrap_or(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn split_extension_matches_dotnet() {
        assert_eq!(split_extension("clip.mp4"), ("clip", ".mp4"));
        assert_eq!(split_extension("archive.tar.gz"), ("archive.tar", ".gz"));
        assert_eq!(split_extension(".gitignore"), ("", ".gitignore"));
        assert_eq!(split_extension("README"), ("README", ""));
        assert_eq!(split_extension("file."), ("file", ""));
    }

    #[test]
    fn file_name_takes_last_component() {
        assert_eq!(file_name(r"C:\a\b\c.txt"), "c.txt");
        assert_eq!(file_name("c.txt"), "c.txt");
        assert_eq!(file_name("C:/a/c.txt"), "c.txt");
    }
}
