use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::undo::{MoveRecord, UndoBatch, UndoLog};
use crate::win;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MoveRequest {
    pub source_path: String,
    pub destination_folder: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MoveResult {
    /// Where the file ended up, when the move succeeded.
    pub final_path: Option<String>,
    /// Why the move failed, when it did.
    pub error: Option<String>,
}

impl MoveResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_none()
    }
}

/// Moves each file into its destination folder as one undo batch, logging each move as it happens. A failed move
/// doesn't stop the rest. `progress` is called with the number of files handled so far.
pub fn move_files(
    undo_log: &UndoLog,
    requests: &[MoveRequest],
    mut progress: impl FnMut(usize),
) -> io::Result<Vec<MoveResult>> {
    let batch = undo_log.begin_batch()?;
    let mut results = Vec::with_capacity(requests.len());
    for (i, request) in requests.iter().enumerate() {
        results.push(match move_one(request, &batch) {
            Ok(final_path) => MoveResult { final_path: Some(final_path), error: None },
            Err(e) => MoveResult { final_path: None, error: Some(e.to_string()) },
        });
        progress(i + 1);
    }
    Ok(results)
}

fn move_one(request: &MoveRequest, batch: &UndoBatch) -> io::Result<String> {
    let source = Path::new(&request.source_path);
    if !source.is_file() {
        return Err(io::Error::new(io::ErrorKind::NotFound, format!("'{}' no longer exists.", request.source_path)));
    }
    std::fs::create_dir_all(&request.destination_folder)?;
    let destination = get_available_path(Path::new(&request.destination_folder), crate::file_name(&request.source_path));
    win::move_file(source, &destination)?;
    let destination = destination.to_string_lossy().into_owned();
    batch.append(&MoveRecord { from: request.source_path.clone(), to: destination.clone() })?;
    Ok(destination)
}

