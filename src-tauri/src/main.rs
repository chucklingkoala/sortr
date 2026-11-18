// No console window in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod commands;
mod imaging;
mod shell;
mod theme;

use tauri::{Emitter, Manager, WebviewUrl, WebviewWindowBuilder, WindowEvent};

fn main() {
    // Sortr's UI runs in WebView2. It ships with Windows 11 and updated Windows 10, but say so plainly if it's missing.
    if tauri::webview_version().is_err() {
        shell::message_box(
            0,
            "error",
            "Sortr",
            "Sortr needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n\
             Download it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, then start Sortr again.",
        );
        return;
    }

    tauri::Builder::default()
        .manage(commands::ScanState::default())
        .invoke_handler(tauri::generate_handler![
            commands::get_settings,
            commands::folder_exists,
            commands::pick_folder,
            commands::message,
            commands::scan,
            commands::search_targets,
            commands::move_files,
            commands::latest_batch_size,
            commands::undo_latest,
            commands::open_file,
            commands::show_in_explorer,
            commands::preview_info,
            commands::read_text,
            commands::decode_image,
            commands::thumbnail,
            commands::accent_colors,
            commands::show_preview,
            commands::hide_preview,
        ])
        .on_window_event(|window, event| match (window.label(), event) {
            // The preview's X hides it so its size and position are kept; the main window decides what that means.
            ("preview", WindowEvent::CloseRequested { api, .. }) => {
                api.prevent_close();
                let _ = window.emit("preview:close-requested", ());
            }
            // The hidden preview window would otherwise keep the app running.
            ("main", WindowEvent::Destroyed) => window.app_handle().exit(0),
            _ => {}
        })
        .setup(|app| {
            let title = format!("Sortr {}", app.package_info().version);
            WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into()))
                .title(title)
                .inner_size(1180.0, 720.0)
                .min_inner_size(760.0, 420.0)
                .center()
                .data_directory(sortr_core::paths::webview_directory())
                .build()?;
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Sortr failed to start");
}
