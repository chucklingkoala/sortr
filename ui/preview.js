// The preview window: shows one file at a time (image, video/audio, text, or Explorer's thumbnail) as the main
// window's highlighted row changes. Only the media player keeps a file open, and clearing releases it.
"use strict";

const THUMBNAIL_SIZE = 256;
// Decoding caps the longer side at this many pixels: sharp on large high-DPI screens, cheap on memory.
const MAX_IMAGE_SIDE = 2560;
const TEXT_LIMIT = 256 * 1024;

// Formats WebView2 shows itself; other image types are decoded with Windows' codecs.
const BROWSER_IMAGES = new Set([".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".ico", ".webp"]);

const GLYPH = { play: "", pause: "", volume: "", mute: "" };

const $ = (id) => document.getElementById(id);
const el = {
  fileName: $("file-name"),
  info: $("info"),
  open: $("open"),
  image: $("image"),
  canvas: $("canvas"),
  media: $("media"),
  audioGlyph: $("audio-glyph"),
  text: $("text"),
  message: $("message"),
  mediaControls: $("media-controls"),
  play: $("play"),
  position: $("position"),
  time: $("time"),
  mute: $("mute"),
  footerText: $("footer-text"),
};

const win = tauri.window.getCurrentWindow();
const { emit, listen } = tauri.event;

// Bumped by every show/clear, so a slow load for an earlier file never overwrites a newer one.
let request = 0;
let currentPath = null;
let baseInfo = "";

function fileNameOf(path) {
  return path.split(/[\\/]/).pop();
}

function extensionOf(path) {
  const name = fileNameOf(path);
  const dot = name.lastIndexOf(".");
  return dot < 0 ? "" : name.slice(dot).toLowerCase();
}

function formatTime(seconds) {
  const s = Math.floor(seconds % 60).toString().padStart(2, "0");
  const m = Math.floor(seconds / 60) % 60;
  const h = Math.floor(seconds / 3600);
  return h > 0 ? `${h}:${m.toString().padStart(2, "0")}:${s}` : `${m}:${s}`;
}

function setTitle(title) {
  document.title = title;
  win.setTitle(title).catch(() => {});
}

/** Hides every content view and stops media. Returns the new request number. */
function resetContent() {
  request++;
  el.media.pause();
  el.media.removeAttribute("src");
  el.media.load(); // lets go of the file
  el.image.removeAttribute("src");
  el.canvas.width = el.canvas.height = 0;
  el.text.textContent = "";
  for (const node of [el.image, el.canvas, el.media, el.audioGlyph, el.text, el.message, el.mediaControls, el.footerText]) {
    node.hidden = true;
  }
  return request;
}

async function showFile(path) {
  if (path !== null && path === currentPath) return;
  const req = resetContent();
  currentPath = path;

  if (path === null) {
    setTitle("Preview");
    el.fileName.textContent = "No file selected";
    el.info.textContent = "";
    el.open.disabled = true;
    return;
  }

  const name = fileNameOf(path);
  setTitle(`Preview – ${name}`);
  el.fileName.textContent = name;
  el.fileName.title = name;
  el.open.disabled = false;

  try {
    const info = await invoke("preview_info", { path });
    if (req !== request) return;
    if (!info.exists) {
      el.info.textContent = "";
      showMessage("This file no longer exists.", true);
      return;
    }
    const modified = new Date(info.modified).toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });
    baseInfo = `${formatSize(info.size)} · Modified ${modified}`;
    el.info.textContent = baseInfo;

    switch (info.kind) {
      case "image": await showImage(path, req); break;
      case "video":
      case "audio": showMedia(path, info.kind === "video"); break;
      case "text": await showText(path, req); break;
      default: await showThumbnail(path, req, "No preview for this file type. Click Open to view it in its default app.");
    }
  } catch (err) {
    if (req === request) showMessage(`Couldn't preview this file: ${err}`, true);
  }
}