/// `folder\name.ext`, or `folder\name (n).ext` with the smallest free n if that already exists.
pub fn get_available_path(folder: &Path, file_name: &str) -> PathBuf {
    let path = folder.join(file_name);
    if path.symlink_metadata().is_err() {
        return path;
    }
    let (stem, extension) = crate::split_extension(file_name);
    (1..)
        .map(|n| folder.join(format!("{stem} ({n}){extension}")))
        .find(|p| p.symlink_metadata().is_err())
        .expect("ran out of numbers")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::UndoResult;
    use std::fs;

    struct Fixture {
        _root: tempfile::TempDir,
        source: PathBuf,
        target: PathBuf,
        undo_log: UndoLog,
    }

    impl Fixture {
        fn new() -> Self {
            let root = tempfile::tempdir().unwrap();
            let source = root.path().join("source");
            let target = root.path().join("targets").join("Breaking Bad");
            fs::create_dir_all(&source).unwrap();
            fs::create_dir_all(&target).unwrap();
            let undo_log = UndoLog::new(root.path().join("undo"));
            Fixture { _root: root, source, target, undo_log }
        }

        fn source_file(&self, name: &str, content: &str) -> String {
            let path = self.source.join(name);
            fs::write(&path, content).unwrap();
            path.to_string_lossy().into_owned()
        }

        fn request(&self, source_path: &str) -> MoveRequest {
            MoveRequest {
                source_path: source_path.to_string(),
                destination_folder: self.target.to_string_lossy().into_owned(),
            }
        }

        fn move_all(&self, requests: &[MoveRequest]) -> Vec<MoveResult> {
            move_files(&self.undo_log, requests, |_| {}).unwrap()
        }
    }

    #[test]
    fn moves_files_and_records_undo() {
        let f = Fixture::new();
        let a = f.source_file("breaking.bad.s01e01.mkv", "x");
        let b = f.source_file("breaking.bad.s01e02.mkv", "x");
        let mut progress = Vec::new();

        let results = move_files(&f.undo_log, &[f.request(&a), f.request(&b)], |n| progress.push(n)).unwrap();

        assert!(results.iter().all(MoveResult::succeeded));
        assert!(!Path::new(&a).exists());
        assert!(f.target.join("breaking.bad.s01e01.mkv").exists());
        assert_eq!(progress, [1, 2]);
        assert_eq!(f.undo_log.read_latest().unwrap().len(), 2);
    }

    #[test]
    fn collision_renames_instead_of_overwriting() {
        let f = Fixture::new();
        fs::write(f.target.join("clip.mp4"), "existing").unwrap();
        fs::write(f.target.join("clip (1).mp4"), "existing").unwrap();
        let src = f.source_file("clip.mp4", "new");

        let results = f.move_all(&[f.request(&src)]);

        let expected = f.target.join("clip (2).mp4");
        assert_eq!(results[0].final_path.as_deref(), Some(expected.to_string_lossy().as_ref()));
        assert_eq!(fs::read_to_string(f.target.join("clip.mp4")).unwrap(), "existing");
        assert_eq!(fs::read_to_string(expected).unwrap(), "new");
    }

    #[test]
    fn move_file_never_replaces_an_existing_file() {
        let f = Fixture::new();
        let src = f.source_file("a.txt", "new");
        let dest = f.target.join("a.txt");
        fs::write(&dest, "existing").unwrap();

        assert!(win::move_file(Path::new(&src), &dest).is_err());
        assert_eq!(fs::read_to_string(&dest).unwrap(), "existing");
        assert!(Path::new(&src).exists());
    }

    #[test]
    fn file_open_for_reading_can_still_be_moved() {
        // The preview reads files with std's default sharing (read, write and delete), which must not block a move.
        let f = Fixture::new();
        let src = f.source_file("open.mp4", "x");
        let _open = fs::File::open(&src).unwrap();

        let results = f.move_all(&[f.request(&src)]);
        assert!(results[0].succeeded(), "{:?}", results[0].error);
    }

    #[test]
    fn missing_source_reports_error_and_continues() {
        let f = Fixture::new();
        let good = f.source_file("good.mp4", "x");
        let missing = f.source.join("missing.mp4").to_string_lossy().into_owned();

        let results = f.move_all(&[f.request(&missing), f.request(&good)]);

        assert!(!results[0].succeeded());
        assert!(results[1].succeeded());
        assert_eq!(f.undo_log.read_latest().unwrap().len(), 1);
    }

    #[test]
    fn undo_latest_restores_files_and_deletes_batch() {
        let f = Fixture::new();
        let a = f.source_file("a.mp4", "x");
        let b = f.source_file("b.mp4", "x");
        f.move_all(&[f.request(&a), f.request(&b)]);

        let result = f.undo_log.undo_latest().unwrap();

        assert_eq!(result.restored, 2);
        assert!(result.errors.is_empty());
        assert!(Path::new(&a).exists());
        assert!(Path::new(&b).exists());
        assert!(f.undo_log.latest_batch_path().is_none());
    }

    #[test]
    fn undo_latest_keeps_entries_that_could_not_be_restored() {
        let f = Fixture::new();
        let a = f.source_file("a.mp4", "x");
        let b = f.source_file("b.mp4", "x");
        f.move_all(&[f.request(&a), f.request(&b)]);
        fs::write(&b, "someone put a new file here").unwrap();

        let result = f.undo_log.undo_latest().unwrap();

        assert_eq!(result.restored, 1);
        assert_eq!(result.errors.len(), 1);
        let remaining = f.undo_log.read_latest().unwrap();
        assert_eq!(remaining.len(), 1);
        assert_eq!(remaining[0].from, b);
        assert_eq!(fs::read_to_string(&b).unwrap(), "someone put a new file here");
    }

    #[test]
    fn undo_latest_with_no_batches_does_nothing() {
        let f = Fixture::new();
        assert_eq!(f.undo_log.undo_latest().unwrap(), UndoResult::default());
    }

    #[test]
    fn available_path_numbers_dotfiles_like_dotnet() {
        let dir = tempfile::tempdir().unwrap();
        fs::write(dir.path().join(".env"), "").unwrap();
        assert_eq!(get_available_path(dir.path(), ".env"), dir.path().join(" (1).env"));
    }
}
