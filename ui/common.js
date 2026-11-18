// Shared by the main and preview windows: calling Rust, theming, and small formatting helpers.
"use strict";

const tauri = window.__TAURI__;
const invoke = tauri.core.invoke;

/** Shows a native message box. kind: "confirm" | "error" | "warning" | "info". Resolves true for OK. */
function messageBox(kind, title, text) {
  return invoke("message", { kind, title, text });
}

/** Applies Windows' accent colour, re-reading it when the window regains focus (the user may have changed it). */
async function applyAccent() {
  try {
    const c = await invoke("accent_colors");
    const root = document.documentElement.style;
    // Fluent uses a darker shade on light backgrounds and a lighter one on dark backgrounds.
    root.setProperty("--accent-light-theme", c.accentDark1);
    root.setProperty("--accent-dark-theme", c.accentLight2);
  } catch {
    // Keep the default blue.
  }
}
applyAccent();
window.addEventListener("focus", applyAccent);

// This is an app, not a browser page: no reload, print, find, caret browsing, page source or browser menu.
document.addEventListener("keydown", (e) => {
  const key = e.key.toLowerCase();
  const browserKey =
    e.key === "F5" || e.key === "F3" || e.key === "F7" ||
    (e.ctrlKey && ["r", "p", "f", "g", "u", "s", "j", "h", "o", "n", "t", "w"].includes(key));
  if (browserKey) e.preventDefault();
}, true);
document.addEventListener("contextmenu", (e) => {
  if (!e.target.closest("input[type=text], pre, textarea")) e.preventDefault();
});

/** "1.4 MB" style sizes. */
function formatSize(bytes) {
  const units = ["bytes", "KB", "MB", "GB", "TB"];
  let size = bytes;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  if (unit === 0) return `${bytes} bytes`;
  return `${Number(size.toFixed(1))} ${units[unit]}`;
}
