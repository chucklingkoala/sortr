//! Decides how the preview window shows a file, and reads the start of text files.

use std::fs::File;
use std::io::{self, Read};
use std::path::Path;

use serde::Serialize;

/// How the preview window should show a file.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum PreviewKind {
    Image,
    Video,
    Audio,
    Text,
    Other,
}

/// How many leading bytes [`classify`] reads to sniff unknown types.
pub const SNIFF_LENGTH: u64 = 8 * 1024;

/// How much of a text file the preview shows.
pub const DEFAULT_MAX_BYTES: u64 = 256 * 1024;

const IMAGE: &[&str] = &[
    ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif",
    ".jxr", ".wdp",
];

const VIDEO: &[&str] =
    &[".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".3g2", ".m2ts", ".asf"];

const AUDIO: &[&str] = &[".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif"];

const TEXT: &[&str] = &[
    ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml",
    ".ini", ".cfg", ".conf", ".config", ".env", ".properties", ".html", ".htm", ".css", ".scss", ".js", ".mjs", ".tsx",
    ".jsx", ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp",
    ".hpp", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".sql", ".srt", ".vtt", ".nfo", ".rtf", ".tex", ".gitignore",
    ".gitattributes", ".editorconfig",
];

/// Binary formats that may contain no NUL bytes in their first few KB but are never readable as text.
const KNOWN_BINARY: &[&str] = &[
    ".pdf", ".zip", ".7z", ".rar", ".gz", ".tar", ".exe", ".dll", ".msi", ".doc", ".docx", ".xls", ".xlsx", ".ppt",
    ".pptx", ".odt", ".ods", ".odp", ".psd", ".ai", ".eps", ".iso", ".bin", ".dat",
];

/// Classifies by extension. Unknown extensions are sniffed: a file whose start contains no NUL bytes is treated as
/// text. Files that can't be read are [`PreviewKind::Other`].
pub fn classify(path: &Path) -> PreviewKind {
    if let Some(kind) = classify_by_extension(path) {
        return kind;
    }
    let mut start = Vec::new();
    match File::open(path).and_then(|f| f.take(SNIFF_LENGTH).read_to_end(&mut start)) {
        Ok(read) if read > 0 && looks_like_text(&start) => PreviewKind::Text,
        _ => PreviewKind::Other,
    }
}

/// The kind implied by the extension alone, or `None` when the extension is unknown.
pub fn classify_by_extension(path: &Path) -> Option<PreviewKind> {
    // ".ts" is deliberately absent: it is both TypeScript and MPEG transport stream, so sniffing decides.
    let name = path.file_name()?.to_string_lossy();
    let (_, extension) = crate::split_extension(&name);
    if extension.is_empty() {
        return None;
    }
    let extension = extension.to_ascii_lowercase();
    let is = |list: &[&str]| list.contains(&extension.as_str());
    if is(IMAGE) {
        Some(PreviewKind::Image)
    } else if is(VIDEO) {
        Some(PreviewKind::Video)
    } else if is(AUDIO) {
        Some(PreviewKind::Audio)
    } else if is(TEXT) {
        Some(PreviewKind::Text)
    } else if is(KNOWN_BINARY) {
        Some(PreviewKind::Other)
    } else {
        None
    }
}

/// True when the bytes look like text: no NUL bytes, except that UTF-16 with a byte order mark (which is full of
/// NULs) is always accepted.
pub fn looks_like_text(start: &[u8]) -> bool {
    start.starts_with(&[0xFF, 0xFE]) || start.starts_with(&[0xFE, 0xFF]) || !start.contains(&0)
}

/// The start of a text file, decoded for display.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PreviewText {
    pub text: String,
    /// True when the file is longer than what was read.
    pub truncated: bool,
}

/// Reads up to `max_bytes` and decodes them. Opening with std's default sharing means a move started meanwhile isn't
/// blocked.
pub fn read_text(path: &Path, max_bytes: u64) -> io::Result<PreviewText> {
    let file = File::open(path)?;
    let length = file.metadata()?.len();
    let mut bytes = Vec::new();
    file.take(max_bytes).read_to_end(&mut bytes)?;
    let truncated = length > bytes.len() as u64;
    Ok(PreviewText { text: decode(&bytes, truncated), truncated })
}

/// Decodes file bytes. A byte order mark picks UTF-8 or UTF-16; otherwise the bytes are decoded as UTF-8, falling
/// back to Latin-1 for legacy single-byte files. `truncated` tolerates a character cut in half at the end.
pub fn decode(bytes: &[u8], truncated: bool) -> String {
    if let Some(rest) = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8_lossy(rest).into_owned();
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE]) {
        return decode_utf16(rest, u16::from_le_bytes);
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFE, 0xFF]) {
        return decode_utf16(rest, u16::from_be_bytes);
    }
    match std::str::from_utf8(bytes) {
        Ok(text) => text.to_string(),
        // A multi-byte character cut off by the read limit isn't a sign of a non-UTF-8 file.
        Err(e) if truncated && e.error_len().is_none() => {
            String::from_utf8(bytes[..e.valid_up_to()].to_vec()).expect("validated prefix")
        }
        Err(_) => bytes.iter().map(|&b| b as char).collect(),
    }
}

