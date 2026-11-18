// Sortr's main window: pick folders, scan, review matches, move and undo.
"use strict";

const ROW_HEIGHT = 38;
const OVERSCAN = 8;
const MAX_ERRORS_SHOWN = 15;
const KIND_ORDER = { Strong: 0, Loose: 1, Ambiguous: 2, Unmatched: 3 };

const $ = (id) => document.getElementById(id);
const ui = {
  source: $("source"),
  targets: $("targets"),
  browseSource: $("browse-source"),
  browseTargets: $("browse-targets"),
  scan: $("scan"),
  selectAll: $("select-all"),
  selectNone: $("select-none"),
  showUnmatched: $("show-unmatched"),
  filter: $("filter"),
  header: $("grid-header"),
  body: $("grid-body"),
  spacer: $("grid-spacer"),
  summary: $("summary"),
  status: $("status"),
  progress: $("progress"),
  undo: $("undo"),
  move: $("move"),
  pickerPopup: $("picker-popup"),
  pickerList: $("picker-list"),
  pickerEmpty: $("picker-empty"),
};

const state = {
  /** Targets from the last scan: [{ name, folder }]. Rows refer to them by index. */
  targets: [],
  /** Every row from the last scan. */
  items: [],
  /** The rows shown: filtered and sorted. */
  view: [],
  /** Row → its index in `view`. */
  index: new Map(),
  /** Rows highlighted in the grid (what Space and the context menu act on). */
  highlighted: new Set(),
  /** The row with the keyboard caret, and the start of a Shift-selection. */
  current: null,
  anchor: null,
  sort: null, // { key, dir: 1 | -1 }
  scanned: false,
  busy: false,
  canUndo: false,
  selectedCount: 0,
};

// ---- Rows ----

/**
 * A row: one file, its match candidates (best first, as target indices), which candidate is chosen, a target picked
 * by hand for an unmatched file, whether it's ticked for moving, and the last move error.
 */
function makeItem(row) {
  const n = row.candidates.length;
  return {
    path: row.path,
    name: row.name,
    candidates: row.candidates,
    kind: n === 0 ? "Unmatched" : n === 1 ? row.candidates[0].confidence : "Ambiguous",
    choice: n > 0 ? 0 : null,
    manual: null,
    // Ambiguous rows start unticked so the user must consciously pick a target.
    checked: n === 1,
    status: "",
  };
}

/** The target index the file will move to: the chosen match, or the hand-picked target. */
function effectiveTarget(item) {
  return item.choice !== null ? item.candidates[item.choice].target : item.manual;
}
const canSelect = (item) => effectiveTarget(item) !== null;
const targetName = (item) => (canSelect(item) ? state.targets[effectiveTarget(item)].name : "");
const destination = (item) => (canSelect(item) ? state.targets[effectiveTarget(item)].folder : "");
const committedText = (item) => (item.manual === null ? "" : state.targets[item.manual].name);

// ---- Filtering and sorting ----

function passesFilter(item) {
  if (!ui.showUnmatched.checked && item.kind === "Unmatched") return false;
  const filter = ui.filter.value;
  if (!filter.trim()) return true;
  const q = filter.toLowerCase();
  const has = (text) => text.toLowerCase().includes(q);
  return has(item.name)
    || item.candidates.some((c) => has(state.targets[c.target].name))
    || (item.manual !== null && has(state.targets[item.manual].name));
}

const collator = new Intl.Collator(undefined, { sensitivity: "base" });
const SORTS = {
  checked: (a, b) => Number(a.checked) - Number(b.checked),
  file: (a, b) => collator.compare(a.name, b.name),
  target: (a, b) => collator.compare(targetName(a), targetName(b)),
  kind: (a, b) => KIND_ORDER[a.kind] - KIND_ORDER[b.kind],
  destination: (a, b) => collator.compare(destination(a), destination(b)),
  status: (a, b) => collator.compare(a.status, b.status),
};

