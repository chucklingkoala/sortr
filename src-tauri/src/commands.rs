//! Everything the page can ask the Rust side to do. Slow or blocking work runs off the UI thread.

use std::path::Path;
use std::sync::Mutex;
use std::time::UNIX_EPOCH;

use serde::Serialize;
use sortr_core::{
    AppSettings, MatchConfidence, MoveRequest, MoveResult, NameMatcher, PreviewKind, PreviewText, SettingsStore, Target,
    UndoLog, UndoResult, paths, preview, scan, search,
};
use tauri::ipc::{Channel, Response};
use tauri::{AppHandle, Manager, PhysicalPosition, State, WebviewUrl, WebviewWindow, WebviewWindowBuilder};
use windows::Win32::UI::WindowsAndMessaging::{SW_HIDE, SW_SHOWNOACTIVATE, ShowWindow};

use crate::{imaging, shell, theme};

/// The targets from the last scan, which the type-to-search picker searches.
#[derive(Default)]
pub struct ScanState {
    targets: Mutex<Vec<Target>>,
}

type CommandResult<T> = Result<T, String>;

/// Runs blocking work on the worker pool.
async fn blocking<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> CommandResult<T> {
    tauri::async_runtime::spawn_blocking(f).await.map_err(|e| e.to_string())
}

/// Runs work that needs COM on its own STA thread.
async fn sta<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> CommandResult<T> {
    blocking(move || shell::on_sta_thread(f)).await
}

fn owner(window: &WebviewWindow) -> isize {
    window.hwnd().map(|h| h.0 as isize).unwrap_or(0)
}

fn undo_log() -> UndoLog {
    UndoLog::new(paths::undo_directory())
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    source_folder: String,
    targets_folder: String,
}

#[tauri::command]
pub async fn get_settings() -> CommandResult<Settings> {
    let saved = SettingsStore::new(paths::settings_file()).load();
    Ok(Settings {
        source_folder: saved.source_folder.unwrap_or_default(),
        targets_folder: saved.targets_folder.unwrap_or_default(),
    })
}

#[tauri::command]
pub async fn folder_exists(path: String) -> CommandResult<bool> {
    Ok(!path.trim().is_empty() && Path::new(&path).is_dir())
}

#[tauri::command]
pub async fn pick_folder(window: WebviewWindow, title: String, initial: Option<String>) -> CommandResult<Option<String>> {
    let owner = owner(&window);
    sta(move || shell::pick_folder(owner, &title, initial.as_deref())).await?.map_err(|e| e.message())
}

/// Shows a native message box: `kind` is "confirm", "error", "warning" or "info". Returns true for OK.
#[tauri::command]
pub async fn message(window: WebviewWindow, kind: String, title: String, text: String) -> CommandResult<bool> {
    let owner = owner(&window);
    blocking(move || shell::message_box(owner, &kind, &title, &text)).await
}

#[derive(Serialize)]
pub struct TargetInfo {
    name: String,
    folder: String,
}

#[derive(Serialize)]
pub struct Candidate {
    target: usize,
    confidence: MatchConfidence,
}

#[derive(Serialize)]
pub struct Row {
    path: String,
    name: String,
    candidates: Vec<Candidate>,
}

#[derive(Serialize)]
pub struct ScanResult {
    targets: Vec<TargetInfo>,
    rows: Vec<Row>,
}

/// Saves the folders, loads the targets and matches every file. Candidates are best-first, as indices into `targets`.
#[tauri::command]
pub async fn scan(state: State<'_, ScanState>, source: String, targets: String) -> CommandResult<ScanResult> {
    let settings = AppSettings { source_folder: Some(source.clone()), targets_folder: Some(targets.clone()) };
    SettingsStore::new(paths::settings_file()).save(&settings).map_err(|e| e.to_string())?;

    let (loaded, rows) = blocking(move || -> std::io::Result<(Vec<Target>, Vec<Row>)> {
        let loaded = scan::load_targets(Path::new(&targets))?;
        let matcher = NameMatcher::new(&loaded);
        let rows = scan::scan_files(Path::new(&source))?
            .into_iter()
            .map(|path| {
                let name = sortr_core::file_name(&path).to_string();
                let candidates = matcher
                    .match_name(&name)
                    .into_iter()
                    .map(|c| Candidate { target: c.target, confidence: c.confidence })
                    .collect();
                Row { path, name, candidates }
            })
            .collect();
        Ok((loaded, rows))
    })
    .await?
    .map_err(|e| e.to_string())?;

    let infos = loaded.iter().map(|t| TargetInfo { name: t.name.clone(), folder: t.folder_path.clone() }).collect();
    *state.targets.lock().unwrap() = loaded;
    Ok(ScanResult { targets: infos, rows })
}

/// Indices of the last scan's targets matching `query`, best first.
#[tauri::command]
pub async fn search_targets(state: State<'_, ScanState>, query: String) -> CommandResult<Vec<usize>> {
    Ok(search::filter(&state.targets.lock().unwrap(), Some(&query)))
}