fn decode_utf16(bytes: &[u8], unit: fn([u8; 2]) -> u16) -> String {
    let units: Vec<u16> = bytes.chunks_exact(2).map(|pair| unit([pair[0], pair[1]])).collect();
    String::from_utf16_lossy(&units)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn utf16le(text: &str) -> Vec<u8> {
        text.encode_utf16().flat_map(u16::to_le_bytes).collect()
    }

    #[test]
    fn known_extensions_are_classified_without_reading_the_file() {
        // The files don't exist, so a correct answer proves the extension alone decided it.
        let dir = tempfile::tempdir().unwrap();
        for (name, expected) in [
            ("photo.jpg", PreviewKind::Image),
            ("PHOTO.JPG", PreviewKind::Image),
            ("scan.TiFf", PreviewKind::Image),
            ("clip.mp4", PreviewKind::Video),
            ("clip.MKV", PreviewKind::Video),
            ("song.mp3", PreviewKind::Audio),
            ("song.flac", PreviewKind::Audio),
            ("notes.txt", PreviewKind::Text),
            ("data.json", PreviewKind::Text),
            (".gitignore", PreviewKind::Text),
            ("report.pdf", PreviewKind::Other),
            ("archive.zip", PreviewKind::Other),
        ] {
            assert_eq!(classify(&dir.path().join(name)), expected, "{name}");
        }
    }

    #[test]
    fn unknown_extension_with_text_is_text() {
        let dir = tempfile::tempdir().unwrap();
        for name in ["README", "thing.xyz"] {
            let path = dir.path().join(name);
            std::fs::write(&path, "Hello, wörld!\r\nSecond line.").unwrap();
            assert_eq!(classify(&path), PreviewKind::Text, "{name}");
        }
    }

    #[test]
    fn unknown_extension_with_nul_bytes_is_other() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("blob.xyz");
        std::fs::write(&path, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x00]).unwrap();
        assert_eq!(classify(&path), PreviewKind::Other);
    }

    #[test]
    fn unknown_extension_empty_file_is_other() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("empty.xyz");
        std::fs::write(&path, []).unwrap();
        assert_eq!(classify(&path), PreviewKind::Other);
    }

    #[test]
    fn missing_file_with_unknown_extension_is_other() {
        let dir = tempfile::tempdir().unwrap();
        assert_eq!(classify(&dir.path().join("missing.xyz")), PreviewKind::Other);
    }

    #[test]
    fn utf16_with_bom_looks_like_text() {
        let mut bytes = vec![0xFF, 0xFE];
        bytes.extend(utf16le("Hello"));
        assert!(looks_like_text(&bytes));
    }

    #[test]
    fn utf8_with_bom_looks_like_text() {
        let mut bytes = vec![0xEF, 0xBB, 0xBF];
        bytes.extend("Hello".as_bytes());
        assert!(looks_like_text(&bytes));
    }

    #[test]
    fn bytes_with_nul_do_not_look_like_text() {
        assert!(!looks_like_text(&[0x41, 0x00, 0x42]));
    }

    #[test]
    fn utf8_without_bom_is_decoded() {
        assert_eq!(decode("Crème brûlée".as_bytes(), false), "Crème brûlée");
    }

    #[test]
    fn utf8_bom_is_stripped() {
        let mut bytes = vec![0xEF, 0xBB, 0xBF];
        bytes.extend("héllo".as_bytes());
        assert_eq!(decode(&bytes, false), "héllo");
    }

    #[test]
    fn utf16_little_endian_bom_is_decoded() {
        let mut bytes = vec![0xFF, 0xFE];
        bytes.extend(utf16le("héllo"));
        assert_eq!(decode(&bytes, false), "héllo");
    }

    #[test]
    fn utf16_big_endian_bom_is_decoded() {
        let mut bytes = vec![0xFE, 0xFF];
        bytes.extend("héllo".encode_utf16().flat_map(u16::to_be_bytes));
        assert_eq!(decode(&bytes, false), "héllo");
    }

    #[test]
    fn invalid_utf8_falls_back_to_latin1() {
        // "café" saved as Windows-1252 / Latin-1: 0xE9 alone is not valid UTF-8.
        assert_eq!(decode(&[0x63, 0x61, 0x66, 0xE9], false), "café");
    }

    #[test]
    fn character_cut_by_the_limit_still_decodes_as_utf8() {
        // "ab" followed by the first byte of the two-byte "é" (0xC3 0xA9).
        assert_eq!(decode(&[0x61, 0x62, 0xC3], true), "ab");
    }

    #[test]
    fn read_reports_truncation() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.txt");
        std::fs::write(&path, "x".repeat(100)).unwrap();

        let cut = read_text(&path, 10).unwrap();
        assert_eq!(cut.text, "x".repeat(10));
        assert!(cut.truncated);

        let whole = read_text(&path, 1000).unwrap();
        assert_eq!(whole.text.len(), 100);
        assert!(!whole.truncated);
    }
}