function setView(view) {
  state.view = view;
  state.index = new Map(view.map((item, i) => [item, i]));
  // Rows that are no longer shown can't stay highlighted.
  for (const item of [...state.highlighted]) {
    if (!state.index.has(item)) state.highlighted.delete(item);
  }
  if (state.current && !state.index.has(state.current)) {
    state.current = null;
    syncPreview();
  }
  if (state.anchor && !state.index.has(state.anchor)) state.anchor = null;
  renderAll();
}

function rebuildView() {
  let view = state.items.filter(passesFilter);
  if (state.sort) {
    const compare = SORTS[state.sort.key];
    const dir = state.sort.dir;
    view = view.map((item, i) => [item, i]).sort(([a, i], [b, j]) => dir * compare(a, b) || i - j).map(([item]) => item);
  }
  setView(view);
}

ui.header.addEventListener("click", (e) => {
  const cell = e.target.closest("[data-sort]");
  if (!cell) return;
  const key = cell.dataset.sort;
  state.sort = { key, dir: state.sort?.key === key ? -state.sort.dir : 1 };
  for (const el of ui.header.children) {
    el.classList.toggle("sorted-asc", el === cell && state.sort.dir === 1);
    el.classList.toggle("sorted-desc", el === cell && state.sort.dir === -1);
  }
  rebuildView();
});

// ---- Rendering (only the rows in view exist in the page) ----

const rendered = new Map(); // item → row element

function renderAll() {
  for (const el of rendered.values()) el.remove();
  rendered.clear();
  ui.spacer.style.height = `${state.view.length * ROW_HEIGHT}px`;
  renderVisible();
}

function renderVisible() {
  const top = ui.body.scrollTop;
  const first = Math.max(0, Math.floor(top / ROW_HEIGHT) - OVERSCAN);
  const last = Math.min(state.view.length, Math.ceil((top + ui.body.clientHeight) / ROW_HEIGHT) + OVERSCAN);
  const wanted = new Set(state.view.slice(first, last));
  for (const [item, el] of rendered) {
    // Keep a row whose search box has focus, so scrolling doesn't throw away what the user is typing.
    if (!wanted.has(item) && !el.contains(document.activeElement)) {
      el.remove();
      rendered.delete(item);
    }
  }
  for (let i = first; i < last; i++) {
    const item = state.view[i];
    let el = rendered.get(item);
    if (!el) {
      el = createRow(item);
      rendered.set(item, el);
      ui.body.append(el);
    }
    el.style.top = `${i * ROW_HEIGHT}px`;
  }
}

function cell(className, ...children) {
  const el = document.createElement("div");
  el.className = `cell ${className}`;
  el.append(...children);
  return el;
}

function createRow(item) {
  const row = document.createElement("div");
  row.className = "row";
  row.setAttribute("role", "row");
  row.item = item;

  const check = document.createElement("input");
  check.type = "checkbox";
  check.tabIndex = -1;
  check.setAttribute("aria-label", "Move this file");
  check.addEventListener("change", () => {
    item.checked = check.checked;
    itemChanged(item);
  });

  const badge = document.createElement("span");
  badge.className = `badge ${item.kind.toLowerCase()}`;
  badge.textContent = item.kind;

  const fileCell = cell("file", item.name);
  row.append(cell("check", check), fileCell, cell("target", createTargetControl(item)), cell("kind", badge),
    cell("destination"), cell("status"));
  updateRow(item, row);
  return row;
}

function createTargetControl(item) {
  if (item.kind === "Ambiguous") {
    const select = document.createElement("select");
    item.candidates.forEach((c, i) => select.add(new Option(state.targets[c.target].name, String(i))));
    select.addEventListener("change", () => {
      item.choice = Number(select.value);
      // Choosing a target for an ambiguous file is a clear signal the user wants it moved.
      item.checked = true;
      itemChanged(item);
    });
    return select;
  }
  if (item.kind === "Unmatched") return createPicker(item);
  return document.createElement("span");
}

