use std::io;
use std::os::windows::fs::MetadataExt;
use std::path::Path;

use crate::normalize::cmp_ignore_case;
use crate::target::Target;

const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;
const FILE_ATTRIBUTE_SYSTEM: u32 = 0x4;
const FILE_ATTRIBUTE_DIRECTORY: u32 = 0x10;

/// Entries of `folder` with their attributes, skipping hidden and system ones.
fn visible_entries(folder: &Path) -> io::Result<Vec<(String, String, u32)>> {
    let mut entries = Vec::new();
    for entry in std::fs::read_dir(folder)? {
        let entry = entry?;
        // On Windows this comes from the directory listing itself, so it costs no extra disk access.
        let Ok(metadata) = entry.metadata() else { continue };
        let attributes = metadata.file_attributes();
        if attributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM) != 0 {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        let path = entry.path().to_string_lossy().into_owned();
        entries.push((name, path, attributes));
    }
    Ok(entries)
}

/// Full paths of the top-level files of `source_folder`, skipping hidden and system files, sorted ignoring case.
pub fn scan_files(source_folder: &Path) -> io::Result<Vec<String>> {
    let mut files: Vec<String> = visible_entries(source_folder)?
        .into_iter()
        .filter(|(_, _, attributes)| attributes & FILE_ATTRIBUTE_DIRECTORY == 0)
        .map(|(_, path, _)| path)
        .collect();
    files.sort_by(|a, b| cmp_ignore_case(a, b));
    Ok(files)
}

/// Every direct subfolder of `targets_folder` is one target, except hidden ones, ones starting with a dot, and ones
/// whose name has no letters or digits. Sorted by name, ignoring case.
pub fn load_targets(targets_folder: &Path) -> io::Result<Vec<Target>> {
    let mut targets: Vec<Target> = visible_entries(targets_folder)?
        .into_iter()
        .filter(|(name, _, attributes)| attributes & FILE_ATTRIBUTE_DIRECTORY != 0 && !name.starts_with('.'))
        .map(|(name, path, _)| Target::new(name, path))
        .filter(|t| !t.tokens.is_empty())
        .collect();
    targets.sort_by(|a, b| cmp_ignore_case(&a.name, &b.name));
    Ok(targets)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn scan_lists_top_level_files_sorted() {
        let dir = tempfile::tempdir().unwrap();
        fs::write(dir.path().join("b.txt"), "x").unwrap();
        fs::write(dir.path().join("A.txt"), "x").unwrap();
        fs::create_dir(dir.path().join("sub")).unwrap();
        fs::write(dir.path().join("sub").join("nested.txt"), "x").unwrap();

        let names: Vec<String> =
            scan_files(dir.path()).unwrap().iter().map(|p| crate::file_name(p).to_string()).collect();
        assert_eq!(names, ["A.txt", "b.txt"]);
    }

    #[test]
    fn load_targets_skips_dot_folders_and_folders_without_letters() {
        let dir = tempfile::tempdir().unwrap();
        for name in ["Jane Doe", "acme corp", ".git", "---"] {
            fs::create_dir(dir.path().join(name)).unwrap();
        }
        fs::write(dir.path().join("not a folder.txt"), "x").unwrap();

        let names: Vec<String> = load_targets(dir.path()).unwrap().into_iter().map(|t| t.name).collect();
        assert_eq!(names, ["acme corp", "Jane Doe"]);
    }
}
