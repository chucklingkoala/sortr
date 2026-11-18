//! Stores each move batch as a JSON-lines file so the most recent batch can be reverted. Entries are appended as each
//! move succeeds, so the log stays accurate after a crash. The format matches Sortr 1.x, so either version can undo a
//! batch the other moved.

use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::win;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct MoveRecord {
    pub from: String,
    pub to: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct UndoResult {
    pub restored: usize,
    pub errors: Vec<String>,
}

pub struct UndoLog {
    directory: PathBuf,
}

impl UndoLog {
    pub fn new(directory: impl Into<PathBuf>) -> Self {
        UndoLog { directory: directory.into() }
    }

    pub fn begin_batch(&self) -> io::Result<UndoBatch> {
        fs::create_dir_all(&self.directory)?;
        let path = self.directory.join(format!("batch-{}.jsonl", win::local_timestamp()));
        Ok(UndoBatch { path })
    }

    /// The newest batch file, if any.
    pub fn latest_batch_path(&self) -> Option<PathBuf> {
        fs::read_dir(&self.directory)
            .ok()?
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|name| name.starts_with("batch-") && name.ends_with(".jsonl"))
            .max()
            .map(|name| self.directory.join(name))
    }

    pub fn read_latest(&self) -> io::Result<Vec<MoveRecord>> {
        match self.latest_batch_path() {
            Some(path) => read_batch(&path),
            None => Ok(Vec::new()),
        }
    }

    /// Moves every file in the latest batch back where it came from. Anything that can't be restored stays in the
    /// log so the user can retry after fixing it.
    pub fn undo_latest(&self) -> io::Result<UndoResult> {
        let Some(path) = self.latest_batch_path() else {
            return Ok(UndoResult::default());
        };

        let records = read_batch(&path)?;
        let mut failed = Vec::new();
        let mut result = UndoResult::default();

        for record in records.iter().rev() {
            match restore(record) {
                Ok(()) => result.restored += 1,
                Err(message) => {
                    failed.push(record);
                    result.errors.push(format!("{}: {message}", crate::file_name(&record.to)));
                }
            }
        }

        if failed.is_empty() {
            fs::remove_file(&path)?;
        } else {
            failed.reverse();
            let mut text = String::new();
            for record in failed {
                text.push_str(&serde_json::to_string(record).map_err(io::Error::other)?);
                text.push_str("\r\n");
            }
            fs::write(&path, text)?;
        }
        Ok(result)
    }
}

fn restore(record: &MoveRecord) -> Result<(), String> {
    let (from, to) = (Path::new(&record.from), Path::new(&record.to));
    if !to.is_file() {
        return Err(format!("'{}' no longer exists.", record.to));
    }
    if from.symlink_metadata().is_ok() {
        return Err(format!("'{}' already exists.", record.from));
    }
    if let Some(parent) = from.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    win::move_file(to, from).map_err(|e| e.to_string())
}

fn read_batch(path: &Path) -> io::Result<Vec<MoveRecord>> {
    fs::read_to_string(path)?
        .lines()
        .filter(|line| !line.trim().is_empty())
        .map(|line| serde_json::from_str(line).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e)))
        .collect()
}

pub struct UndoBatch {
    path: PathBuf,
}

impl UndoBatch {
    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn append(&self, record: &MoveRecord) -> io::Result<()> {
        let mut line = serde_json::to_string(record).map_err(io::Error::other)?;
        line.push_str("\r\n");
        OpenOptions::new().create(true).append(true).open(&self.path)?.write_all(line.as_bytes())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_sortr_1_batches() {
        // .NET's serializer escapes non-ASCII characters; both forms must read the same.
        let dir = tempfile::tempdir().unwrap();
        fs::write(
            dir.path().join("batch-20250101-120000-000.jsonl"),
            "{\"From\":\"C:\\\\in\\\\caf\\u00E9.txt\",\"To\":\"C:\\\\out\\\\caf\\u00E9.txt\"}\r\n\r\n",
        )
        .unwrap();
        let records = UndoLog::new(dir.path()).read_latest().unwrap();
        assert_eq!(records, [MoveRecord { from: r"C:\in\café.txt".into(), to: r"C:\out\café.txt".into() }]);
    }

    #[test]
    fn latest_batch_is_the_newest_name() {
        let dir = tempfile::tempdir().unwrap();
        for name in ["batch-20250101-120000-000.jsonl", "batch-20250102-080000-000.jsonl", "notes.txt"] {
            fs::write(dir.path().join(name), "").unwrap();
        }
        let latest = UndoLog::new(dir.path()).latest_batch_path().unwrap();
        assert_eq!(latest.file_name().unwrap(), "batch-20250102-080000-000.jsonl");
    }

    #[test]
    fn missing_directory_has_no_batches() {
        let dir = tempfile::tempdir().unwrap();
        let log = UndoLog::new(dir.path().join("undo"));
        assert!(log.latest_batch_path().is_none());
        assert!(log.read_latest().unwrap().is_empty());
    }
}