function updateRow(item, row = rendered.get(item)) {
  if (!row) return;
  const [checkCell, , targetCell, , destinationCell, statusCell] = row.children;
  const check = checkCell.firstChild;
  check.checked = item.checked;
  check.disabled = !canSelect(item);
  row.classList.toggle("highlighted", state.highlighted.has(item));
  row.classList.toggle("current", state.current === item);
  row.setAttribute("aria-selected", String(state.highlighted.has(item)));

  const control = targetCell.firstChild;
  if (item.kind === "Ambiguous") control.value = String(item.choice);
  else if (item.kind === "Unmatched") { if (document.activeElement !== control) control.value = committedText(item); }
  else control.textContent = targetName(item);

  destinationCell.textContent = destination(item);
  destinationCell.title = destination(item);
  statusCell.textContent = item.status;
  statusCell.title = item.status;
}

function itemChanged(item) {
  updateRow(item);
  updateSummary();
}

ui.body.addEventListener("scroll", () => {
  renderVisible();
  positionPicker();
});
new ResizeObserver(() => {
  renderVisible();
  positionPicker();
}).observe(ui.body);

// ---- Highlighting rows ----

function setHighlight(items, current, anchor = current) {
  const before = new Set([...state.highlighted, state.current]);
  const currentChanged = state.current !== current;
  state.highlighted = new Set(items);
  state.current = current;
  state.anchor = anchor;
  for (const item of new Set([...before, ...state.highlighted, current])) {
    if (item) updateRow(item);
  }
  if (currentChanged) syncPreview();
}

function highlightRange(from, to) {
  const [a, b] = [state.index.get(from) ?? 0, state.index.get(to)].sort((x, y) => x - y);
  setHighlight(state.view.slice(a, b + 1), to, from);
}

function scrollToIndex(i) {
  const top = i * ROW_HEIGHT;
  if (top < ui.body.scrollTop) ui.body.scrollTop = top;
  else if (top + ROW_HEIGHT > ui.body.scrollTop + ui.body.clientHeight) {
    ui.body.scrollTop = top + ROW_HEIGHT - ui.body.clientHeight;
  }
}

/** Moves the caret by `delta` rows (or to `absolute`), extending the highlight with Shift. */
function moveCaret(delta, { shift = false, ctrl = false, absolute = null } = {}) {
  if (state.view.length === 0) return;
  const from = state.current ? state.index.get(state.current) : -1;
  const next = Math.max(0, Math.min(state.view.length - 1, absolute ?? from + delta));
  const item = state.view[next];
  if (shift) highlightRange(state.anchor ?? item, item);
  else if (ctrl) setHighlight(state.highlighted, item);
  else setHighlight([item], item);
  scrollToIndex(next);
}

/** Flips the tick of the given rows: all on, unless they are all on already. */
function toggleChecked(items) {
  const list = [...items].filter(canSelect);
  if (list.length === 0) return;
  const value = !list.every((item) => item.checked);
  for (const item of list) {
    item.checked = value;
    updateRow(item);
  }
  updateSummary();
}

ui.body.addEventListener("mousedown", (e) => {
  const row = e.target.closest(".row");
  if (!row) return;
  const item = row.item;
  if (e.button === 2) {
    if (!state.highlighted.has(item)) setHighlight([item], item);
    return;
  }
  if (e.button !== 0) return;
  // Clicking a tick box ticks just that row and leaves the highlight alone.
  if (e.target.matches("input[type=checkbox]")) {
    e.preventDefault();
    return;
  }
  if (e.ctrlKey) {
    const set = new Set(state.highlighted);
    if (set.has(item)) set.delete(item);
    else set.add(item);
    setHighlight(set, item);
  } else if (e.shiftKey) {
    highlightRange(state.anchor ?? item, item);
  } else {
    setHighlight([item], item);
  }
  // Clicks on a row's dropdown or search box go to that control; elsewhere the grid takes the keyboard.
  if (!e.target.closest("select, input")) {
    e.preventDefault();
    ui.body.focus({ preventScroll: true });
  }
});