/** Stops playback and lets go of the current file (before files are moved). */
function clear() {
  resetContent();
  currentPath = null;
}

// ---- Images ----

function loadImg(path) {
  return new Promise((resolve, reject) => {
    el.image.onload = () => resolve();
    el.image.onerror = () => reject(new Error("can't decode"));
    el.image.src = tauri.core.convertFileSrc(path);
  });
}

async function showImage(path, req) {
  if (BROWSER_IMAGES.has(extensionOf(path))) {
    try {
      await loadImg(path);
      if (req !== request) return;
      // WebView2 applies EXIF rotation itself, and reports the upright size.
      el.info.textContent = `${baseInfo} · ${el.image.naturalWidth} × ${el.image.naturalHeight}`;
      el.image.hidden = false;
      return;
    } catch {
      if (req !== request) return;
      el.image.removeAttribute("src");
      // Fall through to Windows' codecs, which may know a variant the browser doesn't.
    }
  }

  let pixels = null;
  try {
    pixels = await invoke("decode_image", { path, maxSide: MAX_IMAGE_SIDE });
  } catch {
    // Unsupported codec (e.g. HEIC without the extension) or a damaged file: show the thumbnail instead.
  }
  if (req !== request) return;
  if (pixels === null) {
    await showThumbnail(path, req, "Windows can't decode this image here.");
    return;
  }
  const { fullWidth, fullHeight } = drawPixels(pixels, 1);
  el.info.textContent = `${baseInfo} · ${fullWidth} × ${fullHeight}`;
  el.canvas.hidden = false;
}

/**
 * Draws the Rust side's pixel format (see imaging.rs: four little-endian u32s, then RGBA) into the canvas.
 * `scale` is how many image pixels make one CSS pixel.
 */
function drawPixels(buffer, scale) {
  const header = new DataView(buffer, 0, 16);
  const width = header.getUint32(0, true);
  const height = header.getUint32(4, true);
  const fullWidth = header.getUint32(8, true);
  const fullHeight = header.getUint32(12, true);
  el.canvas.width = width;
  el.canvas.height = height;
  el.canvas.style.width = `${width / scale}px`;
  el.canvas.style.height = `${height / scale}px`;
  const data = new Uint8ClampedArray(buffer, 16, width * height * 4);
  el.canvas.getContext("2d").putImageData(new ImageData(data, width, height), 0, 0);
  return { fullWidth, fullHeight };
}

// ---- Video / audio ----

let playing = false;
let seeking = false;

function showMedia(path, isVideo) {
  el.media.hidden = !isVideo;
  el.audioGlyph.hidden = isVideo;
  el.mediaControls.hidden = false;
  el.position.max = 1;
  el.position.value = 0;
  el.time.textContent = "";

  // Video starts playing silently; audio waits for Play so a preview never starts making noise.
  el.media.muted = isVideo;
  updateMuteButton();
  el.media.src = tauri.core.convertFileSrc(path);
  if (isVideo) play();
  else pause();
}

el.media.addEventListener("loadedmetadata", () => {
  const parts = [baseInfo];
  if (Number.isFinite(el.media.duration)) {
    el.position.max = Math.max(el.media.duration, 0.1);
    parts.push(formatTime(el.media.duration));
  }
  if (el.media.videoWidth > 0) parts.push(`${el.media.videoWidth} × ${el.media.videoHeight}`);
  el.info.textContent = parts.join(" · ");
  updatePosition();
});

el.media.addEventListener("timeupdate", updatePosition);

el.media.addEventListener("ended", () => {
  pause();
  el.media.currentTime = 0;
  updatePosition();
});

el.media.addEventListener("error", async () => {
  // Ignore a late failure from a file that is no longer shown, or from clearing the source.
  const path = currentPath;
  if (path === null || el.mediaControls.hidden || !el.media.getAttribute("src")) return;
  const req = resetContent();
  await showThumbnail(path, req, "This format can't be played here. Click Open to play it in its default app.");
});

