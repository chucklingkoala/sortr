//! Moves a file from the system temp folder to the build's temp folder. When those are on different drives (as on a
//! machine with the repo on D:), this proves moves work across volumes, which a plain rename can't do. On a single
//! drive it is an ordinary move.

use std::fs;
use std::path::Path;

use sortr_core::{MoveRequest, UndoLog, move_files};

#[test]
fn moves_and_undoes_between_temp_and_target_dirs() {
    let source_dir = tempfile::tempdir().unwrap();
    let target_dir = tempfile::tempdir_in(env!("CARGO_TARGET_TMPDIR")).unwrap();
    let undo_log = UndoLog::new(target_dir.path().join("undo"));

    let source = source_dir.path().join("Jane Doe invoice.pdf");
    fs::write(&source, "pdf").unwrap();
    let destination_folder = target_dir.path().join("Jane Doe");

    let results = move_files(
        &undo_log,
        &[MoveRequest {
            source_path: source.to_string_lossy().into_owned(),
            destination_folder: destination_folder.to_string_lossy().into_owned(),
        }],
        |_| {},
    )
    .unwrap();

    assert!(results[0].succeeded(), "{:?}", results[0].error);
    assert!(!source.exists());
    let moved = Path::new(results[0].final_path.as_deref().unwrap());
    assert_eq!(fs::read_to_string(moved).unwrap(), "pdf");

    let undo = undo_log.undo_latest().unwrap();
    assert_eq!(undo.restored, 1, "{:?}", undo.errors);
    assert_eq!(fs::read_to_string(&source).unwrap(), "pdf");
}