ui.body.addEventListener("keydown", (e) => {
  // Keys typed into a row's dropdown or search box belong to that control.
  if (e.target !== ui.body) return;
  const page = Math.max(1, Math.floor(ui.body.clientHeight / ROW_HEIGHT) - 1);
  const mods = { shift: e.shiftKey, ctrl: e.ctrlKey };
  switch (e.key) {
    case "ArrowDown": moveCaret(1, mods); break;
    case "ArrowUp": moveCaret(-1, mods); break;
    case "PageDown": moveCaret(page, mods); break;
    case "PageUp": moveCaret(-page, mods); break;
    case "Home": moveCaret(0, { ...mods, absolute: 0 }); break;
    case "End": moveCaret(0, { ...mods, absolute: state.view.length - 1 }); break;
    case " ": toggleChecked(state.highlighted); break;
    case "p":
    case "P":
      if (e.ctrlKey || e.altKey || e.shiftKey) return;
      togglePreview();
      break;
    case "Escape":
      if (!preview.open) return;
      hidePreview();
      break;
    case "a":
    case "A":
      if (!e.ctrlKey) return;
      if (state.view.length) setHighlight(state.view, state.current ?? state.view[0], state.anchor ?? state.view[0]);
      break;
    default:
      return;
  }
  e.preventDefault();
});

ui.body.addEventListener("dblclick", (e) => {
  const row = e.target.closest(".row");
  if (row && !e.target.closest("select, input")) openFile(row.item);
});

ui.body.addEventListener("contextmenu", (e) => {
  const row = e.target.closest(".row");
  if (!row || e.target.closest("input[type=text]")) return;
  e.preventDefault();
  showRowMenu();
});

async function showRowMenu() {
  const item = state.current;
  if (!item) return;
  const menu = await tauri.menu.Menu.new({
    items: [
      { id: "preview", text: "Preview (P)", action: showPreview },
      { id: "open", text: "Open file", action: () => openFile(item) },
      { id: "explorer", text: "Show in Explorer", action: () => invoke("show_in_explorer", { path: item.path }) },
      { item: "Separator" },
      { id: "toggle", text: "Toggle selected rows (Space)", action: () => toggleChecked(state.highlighted) },
    ],
  });
  await menu.popup();
}

async function openFile(item) {
  try {
    await invoke("open_file", { path: item.path });
  } catch (err) {
    await messageBox("warning", "Could not open file", String(err));
  }
}

// ---- Preview window ----

const preview = { open: false, clearRequest: 0 };

/** Tells the preview window which file to show: the highlighted row's, or none. */
function syncPreview() {
  if (preview.open) tauri.event.emit("preview:show", { path: state.current?.path ?? null });
}

async function showPreview() {
  preview.open = true;
  await invoke("show_preview");
  syncPreview();
}

async function hidePreview() {
  if (!preview.open) return;
  preview.open = false;
  await clearPreview();
  await invoke("hide_preview");
  ui.body.focus({ preventScroll: true });
}

function togglePreview() {
  if (preview.open) hidePreview();
  else showPreview();
}

/**
 * Stops any playing media and makes the preview let go of its file, waiting until it has. A playing video holds its
 * file open, which would make moving it fail.
 */
function clearPreview() {
  const id = ++preview.clearRequest;
  return new Promise((resolve) => {
    let unlisten = null;
    const done = () => {
      clearTimeout(timer);
      unlisten?.();
      resolve();
    };
    const timer = setTimeout(done, 1500); // no preview window yet, or it's not responding
    tauri.event.listen("preview:cleared", (e) => {
      if (e.payload === id) done();
    }).then((stop) => {
      unlisten = stop;
      tauri.event.emit("preview:clear", id);
    });
  });
}

tauri.event.listen("preview:ready", syncPreview);
tauri.event.listen("preview:navigate", (e) => moveCaret(e.payload));
tauri.event.listen("preview:close-requested", hidePreview);

// ---- Type-to-search target picker (unmatched rows) ----

const picker = { input: null, item: null, results: [], highlight: -1, seq: 0 };

