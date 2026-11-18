// No console window in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::path::PathBuf;

use tauri::{WebviewUrl, WebviewWindowBuilder};

fn main() {
    // Sortr's UI runs in WebView2. It ships with Windows 11 and updated Windows 10, but say so plainly if it's missing.
    if tauri::webview_version().is_err() {
        show_webview2_missing();
        return;
    }

    tauri::Builder::default()
        .setup(|app| {
            let title = format!("Sortr {}", app.package_info().version);
            WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into()))
                .title(title)
                .inner_size(1180.0, 720.0)
                .min_inner_size(760.0, 420.0)
                .center()
                .data_directory(webview_data_dir())
                .build()?;
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Sortr failed to start");
}

/// Keeps WebView2's cache next to Sortr's own data in %LocalAppData%\Sortr.
fn webview_data_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    base.join("Sortr").join("WebView2")
}

fn show_webview2_missing() {
    use windows_sys::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};

    let text = "Sortr needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n\
                Download it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, then start Sortr again.";
    let wide = |s: &str| s.encode_utf16().chain(Some(0)).collect::<Vec<u16>>();
    let (text, caption) = (wide(text), wide("Sortr"));
    unsafe {
        MessageBoxW(std::ptr::null_mut(), text.as_ptr(), caption.as_ptr(), MB_OK | MB_ICONERROR);
    }
}
