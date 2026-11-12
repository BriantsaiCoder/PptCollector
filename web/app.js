const byId = id => document.getElementById(id);
const bridge = window.__TAURI__;
const PAGE_SIZE = 40;
const MIXED_SIZES = "結果包含不同尺寸，請依尺寸勾選後匯出。";
const state = { folder: "", terms: [], phase: "", scan: null, selected: new Set(), page: 0, exported: false };
const field = byId("term");
const fileName = byId("name");
const radios = [...document.querySelectorAll("input[name=mode]")];

function notify(text, kind = "") {
  const status = byId("status");
  status.textContent = text;
  status.className = `notice ${kind}`;
  status.hidden = !text;
}

function labelPath(id, text) {
  const element = byId(id);
  element.textContent = text;
  element.title = text;
}

function stem() {
  return fileName.value.trim().replace(/\.pptx$/i, "");
}

function outputPath() {
  if (!state.folder) return "存回所選資料夾";
  const separator = state.folder.includes("\\") ? "\\" : "/";
  return state.folder.replace(/[\\/]$/, "") + separator + (stem() || "搜尋結果") + ".pptx";
}

function updateButtons() {
  const busy = Boolean(state.phase);
  const scanning = state.phase === "search" || state.phase === "cancel";
  byId("choose").disabled = busy;
  field.disabled = busy;
  fileName.disabled = busy;
  for (const radio of radios) radio.disabled = busy;
  for (const chip of byId("chips").querySelectorAll("button")) chip.disabled = busy;
  byId("add").disabled = busy || !field.value.trim();
  byId("search").disabled = busy || !state.folder || (!state.terms.length && !field.value.trim());
  byId("search").textContent = scanning ? "搜尋中…" : "搜尋投影片";
  byId("cancel").hidden = !scanning;
  byId("cancel").disabled = state.phase === "cancel";
  byId("cancel").textContent = state.phase === "cancel" ? "正在取消…" : "取消搜尋";
  byId("toggle").disabled = busy;
  byId("size-select").disabled = busy;
  byId("open").hidden = !state.exported;
  byId("open").disabled = busy;
  const mixed = new Set((state.scan?.hits || []).filter(hit => state.selected.has(hit.id)).map(sizeKey)).size > 1;
  byId("export").disabled = busy || mixed || !state.selected.size || !stem();
  byId("export").title = mixed ? "請在結果上方選擇相同尺寸。" : "";
  byId("export").textContent = state.phase === "export" ? "匯出中…" : "匯出 .pptx";
  const checks = byId("rows").querySelectorAll("input[type=checkbox]");
  for (const check of checks) check.disabled = busy;
  byId("previous").disabled = busy || state.page === 0;
  byId("next").disabled = busy || (state.page + 1) * PAGE_SIZE >= (state.scan?.hits.length || 0);
  labelPath("output-path", outputPath());
}

function resetResults() {
  state.scan = null;
  state.selected.clear();
  state.page = 0;
  state.exported = false;
  notify("");
  drawResults();
}

function commitTerm() {
  const value = field.value.trim();
  if (!value) return;
  field.value = "";
  if (!state.terms.some(term => term.toLowerCase() === value.toLowerCase())) {
    state.terms.push(value);
    resetResults();
  }
  drawChips();
  updateButtons();
}

function drawChips() {
  const list = byId("chips");
  list.replaceChildren();
  for (const [index, term] of state.terms.entries()) {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = `${term} ×`;
    button.title = term;
    button.setAttribute("aria-label", `移除關鍵字 ${term}`);
    button.addEventListener("click", () => {
      state.terms.splice(index, 1);
      resetResults();
      drawChips();
      field.focus();
    });
    item.append(button);
    list.append(item);
  }
}

function sizeKey(hit) {
  return `${hit.width}:${hit.height}`;
}

function sizeLabel(hit) {
  const centimeters = emu => (emu / 360000).toFixed(2);
  return `${centimeters(hit.width)} × ${centimeters(hit.height)} cm`;
}