function createPicker(item) {
  const input = document.createElement("input");
  input.type = "text";
  input.placeholder = "Type to search targets…";
  input.spellcheck = false;
  input.autocomplete = "off";
  input.value = committedText(item);

  // Take focus on the first click without the click moving the caret, so the select-all below sticks.
  input.addEventListener("mousedown", (e) => {
    if (document.activeElement !== input) {
      e.preventDefault();
      input.focus();
    }
  });
  // Show every target, with the current one highlighted; typing replaces the selected text.
  input.addEventListener("focus", () => {
    input.select();
    showPickerResults(input, item, "", item.manual);
  });
  input.addEventListener("blur", () => resetPicker(input, item));
  input.addEventListener("input", () => showPickerResults(input, item, input.value));
  input.addEventListener("keydown", (e) => pickerKeyDown(e, input, item));
  return input;
}

async function showPickerResults(input, item, query, highlightTarget = null) {
  const seq = ++picker.seq;
  const results = await invoke("search_targets", { query });
  // A newer search, or the box lost focus, while this one ran.
  if (seq !== picker.seq || document.activeElement !== input) return;

  Object.assign(picker, { input, item, results });
  const index = highlightTarget === null ? -1 : results.indexOf(highlightTarget);
  picker.highlight = index >= 0 ? index : results.length > 0 ? 0 : -1;

  ui.pickerList.replaceChildren(...results.map((target, i) => {
    const option = document.createElement("div");
    option.className = "picker-option";
    option.textContent = state.targets[target].name;
    // Keep the keyboard in the search box while the list is open.
    option.addEventListener("mousedown", (e) => e.preventDefault());
    option.addEventListener("click", () => commitPicker(input, item, target));
    option.dataset.index = String(i);
    return option;
  }));
  ui.pickerEmpty.hidden = results.length > 0;
  ui.pickerList.hidden = results.length === 0;
  ui.pickerPopup.hidden = false;
  positionPicker();
  updatePickerHighlight();
}

function updatePickerHighlight() {
  for (const option of ui.pickerList.children) {
    option.classList.toggle("highlighted", Number(option.dataset.index) === picker.highlight);
  }
  ui.pickerList.children[picker.highlight]?.scrollIntoView({ block: "nearest" });
}

function movePickerHighlight(delta) {
  const count = picker.results.length;
  if (count === 0) return;
  picker.highlight = Math.max(0, Math.min(count - 1, picker.highlight + delta));
  updatePickerHighlight();
}

function positionPicker() {
  if (ui.pickerPopup.hidden || !picker.input) return;
  const box = picker.input.getBoundingClientRect();
  const grid = ui.body.getBoundingClientRect();
  if (box.bottom < grid.top || box.top > grid.bottom) {
    ui.pickerPopup.style.visibility = "hidden";
    return;
  }
  ui.pickerPopup.style.visibility = "";
  ui.pickerPopup.style.minWidth = `${box.width}px`;
  ui.pickerPopup.style.left = `${Math.min(box.left, window.innerWidth - ui.pickerPopup.offsetWidth - 4)}px`;
  const below = box.bottom + 2;
  const height = ui.pickerPopup.offsetHeight;
  const top = below + height > window.innerHeight - 4 ? Math.max(4, box.top - 2 - height) : below;
  ui.pickerPopup.style.top = `${top}px`;
}

function closePicker() {
  picker.seq++; // ignore any search still running
  picker.input = null;
  ui.pickerPopup.hidden = true;
}

function resetPicker(input, item) {
  closePicker();
  input.value = committedText(item);
}

function commitPicker(input, item, target) {
  if (item.manual !== target) {
    item.manual = target;
    // Picking a target ticks the row; clearing it unticks the row.
    item.checked = target !== null;
  }
  resetPicker(input, item);
  itemChanged(item);
}

