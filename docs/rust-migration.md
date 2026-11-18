# Rust migration plan

**Goal:** rewrite Sortr in Rust to shrink `Sortr.exe` from about 60 MB to about 3–6 MB, with the same features.

**Approach:** a Rust core library plus a Tauri 2 app. The UI is plain HTML/CSS/JS shown in WebView2, the browser component built into Windows. No Node/npm toolchain is needed.

## Status

Phases 0–5 are done on the `dev` branch, one commit per phase. The release exe is **3.5 MB** (1.x: about 60 MB).

Where the implementation differs from the plan below:

- **No Tauri CLI.** `cargo build --release` produces the exe, because the app crate turns on Tauri's `custom-protocol` feature by default.
- **No dialog plugin.** The folder picker (`IFileOpenDialog`) and message boxes (`MessageBoxW`) are called directly. They're the same native dialogs 1.x used, and this avoids the plugin and the file-system plugin it depends on.
- **Raw pixels instead of PNG.** WIC-decoded images and shell thumbnails go to the page as RGBA and are drawn on a canvas, so no image encoder is needed.
- **Animated GIFs play.** 1.x showed only the first frame.
- **WMV and AVI fall back to the thumbnail.** WebView2 can't play them, as expected; MP4, MOV, WebM, MP3, WAV and FLAC play.
- **Unicode check.** The Rust and .NET tokenizers were compared over every BMP character. They differ only on characters added in newer Unicode versions than .NET's tables. .NET also throws on U+FFFE, which the Rust version handles.
- **Testing.** Besides the unit tests, the main and preview windows were driven end to end through WebView2's DevTools protocol: scanning, the picker, keyboard use, moving (including a file whose video was playing in the preview), undo, every preview type and both themes.

## Why Tauri

Most of the 60 MB is the bundled .NET runtime and WPF. Sortr's own code is small: about 500 lines of logic in `Sortr.Core` and 1,700 lines of UI. Any Rust toolkit removes the runtime, so the real choice is which one can still do the hard parts:

- the in-app video/audio player
- the Fluent look that follows Windows' light/dark setting and accent colour
- editable cells in the results list: the dropdown for ambiguous files and the type-to-search picker for unmatched ones

| Option | Exe size | Cost |
|---|---|---|
| **Tauri 2 (chosen)** | ~3–6 MB | `<video>`/`<audio>`, light/dark and the editable cells are all easy in HTML. It relies on WebView2, which ships with Windows 11 and almost all updated Windows 10 PCs. |
| Native Win32 (`windows` crate) | ~1 MB | Common controls have no official dark mode. Dropdowns and search boxes inside a ListView row need custom subclassing, and video needs hand-written Media Foundation code. Several times the work. |
| Slint / egui | ~3–6 MB | No video playback, so the preview would fall back to thumbnails (a lost feature). Slint's free licence also requires an attribution notice. |

If the smallest possible exe ever matters more than the effort, native Win32 is the other real option. Phase 0 tests the Tauri choice before anything else depends on it.

## Target layout

```
Cargo.toml                 workspace + size-tuned release profile
crates/sortr-core/         port of Sortr.Core (no UI), with tests
src-tauri/                 app crate: commands, Windows shell/imaging code, tauri.conf.json
ui/                        index.html, preview.html, app.js, preview.js, style.css (served as-is)
```

Release profile:

```toml
[profile.release]
opt-level = "s"
lto = true
codegen-units = 1
panic = "abort"
strip = true
```

To keep the exe small:

- Use `windows-sys`/`windows` for Win32 calls.
- Avoid heavy crates. Use the Win32 `GetLocalTime` call instead of `chrono`, and decode images with the WIC codecs built into Windows instead of the `image` crate.
- Don't use UPX. It causes antivirus false positives, and the exe is already unsigned.

## Phases

### Phase 0: Spike (go/no-go)

- Build an empty Tauri 2 app with the release profile and measure the exe. **Gate: about 6 MB or less.**
- Play a local MP4 through the asset protocol, and check that the file can still be moved while it's playing.
- Check what happens when WebView2 is missing. Plan a startup check that shows a message with the download link instead of crashing.

### Phase 1: Port `Sortr.Core` to `sortr-core`

These map almost one-to-one: `NameNormalizer`, `NameMatcher`, `TargetSearch`, `TargetLoader`, `FileScanner`, `PreviewClassifier`, `PreviewText`, `SettingsStore`, `UndoLog`, `FileMover`. Port every xUnit test in `tests/Sortr.Tests` as a Rust unit test before changing any behaviour.

Five places need care:

1. **Moves would overwrite files.** On Windows, `std::fs::rename` replaces an existing file and fails across drives. That breaks the "nothing is overwritten" promise. Call `MoveFileExW` with `MOVEFILE_COPY_ALLOWED` and *without* `MOVEFILE_REPLACE_EXISTING`, and add a test for each case.
2. **Unicode rules differ from .NET.**
   - C#'s `char.IsLetterOrDigit` is not the same as Rust's `char::is_alphanumeric` (e.g. `²`, `Ⅻ`).
   - `ToLowerInvariant` maps one character to one, while Rust's `to_lowercase` can return more (`İ`).
   - Removing accents needs `unicode-normalization`: decompose to NFD, then drop non-spacing marks.

   Match the .NET behaviour exactly and add tests for these characters.
