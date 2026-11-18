//! Native Windows pieces: folder picker, message boxes, opening files, Explorer and shell thumbnails.

use std::path::Path;

use windows::Win32::Foundation::{HWND, SIZE};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAP, BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, DeleteObject, GetDC, GetDIBits, GetObjectW, HBITMAP,
    HGDIOBJ, ReleaseDC,
};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE, CoCreateInstance, CoInitializeEx,
    CoTaskMemFree, CoUninitialize,
};
use windows::Win32::UI::Shell::{
    FOS_FORCEFILESYSTEM, FOS_PICKFOLDERS, FileOpenDialog, IFileOpenDialog, IShellItem, IShellItemImageFactory,
    SHCreateItemFromParsingName, SIGDN_FILESYSPATH, SIIGBF_RESIZETOFIT, ShellExecuteW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    IDOK, MB_ICONERROR, MB_ICONINFORMATION, MB_ICONQUESTION, MB_ICONWARNING, MB_OK, MB_OKCANCEL, MessageBoxW,
    SW_SHOWNORMAL,
};
use windows::core::{HSTRING, PCWSTR};

use crate::imaging::Pixels;

/// Runs `f` on a fresh single-threaded-apartment thread. Shell dialogs and thumbnail providers are COM objects that
/// expect one, and this keeps them off both the UI thread and the shared worker pool.
pub fn on_sta_thread<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> T {
    std::thread::spawn(move || {
        let initialized = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) }.is_ok();
        let result = f();
        if initialized {
            unsafe { CoUninitialize() };
        }
        result
    })
    .join()
    .expect("STA worker thread failed")
}

fn hwnd(owner: isize) -> Option<HWND> {
    (owner != 0).then_some(HWND(owner as *mut _))
}

/// Shows the folder picker. `None` when the user cancels.
pub fn pick_folder(owner: isize, title: &str, initial: Option<&str>) -> windows::core::Result<Option<String>> {
    unsafe {
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)?;
        dialog.SetOptions(dialog.GetOptions()? | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM)?;
        dialog.SetTitle(&HSTRING::from(title))?;
        if let Some(initial) = initial.filter(|p| !p.trim().is_empty() && Path::new(p).is_dir()) {
            if let Ok(item) = SHCreateItemFromParsingName::<_, _, IShellItem>(&HSTRING::from(initial), None) {
                let _ = dialog.SetFolder(&item);
            }
        }
        if dialog.Show(hwnd(owner)).is_err() {
            return Ok(None); // cancelled
        }
        let name = dialog.GetResult()?.GetDisplayName(SIGDN_FILESYSPATH)?;
        let path = name.to_string();
        CoTaskMemFree(Some(name.0 as *const _));
        Ok(Some(path.map_err(|_| windows::core::Error::from_thread())?))
    }
}

/// A standard Windows message box. Returns true when the user clicks OK.
pub fn message_box(owner: isize, kind: &str, title: &str, text: &str) -> bool {
    let style = match kind {
        "confirm" => MB_OKCANCEL | MB_ICONQUESTION,
        "warning" => MB_OK | MB_ICONWARNING,
        "info" => MB_OK | MB_ICONINFORMATION,
        _ => MB_OK | MB_ICONERROR,
    };
    unsafe { MessageBoxW(hwnd(owner), &HSTRING::from(text), &HSTRING::from(title), style) == IDOK }
}

/// Opens a file with its default app, as double-clicking it in Explorer would.
pub fn open_file(path: &str) -> Result<(), String> {
    if !Path::new(path).is_file() {
        return Ok(());
    }
    let result = unsafe { ShellExecuteW(None, PCWSTR::null(), &HSTRING::from(path), None, None, SW_SHOWNORMAL) };
    // ShellExecute reports success as a value above 32.
    if result.0 as isize > 32 { Ok(()) } else { Err(std::io::Error::last_os_error().to_string()) }
}

/// Opens Explorer with the file selected.
pub fn show_in_explorer(path: &str) {
    use std::os::windows::process::CommandExt;
    if Path::new(path).is_file() {
        let _ = std::process::Command::new("explorer.exe").raw_arg(format!("/select,\"{path}\"")).spawn();
    }
}

/// The thumbnail Explorer would show for a file (a video frame, a PDF's first page, …), or its file-type icon when it
/// has none, at most `size` pixels square. Call on an STA thread.
pub fn thumbnail(path: &str, size: i32) -> windows::core::Result<Pixels> {
    // The shell parser rejects forward slashes; normalise to a plain absolute Windows path.
    let path = std::path::absolute(path).map_err(|_| windows::core::Error::from_thread())?;
    unsafe {
        let factory: IShellItemImageFactory = SHCreateItemFromParsingName(&HSTRING::from(path.as_os_str()), None)?;
        let bitmap = factory.GetImage(SIZE { cx: size, cy: size }, SIIGBF_RESIZETOFIT)?;
        let pixels = bitmap_pixels(bitmap);
        let _ = DeleteObject(HGDIOBJ(bitmap.0));
        pixels
    }
}

/// Copies a 32-bit HBITMAP's pixels, keeping its alpha channel.
unsafe fn bitmap_pixels(bitmap: HBITMAP) -> windows::core::Result<Pixels> {
    let mut header = BITMAP::default();
    let got = unsafe {
        GetObjectW(HGDIOBJ(bitmap.0), size_of::<BITMAP>() as i32, Some(&mut header as *mut BITMAP as *mut _))
    };
    if got == 0 || header.bmWidth <= 0 || header.bmHeight <= 0 {
        return Err(windows::core::Error::from_thread());
    }
    let (width, height) = (header.bmWidth, header.bmHeight);
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height, // negative = top-down rows
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut bgra = vec![0u8; width as usize * height as usize * 4];
    let lines = unsafe {
        let dc = GetDC(None);
        let lines = GetDIBits(dc, bitmap, 0, height as u32, Some(bgra.as_mut_ptr() as *mut _), &mut info, DIB_RGB_COLORS);
        ReleaseDC(None, dc);
        lines
    };
    if lines == 0 {
        return Err(windows::core::Error::from_thread());
    }
    Ok(Pixels::from_premultiplied_bgra(width as u32, height as u32, bgra))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn thumbnail_of_a_real_file_has_pixels() {
        let icon = concat!(env!("CARGO_MANIFEST_DIR"), "/icons/icon-256.png");
        let pixels = on_sta_thread(move || thumbnail(icon, 64)).expect("thumbnail");
        assert!(pixels.width > 0 && pixels.width <= 64 && pixels.height <= 64);
        assert_eq!(pixels.rgba.len(), (pixels.width * pixels.height * 4) as usize);
    }

    #[test]
    fn thumbnail_of_a_missing_file_fails() {
        assert!(on_sta_thread(|| thumbnail(r"C:\definitely\not\here.png", 64)).is_err());
    }
}