function pickerKeyDown(e, input, item) {
  const open = !ui.pickerPopup.hidden && picker.input === input;
  switch (e.key) {
    case "ArrowDown":
      if (!open) showPickerResults(input, item, input.value === committedText(item) ? "" : input.value, item.manual);
      else movePickerHighlight(1);
      break;
    case "ArrowUp":
      if (open) movePickerHighlight(-1);
      break;
    case "PageDown":
    case "PageUp":
      if (!open) return;
      movePickerHighlight(e.key === "PageDown" ? 10 : -10);
      break;
    case "Enter":
      if (!input.value.trim()) commitPicker(input, item, null);
      else if (open && picker.highlight >= 0) commitPicker(input, item, picker.results[picker.highlight]);
      break;
    case "Escape":
      // Only swallow Esc when there's something to cancel, so it can still close the preview otherwise.
      if (!open && input.value === committedText(item)) return;
      resetPicker(input, item);
      break;
    default:
      return;
  }
  e.preventDefault();
}

window.addEventListener("resize", positionPicker);

// ---- Summary, status and buttons ----

function setStatus(text) {
  ui.status.textContent = text;
  ui.status.title = text;
}

function setProgress(value, max) {
  ui.progress.max = Math.max(max, 1);
  ui.progress.value = value;
}

function canScan() {
  return !state.busy && ui.source.value.trim() !== "" && ui.targets.value.trim() !== "";
}

function updateButtons() {
  ui.scan.disabled = !canScan();
  ui.move.disabled = state.busy || state.selectedCount === 0;
  ui.undo.disabled = state.busy || !state.canUndo;
}

function updateSummary() {
  const counts = { Strong: 0, Loose: 0, Ambiguous: 0, Unmatched: 0 };
  let selected = 0;
  for (const item of state.items) {
    counts[item.kind]++;
    if (item.checked && canSelect(item)) selected++;
  }
  state.selectedCount = selected;
  ui.summary.textContent = state.scanned
    ? `${counts.Strong} strong · ${counts.Loose} loose · ${counts.Ambiguous} ambiguous · ${counts.Unmatched} unmatched · ${selected} selected`
    : "";
  updateButtons();
}

async function setBusy(busy) {
  state.busy = busy;
  ui.progress.hidden = !busy;
  updateButtons();
  if (busy && preview.open) await clearPreview();
  else if (!busy) syncPreview();
}

async function refreshCanUndo() {
  try {
    state.canUndo = (await invoke("latest_batch_size")) !== null;
  } catch {
    state.canUndo = true; // a batch exists but couldn't be read; Undo will say why
  }
  updateButtons();
}

function formatErrors(header, errors) {
  const lines = errors.slice(0, MAX_ERRORS_SHOWN);
  if (errors.length > MAX_ERRORS_SHOWN) lines.push(`...and ${errors.length - MAX_ERRORS_SHOWN} more.`);
  return `${header}\n\n${lines.join("\n")}`;
}

// ---- Commands ----

async function browse(input, title) {
  try {
    const folder = await invoke("pick_folder", { title, initial: input.value });
    if (folder !== null) {
      input.value = folder;
      updateButtons();
    }
  } catch (err) {
    await messageBox("error", "Sortr", String(err));
  }
}

async function scan() {
  const source = ui.source.value;
  const targets = ui.targets.value;
  if (!(await invoke("folder_exists", { path: source }))) {
    await messageBox("error", "Sortr", `Source folder not found:\n${source}`);
    return;
  }
  if (!(await invoke("folder_exists", { path: targets }))) {
    await messageBox("error", "Sortr", `Targets folder not found:\n${targets}`);
    return;
  }

  await setBusy(true);
  setProgress(0, 1);
  setStatus("Scanning...");
  try {
    const result = await invoke("scan", { source, targets });
    closePicker();
    state.targets = result.targets;
    state.items = result.rows.map(makeItem);
    state.scanned = true;
    state.highlighted = new Set();
    state.current = state.anchor = null;
    ui.body.scrollTop = 0;
    rebuildView();
    setStatus(`Scanned ${result.rows.length} files against ${result.targets.length} targets.`);
    updateSummary();
  } catch (err) {
    await messageBox("error", "Scan failed", String(err));
    setStatus("Scan failed.");
  } finally {
    setBusy(false);
  }
}