3. **Comparisons need a Rust equivalent.** `OrdinalIgnoreCase` sorting and `StartsWith`/`Contains` need explicit case-folding in Rust. Match offsets can be byte offsets, because they are only compared with each other.
4. **Keep the on-disk formats.** Use serde renames to keep the `settings.json` fields `SourceFolder`/`TargetsFolder` and the undo-log lines `{"From","To"}`. Keep the `batch-yyyyMMdd-HHmmss-fff.jsonl` file names too. That way existing users keep their settings and can still undo a batch moved by the .NET version.
5. **Text preview decoding.**
   - Without a BOM, decode as strict UTF-8 but accept a character cut off at the read limit (`Utf8Error::error_len() == None`). Fall back to Latin-1 when the bytes aren't valid UTF-8.
   - With a UTF-16 BOM, use `String::from_utf16`.

### Phase 2: Rust app commands

| Command | Notes |
|---|---|
| `load_settings` | |
| `scan` | Loads targets, scans files and matches them. |
| `search_targets` | Reuses `TargetSearch`, called as the user types. |
| `move_files` | Reports progress through a Tauri `Channel`. |
| `undo_latest`, `latest_batch_count` | |
| `open_file` | `ShellExecuteW` |
| `show_in_explorer` | `SHOpenFolderAndSelectItems` |
| `preview_info` | Kind, size, modified time. |
| `read_text` | First 256 KB. |
| `thumbnail` | Port of `ShellThumbnail.cs`: `IShellItemImageFactory` on an STA thread, returned as PNG. |
| `decode_image` | WIC fallback for formats WebView2 can't show. |
| `accent_color` | `UISettings` |

Folder pickers and the confirm/error boxes use `tauri-plugin-dialog`. The window title shows the version from `CARGO_PKG_VERSION`.

### Phase 3: Main window (`ui/index.html`)

- Folder pickers and Scan.
- Toolbar with Select all/none, Show unmatched and the filter box.
- Results table: checkbox, file, target, badge, destination, status. The target cell is plain text, a `<select>` for ambiguous rows, or the type-ahead picker for unmatched rows.
- Summary line, progress bar, Undo and Move buttons.
- Shortcuts: Space, P, Esc, double-click to open, and a native context menu (Tauri `Menu::popup`).
- Light/dark through `prefers-color-scheme`, the accent colour as a CSS variable, and the same badge colours as now.
- Render rows in slices of the visible range so folders with thousands of files stay fast.

### Phase 4: Preview window (`ui/preview.html`)

- A second window that opens without taking focus and hides instead of closing, so its size and position are kept.
- Content by type:
  - Images use `<img>`, which applies EXIF rotation itself. TIFF, HEIC and JXR go through WIC.
  - Video and audio use `<video>`/`<audio>` with the current rules: video starts muted, audio waits for Play, and any error falls back to the thumbnail. WebView2 can't play WMV or AVI, which MediaElement could, so those will now show the thumbnail. That fallback already exists.
  - Text shows the first 256 KB in a `<pre>`.
  - Anything else shows the shell thumbnail.
- Up/down navigation and close requests pass between the windows as events.
- Before Move or Undo, the main window tells the preview to clear and waits for a reply. This keeps the guarantee that a playing video is stopped first.

### Phase 5: Release and docs

- Update [release.yml](../.github/workflows/release.yml):
  1. Set up Rust with `dtolnay/rust-toolchain`.
  2. Run `cargo test`.
  3. Set the version from the tag.
  4. Run `cargo tauri build --no-bundle`.
  5. Upload `Sortr.exe` and its SHA-256 as now.
- [README.md](../README.md): update Building, the exe size, the WebView2 note and Project layout.
- Icon: keep the generated `Sortr.ico` and PNG checked in. Either keep `tools/IconGen` as a .NET tool used only for development, or port it later.

### Phase 6: Cutover

Remove `src/Sortr.App`, `src/Sortr.Core`, `tests/`, `Sortr.slnx` and the .NET `.gitignore` entries only after a manual run through the parity checklist below. Until then the .NET app keeps building alongside the new one.

## Parity checklist (manual, before cutover)

- [ ] Strong, Loose, Ambiguous and Unmatched rows, with the right default ticks.
- [ ] Longest name wins, and the rules for one-word and short names.
- [ ] The ambiguous dropdown ticks the row.
- [ ] The unmatched picker filters, works with ↑/↓/Enter/Esc, and clearing it unticks the row.
- [ ] Move confirms first, renames clashes to `name (1).ext`, works across drives and logs each move as it happens.
- [ ] Undo refuses to overwrite, keeps any failed files in the log and rescans afterwards.
- [ ] Undo restores a batch moved by the .NET version.
- [ ] Every preview type works, ↑/↓ steps through the list, and the preview never blocks a move.
- [ ] Light/dark switches live, the accent colour is used, the version shows in the title and settings are kept between runs.