function play() {
  el.media.play().catch(() => {});
  playing = true;
  el.play.textContent = GLYPH.pause;
  el.play.title = "Pause";
}

function pause() {
  el.media.pause();
  playing = false;
  el.play.textContent = GLYPH.play;
  el.play.title = "Play";
}

function togglePlay() {
  if (playing) pause();
  else play();
}

function updateMuteButton() {
  el.mute.textContent = el.media.muted ? GLYPH.mute : GLYPH.volume;
  el.mute.title = el.media.muted ? "Unmute" : "Mute";
}

function updatePosition() {
  if (seeking) return; // the user is dragging the slider
  const position = el.media.currentTime || 0;
  el.position.value = Math.min(position, Number(el.position.max));
  el.time.textContent = Number.isFinite(el.media.duration)
    ? `${formatTime(position)} / ${formatTime(el.media.duration)}`
    : formatTime(position);
}

el.play.addEventListener("click", togglePlay);
el.mute.addEventListener("click", () => {
  el.media.muted = !el.media.muted;
  updateMuteButton();
});
el.position.addEventListener("pointerdown", () => { seeking = true; });
el.position.addEventListener("pointerup", () => { seeking = false; });
el.position.addEventListener("input", () => {
  el.media.currentTime = Number(el.position.value);
  if (!playing || seeking) {
    const saved = seeking;
    seeking = false;
    updatePosition();
    seeking = saved;
  }
});

// ---- Text ----

async function showText(path, req) {
  const result = await invoke("read_text", { path });
  if (req !== request) return;
  el.text.textContent = result.text;
  el.text.scrollTop = el.text.scrollLeft = 0;
  el.text.hidden = false;
  if (result.truncated) showFooter(`Showing the first ${formatSize(TEXT_LIMIT)}. Click Open to see the whole file.`);
}

// ---- Everything else ----

async function showThumbnail(path, req, message) {
  const scale = window.devicePixelRatio || 1;
  let pixels = null;
  try {
    pixels = await invoke("thumbnail", { path, size: Math.round(THUMBNAIL_SIZE * scale) });
  } catch {
    // No thumbnail or icon at all; the message says what to do.
  }
  if (req !== request) return;
  if (pixels !== null) {
    drawPixels(pixels, scale);
    el.canvas.hidden = false;
  }
  showMessage(message, pixels === null);
}

function showMessage(text, centered) {
  el.message.textContent = text;
  el.message.classList.toggle("centered", centered);
  el.message.hidden = false;
}

function showFooter(text) {
  el.footerText.textContent = text;
  el.footerText.title = text;
  el.footerText.hidden = false;
}

el.open.addEventListener("click", async () => {
  if (currentPath === null) return;
  try {
    await invoke("open_file", { path: currentPath });
  } catch (err) {
    await messageBox("warning", "Could not open file", String(err));
  }
});

// ---- Keyboard ----

document.addEventListener("keydown", (e) => {
  if (e.ctrlKey || e.altKey || e.shiftKey || e.metaKey) return;
  const inText = e.target === el.text;
  const onSlider = e.target === el.position;
  switch (e.key) {
    case "Escape":
    case "p":
    case "P":
      emit("preview:close-requested");
      break;
    // Arrow keys still scroll the text and move the slider when those have focus.
    case "ArrowUp":
      if (inText || onSlider) return;
      emit("preview:navigate", -1);
      break;
    case "ArrowDown":
      if (inText || onSlider) return;
      emit("preview:navigate", 1);
      break;
    case " ":
      if (e.target.tagName === "BUTTON" || el.mediaControls.hidden) return;
      togglePlay();
      break;
    default:
      return;
  }
  e.preventDefault();
});

// ---- Messages from the main window ----

listen("preview:show", (e) => showFile(e.payload.path));
listen("preview:clear", (e) => {
  clear();
  emit("preview:cleared", e.payload);
});
emit("preview:ready");