/// Moves files as one undo batch, reporting the number handled so far through `progress`.
#[tauri::command]
pub async fn move_files(requests: Vec<MoveRequest>, progress: Channel<usize>) -> CommandResult<Vec<MoveResult>> {
    blocking(move || sortr_core::move_files(&undo_log(), &requests, |n| _ = progress.send(n)))
        .await?
        .map_err(|e| e.to_string())
}

/// How many moves the latest undo batch holds, or `None` when there is nothing to undo.
#[tauri::command]
pub async fn latest_batch_size() -> CommandResult<Option<usize>> {
    blocking(|| {
        let log = undo_log();
        match log.latest_batch_path() {
            None => Ok(None),
            Some(_) => log.read_latest().map(|records| Some(records.len())).map_err(|e| e.to_string()),
        }
    })
    .await?
}

#[tauri::command]
pub async fn undo_latest() -> CommandResult<UndoResult> {
    blocking(|| undo_log().undo_latest()).await?.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn open_file(path: String) -> CommandResult<()> {
    blocking(move || shell::open_file(&path)).await?
}

#[tauri::command]
pub async fn show_in_explorer(path: String) -> CommandResult<()> {
    blocking(move || shell::show_in_explorer(&path)).await
}

#[derive(Serialize)]
pub struct FileInfo {
    exists: bool,
    size: u64,
    /// Milliseconds since 1970, for the page to format in local time.
    modified: u64,
    kind: PreviewKind,
}

#[tauri::command]
pub async fn preview_info(path: String) -> CommandResult<FileInfo> {
    blocking(move || {
        let path = Path::new(&path);
        match std::fs::metadata(path) {
            Ok(meta) if meta.is_file() => FileInfo {
                exists: true,
                size: meta.len(),
                modified: meta
                    .modified()
                    .ok()
                    .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                    .map_or(0, |d| d.as_millis() as u64),
                kind: preview::classify(path),
            },
            _ => FileInfo { exists: false, size: 0, modified: 0, kind: PreviewKind::Other },
        }
    })
    .await
}

#[tauri::command]
pub async fn read_text(path: String) -> CommandResult<PreviewText> {
    blocking(move || preview::read_text(Path::new(&path), preview::DEFAULT_MAX_BYTES)).await?.map_err(|e| e.to_string())
}

/// Decodes an image with Windows' codecs. Returns the wire format described in [`imaging::Pixels::into_bytes`].
#[tauri::command]
pub async fn decode_image(path: String, max_side: u32) -> CommandResult<Response> {
    let pixels = sta(move || imaging::decode(&path, max_side)).await?.map_err(|e| e.message())?;
    Ok(Response::new(pixels.into_bytes()))
}

/// Explorer's thumbnail (or icon) for a file, in the same wire format as [`decode_image`].
#[tauri::command]
pub async fn thumbnail(path: String, size: i32) -> CommandResult<Response> {
    let pixels = sta(move || shell::thumbnail(&path, size)).await?.map_err(|e| e.message())?;
    Ok(Response::new(pixels.into_bytes()))
}

#[tauri::command]
pub async fn accent_colors() -> CommandResult<theme::AccentColors> {
    sta(theme::accent_colors).await?.map_err(|e| e.message())
}

/// Shows the preview window without taking focus from the main window, creating it on first use.
#[tauri::command]
pub async fn show_preview(app: AppHandle) -> CommandResult<()> {
    let preview = match app.get_webview_window("preview") {
        Some(window) => window,
        None => create_preview(&app).map_err(|e| e.to_string())?,
    };
    // Showing without activating keeps the keyboard in the file list, so ↑/↓ step through files.
    if let Ok(hwnd) = preview.hwnd() {
        unsafe {
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }
    }
    Ok(())
}

fn create_preview(app: &AppHandle) -> tauri::Result<WebviewWindow> {
    let main = app.get_webview_window("main").ok_or(tauri::Error::WindowNotFound)?;
    let preview = WebviewWindowBuilder::new(app, "preview", WebviewUrl::App("preview.html".into()))
        .title("Preview")
        .inner_size(760.0, 600.0)
        .min_inner_size(360.0, 300.0)
        .skip_taskbar(true)
        .focused(false)
        .visible(false)
        .owner(&main)?
        .data_directory(paths::webview_directory())
        .build()?;
    // Centre it over the main window the first time; after that it keeps wherever the user puts it.
    if let (Ok(pos), Ok(size), Ok(own)) = (main.outer_position(), main.outer_size(), preview.outer_size()) {
        let x = pos.x + (size.width as i32 - own.width as i32) / 2;
        let y = pos.y + (size.height as i32 - own.height as i32) / 2;
        let _ = preview.set_position(PhysicalPosition::new(x, y));
    }
    Ok(preview)
}

/// Hides the preview window (keeping it for next time) and gives the main window the keyboard back.
#[tauri::command]
pub async fn hide_preview(app: AppHandle) -> CommandResult<()> {
    // Shown with a raw ShowWindow (see show_preview), so hidden the same way: Tauri's hide() would think it's
    // already hidden and do nothing.
    if let Some(Ok(hwnd)) = app.get_webview_window("preview").map(|w| w.hwnd()) {
        unsafe {
            let _ = ShowWindow(hwnd, SW_HIDE);
        }
    }
    if let Some(main) = app.get_webview_window("main") {
        let _ = main.set_focus();
    }
    Ok(())
}
