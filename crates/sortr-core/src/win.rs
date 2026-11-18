//! The Windows calls the core needs and the standard library doesn't provide in the right form.

use std::ffi::OsStr;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use windows_sys::Win32::Storage::FileSystem::{MOVEFILE_COPY_ALLOWED, MoveFileExW};
use windows_sys::Win32::System::SystemInformation::GetLocalTime;

/// A NUL-terminated UTF-16 path. Long paths get the `\\?\` prefix so they work without the long-path manifest.
fn wide_path(path: &Path) -> Vec<u16> {
    let raw = path.as_os_str();
    let text = raw.to_string_lossy();
    let prefixed: std::borrow::Cow<OsStr> = if text.len() >= 248 && path.is_absolute() && !text.starts_with(r"\\?\") {
        match text.strip_prefix(r"\\") {
            Some(unc) => std::borrow::Cow::Owned(format!(r"\\?\UNC\{unc}").into()),
            None => std::borrow::Cow::Owned(format!(r"\\?\{text}").into()),
        }
    } else {
        std::borrow::Cow::Borrowed(raw)
    };
    prefixed.encode_wide().chain(Some(0)).collect()
}

/// Moves a file, copying it across drives when needed. Never replaces an existing file: if `to` exists the move
/// fails. (`std::fs::rename` would silently overwrite it and can't move between drives.)
pub fn move_file(from: &Path, to: &Path) -> io::Result<()> {
    let (from, to) = (wide_path(from), wide_path(to));
    let ok = unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), MOVEFILE_COPY_ALLOWED) };
    if ok == 0 { Err(io::Error::last_os_error()) } else { Ok(()) }
}

/// The local time as `yyyyMMdd-HHmmss-fff`, which sorts in time order.
pub fn local_timestamp() -> String {
    let t = unsafe {
        let mut t = std::mem::zeroed();
        GetLocalTime(&mut t);
        t
    };
    format!(
        "{:04}{:02}{:02}-{:02}{:02}{:02}-{:03}",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds
    )
}