async function moveSelected() {
  const selected = state.items.filter((item) => item.checked && canSelect(item));
  if (selected.length === 0) return;

  const targetCount = new Set(selected.map(effectiveTarget)).size;
  if (!(await messageBox("confirm", "Confirm move", `Move ${selected.length} file(s) into ${targetCount} target folder(s)?`))) {
    return;
  }

  await setBusy(true);
  setProgress(0, selected.length);
  setStatus("Moving files...");
  try {
    const progress = new tauri.core.Channel();
    progress.onmessage = (n) => setProgress(n, selected.length);
    const requests = selected.map((item) => ({
      sourcePath: item.path,
      destinationFolder: state.targets[effectiveTarget(item)].folder,
    }));
    const results = await invoke("move_files", { requests, progress });

    const moved = new Set();
    const errors = [];
    results.forEach((result, i) => {
      const item = selected[i];
      if (result.error === null) {
        moved.add(item);
      } else {
        item.status = result.error;
        errors.push(`${item.name}: ${result.error}`);
      }
    });

    // Moved rows leave the list; everything else stays where it is.
    state.items = state.items.filter((item) => !moved.has(item));
    setView(state.view.filter((item) => !moved.has(item)));
    await refreshCanUndo();
    setStatus(errors.length === 0
      ? `Moved ${moved.size} file(s).`
      : `Moved ${moved.size} file(s); ${errors.length} failed (see Status column).`);
    updateSummary();

    if (errors.length > 0) {
      await messageBox("error", "Some moves failed", formatErrors(`${errors.length} file(s) could not be moved:`, errors));
    }
  } catch (err) {
    await messageBox("error", "Move failed", String(err));
    setStatus("Move failed.");
  } finally {
    setBusy(false);
    setProgress(0, 1);
  }
}

async function undoLastMove() {
  let count;
  try {
    count = await invoke("latest_batch_size");
  } catch (err) {
    await messageBox("error", "Undo failed", String(err));
    return;
  }
  if (!count) {
    await refreshCanUndo();
    return;
  }
  if (!(await messageBox("confirm", "Undo last move", `Move ${count} file(s) from the last batch back to where they came from?`))) {
    return;
  }

  await setBusy(true);
  setStatus("Undoing last move...");
  let result;
  try {
    result = await invoke("undo_latest");
  } catch (err) {
    setBusy(false);
    await refreshCanUndo();
    await messageBox("error", "Undo failed", String(err));
    setStatus("Undo failed.");
    return;
  }
  setBusy(false);
  await refreshCanUndo();

  const failed = result.errors.length;
  const message = `Restored ${result.restored} file(s)` + (failed > 0 ? `; ${failed} could not be restored.` : ".");
  if (failed > 0) {
    await messageBox("error", "Undo incomplete", formatErrors(`${failed} file(s) could not be restored:`, result.errors));
  }
  // Restored files are back in the source folder, so rescan to show them for review again.
  if (canScan()) await scan();
  setStatus(message);
}

function setVisibleChecked(value) {
  for (const item of state.view) {
    if (canSelect(item)) {
      item.checked = value;
      updateRow(item);
    }
  }
  updateSummary();
}

ui.browseSource.addEventListener("click", () => browse(ui.source, "Select the folder to sort"));
ui.browseTargets.addEventListener("click", () => browse(ui.targets, "Select the folder containing target folders"));
ui.source.addEventListener("input", updateButtons);
ui.targets.addEventListener("input", updateButtons);
ui.scan.addEventListener("click", scan);
ui.selectAll.addEventListener("click", () => setVisibleChecked(true));
ui.selectNone.addEventListener("click", () => setVisibleChecked(false));
ui.showUnmatched.addEventListener("change", rebuildView);
ui.filter.addEventListener("input", rebuildView);
ui.move.addEventListener("click", moveSelected);
ui.undo.addEventListener("click", undoLastMove);

// ---- Startup ----

(async function init() {
  const settings = await invoke("get_settings");
  ui.source.value = settings.sourceFolder;
  ui.targets.value = settings.targetsFolder;
  setStatus("Choose a source folder and a targets folder, then click Scan.");
  updateSummary();
  renderAll();
  await refreshCanUndo();
})();