function drawResults() {
  const found = state.scan?.hits || [];
  const sizes = new Map();
  for (const hit of found) {
    const key = sizeKey(hit);
    const entry = sizes.get(key) || { name: sizeLabel(hit), count: 0 };
    entry.count += 1;
    sizes.set(key, entry);
  }
  const sizesSelect = byId("size-select");
  sizesSelect.replaceChildren(new Option("選擇相同尺寸", ""));
  for (const [key, size] of sizes) sizesSelect.add(new Option(`${size.name}（${size.count} 張）`, key));
  byId("size-panel").hidden = sizes.size < 2;
  byId("empty").hidden = found.length > 0;
  byId("table-wrap").hidden = !found.length;
  byId("pages").hidden = found.length <= PAGE_SIZE;
  byId("toggle").hidden = !found.length;
  byId("result-summary").textContent = state.scan
    ? `搜尋 ${state.scan.files} 份簡報 · 找到 ${found.length} 張${state.scan.legacy ? ` · 略過 ${state.scan.legacy} 份舊版 .ppt` : ""}`
    : "可在匯出前勾選要保留的投影片。";
  byId("empty-title").textContent = state.scan ? "找不到符合的投影片" : "先選資料夾，再輸入關鍵字";
  byId("empty-hint").textContent = state.scan ? "試試其他關鍵字，或切換成「任一符合」。" : "命中的投影片會顯示來源、頁碼及文字片段。";
  const issues = state.scan?.issues || [];
  byId("issues").hidden = !issues.length;
  byId("issues").open = false;
  byId("issue-summary").textContent = `${issues.length} 個項目未能讀取，結果可能不完整`;
  byId("issue-list").replaceChildren(...issues.map(issue => {
    const row = document.createElement("li");
    row.textContent = issue;
    return row;
  }));
  drawPage();
}

function addText(parent, kind, content, style) {
  const node = document.createElement(kind);
  node.textContent = content;
  if (style) node.className = style;
  parent.append(node);
  return node;
}

function drawPage() {
  const hits = state.scan?.hits || [];
  const rowBody = byId("rows");
  rowBody.replaceChildren();
  const start = state.page * PAGE_SIZE;
  for (const hit of hits.slice(start, start + PAGE_SIZE)) {
    const row = rowBody.insertRow();
    const check = document.createElement("input");
    check.type = "checkbox";
    check.checked = state.selected.has(hit.id);
    check.setAttribute("aria-label", `選取 ${hit.path} 第 ${hit.page} 頁`);
    check.addEventListener("change", () => {
      if (check.checked) state.selected.add(hit.id);
      else state.selected.delete(hit.id);
      byId("size-select").value = "";
      updateSelection();
    });
    row.insertCell().append(check);
    const source = row.insertCell();
    const pieces = hit.path.split(/[\\/]/);
    addText(source, "span", pieces.pop(), "file-name").title = hit.path;
    if (pieces.length) addText(source, "span", pieces.join(" / "), "row-detail");
    row.insertCell().textContent = String(hit.page);
    const match = row.insertCell();
    addText(match, "span", hit.terms.join("、"), "matched");
    addText(match, "span", hit.snippet, "snippet");
  }
  byId("page-label").textContent = hits.length ? `第 ${state.page + 1} / ${Math.ceil(hits.length / PAGE_SIZE)} 頁` : "";
  byId("table-wrap").scrollTop = 0;
  updateSelection();
}

function updateSelection() {
  const found = state.scan?.hits || [];
  const sizes = new Set(found.filter(hit => state.selected.has(hit.id)).map(sizeKey));
  byId("toggle").textContent = state.selected.size === found.length ? "取消全選" : "全選";
  byId("selection").textContent = state.selected.size
    ? `已選 ${state.selected.size} 張${sizes.size > 1 ? " · 尺寸不同" : ""}`
    : "尚未選取投影片";
  if (sizes.size < 2 && byId("status").textContent === MIXED_SIZES) notify("");
  updateButtons();
}

async function invoke(command, args) {
  if (!bridge) throw new Error("請從 PptCollector 桌面應用程式執行。");
  return bridge.core.invoke(command, args);
}

