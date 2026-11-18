//! Where Sortr keeps its data: `%LocalAppData%\Sortr`, the same folder Sortr 1.x used.

use std::path::PathBuf;

pub fn data_directory() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    base.join("Sortr")
}

pub fn undo_directory() -> PathBuf {
    data_directory().join("undo")
}

pub fn settings_file() -> PathBuf {
    data_directory().join("settings.json")
}

/// WebView2's profile and cache.
pub fn webview_directory() -> PathBuf {
    data_directory().join("WebView2")
}