function errorText(error) {
  return error instanceof Error ? error.message : String(error);
}

field.addEventListener("input", updateButtons);
field.addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.isComposing && field.value.trim()) {
    event.preventDefault();
    commitTerm();
  }
});
byId("add").addEventListener("click", () => { commitTerm(); field.focus(); });
for (const radio of radios) {
  radio.addEventListener("change", () => {
    byId("mode-hint").textContent = radio.value === "all"
      ? "同一張投影片須包含所有關鍵字。" : "投影片包含任一關鍵字即列入結果。";
    resetResults();
  });
}

byId("choose").addEventListener("click", async () => {
  state.phase = "pick";
  updateSelection();
  try {
    const folder = await invoke("pick_folder");
    if (folder) {
      state.folder = folder;
      labelPath("folder", folder);
      resetResults();
    }
  } catch (error) { notify(errorText(error), "error"); }
  finally { state.phase = ""; updateSelection(); }
  if (state.folder) field.focus();
});

byId("search-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (state.phase || !state.folder) return;
  commitTerm();
  if (!state.terms.length) return;
  resetResults();
  state.phase = "search";
  byId("progress").removeAttribute("value");
  byId("progress-label").textContent = "正在讀取資料夾…";
  byId("progress-box").hidden = false;
  updateSelection();
  try {
    const requireAll = radios.some(radio => radio.checked && radio.value === "all");
    state.scan = await invoke("search", { terms: state.terms, requireAll });
    state.selected = new Set(state.scan.hits.map(hit => hit.id));
    drawResults();
    if (new Set(state.scan.hits.map(sizeKey)).size > 1) {
      notify(MIXED_SIZES);
    }
  } catch (error) {
    notify(errorText(error) === "已取消" ? "搜尋已取消。" : errorText(error),
      errorText(error) === "已取消" ? "" : "error");
  } finally {
    state.phase = "";
    byId("progress-box").hidden = true;
    updateSelection();
  }
});

byId("cancel").addEventListener("click", async () => {
  state.phase = "cancel";
  updateSelection();
  try { await invoke("cancel_scan"); }
  catch (error) { notify(errorText(error), "error"); }
});

byId("toggle").addEventListener("click", () => {
  if (!state.scan) return;
  state.selected = state.selected.size === state.scan.hits.length
    ? new Set() : new Set(state.scan.hits.map(hit => hit.id));
  byId("size-select").value = "";
  drawPage();
});

byId("size-select").addEventListener("change", event => {
  const size = event.target.value;
  if (!size || !state.scan) return;
  state.selected = new Set(state.scan.hits.filter(hit => sizeKey(hit) === size).map(hit => hit.id));
  drawPage();
});
byId("previous").addEventListener("click", () => { state.page -= 1; drawPage(); });
byId("next").addEventListener("click", () => { state.page += 1; drawPage(); });
fileName.addEventListener("input", updateSelection);
fileName.addEventListener("blur", () => { fileName.value = stem(); updateSelection(); });

byId("export-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (state.phase || !state.scan || byId("export").disabled) return;
  state.phase = "export";
  state.exported = false;
  notify("正在合併投影片並檢查輸出檔…");
  updateSelection();
  try {
    const result = await invoke("export", { revision: state.scan.revision, ids: [...state.selected], name: fileName.value });
    state.exported = true;
    notify(`已匯出 ${result.count} 張投影片。`, "success");
  } catch (error) { notify(errorText(error), "error"); }
  finally { state.phase = ""; updateSelection(); }
});
byId("open").addEventListener("click", async () => {
  try { await invoke("open_output_folder"); }
  catch (error) { notify(errorText(error), "error"); }
});

if (bridge) {
  bridge.event.listen("scan-progress", ({ payload }) => {
    if (state.phase !== "search") return;
    const { done, total, hits } = payload;
    byId("progress").max = Math.max(total, 1);
    byId("progress").value = done;
    byId("progress-label").textContent = `已掃描 ${done} / ${total} 份 · ${hits} 張符合`;
  }).catch(error => notify(errorText(error), "error"));
}
updateButtons();
