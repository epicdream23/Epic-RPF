import * as viewport from './viewport.js';
import * as editor from './editor.js';

// ---------------------------------------------------------------- bridge
const pending = new Map();
let seq = 1;
function call(cmd, payload = {}) {
  return new Promise(resolve => {
    const id = seq++;
    pending.set(id, resolve);
    window.chrome.webview.postMessage({ id, cmd, ...payload });
  });
}
// Fire-and-forget (no reply expected) — used for window controls.
function post(cmd, payload = {}) { window.chrome.webview.postMessage({ id: 0, cmd, ...payload }); }
// Like call(), but also hands File objects to the host, which receives their REAL
// disk paths (postMessageWithAdditionalObjects). Imports use paths instead of base64 —
// a base64 string of a big file blows past JS's max string length.
function callWithFiles(cmd, payload, files) {
  return new Promise(resolve => {
    const id = seq++;
    pending.set(id, resolve);
    window.chrome.webview.postMessageWithAdditionalObjects({ id, cmd, ...payload }, files);
  });
}
window.chrome.webview.addEventListener('message', ev => {
  const m = ev.data;
  if (m && m.reqId && pending.has(m.reqId)) {
    const r = pending.get(m.reqId);
    pending.delete(m.reqId);
    r(m);
  } else if (m && m.type === 'fschange') {
    onFsChange(m);
  } else if (m && m.type === 'dragOutDone') {
    // Our own OLE drag finished; ignore drop events for a moment — a drop back onto
    // our own window arrives as a normal HTML drop and must NOT be re-imported.
    dragOutUntil = performance.now() + 1000;
  }
});
let dragOutUntil = 0;   // while now() < this, drops are our own drag-out — ignore
function selfDropActive() { return performance.now() < dragOutUntil; }

// ---------------------------------------------------------------- dom
const $ = id => document.getElementById(id);
const folderInput = $('folder'), gen9Chk = $('gen9'), mountBtn = $('mount'), browseBtn = $('browse');
const mountInfo = $('mountInfo'), treeEl = $('tree'), filterInput = $('filter');
const optExt = $('optExt'), optArchive = $('optArchive'), optFolder = $('optFolder');
const tabsEl = $('tabs'), inspBody = $('inspBody');
const statusLeft = $('statusLeft'), statusRight = $('statusRight');
const listBody = $('listBody'), crumbsEl = $('crumbs');
const modal = $('modal'), modalList = $('modalList'), modalCancel = $('modalCancel');

function setStatus(msg, isErr = false) {
  msg = String(msg == null ? '' : msg);
  if (msg.length > 300) msg = msg.slice(0, 300) + '…';   // keep the status bar readable
  statusLeft.textContent = msg;
  statusLeft.title = msg;
  statusLeft.style.color = isErr ? 'var(--danger)' : '';
}

// ---- loading bar -------------------------------------------------------------
// Indeterminate top bar so the app never feels frozen. Ref-counted so overlapping
// operations keep it visible until the last finishes. `delay` lets fast operations
// (file opens) skip the bar entirely unless they run long (>250 ms).
let busyCount = 0;
function busyInc() { busyCount++; $('progress').hidden = false; }
function busyDec() { busyCount = Math.max(0, busyCount - 1); if (busyCount === 0) $('progress').hidden = true; }
async function withProgress(promise, delay = 0) {
  let shown = false, timer = null;
  const show = () => { shown = true; busyInc(); };
  if (delay > 0) timer = setTimeout(show, delay); else show();
  try { return await promise; }
  finally { if (timer) clearTimeout(timer); if (shown) busyDec(); }
}

// Image icon system. Icons live in wwwroot/icons/<name>.png — folders/archives
// use 'folder'/'archive', files use their extension (e.g. ydr.png, xml.png), and
// anything without a matching image falls back to file.png. To customise an icon,
// just replace the PNG of the same name; no code changes needed.
// Special folder names get their own coloured icon (OpenIV-style), e.g. update,
// common, data, models… To add one: drop a folder-<type>.png in icons/ and map it.
const FOLDER_ICONS = {
  update: 'folder-update', x64: 'folder-update', common: 'folder-common', content: 'folder-content',
  data: 'folder-data', metadata: 'folder-data',
  models: 'folder-models', model: 'folder-models', props: 'folder-models',
  textures: 'folder-images', images: 'folder-images', image: 'folder-images',
  audio: 'folder-sounds', sounds: 'folder-sounds', sfx: 'folder-sounds',
  text: 'folder-text', lang: 'folder-text',
  levels: 'folder-maps', maps: 'folder-maps', map: 'folder-maps',
  anim: 'folder-anim', anims: 'folder-anim', cutscene: 'folder-anim', cuts: 'folder-anim', movies: 'folder-anim',
  user: 'folder-user', appdata: 'folder-user',
  config: 'folder-config', settings: 'folder-config',
  temp: 'folder-temp', tmp: 'folder-temp', cache: 'folder-temp',
  epic: 'folder-epic',
};
function iconName(node) {
  if (node.kind === 'folder' || node.kind === 'dir')
    return FOLDER_ICONS[node.name.toLowerCase()] || 'folder';
  if (node.kind === 'archive') return 'archive';
  return (node.name.split('.').pop() || 'file').toLowerCase();
}
// Folders named "epic" (the user's own mod folder) get a glowing, animated label.
function isEpicNode(node) { return (node.kind === 'folder' || node.kind === 'dir') && node.name.toLowerCase() === 'epic'; }
function iconImg(name, cls = 'ic') {
  return `<img class="${cls}" src="./icons/${name}.png" onerror="this.onerror=null;this.src='./icons/file.png'" alt="">`;
}
function iconEl(name) {
  const img = document.createElement('img');
  img.className = 'ic';
  img.src = `./icons/${name}.png`;
  img.onerror = () => { img.onerror = null; img.src = './icons/file.png'; };
  return img;
}

// ---------------------------------------------------------------- detect + mount
const childrenCache = new Map();
let mountRoots = [], rootName = 'GTA V';

async function detectAndMount() {
  setStatus('Searching for GTA V installs…');
  const res = await call('detect');
  const installs = res.installs || [];
  if (installs.length === 0) { setStatus('No GTA V install found automatically — pick a folder and Mount.'); return; }
  if (installs.length === 1) {
    const i = installs[0];
    folderInput.value = i.path; gen9Chk.checked = i.gen9;
    setStatus(`Found ${i.label} — mounting…`);
    await mount();
    return;
  }
  showInstallModal(installs);
}

function showInstallModal(installs) {
  modalList.innerHTML = '';
  for (const i of installs) {
    const el = document.createElement('div');
    el.className = 'install';
    el.innerHTML = `<span class="src">${i.source}</span><span class="ed">${i.gen9 ? 'Enhanced' : 'Legacy'}</span>
      <span class="pth">${i.path}</span>`;
    el.onclick = () => {
      modal.hidden = true;
      folderInput.value = i.path; gen9Chk.checked = i.gen9;
      mount();
    };
    modalList.appendChild(el);
  }
  modal.hidden = false;
}
modalCancel.onclick = () => { modal.hidden = true; setStatus('Pick a folder and Mount.'); };

async function mount() {
  const folder = folderInput.value.trim();
  if (!folder) return;
  mountBtn.disabled = true;
  setStatus(`Mounting ${folder}…`);
  const res = await withProgress(call('mount', { folder, gen9: gen9Chk.checked }));   // ~2-3s: show at once
  mountBtn.disabled = false;
  if (!res.ok) { setStatus('Mount failed: ' + (res.error || 'unknown'), true); mountInfo.textContent = ''; return; }
  mountInfo.innerHTML =
    `<b>${res.archives.toLocaleString()}</b> archives · <b>${res.files.toLocaleString()}</b> files · ${res.gen9 ? 'Gen9' : 'Gen8'}`;
  setStatus(`Mounted ${res.folder}` + (res.mountErrors ? ` (${res.mountErrors} archive errors)` : ''));
  childrenCache.clear();
  rootName = res.rootName || 'GTA V';
  mountRoots = res.roots;
  buildTree(mountRoots);
  ensureExplorerTab();
  navigate([{ id: 0, name: rootName }]);
}

browseBtn.onclick = async () => { const res = await call('pickFolder'); if (res.path) folderInput.value = res.path; };
mountBtn.onclick = mount;
folderInput.addEventListener('keydown', e => { if (e.key === 'Enter') mount(); });

async function getChildren(id) {
  if (id === 0) return mountRoots;
  if (childrenCache.has(id)) return childrenCache.get(id);
  const res = await call('expand', { node: id });
  childrenCache.set(id, res.nodes || []);
  return childrenCache.get(id);
}

// ---------------------------------------------------------------- tree (containers only)
function buildTree(roots) {
  treeEl.innerHTML = '';
  for (const n of roots) if (n.container) treeEl.appendChild(makeTreeNode(n, 0));
}

function makeTreeNode(n, depth) {
  const el = document.createElement('div');
  el.className = 'node'; el.__node = n; el.__depth = depth;

  const row = document.createElement('div');
  row.className = 'row';
  row.style.paddingLeft = (depth * 14 + 6) + 'px';

  const tw = document.createElement('span');
  tw.className = 'twisty' + (n.expandable ? '' : ' empty');
  tw.textContent = '▶';

  const icEl = iconEl(iconName(n));

  const lab = document.createElement('span');
  lab.className = isEpicNode(n) ? 'label epic-glow' : 'label'; lab.textContent = n.name;

  row.append(tw, icEl, lab);
  const kids = document.createElement('div');
  kids.className = 'children';
  el.append(row, kids);

  tw.onclick = e => { e.stopPropagation(); toggleTree(el); };
  row.onclick = () => onTreeClick(el);
  row.oncontextmenu = e => { e.preventDefault(); e.stopPropagation(); showMenu(menuFor(el.__node), e.clientX, e.clientY); };
  return el;
}

async function toggleTree(el) {
  const n = el.__node;
  if (!n.expandable) return;
  if (!el.__loaded) {
    const kids = el.querySelector('.children');
    for (const c of await getChildren(n.id)) if (c.container) kids.appendChild(makeTreeNode(c, el.__depth + 1));
    el.__loaded = true;
  }
  el.classList.toggle('open');
}

let selectedTreeEl = null;
async function onTreeClick(el) {
  if (selectedTreeEl) selectedTreeEl.classList.remove('selected');
  selectedTreeEl = el; el.classList.add('selected');
  if (el.__node.expandable && !el.classList.contains('open')) await toggleTree(el);
  navigate(crumbsFromTree(el));
}

function crumbsFromTree(el) {
  const chain = [];
  let cur = el;
  while (cur && cur.classList && cur.classList.contains('node')) {
    chain.unshift({ id: cur.__node.id, name: cur.__node.name, path: cur.__node.path });
    cur = cur.parentElement.closest('.node');
  }
  return [{ id: 0, name: rootName }, ...chain];
}

// ---------------------------------------------------------------- global search
let searchActive = false, searchData = null, searchTimer = null;

async function runSearch() {
  const q = filterInput.value.trim();
  if (q.length === 0) { exitSearch(); return; }
  if (!mountRoots.length) return;
  const scope = optFolder.checked ? 'folder' : (optArchive.checked ? 'archive' : 'none');
  const node = explorer.crumbs.length ? explorer.crumbs[explorer.crumbs.length - 1].id : 0;
  setStatus(`Searching “${q}”…`);
  const res = await call('search', { query: q, ext: optExt.checked, scope, node, limit: 1000 });
  searchActive = true; searchData = res;
  ensureExplorerTab(); activate(explorerTab);
  setStatus(`${res.total.toLocaleString()} match${res.total === 1 ? '' : 'es'} for “${q}”`);
}
function exitSearch() {
  searchActive = false; searchData = null;
  if (activeTab && activeTab.kind === 'explorer') { renderCrumbs(); renderList(explorer.items); }
}
filterInput.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(runSearch, 300); });
filterInput.addEventListener('keydown', e => {
  if (e.key === 'Enter') { clearTimeout(searchTimer); runSearch(); }
  else if (e.key === 'Escape') { filterInput.value = ''; exitSearch(); }
});
[optExt, optArchive, optFolder].forEach(c => c.addEventListener('change', () => { if (filterInput.value.trim()) runSearch(); }));

function renderSearch() {
  const d = searchData || { results: [], total: 0 };
  const q = d.query || filterInput.value.trim();
  crumbsEl.innerHTML = `<span class="crumb last">Search: “${q}” — ${d.total.toLocaleString()} result${d.total === 1 ? '' : 's'}${d.capped ? ` (showing first ${d.results.length})` : ''}</span>`;
  document.querySelector('#pane-explorer .list-head').style.display = 'none';
  listBody.className = 'list-body';
  listBody.innerHTML = '';
  if (!d.results.length) { listBody.innerHTML = '<div class="muted" style="padding:14px">No matches.</div>'; return; }
  for (const r of d.results) listBody.append(makeResultRow(r));
}
function makeResultRow(r) {
  const row = document.createElement('div'); row.className = 'result-row'; row.__r = r;
  row.innerHTML = `<span class="c-name">${iconImg(iconName(r))}<span class="label">${r.name}</span></span><span class="rpath">${r.path}</span>`;
  row.onclick = () => { if (selectedRow) selectedRow.classList.remove('selected'); selectedRow = row; row.classList.add('selected'); onResult(r, false); };
  row.ondblclick = () => onResult(r, true);
  return row;
}
async function onResult(r, open) {
  const rv = await call('reveal', { node: r.rid });
  if (!rv.ok) { setStatus('Could not locate ' + r.name, true); return; }
  searchActive = false;
  filterInput.value = '';
  await navigate(rv.crumbs);
  selectByName(r.name);
  if (open && r.kind !== 'archive') openFile({ id: rv.fileId, name: r.name, viewer: rv.viewer });
}
function selectByName(name) {
  const o = explorer.ordered.find(x => x.node.name === name);
  if (!o) return;
  setSelection([o.node]); selAnchor = o.row.__idx; selectedRow = o.row;
  o.row.scrollIntoView({ block: 'center' });
  showInspectorForSelection(o.node);
}

// ---------------------------------------------------------------- explorer (detail list)
const explorer = { crumbs: [], items: [], ordered: [] };

// directory history (Windows-style back / forward / up)
let navHist = [], navPos = -1;
async function navigate(crumbs) {
  navHist = navHist.slice(0, navPos + 1);
  navHist.push(crumbs);
  navPos = navHist.length - 1;
  await doNavigate(crumbs);
}
async function doNavigate(crumbs) {
  searchActive = false;
  clearSelection();
  explorer.crumbs = crumbs;
  const last = crumbs[crumbs.length - 1];
  explorer.items = await getChildren(last.id);
  ensureExplorerTab();
  activate(explorerTab);
  updateNavButtons();
}
function navBack() { if (navPos > 0) { navPos--; doNavigate(navHist[navPos]); } }
function navFwd() { if (navPos < navHist.length - 1) { navPos++; doNavigate(navHist[navPos]); } }
function navUp() { const c = explorer.crumbs; if (c.length > 1) navigate(c.slice(0, -1)); }
function updateNavButtons() {
  $('navBack').disabled = navPos <= 0;
  $('navFwd').disabled = navPos >= navHist.length - 1;
  $('navUp').disabled = (explorer.crumbs.length <= 1) || searchActive;
}
$('navBack').onclick = navBack;
$('navFwd').onclick = navFwd;
$('navUp').onclick = navUp;
window.addEventListener('keydown', e => {
  if (!e.altKey || e.ctrlKey || (activeTab && activeTab.kind === 'edit')) return;
  if (e.key === 'ArrowLeft') { e.preventDefault(); navBack(); }
  else if (e.key === 'ArrowRight') { e.preventDefault(); navFwd(); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); navUp(); }
});
updateNavButtons();   // start with the global arrows in their correct enabled state

// ---------------------------------------------------------------- resizable + collapsible panels
// Each side panel can be dragged wider/narrower (a splitter) and collapsed to a
// thin rail (a toggle). Sizes + collapsed state persist across sessions.
const PANELS = {
  sidebar:   { el: () => $('sidebar'),   prop: 'width',     min: 180, max: 680, toggle: 'sideToggle',     open: '⟨', shut: '⟩' },
  inspector: { el: () => $('inspector'), prop: 'width',     min: 180, max: 680, toggle: 'inspToggle',     open: '⟩', shut: '⟨' },
  vpmodels:  { el: () => $('vpModels'),  prop: 'flexBasis', min: 120, max: 520, toggle: 'vpModelsToggle', open: '⟨', shut: '⟩' },
};
function setPanelSize(name, w) {
  const p = PANELS[name], el = p.el(); if (!el) return;
  w = Math.max(p.min, Math.min(p.max, w));
  el.style[p.prop] = w + 'px';
  try { localStorage.setItem('panelw_' + name, String(w)); } catch { }
}
function setPanelCollapsed(name, on) {
  const p = PANELS[name], el = p.el(); if (!el) return;
  el.classList.toggle('collapsed', on);
  const btn = $(p.toggle); if (btn) { btn.textContent = on ? p.shut : p.open; btn.title = on ? 'Expand panel' : 'Collapse panel'; }
  try { localStorage.setItem('panelc_' + name, on ? '1' : '0'); } catch { }
}
function togglePanel(name) { setPanelCollapsed(name, !PANELS[name].el().classList.contains('collapsed')); }
function restorePanels() {
  for (const name in PANELS) {
    const w = parseFloat(localStorage.getItem('panelw_' + name));
    if (!isNaN(w)) setPanelSize(name, w);
    setPanelCollapsed(name, localStorage.getItem('panelc_' + name) === '1');
  }
}
function makeResizer(splitId, name, side) {
  const split = $(splitId); if (!split) return;
  split.addEventListener('mousedown', e => {
    const el = PANELS[name].el();
    if (!el || el.classList.contains('collapsed')) return;
    e.preventDefault();
    const startX = e.clientX, startW = el.getBoundingClientRect().width;
    split.classList.add('active'); document.body.classList.add('resizing');
    const onMove = ev => setPanelSize(name, side === 'right' ? startW - (ev.clientX - startX) : startW + (ev.clientX - startX));
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      split.classList.remove('active'); document.body.classList.remove('resizing');
    };
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  });
}
$('sideToggle').onclick = () => togglePanel('sidebar');
$('inspToggle').onclick = () => togglePanel('inspector');
$('vpModelsToggle').onclick = () => togglePanel('vpmodels');
makeResizer('splitSidebar', 'sidebar', 'left');
makeResizer('splitInspector', 'inspector', 'right');
makeResizer('vpSplit', 'vpmodels', 'left');
restorePanels();

function renderCrumbs() {
  crumbsEl.innerHTML = '';
  explorer.crumbs.forEach((c, idx) => {
    if (idx) { const s = document.createElement('span'); s.className = 'crumb-sep'; s.textContent = '›'; crumbsEl.append(s); }
    const el = document.createElement('span');
    el.className = 'crumb' + (idx === explorer.crumbs.length - 1 ? ' last' : '');
    el.textContent = c.name;
    el.onclick = () => navigate(explorer.crumbs.slice(0, idx + 1));
    crumbsEl.append(el);
  });
}

let selectedRow = null;
let sortMode = 'name';   // name | type | size | attr
let sortDir = 1;         // 1 = ascending, -1 = descending
let viewMode = 'details'; // details | list | icons

// ---- multi-selection (Windows-style: click / Ctrl+click / Shift+click) ----
let selection = [];     // selected node objects
let selAnchor = -1;     // index into explorer.ordered for shift-range
function isSelected(node) { return selection.some(s => s.id === node.id); }
function clearSelection() { selection = []; selAnchor = -1; selectedRow = null; }
function setSelection(nodes) { selection = nodes.slice(); applySelectionStyles(); }
function toggleSelection(node) {
  if (isSelected(node)) selection = selection.filter(s => s.id !== node.id);
  else selection.push(node);
  applySelectionStyles();
}
function applySelectionStyles() {
  for (const o of explorer.ordered) o.row.classList.toggle('selected', isSelected(o.node));
}
function onRowClick(node, row, e) {
  const idx = row.__idx;
  if (e.shiftKey && selAnchor >= 0) {
    const a = Math.min(selAnchor, idx), b = Math.max(selAnchor, idx);
    setSelection(explorer.ordered.slice(a, b + 1).map(o => o.node));
  } else if (e.ctrlKey || e.metaKey) {
    toggleSelection(node); selAnchor = idx;
  } else {
    setSelection([node]); selAnchor = idx;
  }
  selectedRow = row;
  showInspectorForSelection(node);
}

function cmpItems(a, b) {
  const byName = a.name.localeCompare(b.name, undefined, { numeric: true });
  if (sortMode === 'size') { const sa = a.container ? -1 : (a.size || 0), sb = b.container ? -1 : (b.size || 0); return (sa - sb) || byName; }
  if (sortMode === 'type') return (a.type || '').localeCompare(b.type || '') || byName;
  if (sortMode === 'attr') return (a.attrs || '').localeCompare(b.attrs || '') || byName;
  return byName;
}
function sortItems(arr) { return arr.slice().sort((a, b) => sortDir * cmpItems(a, b)); }

function renderList(items) {
  listBody.className = 'list-body view-' + viewMode;
  document.querySelector('#pane-explorer .list-head').style.display = viewMode === 'details' ? '' : 'none';
  listBody.innerHTML = '';
  explorer.ordered = [];

  // Group by type (OpenIV-style): "Folder (4)", "XML (2)", …
  const groups = new Map();
  for (const it of items) { const t = it.type || 'Other'; (groups.get(t) || (groups.set(t, []), groups.get(t))).push(it); }
  const rank = t => (t === 'Folder' ? 0 : t === 'Archive' ? 1 : 2);
  const order = [...groups.keys()].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));

  for (const t of order) {
    const arr = sortItems(groups.get(t));
    const gh = document.createElement('div');
    gh.className = 'group-head';
    gh.innerHTML = `<span class="gt">${t}</span><span class="gc">(${arr.length})</span>`;
    listBody.appendChild(gh);
    for (const it of arr) {
      const row = makeRow(it);
      row.__idx = explorer.ordered.length;
      explorer.ordered.push({ node: it, row });
      listBody.appendChild(row);
    }
  }
  applySelectionStyles();
}

function makeRow(it) {
  const row = document.createElement('div');
  row.className = 'row-item'; row.__node = it;
  if (viewMode === 'details') {
    const sizeText = it.container
      ? (it.count != null ? `${it.count} item${it.count === 1 ? '' : 's'}` : '')
      : (it.size >= 0 ? fmtSize(it.size) : '');
    const lc = isEpicNode(it) ? 'label epic-glow' : 'label';
    row.innerHTML =
      `<span class="c-name">${iconImg(iconName(it))}<span class="${lc}">${it.name}</span></span>` +
      `<span class="c-type">${it.type || ''}</span>` +
      `<span class="c-size">${sizeText}</span>` +
      `<span class="c-attr">${it.attrs || ''}</span>`;
  } else {
    row.innerHTML = `${iconImg(iconName(it))}<span class="${isEpicNode(it) ? 'label epic-glow' : 'label'}">${it.name}</span>`;
  }
  row.onclick = e => onRowClick(it, row, e);
  row.ondblclick = () => onItemDbl(it);
  row.oncontextmenu = e => {
    e.preventDefault(); e.stopPropagation();
    if (!isSelected(it)) { setSelection([it]); selAnchor = row.__idx; selectedRow = row; showInspectorForSelection(it); }
    showMenu(menuFor(it), e.clientX, e.clientY);
  };
  row.addEventListener('pointerdown', e => beginRowDrag(it, e));
  row.addEventListener('dragstart', e => e.preventDefault());  // suppress HTML5 drag; we do native OS drag
  return row;
}

// ---- native file drag-out (to Explorer / desktop / any app) ----
// HTML5 drag can't produce real OS files, so we detect the gesture here and the C#
// host runs an OLE DoDragDrop with an actual FileDrop. We fire on the first move past
// a small threshold (button still down) so the native drag picks up the gesture.
let dragRow = null;
function beginRowDrag(it, e) {
  if (e.button !== 0) return;
  dragRow = { it, x: e.clientX, y: e.clientY };
}
window.addEventListener('pointermove', e => {
  if (!dragRow) return;
  if (!(e.buttons & 1)) { dragRow = null; return; }     // button released — not a drag
  // Generous threshold: a tiny wiggle while clicking must not start an OS drag.
  if (Math.abs(e.clientX - dragRow.x) + Math.abs(e.clientY - dragRow.y) < 14) return;
  const it = dragRow.it; dragRow = null;
  const set = (isSelected(it) && selection.length > 1) ? selection : [it];
  const ids = set.filter(n => n && n.id !== 0 &&
    (n.kind === 'file' || n.kind === 'archive' || n.kind === 'dir' || n.kind === 'folder')).map(n => n.id);
  if (!ids.length) return;
  setStatus(ids.length === 1 ? `Dragging ${it.name}…` : `Dragging ${ids.length} items…`);
  dragOutUntil = Infinity;            // suppress drops until the bridge says the drag ended
  post('dragOut', { nodes: ids });
});
window.addEventListener('pointerup', () => { dragRow = null; });

// ---- rubber-band (marquee) selection ----------------------------------------
// Drag a box over empty space in the explorer list; every row the box touches is
// selected (Ctrl = add to the current selection). Coordinates are kept in the
// list's content space (offsetTop/scroll) so the box and hit-test survive scrolling.
(function marquee() {
  let on = false, ax = 0, ay = 0, lastX = 0, lastY = 0, box = null, additive = false, baseSel = [], scrollTimer = 0;
  const uniqById = arr => { const m = new Map(); for (const n of arr) m.set(n.id, n); return [...m.values()]; };

  function update() {
    const r = listBody.getBoundingClientRect();
    const cx = lastX - r.left + listBody.scrollLeft, cy = lastY - r.top + listBody.scrollTop;
    const x1 = Math.min(ax, cx), y1 = Math.min(ay, cy), x2 = Math.max(ax, cx), y2 = Math.max(ay, cy);
    box.style.cssText = `left:${x1}px;top:${y1}px;width:${x2 - x1}px;height:${y2 - y1}px`;
    const hits = [];
    for (const o of explorer.ordered) {
      const t = o.row.offsetTop, b = t + o.row.offsetHeight, l = o.row.offsetLeft, rr = l + o.row.offsetWidth;
      if (rr > x1 && l < x2 && b > y1 && t < y2) hits.push(o.node);
    }
    setSelection(additive ? uniqById([...baseSel, ...hits]) : hits);
  }
  function autoScroll() {
    const r = listBody.getBoundingClientRect();
    if (lastY > r.bottom - 22 && listBody.scrollTop < listBody.scrollHeight - listBody.clientHeight) { listBody.scrollTop += 16; update(); }
    else if (lastY < r.top + 22 && listBody.scrollTop > 0) { listBody.scrollTop -= 16; update(); }
  }
  listBody.addEventListener('pointerdown', e => {
    // Only start on empty space (a row handles its own click/drag), left button, not while searching.
    if (e.button !== 0 || searchActive || e.target.closest('.row-item')) return;
    on = true; additive = e.ctrlKey || e.metaKey;
    baseSel = additive ? selection.slice() : [];
    const r = listBody.getBoundingClientRect();
    ax = e.clientX - r.left + listBody.scrollLeft; ay = e.clientY - r.top + listBody.scrollTop;
    lastX = e.clientX; lastY = e.clientY;
    box = document.createElement('div'); box.className = 'marquee';
    box.style.cssText = `left:${ax}px;top:${ay}px;width:0;height:0`;
    listBody.appendChild(box);
    if (!additive) { clearSelection(); applySelectionStyles(); }
    try { listBody.setPointerCapture(e.pointerId); } catch {}
    scrollTimer = setInterval(autoScroll, 50);
    e.preventDefault();
  });
  listBody.addEventListener('pointermove', e => { if (!on) return; lastX = e.clientX; lastY = e.clientY; update(); });
  function end(e) {
    if (!on) return; on = false;
    clearInterval(scrollTimer); scrollTimer = 0;
    if (box) { box.remove(); box = null; }
    try { listBody.releasePointerCapture(e.pointerId); } catch {}
    selAnchor = -1;
    if (selection.length) showInspectorForSelection(selection[selection.length - 1]);
  }
  listBody.addEventListener('pointerup', end);
  listBody.addEventListener('pointercancel', end);
})();

function setSort(m, dir) {
  if (dir === undefined) sortDir = (m === sortMode) ? -sortDir : 1;  // toggle on repeat
  else sortDir = dir;
  sortMode = m;
  if (activeTab && activeTab.kind === 'explorer') { renderList(explorer.items); updateSortHeaders(); }
}
function setView(m) { viewMode = m; if (activeTab && activeTab.kind === 'explorer') renderList(explorer.items); }
// Clickable column headers (Windows-style), with an asc/desc arrow on the active one.
function updateSortHeaders() {
  const map = { 'c-name': 'name', 'c-type': 'type', 'c-size': 'size', 'c-attr': 'attr' };
  for (const cls of Object.keys(map)) {
    const el = document.querySelector('#pane-explorer .list-head .' + cls);
    if (!el) continue;
    if (!el.dataset.base) el.dataset.base = el.textContent;
    el.classList.toggle('sorted', sortMode === map[cls]);
    el.textContent = el.dataset.base + (sortMode === map[cls] ? (sortDir > 0 ? ' ▲' : ' ▼') : '');
  }
}
(function wireSortHeaders() {
  const map = { 'c-name': 'name', 'c-type': 'type', 'c-size': 'size', 'c-attr': 'attr' };
  for (const cls of Object.keys(map)) {
    const el = document.querySelector('#pane-explorer .list-head .' + cls);
    if (el) { el.style.cursor = 'pointer'; el.onclick = () => setSort(map[cls]); }
  }
})();

function onItemDbl(item) {
  if (item.container) navigate([...explorer.crumbs, { id: item.id, name: item.name, path: item.path }]);
  else openFile(item);
}

async function openFile(n, as) {
  setStatus(`Opening ${n.name}…`);
  const res = await withProgress(call('open', { node: n.id, as }), 250);   // only if slow (>250ms)
  if (res.type === 'error') { setStatus('Open failed: ' + res.message, true); return; }
  if (res.type === 'model') openModelTab(res, n.id);
  else if (res.type === 'edit') openTab(n.id, 'edit', n.name, res, n.id);
  else if (res.type === 'hex') openTab(n.id, 'hex', n.name, res, n.id);
  else if (res.type === 'texture') openTab(n.id, 'texture', n.name, res, n.id);
  else if (res.type === 'image') openTab(n.id, 'image', n.name, res, n.id);
  else if (res.type === 'gfx') openTab(n.id, 'gfx', n.name, res, n.id);
  setStatus(`Opened ${n.name}`);
}

// Resource types whose default viewer is visual, but that can also be opened as
// CodeWalker XML on demand ("Edit as XML").
const XML_EDITABLE = new Set(['ydr', 'ydd', 'yft', 'ytd', 'ypt']);
function extOf(name) { return (name.split('.').pop() || '').toLowerCase(); }

// ---------------------------------------------------------------- tabs / panes
const tabs = [];
let activeTab = null, explorerTab = null;

function ensureExplorerTab() {
  if (!explorerTab) { explorerTab = { key: 'explorer', kind: 'explorer', title: 'Explorer', noClose: true }; tabs.unshift(explorerTab); }
  renderTabs();
}

function openTab(key, kind, title, data, nodeId) {
  let tab = tabs.find(t => t.key === key && t.kind === kind);
  if (!tab) { tab = { key, kind, title, data, nodeId }; tabs.push(tab); renderTabs(); }
  else { tab.data = data; if (nodeId != null) tab.nodeId = nodeId; }
  activate(tab);
}
function openModelTab(res, nodeId) { openTab('m' + res.path, 'model', res.name, res, nodeId); }
// The archive node id behind a tab (numeric tab keys ARE node ids; model tabs keep it separately).
function tabNodeId(t) { return (typeof t.key === 'number') ? t.key : (t.nodeId != null ? t.nodeId : null); }

function renderTabs() {
  tabsEl.innerHTML = '';
  for (const t of tabs) {
    const el = document.createElement('div');
    el.className = 'tab' + (t === activeTab ? ' active' : '');
    const icn = t.kind === 'explorer' ? 'explorer' : (t.title.split('.').pop() || 'file').toLowerCase();
    el.innerHTML = `${iconImg(icn)}<span>${t.title}</span>`;
    if (!t.noClose) {
      const x = document.createElement('span');
      x.className = 'close'; x.textContent = '×';
      x.onclick = e => { e.stopPropagation(); closeTab(t); };
      el.append(x);
      attachTabDrag(el, t);   // drag a tab out of the window to pop it out
      el.oncontextmenu = e => {
        e.preventDefault(); e.stopPropagation();
        showMenu([{ label: 'Open in new window', action: () => popoutTab(t) },
                  { sep: true }, { label: 'Close', action: () => closeTab(t) }], e.clientX, e.clientY);
      };
    }
    el.onclick = () => { if (el.__dragged) { el.__dragged = false; return; } activate(t); };
    tabsEl.appendChild(el);
  }
}

// Drag a tab; if released outside this window's bounds, tear it into a new window.
function attachTabDrag(el, t) {
  let drag = null;
  el.onpointerdown = e => {
    if (e.button !== 0 || e.target.classList.contains('close')) return;
    drag = { x: e.screenX, y: e.screenY, moved: false };
    try { el.setPointerCapture(e.pointerId); } catch {}
  };
  el.onpointermove = e => {
    if (!drag) return;
    if (!drag.moved && Math.abs(e.screenX - drag.x) + Math.abs(e.screenY - drag.y) > 10) {
      drag.moved = true; el.classList.add('dragging');
    }
  };
  el.onpointerup = e => {
    if (!drag) return;
    try { el.releasePointerCapture(e.pointerId); } catch {}
    el.classList.remove('dragging');
    const moved = drag.moved; drag = null;
    if (!moved) return;               // plain click -> onclick activates
    el.__dragged = true;              // swallow the click that follows
    const outside = e.screenX < window.screenX || e.screenY < window.screenY
      || e.screenX > window.screenX + window.outerWidth
      || e.screenY > window.screenY + window.outerHeight;
    if (outside) popoutTab(t);
  };
}

async function popoutTab(t) {
  if (t.noClose) return;
  const payload = JSON.stringify({ kind: t.kind, title: t.title, nodeId: tabNodeId(t), data: t.data });
  const res = await call('popout', { title: t.title, payload });
  if (res && res.ok) closeTab(t);
}

function closeTab(t) {
  if (t.kind === 'edit') editor.disposeTab(t);
  const i = tabs.indexOf(t);
  tabs.splice(i, 1);
  if (activeTab === t) activeTab = tabs[Math.min(i, tabs.length - 1)] || null;
  renderTabs();
  if (activeTab) activate(activeTab); else showPane('welcome');
}

function showPane(kind) {
  for (const p of document.querySelectorAll('.pane')) p.hidden = true;
  const pane = $('pane-' + kind);
  if (pane) pane.hidden = false;
}

function activate(tab) {
  activeTab = tab;
  renderTabs();
  showPane(tab.kind);
  if (tab.kind === 'explorer') { if (searchActive) renderSearch(); else { renderCrumbs(); renderList(explorer.items); } }
  else if (tab.kind === 'model') fillModel(tab.data);
  else if (tab.kind === 'edit') fillEdit(tab);
  else if (tab.kind === 'hex') renderHex(tab.data);
  else if (tab.kind === 'texture') renderTextures(tab.data);
  else if (tab.kind === 'image') fillImage(tab.data);
  else if (tab.kind === 'gfx') fillGfx(tab.data);
  showInspectorForTab(tab);
}

// ---------------------------------------------------------------- model pane
let modelTextures = [], curModel = null;
function fillModel(res) {
  curModel = res;
  collapseTextures();
  $('tgSkel').classList.remove('on');   // fresh model: skeleton overlay off
  viewport.setSkeleton(false);
  viewport.stopClip();
  additionsEnabled = new Set();         // additions start hidden on every model
  viewport.setAdditions([]);
  viewport.loadModel(res);
  fillModelList();
  fillLodSelector();
  fillModelTextures(res.textures || [], res.node);
  renderDetails();
  requestAnimationFrame(() => requestAnimationFrame(() => viewport.fit()));
  updateVpStats();
}
function fillLodSelector() {
  const sel = $('lodSel'); sel.innerHTML = '';
  for (const l of viewport.availableLods()) { const o = document.createElement('option'); o.value = l; o.textContent = l; sel.append(o); }
}
// A .ydd / .ypt holds several independent drawables — list them down the left so
// the user clicks one (or "All"). Double-click a name to rename it (custom, saved).
function fillModelList() {
  const wrap = $('vpModels'), list = $('vpModelList'), split = $('vpSplit');
  const names = viewport.partNames();
  list.innerHTML = '';
  const multi = names.length > 1;
  wrap.hidden = !multi; split.hidden = !multi;
  if (!multi) return;
  $('vpModelsTitle').textContent = `Models (${names.length})`;
  const add = (label, idx) => {
    const el = document.createElement('div');
    el.className = 'vp-model'; el.dataset.idx = String(idx); el.textContent = label; el.title = label + (idx >= 0 ? '  (double-click to rename)' : '');
    if (viewport.currentPart() === idx) el.classList.add('sel');
    el.onclick = () => selectPart(idx);
    if (idx >= 0) el.ondblclick = e => { e.stopPropagation(); editPartName(idx, el); };
    list.append(el);
  };
  add(`All (${names.length})`, -1);
  names.forEach((n, i) => add(n, i));
}
async function selectPart(idx) {
  // Lazily decode the drawable's geometry the first time it's shown (fast open).
  if (idx >= 0 && curModel && curModel.parts[idx] && curModel.parts[idx].lazy) {
    const res = await withProgress(call('modelPart', { node: curModel.node, index: idx }), 250);
    if (res.ok) { curModel.parts[idx] = res.part; viewport.setPartData(idx, res.part); }
  }
  viewport.setPart(idx);
  for (const el of $('vpModels').querySelectorAll('.vp-model')) el.classList.toggle('sel', parseInt(el.dataset.idx, 10) === idx);
  fillLodSelector();
  renderDetails();
  updateVpStats();
}
// Inline-rename a model; the name is saved client-side (LUT), never into the file.
function editPartName(idx, el) {
  const hash = viewport.partHash(idx);
  if (hash == null || !curModel) return;
  const inp = document.createElement('input');
  inp.className = 'vp-rename'; inp.value = el.textContent; el.textContent = ''; el.append(inp);
  inp.focus(); inp.select();
  const commit = async () => {
    const name = inp.value.trim();
    const res = await call('renameModel', { file: curModel.file, hash, name });
    el.textContent = (res.ok && name) ? name : (viewport.partName(idx) || '');
    viewport.setPartName(idx, el.textContent);
  };
  inp.onkeydown = e => { if (e.key === 'Enter') inp.blur(); else if (e.key === 'Escape') { inp.value = viewport.partName(idx) || ''; inp.blur(); } };
  inp.onblur = commit;
}

// ---- lazy texture thumbnails (decode on demand as they scroll into view) ----
const texObserver = new IntersectionObserver(es => {
  for (const e of es) if (e.isIntersecting) { texObserver.unobserve(e.target); loadTexThumb(e.target); }
}, { rootMargin: '150px' });
async function loadTexThumb(ph) {
  const res = await call('texImage', { node: ph.__node, index: ph.__index });
  if (res && res.img) { ph.style.backgroundImage = `url("${res.img}")`; ph.textContent = ''; ph.classList.remove('noimg'); ph.__img = res.img; }
  else { ph.classList.add('noimg'); ph.textContent = 'no preview'; }
}
function texThumb(t, node, cls) {
  const ph = document.createElement('div'); ph.className = cls + ' checker'; ph.__node = node; ph.__index = t.index;
  texObserver.observe(ph);
  return ph;
}
function openTexImageUrl(name, fmt, img) {
  openTab('img:' + name, 'image', name + '.dds', { dataUrl: img, format: fmt, name });
}

// ---- import / replace a texture inside an open .ytd / .ypt ----
// Pick an image or .dds; a .dds imports as-is, an image is re-encoded server-side to
// the original texture's format. The replaced texture keeps its name so the model
// keeps referencing it. The new bytes are written straight back into the archive.
function replaceTexturePrompt(node, index, name) {
  const inp = document.createElement('input');
  inp.type = 'file';
  inp.accept = '.dds,image/png,image/jpeg,image/bmp,image/webp,image/*';
  inp.onchange = async () => {
    const f = inp.files && inp.files[0];
    if (!f) return;
    try {
      const buf = new Uint8Array(await f.arrayBuffer());
      const isDds = /\.dds$/i.test(f.name) ||
        (buf.length > 4 && buf[0] === 0x44 && buf[1] === 0x44 && buf[2] === 0x53 && buf[3] === 0x20); // "DDS "
      let payload;
      if (isDds) payload = { node, index, name, content: bytesToB64(buf) };
      else {
        const { rgba, w, h } = await imageFileToRgba(f);
        payload = { node, index, name, rgba: bytesToB64(rgba), w, h };
      }
      setStatus(`Replacing ${name}…`);
      const res = await withProgress(call('replaceTexture', payload), 250);
      if (res.ok) { setStatus(`Replaced ${res.name} (${fmtSize(res.size)})`); applyReplacedTextures(res.node, res.textures); }
      else setStatus('Replace failed: ' + (res.message || ''), true);
    } catch (e) { setStatus('Replace failed: ' + e.message, true); }
  };
  inp.click();
}
function imageFileToRgba(file) {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      const cv = document.createElement('canvas'); cv.width = img.naturalWidth; cv.height = img.naturalHeight;
      const cx = cv.getContext('2d'); cx.drawImage(img, 0, 0);
      let data;
      try { data = cx.getImageData(0, 0, cv.width, cv.height).data; }
      catch (e) { URL.revokeObjectURL(url); reject(e); return; }
      URL.revokeObjectURL(url);
      resolve({ rgba: new Uint8Array(data.buffer), w: cv.width, h: cv.height });
    };
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('cannot decode image')); };
    img.src = url;
  });
}
// After a replace, rebuild whichever texture view is showing this dictionary so the
// new thumbnail (and shifted indices) appear without reopening.
function applyReplacedTextures(node, textures) {
  if (activeTab && activeTab.kind === 'texture' && activeTab.data && activeTab.data.node === node) {
    activeTab.data.textures = textures; renderTextures(activeTab.data);
  }
  if (curModel && curModel.node === node) {
    curModel.textures = textures;
    fillModelTextures(textures, node);
    if (!$('vpTexFull').hidden) expandTextures();
  }
}
function texMenu(node, t, ev, ph, fmt) {
  ev.preventDefault(); ev.stopPropagation();
  showMenu([
    { label: 'Replace with image / DDS…', action: () => replaceTexturePrompt(node, t.index, t.name) },
    { label: 'Delete texture', action: () => deleteTexture(node, t.index, t.name) },
    { sep: true },
    { label: 'Open', action: () => { if (ph.__img) openTexImageUrl(t.name, fmt, ph.__img); } },
  ], ev.clientX, ev.clientY);
}
async function deleteTexture(node, index, name) {
  const yes = await confirmDialog({ title: 'Delete texture', body: `Remove “${name}” from this dictionary? This rewrites the file.`, okLabel: 'Delete' });
  if (!yes) return;
  setStatus(`Deleting ${name}…`);
  const res = await call('deleteTexture', { node, index, name });
  if (res.ok) { setStatus(`Deleted ${name} (${res.count} left)`); applyReplacedTextures(res.node, res.textures); }
  else setStatus('Delete failed: ' + (res.message || ''), true);
}

// The open dictionary a dropped file would import into: the active .ytd (texture
// tab) or .ypt (model tab). Returns null when there's no archive to drop into.
function dropTargetNode() {
  if (activeTab && activeTab.kind === 'texture' && activeTab.data) return activeTab.data.node;
  if (activeTab && activeTab.kind === 'model' && curModel && /\.ypt$/.test(curModel.file || '')) return curModel.node;
  return null;
}
function dropTargetName() {
  if (activeTab && activeTab.kind === 'texture') return activeTab.title || 'archive';
  if (activeTab && activeTab.kind === 'model' && curModel) return curModel.name || 'archive';
  return 'archive';
}
// Import a dropped image/DDS into the open dictionary. The texture name is the
// dropped file's base name — matching name replaces, otherwise it's added.
async function importDroppedTexture(node, file) {
  const baseName = file.name.replace(/\.[^.]+$/, '').toLowerCase();
  try {
    const buf = new Uint8Array(await file.arrayBuffer());
    const isDds = /\.dds$/i.test(file.name) ||
      (buf.length > 4 && buf[0] === 0x44 && buf[1] === 0x44 && buf[2] === 0x53 && buf[3] === 0x20); // "DDS "
    let payload;
    if (isDds) payload = { node, name: baseName, index: -1, content: bytesToB64(buf) };
    else {
      const { rgba, w, h } = await imageFileToRgba(file);
      payload = { node, name: baseName, index: -1, rgba: bytesToB64(rgba), w, h };
    }
    setStatus(`Importing ${baseName}…`);
    const res = await withProgress(call('replaceTexture', payload), 250);
    if (res.ok) { setStatus(`Imported ${res.name} into ${dropTargetName()} (${fmtSize(res.size)})`); applyReplacedTextures(res.node, res.textures); }
    else setStatus('Import failed: ' + (res.message || ''), true);
  } catch (e) { setStatus('Import failed: ' + e.message, true); }
}

// Embedded textures (e.g. .ypt) in a toggleable strip — metadata shows instantly,
// thumbnails stream in lazily so opening is fast even with 100+ textures.
function fillModelTextures(texs, node) {
  modelTextures = (texs || []).map(t => ({ ...t, node }));
  const wrap = $('vpTextures'), row = $('vpTexRow'), chip = $('tgTex');
  row.innerHTML = '';
  const has = modelTextures.length > 0;
  chip.hidden = !has; chip.classList.toggle('on', has); wrap.hidden = !has;
  if (!has) return;
  for (const t of modelTextures) row.append(makeTexThumb(t));
}
function makeTexThumb(t) {
  const fmt = (t.format || '').replace('D3DFMT_', '');
  const c = document.createElement('div'); c.className = 'vp-tex';
  const ph = texThumb(t, t.node, 'vt-img');
  const name = document.createElement('span'); name.className = 'vt-name'; name.textContent = t.name;
  const dim = document.createElement('span'); dim.className = 'vt-dim'; dim.textContent = `${t.width}×${t.height} · ${fmt}`;
  c.append(ph, name, dim);
  c.onclick = () => { if (ph.__img) openTexImageUrl(t.name, fmt, ph.__img); };
  c.oncontextmenu = ev => texMenu(t.node, t, ev, ph, fmt);
  return c;
}
// "Expand" -> textures fill the pane (model list + 3D viewer hidden).
function expandTextures() {
  const grid = $('vpTexFullGrid'); grid.innerHTML = '';
  $('vpTexFullTitle').textContent = `Textures (${modelTextures.length})`;
  for (const t of modelTextures) {
    const fmt = (t.format || '').replace('D3DFMT_', '');
    const c = document.createElement('div'); c.className = 'tex-card';
    const ph = texThumb(t, t.node, 'preview');
    const tn = document.createElement('div'); tn.className = 'tn'; tn.textContent = t.name;
    const td = document.createElement('div'); td.className = 'td'; td.textContent = `${t.width}×${t.height} · ${fmt}`;
    c.append(ph, tn, td);
    c.onclick = () => { if (ph.__img) openTexImageUrl(t.name, fmt, ph.__img); };
    c.oncontextmenu = ev => texMenu(t.node, t, ev, ph, fmt);
    grid.append(c);
  }
  $('vpTexFull').hidden = false;
}
function collapseTextures() { $('vpTexFull').hidden = true; }
function updateVpStats() {
  const s = viewport.getStats();
  statusRight.textContent = `${s.meshes} mesh · ${s.verts.toLocaleString()} verts · ${s.tris.toLocaleString()} tris`
    + (s.skipped ? ` · ${s.skipped} skipped` : '');
}
function chip(id, fn, initial) { const el = $(id); if (initial) el.classList.add('on'); el.onclick = () => fn(el.classList.toggle('on')); }
chip('tgWire', viewport.setWire, false);
chip('tgVColor', viewport.setVertexColors, false);
chip('tgBox', viewport.setBox, false);
chip('tgGrid', viewport.setGrid, true);
chip('tgLight', viewport.setLit, true);
chip('tgSkel', viewport.setSkeleton, false);
$('tgTex').onclick = () => { const on = $('tgTex').classList.toggle('on'); $('vpTextures').hidden = !on; };
$('btnFit').onclick = () => viewport.fit();
$('lodSel').onchange = e => { viewport.setLod(e.target.value); updateVpStats(); };
$('btnTexExpand').onclick = expandTextures;
$('btnTexCollapse').onclick = collapseTextures;

// ---------------------------------------------------------------- model details
// Right-side panel: per-material shader info (samplers + EDITABLE value params)
// and the skeleton bone tree (click a bone to flash it in the 3D view).
let vdTab = 'mats';
$('vdTabMats').onclick = () => { vdTab = 'mats'; renderDetails(); };
$('vdTabSkel').onclick = () => { vdTab = 'skel'; renderDetails(); };
$('vdTabAnims').onclick = () => { vdTab = 'anims'; renderDetails(); };
$('vpDetailsToggle').onclick = () => {
  const el = $('vpDetails');
  const c = el.classList.toggle('collapsed');
  $('vpDetailsToggle').innerHTML = c ? '&#x27E8;' : '&#x27E9;';
  localStorage.setItem('panelc_vpdetails', c ? '1' : '0');
};
if (localStorage.getItem('panelc_vpdetails') === '1') { $('vpDetails').classList.add('collapsed'); $('vpDetailsToggle').innerHTML = '&#x27E8;'; }
$('btnAddYtd').onclick = () => ytdPickerDialog();

function curPartIdx() { const i = viewport.currentPart(); return i >= 0 ? i : 0; }
function curPart() { return curModel && curModel.parts ? curModel.parts[curPartIdx()] : null; }

function renderDetails() {
  const panel = $('vpDetails');
  const part = curPart();
  if (!curModel || !part) { panel.hidden = true; return; }
  panel.hidden = false;

  const hasSkel = !!(part.skeleton && part.skeleton.length);
  $('vdTabSkel').hidden = !hasSkel;
  $('vdTabAnims').hidden = !hasSkel;       // animations drive the rig
  $('tgSkel').hidden = !hasSkel;
  if (!hasSkel && (vdTab === 'skel' || vdTab === 'anims')) vdTab = 'mats';
  $('vdTabMats').classList.toggle('on', vdTab === 'mats');
  $('vdTabSkel').classList.toggle('on', vdTab === 'skel');
  $('vdTabAnims').classList.toggle('on', vdTab === 'anims');
  $('vdTabSkel').textContent = hasSkel ? `Skeleton (${part.skeleton.length})` : 'Skeleton';

  const body = $('vdBody');
  body.innerHTML = '';
  if (part.lazy) { body.innerHTML = '<div class="vd-empty">Select the model to load its details.</div>'; return; }
  if (vdTab === 'mats') { renderAdditions(body); renderMatDetails(body, part); }
  else if (vdTab === 'skel') renderSkelDetails(body, part);
  else renderAnimDetails(body);
}

// ---- additions (extra_* parts — hidden unless ticked) ----
let additionsEnabled = new Set();
function renderAdditions(body) {
  const names = viewport.partAdditions();
  if (!names.length) return;
  const h = document.createElement('div'); h.className = 'vd-sect'; h.textContent = `Additions (${names.length})`;
  const box = document.createElement('div'); box.className = 'vd-adds';
  const all = document.createElement('label'); all.className = 'vd-add';
  all.innerHTML = `<input type="checkbox" id="addAll"> <b>All</b>`;
  box.append(all);
  const update = () => {
    viewport.setAdditions([...additionsEnabled]);
    all.querySelector('input').checked = names.every(n => additionsEnabled.has(n));
  };
  all.querySelector('input').onchange = e => {
    additionsEnabled = e.target.checked ? new Set(names) : new Set();
    box.querySelectorAll('input[data-add]').forEach(i => i.checked = e.target.checked);
    update();
  };
  for (const n of names) {
    const w = document.createElement('label'); w.className = 'vd-add';
    const c = document.createElement('input'); c.type = 'checkbox'; c.dataset.add = n; c.checked = additionsEnabled.has(n);
    c.onchange = () => { if (c.checked) additionsEnabled.add(n); else additionsEnabled.delete(n); update(); };
    w.append(c, document.createTextNode(' ' + n));
    box.append(w);
  }
  body.append(h, box);
  update();
}

// ---- grip fine-tune (live, persisted) ----
const DEFAULT_GRIP = { ox: 0.15, oy: -0.04, oz: 0.012, rx: 0, ry: 0, rz: 0 };
function loadGrip() {
  try { return { ...DEFAULT_GRIP, ...JSON.parse(localStorage.getItem('gripAdjust') || '{}') }; }
  catch { return { ...DEFAULT_GRIP }; }
}
function saveGrip(g) { try { localStorage.setItem('gripAdjust', JSON.stringify(g)); } catch {} }
function applyGripToViewport(g) { viewport.setGripOffset(g.ox, g.oy, g.oz); viewport.setGripRot(g.rx, g.ry, g.rz); }

// ---- animations (.ycd discovery + playback) ----
let animTimer = null;
function renderAnimDetails(body) {
  // playback controls
  const bar = document.createElement('div'); bar.className = 'vd-anim-bar';
  bar.innerHTML = `
    <button class="chip" id="anPlay" title="Play / pause">▶</button>
    <button class="chip" id="anStop" title="Stop & reset pose">⏹</button>
    <select id="anSpeed" class="ext-in" style="width:62px" title="Speed">
      <option value="0.25">0.25×</option><option value="0.5">0.5×</option>
      <option value="1" selected>1×</option><option value="2">2×</option>
    </select>
    <span class="vd-anim-time" id="anTime">–</span>`;
  const slider = document.createElement('input');
  slider.type = 'range'; slider.min = '0'; slider.max = '1000'; slider.value = '0'; slider.className = 'vd-anim-slider'; slider.id = 'anSlider';
  // character toggle — most clips animate the PED skeleton (arms, spine, legs…), not the
  // weapon's own bones, so they only look right on a full body holding the weapon. The
  // full character means run/walk/melee/aim all play. Offered for weapon models (w_*).
  const isWeapon = /^w_/i.test(curModel.file || '');
  const armsRow = document.createElement('label'); armsRow.className = 'vd-add'; armsRow.style.marginBottom = '6px';
  armsRow.hidden = !isWeapon;
  armsRow.innerHTML = `<input type="checkbox" id="anArms"> Show character (full body — for run/aim/reload anims)`;
  // live grip adjustment (so the weapon can be positioned exactly in the hand)
  const grip = loadGrip();
  const gripBox = document.createElement('div'); gripBox.className = 'vd-grip'; gripBox.hidden = !viewport.hasArms();
  gripBox.innerHTML = '<div class="vd-sect">Grip adjust (live · saved)</div>';
  const gaxes = [['ox', 'fwd', -0.4, 0.4, 0.005], ['oy', 'down', -0.4, 0.4, 0.005], ['oz', 'side', -0.4, 0.4, 0.005],
                 ['rx', 'pitch', -180, 180, 1], ['ry', 'yaw', -180, 180, 1], ['rz', 'roll', -180, 180, 1]];
  const fmtG = (k, v) => k[0] === 'o' ? v.toFixed(3) : Math.round(v).toString();
  for (const [key, label, min, max, step] of gaxes) {
    const row = document.createElement('div'); row.className = 'vd-grip-row';
    const nm = document.createElement('span'); nm.className = 'gname'; nm.textContent = label;
    const sl = document.createElement('input'); sl.type = 'range'; sl.min = min; sl.max = max; sl.step = step; sl.value = grip[key];
    const num = document.createElement('input'); num.type = 'text'; num.className = 'gnum'; num.value = fmtG(key, grip[key]);
    sl.oninput = () => { grip[key] = parseFloat(sl.value); num.value = fmtG(key, grip[key]); applyGripToViewport(grip); saveGrip(grip); };
    num.onchange = () => { const v = parseFloat(num.value); if (!Number.isNaN(v)) { grip[key] = v; sl.value = v; applyGripToViewport(grip); saveGrip(grip); } };
    row.append(nm, sl, num); gripBox.append(row);
  }
  const greset = document.createElement('button'); greset.className = 'btn ghost sm'; greset.style.width = '100%';
  greset.textContent = 'Reset grip';
  greset.onclick = () => { localStorage.removeItem('gripAdjust'); applyGripToViewport(DEFAULT_GRIP); renderDetails(); };
  gripBox.append(greset);

  const list = document.createElement('div'); list.id = 'anList';
  const browse = document.createElement('button'); browse.className = 'btn ghost sm'; browse.style.cssText = 'width:100%;margin-top:7px';
  browse.textContent = '+ Browse any .ycd…';
  body.append(bar, slider, armsRow, gripBox, list, browse);

  const armsChk = armsRow.querySelector('#anArms');
  armsChk.checked = viewport.hasArms();
  armsChk.onchange = async () => {
    if (armsChk.checked) {
      if (!window.__pedBody) {
        setStatus('Loading character…');
        const res = await withProgress(call('pedBody', {}), 0);
        if (!res.ok) { setStatus('Could not load character: ' + (res.message || ''), true); armsChk.checked = false; return; }
        window.__pedBody = res;
      }
      applyGripToViewport(grip);          // restore saved grip before attaching
      viewport.setArms(true, window.__pedBody);
      gripBox.hidden = false;
      setStatus('Character attached — play a clip, fine-tune the grip below');
    } else { viewport.setArms(false); gripBox.hidden = true; }
  };

  bar.querySelector('#anPlay').onclick = () => {
    const st = viewport.clipState();
    if (!st.duration) return;
    viewport.pauseClip(st.playing);
    bar.querySelector('#anPlay').textContent = st.playing ? '▶' : '⏸';
  };
  bar.querySelector('#anStop').onclick = () => { viewport.stopClip(); bar.querySelector('#anPlay').textContent = '▶'; };
  bar.querySelector('#anSpeed').onchange = e => viewport.setClipSpeed(parseFloat(e.target.value));
  slider.oninput = () => {
    const st = viewport.clipState();
    if (st.duration) { viewport.pauseClip(true); bar.querySelector('#anPlay').textContent = '▶'; viewport.setClipTime(slider.value / 1000 * st.duration); }
  };
  clearInterval(animTimer);
  animTimer = setInterval(() => {
    if (!document.body.contains(slider)) { clearInterval(animTimer); return; }
    const st = viewport.clipState();
    $('anTime') && ($('anTime').textContent = st.duration ? `${st.time.toFixed(2)} / ${st.duration.toFixed(2)}s` : '–');
    if (st.playing && st.duration) slider.value = String(Math.round(st.time / st.duration * 1000));
  }, 150);

  renderAnimDicts(list);
  browse.onclick = async () => {
    const p = await call('pickYcd');
    if (p.path) addAnimDict(list, { name: p.path.split(/[\\/]/).pop(), path: p.path, node: 0, source: 'disk' }, true);
  };
}

async function renderAnimDicts(list) {
  list.innerHTML = '<div class="vd-empty">Searching for linked animations…</div>';
  if (!curModel.__anims) {
    const res = await call('findAnims', { node: curModel.node });
    curModel.__anims = res.ok ? (res.items || []) : [];
  }
  list.innerHTML = '';
  if (!curModel.__anims.length) {
    list.innerHTML = '<div class="vd-empty">No linked .ycd found — use “Browse any .ycd…” below.</div>';
    return;
  }
  for (const d of curModel.__anims) addAnimDict(list, d, false);
}

function addAnimDict(list, d, open) {
  const card = document.createElement('div'); card.className = 'vd-mat';
  const head = document.createElement('div'); head.className = 'vd-mat-head';
  const srcBadge = d.source === 'epic' ? '<span class="vd-badge ems">epic</span>'
                 : d.source === 'update' ? '<span class="vd-badge nrm">update</span>' : '';
  head.innerHTML = `<span class="vd-mat-name" title="${esc(d.path || '')}">${esc(d.name)}</span>${srcBadge}`;
  const bodyEl = document.createElement('div'); bodyEl.className = 'vd-mat-body';
  card.append(head, bodyEl);
  let loaded = false;
  const toggle = async () => {
    card.classList.toggle('open');
    if (loaded || !card.classList.contains('open')) return;
    loaded = true;
    bodyEl.innerHTML = '<div class="vd-empty">Loading clips…</div>';
    const res = await call('animClips', { ycd: d.node, path: d.disk || d.path });
    bodyEl.innerHTML = '';
    if (!res.ok || !res.clips.length) { bodyEl.innerHTML = `<div class="vd-empty">${esc(res.message || 'No clips.')}</div>`; return; }
    for (const c of res.clips) {
      const row = document.createElement('div'); row.className = 'vd-bone';
      row.innerHTML = `▶ ${esc(c.name)} <span class="btag">${c.duration ? c.duration.toFixed(2) + 's' : ''}</span>`;
      row.onclick = async () => {
        setStatus(`Baking ${c.name}…`);
        const bake = await withProgress(call('animBake', { ycd: d.node, path: d.disk || d.path, clip: c.hash }), 0);
        if (!bake.ok) { setStatus('Could not play clip: ' + (bake.message || ''), true); return; }
        const n = viewport.playClip(bake);
        const play = $('anPlay'); if (play) play.textContent = '⏸';
        setStatus(n ? `Playing ${c.name} (${n} bone tracks)` : 'Clip has no tracks matching this skeleton', !n);
        bodyEl.querySelectorAll('.vd-bone.sel').forEach(x => x.classList.remove('sel'));
        row.classList.add('sel');
      };
      bodyEl.append(row);
    }
  };
  head.onclick = toggle;
  list.append(card);
  if (open) toggle();
}

function renderMatDetails(body, part) {
  const mats = part.materials || [];
  if (!mats.length) { body.innerHTML = '<div class="vd-empty">No materials.</div>'; return; }
  const partIdx = curPartIdx();
  mats.forEach((mi, idx) => body.append(matCard(mi, idx, partIdx)));
}

// What kind of material is it? Badges summarise the lighting features in play.
function matBadges(mi) {
  const b = [];
  if (mi.emissive) b.push('<span class="vd-badge ems">emissive</span>');
  if (mi.nrmTex) b.push('<span class="vd-badge nrm">normal</span>');
  if (mi.spcTex) b.push('<span class="vd-badge spc">spec</span>');
  return b.join('');
}

function matCard(mi, matIdx, partIdx) {
  const card = document.createElement('div'); card.className = 'vd-mat';
  const head = document.createElement('div'); head.className = 'vd-mat-head';
  head.innerHTML = `<span class="vd-mat-idx">#${matIdx}</span>
    <span class="vd-mat-name" title="${esc(mi.sps || mi.shader || '')}">${esc(mi.shader || 'shader')}</span>${matBadges(mi)}`;
  const bodyEl = document.createElement('div'); bodyEl.className = 'vd-mat-body';
  head.onclick = () => { card.classList.toggle('open'); viewport.flashMaterial(partIdx, matIdx); };
  card.append(head, bodyEl);

  // textures (every sampler the shader declares)
  const texs = mi.texs || [];
  if (texs.length) {
    const h = document.createElement('div'); h.className = 'vd-sect'; h.textContent = 'Textures';
    bodyEl.append(h);
    for (const t of texs) {
      const row = document.createElement('div'); row.className = 'vd-tex' + (t.found ? '' : ' missing');
      row.title = `${t.sampler}${t.embedded ? ' (embedded)' : ''}${t.found ? '' : ' — not found'}`;
      row.innerHTML = `<span class="role">${esc(t.role)}</span>
        <span class="tname">${esc(t.name || '—')}</span>
        <span class="tdim">${t.found ? `${t.w}×${t.h} ${esc(t.fmt)}` : 'missing'}</span>`;
      bodyEl.append(row);
    }
  }

  // editable value parameters
  const prms = mi.params || [];
  if (prms.length) {
    const h = document.createElement('div'); h.className = 'vd-sect'; h.textContent = 'Parameters';
    bodyEl.append(h);
    const edited = new Map();   // name -> values[]
    let saveBtn = null;
    for (const p of prms) {
      const row = document.createElement('div'); row.className = 'vd-prm';
      const nm = document.createElement('span'); nm.className = 'pname';
      nm.textContent = p.name + (p.count > 1 ? ` [${p.count}]` : '');
      nm.title = p.name;
      row.append(nm);
      p.values.forEach((v, vi) => {
        const inp = document.createElement('input');
        inp.type = 'text'; inp.value = fmtF(v);
        inp.oninput = () => {
          const f = parseFloat(inp.value);
          if (Number.isNaN(f)) return;
          p.values[vi] = f;
          inp.classList.add('edited');
          edited.set(p.name, p.values.slice());
          if (saveBtn) saveBtn.hidden = false;
          viewport.updateMaterialParams(partIdx, matIdx, prms);   // live preview
        };
        row.append(inp);
      });
      bodyEl.append(row);
    }
    saveBtn = document.createElement('button');
    saveBtn.className = 'btn primary sm vd-mat-save'; saveBtn.textContent = 'Save changes to file'; saveBtn.hidden = true;
    saveBtn.onclick = async () => {
      const params = [...edited].map(([name, values]) => ({ name, values }));
      setStatus('Saving shader parameters…');
      const res = await withProgress(call('setShaderParams', { node: curModel.node, index: partIdx, mat: matIdx, save: true, params }), 0);
      if (res.ok) { setStatus(`Saved ${res.applied} parameter(s)${res.saved ? ` — file rewritten (${fmtSize(res.size)})` : ''}`); edited.clear(); saveBtn.hidden = true; bodyEl.querySelectorAll('input.edited').forEach(i => i.classList.remove('edited')); }
      else setStatus('Save failed: ' + (res.message || ''), true);
    };
    bodyEl.append(saveBtn);
  }
  if (!texs.length && !prms.length) {
    const e = document.createElement('div'); e.className = 'vd-empty'; e.textContent = 'No shader data.'; bodyEl.append(e);
  }
  return card;
}

function renderSkelDetails(body, part) {
  const bones = part.skeleton || [];
  // children lookup for correct hierarchy order (roots first, depth-first)
  const kids = new Map();
  bones.forEach(b => { if (!kids.has(b.parent)) kids.set(b.parent, []); kids.get(b.parent).push(b); });
  const partIdx = curPartIdx();
  let selEl = null;
  const addRow = (b, depth) => {
    const el = document.createElement('div'); el.className = 'vd-bone';
    el.style.paddingLeft = (6 + depth * 12) + 'px';
    el.innerHTML = `${depth ? '└ ' : ''}${esc(b.name)} <span class="btag">#${b.i} tag ${b.tag}</span>`;
    el.title = `${b.name}  (index ${b.i}, tag ${b.tag}, parent ${b.parent})`;
    el.onclick = () => {
      if (selEl) selEl.classList.remove('sel');
      selEl = el; el.classList.add('sel');
      if (!$('tgSkel').classList.contains('on')) { $('tgSkel').classList.add('on'); viewport.setSkeleton(true); }
      viewport.highlightBone(b.i);
    };
    body.append(el);
    for (const c of (kids.get(b.i) || [])) addRow(c, depth + 1);
  };
  const roots = bones.filter(b => b.parent < 0 || !bones[b.parent]);
  if (!roots.length && bones.length) roots.push(bones[0]);
  roots.forEach(b => addRow(b, 0));
  if (!bones.length) body.innerHTML = '<div class="vd-empty">No skeleton in this model.</div>';
}

function fmtF(v) { const s = (Math.round(v * 10000) / 10000).toString(); return s; }

// ---- user-picked .ytd texture sources ----
function ytdPickerDialog() {
  if (!curModel) return;
  const m = modalShell('Textures from .ytd', 'Resolve this model’s textures from any texture dictionary — search the mounted game files or browse a loose .ytd on disk. Newest picks win.');
  m.body.innerHTML = `<input class="dlg-in" id="ytdSearch" placeholder="Search .ytd by name… (min 2 chars)" spellcheck="false">
    <div class="ytd-list" id="ytdList"><div class="vd-empty">Type to search the mounted game files.</div></div>`;
  const inp = m.body.querySelector('#ytdSearch'), list = m.body.querySelector('#ytdList');
  let seq = 0, timer = null;
  inp.oninput = () => { clearTimeout(timer); timer = setTimeout(run, 250); };
  inp.onkeydown = e => { if (e.key === 'Enter') { clearTimeout(timer); run(); } };
  async function run() {
    const q = inp.value.trim();
    if (q.length < 2) { list.innerHTML = '<div class="vd-empty">Type at least 2 characters.</div>'; return; }
    const my = ++seq;
    const res = await call('search', { query: q, ext: false, scope: 'none', node: 0, limit: 600 });
    if (my !== seq) return;
    const ytds = (res.results || []).filter(x => x.name.toLowerCase().endsWith('.ytd')).slice(0, 150);
    list.innerHTML = ytds.length ? '' : '<div class="vd-empty">No .ytd matches.</div>';
    for (const x of ytds) {
      const row = document.createElement('div'); row.className = 'ytd-row';
      row.innerHTML = `<span class="yname">${esc(x.name)}</span><span class="ypath">${esc(x.path)}</span>`;
      row.onclick = () => { m.close(); useTxd({ rid: x.rid }, x.name); };
      list.append(row);
    }
  }
  addBtn(m.actions, 'Browse disk…', 'ghost', async () => {
    const p = await call('pickYtd');
    if (p.path) { m.close(); useTxd({ path: p.path }, p.path.split(/[\\/]/).pop()); }
  });
  addBtn(m.actions, 'Close', 'ghost', m.close);
  inp.focus();
}

// Register the picked txd, then re-resolve the displayed part with it (other loaded
// parts are marked lazy so they pick the new textures up when next selected).
async function useTxd(srcArg, label) {
  if (!curModel) return;
  setStatus(`Loading ${label || '.ytd'}…`);
  const res = await withProgress(call('useTxd', { node: curModel.node, ...srcArg }), 0);
  if (!res.ok) { setStatus('Could not use that .ytd: ' + (res.message || ''), true); return; }
  setStatus(`Using ${res.name} (${res.count} textures) — re-resolving…`);
  const idx = curPartIdx();
  curModel.parts.forEach((p, i) => { if (i !== idx && p && !p.lazy) p.lazy = true; });
  const pr = await withProgress(call('modelPart', { node: curModel.node, index: idx }), 0);
  if (pr.ok) {
    curModel.parts[idx] = pr.part;
    viewport.setPartData(idx, pr.part);
    viewport.setPart(viewport.currentPart());   // rebuild with new materials
    renderDetails();
    setStatus(`Textures from ${res.name} applied (${res.count} textures)`);
  }
}

// ---------------------------------------------------------------- editor pane
function fillEdit(tab) {
  $('editLang').textContent = tab.data.language || 'text';
  $('editFmt').textContent = tab.data.format === 'meta' ? '(binary meta · edits convert back on save)' : '';
  editor.showTab(tab, t => { if (t === activeTab) updateDirty(t); });
  updateDirty(tab);
}
function updateDirty(tab) { $('editDirty').hidden = !editor.dirty(tab); }
async function saveActive(target) {
  const t = activeTab;
  if (!t || t.kind !== 'edit') return;
  const content = editor.value(t);
  setStatus(target === 'rpf' ? 'Saving to archive…' : 'Exporting…');
  // Pass the file's stable path too: a background remount (live file watch) recycles
  // node ids, so the bridge re-resolves the save target by path when the node is stale.
  const res = await call('save', { node: t.key, path: t.data.path, content, format: t.data.format, metaName: t.data.metaName, target });
  if (res.canceled) { setStatus('Save canceled'); return; }
  if (res.ok) {
    if (target === 'rpf') { editor.markSaved(t); updateDirty(t); setStatus(`Saved ${t.title} into archive (${fmtSize(res.size)})`); }
    else setStatus(`Exported ${t.title} → ${res.path}`);
  } else setStatus('Save failed: ' + (res.message || ''), true);
}
$('btnSaveRpf').onclick = () => saveActive('rpf');
// When the file is shown AS XML (a binary meta/resource), ask which form to extract.
$('btnExport').onclick = async () => {
  const t = activeTab;
  if (t && t.kind === 'edit' && t.data && t.data.format === 'meta') {
    const choice = await choiceDialog({
      title: `Extract “${t.title}”`,
      body: "You're viewing this file as XML. Extract which format?",
      buttons: [{ label: 'As XML', value: 'xml', cls: 'primary' }, { label: 'Original file', value: 'bin', cls: 'ghost' }],
    });
    if (choice === 'xml') return saveActive('export');
    if (choice === 'bin') {
      setStatus(`Extracting ${t.title}…`);
      const res = await call('extract', { node: t.key });
      if (res.canceled) { setStatus('Extract canceled'); return; }
      setStatus(res.ok ? `Extracted ${t.title} → ${res.path}` : 'Extract failed: ' + (res.message || ''), !res.ok);
    }
    return;
  }
  saveActive('export');
};

// Small N-button choice dialog. Resolves to the picked value (or null on Escape).
function choiceDialog({ title, body, buttons }) {
  return new Promise(resolve => {
    const ov = document.createElement('div'); ov.className = 'modal';
    ov.innerHTML = `<div class="modal-card"><h3>${esc(title)}</h3>
      <p class="muted" style="white-space:normal">${esc(body || '')}</p>
      <div class="modal-actions"></div></div>`;
    document.body.append(ov);
    const acts = ov.querySelector('.modal-actions');
    const done = v => { ov.remove(); resolve(v); };
    for (const b of buttons) { const el = document.createElement('button'); el.className = 'btn ' + (b.cls || 'ghost'); el.textContent = b.label; el.onclick = () => done(b.value); acts.append(el); }
    ov.onkeydown = e => { if (e.key === 'Escape') done(null); };
  });
}

// ---------------------------------------------------------------- hex / texture
function b64bytes(s) { const bin = atob(s || ''); const o = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) o[i] = bin.charCodeAt(i); return o; }
function renderHex(res) {
  const bytes = b64bytes(res.data);
  const lines = [];
  for (let off = 0; off < bytes.length; off += 16) {
    let hex = '', asc = '';
    for (let i = 0; i < 16; i++) {
      if (off + i < bytes.length) { const b = bytes[off + i]; hex += b.toString(16).padStart(2, '0') + ' '; asc += (b >= 32 && b < 127) ? String.fromCharCode(b) : '.'; }
      else hex += '   ';
    }
    lines.push(off.toString(16).padStart(8, '0') + '  ' + hex + ' ' + asc);
  }
  $('hexBody').textContent = lines.join('\n') + (res.total > res.shown ? `\n\n… ${(res.total - res.shown).toLocaleString()} more bytes (truncated)` : '');
}
function renderTextures(res) {
  const g = $('texGrid'); g.innerHTML = '';
  if (!res.textures.length) { g.innerHTML = '<div class="muted" style="padding:14px">No textures.</div>'; return; }
  for (const t of res.textures) {
    const fmt = (t.format || '').replace('D3DFMT_', '');
    const c = document.createElement('div'); c.className = 'tex-card';
    const ph = texThumb(t, res.node, 'preview');
    const tn = document.createElement('div'); tn.className = 'tn'; tn.textContent = t.name;
    const td = document.createElement('div'); td.className = 'td'; td.textContent = `${t.width}×${t.height} · ${fmt} · ${t.levels} mip${t.levels === 1 ? '' : 's'}`;
    c.append(ph, tn, td);
    c.onclick = () => { if (ph.__img) openTexImageUrl(t.name, fmt, ph.__img); };
    c.oncontextmenu = ev => texMenu(res.node, t, ev, ph, fmt);
    g.appendChild(c);
  }
}

// ---------------------------------------------------------------- gfx (Scaleform/SWF) structure viewer
function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
// JPEXS-style layout: a tree of every defined symbol on the left, a live preview
// (vector shapes / sprite timelines / resolved bitmaps) on the right. GTA's main
// timeline is almost always empty (content is attached by ActionScript at runtime),
// so browsing the symbol tree — not "playing the main timeline" — is how you see
// what's in the file.
const gfx = {
  canvas: null, ctx: null, res: null, scene: null,
  symbol: 'summary', curKind: 'summary', curId: -1,
  frame: 0, playing: false, raf: 0, lastT: 0,
  zoom: 1, panX: 0, panY: 0, images: new Map(),
};
const GFX_GROUPS = ['Sprite', 'Shape', 'Image', 'Font', 'Text', 'Button', 'Sound'];
const GFX_KIND = { Sprite: 'sprite', Shape: 'shape', Image: 'image', Font: 'info', Text: 'info', Button: 'info', Sound: 'info' };

function fillGfx(res) {
  gfxStop();
  gfx.res = res; gfx.scene = null; gfx.images = new Map();
  gfx.symbol = 'summary'; gfx.curKind = 'summary'; gfx.curId = -1; gfx.frame = 0;
  $('gfxFileName').textContent = res.name || '';
  $('gfxFileMeta').textContent = res.ok
    ? `${esc(res.signature)} · ${Math.round(res.width)}×${Math.round(res.height)} · ${res.frameCount}f`
    : 'parse error';
  $('gfxFilter').value = '';
  gfxBuildTree(res);
  gfxEnsureScene().then(() => gfxSelect('summary'));
}

// Fetch the renderable scene (shapes/sprites/timeline + resolved bitmaps) once.
async function gfxEnsureScene() {
  if (gfx.scene || !gfx.res) return gfx.scene;
  const sc = await call('gfxScene', { node: gfx.res.node });
  if (sc && sc.ok) {
    gfx.scene = sc; gfx.images = new Map();
    for (const im of sc.images || []) if (im.dataUrl) {
      const img = new Image();
      img.onload = () => { if (['image', 'shape', 'sprite', 'main'].includes(gfx.curKind)) gfxDraw(); };
      img.src = im.dataUrl; gfx.images.set(im.id, img);
    }
  }
  return gfx.scene;
}

// Build the left tree from the structural tag list (every defined character),
// labelled by exported symbol name where the file provides one.
function gfxBuildTree(res) {
  const tree = $('gfxTree'); tree.innerHTML = '';
  const nameById = new Map();
  for (const s of res.symbols || []) if (!nameById.has(s.id)) nameById.set(s.id, s.name);

  tree.append(gfxRow({ key: 'summary', label: 'Summary', kind: 'doc', depth: 0 }));
  tree.append(gfxRow({ key: 'main', label: 'Main timeline', kind: 'main', meta: `${res.frameCount}f`, depth: 0 }));

  const groups = {}; for (const g of GFX_GROUPS) groups[g] = [];
  const seen = new Set();
  for (const t of res.tags || []) {
    if (t.id == null || !(t.category in groups)) continue;
    const k = t.category + ':' + t.id; if (seen.has(k)) continue; seen.add(k);
    groups[t.category].push({ id: t.id, name: nameById.get(t.id) || '', detail: t.detail || '' });
  }
  for (const cat of GFX_GROUPS) {
    const arr = groups[cat]; if (!arr.length) continue;
    arr.sort((a, b) => a.id - b.id);
    tree.append(gfxGroupEl(cat, arr));
  }
}
// A plain (non-expandable) tree row. depth drives indentation; the blank twisty
// spacer keeps icons aligned with expandable rows.
function gfxRow({ key, label, kind, meta, depth }) {
  const el = document.createElement('div');
  el.className = 'gfx-node'; el.dataset.key = key; el.style.setProperty('--d', depth || 0);
  el.innerHTML = `<span class="gfx-tw-sp"></span><span class="gfx-ic gfx-ic-${kind}"></span><span class="gfx-nlabel">${esc(label)}</span>`
    + (meta ? `<span class="gfx-nmeta">${esc(meta)}</span>` : '');
  el.onclick = () => gfxSelect(key);
  return el;
}
// An expandable sprite row: clicking the label previews the sprite, clicking the
// twisty reveals the objects it contains (its display list), each itself selectable
// — and nested sprites expand recursively.
function gfxSpriteRow(id, label, meta, depth) {
  const wrap = document.createElement('div'); wrap.className = 'gfx-snode';
  const row = document.createElement('div'); row.className = 'gfx-node'; row.dataset.key = 'sprite:' + id; row.style.setProperty('--d', depth);
  row.innerHTML = `<span class="gfx-tw gfx-ctw">▸</span><span class="gfx-ic gfx-ic-sprite"></span><span class="gfx-nlabel">${esc(label)}</span>`
    + (meta ? `<span class="gfx-nmeta">${esc(meta)}</span>` : '');
  const kids = document.createElement('div'); kids.className = 'gfx-children'; kids.hidden = true;
  let built = false;
  const tw = row.querySelector('.gfx-ctw');
  tw.onclick = e => {
    e.stopPropagation();
    const open = kids.hidden;
    if (open && !built) { gfxBuildChildren(kids, id, depth + 1); built = true; }
    kids.hidden = !open; tw.textContent = open ? '▾' : '▸';
  };
  row.onclick = () => gfxSelect('sprite:' + id);
  wrap.append(row, kids);
  return wrap;
}
// Build a sprite's child rows from its display list (the union of placed objects
// across its frames, by depth). Shapes/images are leaves; sprites are expandable.
function gfxBuildChildren(container, spriteId, depth) {
  const sp = gfx.scene && gfx.scene.sprites[spriteId];
  const items = [];
  if (sp) { const seen = new Set(); for (const fr of sp.frames) for (const pl of fr) { if (pl.char < 0 || seen.has(pl.depth)) continue; seen.add(pl.depth); items.push(pl); } }
  if (!items.length) { const e = document.createElement('div'); e.className = 'gfx-empty'; e.style.setProperty('--d', depth); e.textContent = sp ? '(empty)' : '(no contents)'; container.append(e); return; }
  items.sort((a, b) => a.depth - b.depth);
  for (const pl of items) {
    const isSprite = !!gfx.scene.sprites[pl.char], isShape = !!gfx.scene.shapes[pl.char];
    const kind = isSprite ? 'sprite' : isShape ? 'shape' : 'image';
    const sym = (gfx.res.symbols || []).find(s => s.id === pl.char);
    const label = pl.name || (sym && sym.name) || (`${kind[0].toUpperCase()}${kind.slice(1)} ${pl.char}`);
    if (isSprite) container.append(gfxSpriteRow(pl.char, label, `#${pl.char}`, depth));
    else container.append(gfxRow({ key: kind + ':' + pl.char, label, kind, meta: `#${pl.char}`, depth }));
  }
}
function gfxGroupEl(cat, arr) {
  const wrap = document.createElement('div'); wrap.className = 'gfx-group';
  const head = document.createElement('div'); head.className = 'gfx-ghead open';
  head.innerHTML = `<span class="gfx-tw">▾</span><span class="gfx-gname">${cat}s</span><span class="gfx-gcount">${arr.length}</span>`;
  const body = document.createElement('div'); body.className = 'gfx-gbody';
  const kind = GFX_KIND[cat];
  for (const it of arr) {
    if (cat === 'Sprite') body.append(gfxSpriteRow(it.id, it.name || `Sprite ${it.id}`, it.name ? `#${it.id}` : '', 1));
    else body.append(gfxRow({ key: (kind === 'info' ? 'info:' + cat + ':' : kind + ':') + it.id, label: it.name || `${cat} ${it.id}`, kind, meta: it.name ? `#${it.id}` : (it.detail || ''), depth: 1 }));
  }
  head.onclick = () => {
    const open = head.classList.toggle('open');
    body.hidden = !open; head.querySelector('.gfx-tw').textContent = open ? '▾' : '▸';
  };
  wrap.append(head, body);
  return wrap;
}

// ---- selection + preview ----
async function gfxSelect(key) {
  gfx.symbol = key; gfx.frame = 0; gfxStop();
  for (const el of $('gfxTree').querySelectorAll('.gfx-node')) el.classList.toggle('sel', el.dataset.key === key);
  await gfxEnsureScene();

  if (key === 'summary') { gfx.curKind = 'summary'; gfxShowSummary(); return; }
  if (key.startsWith('info:')) { gfx.curKind = 'info'; gfxShowItemInfo(key); return; }

  // visual: main / sprite / shape / image
  $('gfxInfo').hidden = true; $('gfxStage').hidden = false; $('gfxRenderMsg') && ($('gfxRenderMsg').textContent = '');
  if (!gfx.scene) { gfx.curKind = 'main'; $('gfxSel').textContent = gfxSelLabel(key); $('gfxPlay').hidden = true; $('gfxFrame').hidden = true; $('gfxRenderMsg').textContent = 'Could not build render scene'; return; }
  gfx.curKind = key === 'main' ? 'main' : key.split(':')[0];
  gfx.curId = key.includes(':') ? parseInt(key.split(':')[1]) : -1;
  $('gfxSel').textContent = gfxSelLabel(key);
  const anim = key === 'main' || key.startsWith('sprite:');
  $('gfxPlay').hidden = !anim; $('gfxFrame').hidden = !anim;
  if (anim) { const r = gfxAnimRange(); $('gfxFrame').max = Math.max(r - 1, 0); $('gfxFrame').value = 0; gfxUpdateFrameLbl(); }
  else $('gfxFrameLbl').textContent = '';
  if (gfx.curKind === 'image' && !gfx.images.has(gfx.curId)) {
    const e = (gfx.scene.images || []).find(i => i.id === gfx.curId);
    $('gfxRenderMsg').textContent = e && e.file ? `External image not found: ${e.file}` : 'Image not resolved';
  }
  // double rAF: the stage was just un-hidden, so wait for layout before fitting
  // (a single frame can still measure a zero-size canvas).
  requestAnimationFrame(() => requestAnimationFrame(gfxFit));
}
function gfxSelLabel(key) {
  if (key === 'main') return 'Main timeline';
  const [k, idStr] = key.split(':'); const id = parseInt(idStr);
  const s = (gfx.res.symbols || []).find(x => x.id === id);
  return `${k.charAt(0).toUpperCase()}${k.slice(1)} #${id}` + (s ? ` · ${s.name}` : '');
}
function gfxShowSummary() {
  $('gfxStage').hidden = true; $('gfxInfo').hidden = false;
  $('gfxPlay').hidden = true; $('gfxFrame').hidden = true; $('gfxFrameLbl').textContent = '';
  $('gfxSel').textContent = 'Summary';
  const res = gfx.res, el = $('gfxInfo');
  if (!res.ok) { el.innerHTML = `<div class="gfx-pad muted">Could not parse GFX: ${esc(res.error || 'unknown error')}</div>`; return; }
  const r1 = n => Math.round(n * 10) / 10, counts = res.counts || {};
  const order = ['Shape', 'Sprite', 'Font', 'Image', 'Text', 'Button', 'Sound', 'Control', 'Meta', 'Other'];
  let h = `<div class="gfx-isect"><div class="gfx-meta2">${esc(res.signature)} · ${esc(res.compression)} · v${res.version} · ${r1(res.width)}×${r1(res.height)} · ${r1(res.frameRate)} fps · ${res.frameCount} frames · ${res.tagCount} tags</div>`;
  h += `<div class="gfx-chips">` + order.filter(k => counts[k]).map(k => `<span class="gfx-chip"><b>${counts[k]}</b> ${k}${counts[k] === 1 ? '' : 's'}</span>`).join('') + `</div></div>`;
  if (res.images && res.images.length)
    h += `<div class="gfx-isect"><h4>Referenced images (${res.images.length})</h4>` +
      res.images.map(im => `<div class="gfx-irow"><span class="gfx-id">#${im.id}</span><span class="gfx-iname">${esc(im.file)}</span><span class="gfx-idim">${im.width}×${im.height}</span></div>`).join('') + `</div>`;
  h += `<div class="gfx-isect"><h4>Tags (${res.tags.length})</h4><div class="gfx-tags">` +
    res.tags.map(t => `<div class="gfx-trow gfx-cat-${esc(t.category)}"><span class="gfx-tcode">${t.code}</span><span class="gfx-tname">${esc(t.name)}</span><span class="gfx-id">${t.id != null ? '#' + t.id : ''}</span><span class="gfx-tdetail">${esc(t.detail || '')}</span></div>`).join('') + `</div></div>`;
  el.innerHTML = h;
}
function gfxShowItemInfo(key) {
  $('gfxStage').hidden = true; $('gfxInfo').hidden = false;
  $('gfxPlay').hidden = true; $('gfxFrame').hidden = true; $('gfxFrameLbl').textContent = '';
  const [, cat, idStr] = key.split(':'); const id = parseInt(idStr), res = gfx.res;
  const tags = (res.tags || []).filter(t => t.id === id && t.category === cat);
  const sym = (res.symbols || []).find(s => s.id === id);
  $('gfxSel').textContent = `${cat} #${id}` + (sym ? ` · ${sym.name}` : '');
  let h = `<div class="gfx-isect"><h4>${esc(cat)} #${id}</h4>`;
  if (sym) h += `<div class="gfx-prow"><span class="gk">Exported name</span><span class="gv">${esc(sym.name)}</span></div>`;
  for (const t of tags) h += `<div class="gfx-prow"><span class="gk">${esc(t.name)}</span><span class="gv">${esc(t.detail || '—')}</span></div>`;
  h += `<div class="gfx-note">Fonts, text and buttons aren't vector-rendered here. Shapes, sprites and images preview in the canvas.</div></div>`;
  $('gfxInfo').innerHTML = h;
}
function gfxAnimRange() {
  const sc = gfx.scene, sel = gfx.symbol; let own = 1;
  if (sel === 'main') own = sc.main.frameCount;
  else if (sel.startsWith('sprite:')) own = (sc.sprites[sel.slice(7)] || {}).frameCount || 1;
  else return 1;
  if (own > 1) return own;
  let mx = 1; for (const k in sc.sprites) mx = Math.max(mx, sc.sprites[k].frameCount || 1);   // animate nested sprites
  return mx;
}
function gfxUpdateFrameLbl() { $('gfxFrameLbl').textContent = `${gfx.frame + 1} / ${gfxAnimRange()}`; }

function gfxApply(m, x, y) { return [m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]]; }
function gfxMul(a, b) { return [a[0] * b[0] + a[2] * b[1], a[1] * b[0] + a[3] * b[1], a[0] * b[2] + a[2] * b[3], a[1] * b[2] + a[3] * b[3], a[0] * b[4] + a[2] * b[5] + a[4], a[1] * b[4] + a[3] * b[5] + a[5]]; }
function gfxPath(cmds) {
  const p = new Path2D();
  for (let i = 0; i < cmds.length;) { const op = cmds[i++]; if (op === 0) p.moveTo(cmds[i++], cmds[i++]); else if (op === 1) p.lineTo(cmds[i++], cmds[i++]); else p.quadraticCurveTo(cmds[i++], cmds[i++], cmds[i++], cmds[i++]); }
  return p;
}
// Apply an accumulated CXFORM colour transform to a css rgba() string (RGB only;
// alpha rides on globalAlpha). null cx = identity. This is what makes runtime-neutral
// shapes that are *statically* tinted via a placement's colour transform show their
// real colour instead of the base (often white/grey) fill.
function gfxApplyCx(css, cx) {
  if (!cx || !css) return css;
  const m = /rgba?\(([^)]+)\)/i.exec(css); if (!m) return css;
  const p = m[1].split(',').map(parseFloat);
  const cl = v => v < 0 ? 0 : v > 255 ? 255 : v;
  return `rgba(${Math.round(cl(p[0] * cx.mul[0] + cx.add[0]))},${Math.round(cl(p[1] * cx.mul[1] + cx.add[1]))},${Math.round(cl(p[2] * cx.mul[2] + cx.add[2]))},${p.length > 3 ? p[3] : 1})`;
}
// Compose a placement's CXFORM under the current one: result = parent(place(colour)).
function gfxCompose(cx, pl) {
  const pm = pl.cMul, pa = pl.cAdd;
  if (!pm && !pa) return cx || null;
  const m0 = cx ? cx.mul : [1, 1, 1], a0 = cx ? cx.add : [0, 0, 0];
  const pmul = pm || [1, 1, 1, 1], padd = pa || [0, 0, 0, 0];
  return {
    mul: [m0[0] * pmul[0], m0[1] * pmul[1], m0[2] * pmul[2]],
    add: [padd[0] * m0[0] + a0[0], padd[1] * m0[1] + a0[1], padd[2] * m0[2] + a0[2]],
  };
}
function gfxStyle(ctx, f, cx) {
  if (f.type === 'solid') return gfxApplyCx(f.color || '#888', cx);
  if (f.type === 'linear') { const m = f.matrix, a = gfxApply(m, -819.2, 0), b = gfxApply(m, 819.2, 0); const g = ctx.createLinearGradient(a[0], a[1], b[0], b[1]); for (const s of f.stops) g.addColorStop(Math.max(0, Math.min(1, s.pos)), gfxApplyCx(s.color, cx)); return g; }
  if (f.type === 'radial') { const m = f.matrix, c = gfxApply(m, 0, 0), e = gfxApply(m, 819.2, 0); const r = Math.hypot(e[0] - c[0], e[1] - c[1]) || 1; const g = ctx.createRadialGradient(c[0], c[1], 0, c[0], c[1], r); for (const s of f.stops) g.addColorStop(Math.max(0, Math.min(1, s.pos)), gfxApplyCx(s.color, cx)); return g; }
  if (f.type === 'bitmap') { const img = gfx.images.get(f.image); if (img && img.complete && img.naturalWidth) { const pat = ctx.createPattern(img, f.repeat ? 'repeat' : 'no-repeat'); try { pat.setTransform(new DOMMatrix(f.matrix)); } catch { } return pat; } return 'rgba(120,140,160,.45)'; }
  return '#888';
}
function gfxDrawShape(ctx, sh, cx) {
  for (const f of sh.fills) { ctx.fillStyle = gfxStyle(ctx, f, cx); ctx.fill(gfxPath(f.path)); }
  for (const s of sh.strokes) { ctx.strokeStyle = gfxApplyCx(s.color || '#000', cx); ctx.lineWidth = Math.max(s.width || 0, 0.15); ctx.stroke(gfxPath(s.path)); }
}
function gfxDrawChar(ctx, id, tick, guard, cx) {
  if (guard > 64) return; const sc = gfx.scene, ids = String(id);
  if (sc.shapes[ids]) { gfxDrawShape(ctx, sc.shapes[ids], cx); return; }
  const sp = sc.sprites[ids];
  if (sp) { const fc = sp.frameCount || 1; const fr = sp.frames[((tick % fc) + fc) % fc] || sp.frames[0] || []; gfxDrawList(ctx, fr, tick, guard + 1, cx); }
}
function gfxDrawList(ctx, list, tick, guard, cx) {
  for (const pl of list) {
    if (pl.char < 0) continue;
    ctx.save();
    const m = pl.matrix; ctx.transform(m[0], m[1], m[2], m[3], m[4], m[5]);
    const a = ctx.globalAlpha; ctx.globalAlpha = a * (pl.alpha == null ? 1 : pl.alpha);
    gfxDrawChar(ctx, pl.char, tick, guard, gfxCompose(cx, pl));
    ctx.globalAlpha = a; ctx.restore();
  }
}
function gfxBoundsOf(id, m, acc, guard) {
  if (guard > 64) return; const sc = gfx.scene, ids = String(id);
  if (sc.shapes[ids]) {
    const b = sc.shapes[ids].bounds;
    for (const [x, y] of [[b[0], b[1]], [b[0] + b[2], b[1]], [b[0], b[1] + b[3]], [b[0] + b[2], b[1] + b[3]]]) {
      const t = gfxApply(m, x, y); acc.x0 = Math.min(acc.x0, t[0]); acc.y0 = Math.min(acc.y0, t[1]); acc.x1 = Math.max(acc.x1, t[0]); acc.y1 = Math.max(acc.y1, t[1]);
    }
    return;
  }
  const sp = sc.sprites[ids];
  if (sp) for (const pl of (sp.frames[0] || [])) if (pl.char >= 0) gfxBoundsOf(pl.char, gfxMul(m, pl.matrix), acc, guard + 1);
}
function gfxSymbolBounds() {
  const acc = { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity }; const sel = gfx.symbol, I = [1, 0, 0, 1, 0, 0];
  if (sel.startsWith('image:')) {
    const e = (gfx.scene.images || []).find(i => i.id === gfx.curId), img = gfx.images.get(gfx.curId);
    const w = (e && e.w) || (img && img.naturalWidth) || 100, h = (e && e.h) || (img && img.naturalHeight) || 100;
    return { x0: 0, y0: 0, x1: w, y1: h };
  }
  if (sel === 'main') for (const pl of (gfx.scene.main.frames[0] || [])) if (pl.char >= 0) gfxBoundsOf(pl.char, pl.matrix, acc, 0);
  else if (sel.startsWith('sprite:')) gfxBoundsOf(parseInt(sel.slice(7)), I, acc, 0);
  else if (sel.startsWith('shape:')) gfxBoundsOf(parseInt(sel.slice(6)), I, acc, 0);
  if (!isFinite(acc.x0)) return { x0: 0, y0: 0, x1: gfx.scene.width || 100, y1: gfx.scene.height || 100 };
  return acc;
}
function gfxFit() {
  if (!gfx.scene) return;
  const cv = gfx.canvas, cw = cv.clientWidth || 1, ch = cv.clientHeight || 1, b = gfxSymbolBounds();
  const bw = Math.max(b.x1 - b.x0, 1), bh = Math.max(b.y1 - b.y0, 1);
  gfx.zoom = Math.min(cw / bw, ch / bh) * 0.92;
  gfx.panX = cw / 2 - gfx.zoom * (b.x0 + bw / 2);
  gfx.panY = ch / 2 - gfx.zoom * (b.y0 + bh / 2);
  gfxDraw();
}
function gfxDraw() {
  if (!gfx.scene || !gfx.canvas) return;
  const cv = gfx.canvas, ctx = gfx.ctx, dpr = Math.min(window.devicePixelRatio || 1, 2);
  const cw = cv.clientWidth, ch = cv.clientHeight;
  if (cv.width !== Math.round(cw * dpr) || cv.height !== Math.round(ch * dpr)) { cv.width = Math.round(cw * dpr); cv.height = Math.round(ch * dpr); }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cw, ch);
  ctx.save();
  ctx.translate(gfx.panX, gfx.panY); ctx.scale(gfx.zoom, gfx.zoom);
  const tick = gfx.frame | 0, sel = gfx.symbol, sc = gfx.scene;
  try {
    if (sel === 'main') gfxDrawList(ctx, sc.main.frames[tick % Math.max(sc.main.frameCount, 1)] || [], tick, 0);
    else if (sel.startsWith('sprite:')) gfxDrawChar(ctx, parseInt(sel.slice(7)), tick, 0);
    else if (sel.startsWith('shape:')) gfxDrawChar(ctx, parseInt(sel.slice(6)), 0, 0);
    else if (sel.startsWith('image:')) {
      const img = gfx.images.get(gfx.curId);
      if (img && img.complete && img.naturalWidth) {
        const e = (sc.images || []).find(i => i.id === gfx.curId);
        ctx.drawImage(img, 0, 0, (e && e.w) || img.naturalWidth, (e && e.h) || img.naturalHeight);
      }
    }
  } catch { }
  ctx.restore();
}
function gfxStop() { gfx.playing = false; if (gfx.raf) cancelAnimationFrame(gfx.raf); gfx.raf = 0; $('gfxPlay').textContent = '▶'; $('gfxPlay').classList.remove('on'); }
function gfxPlayToggle() {
  gfx.playing = !gfx.playing;
  $('gfxPlay').textContent = gfx.playing ? '❚❚' : '▶'; $('gfxPlay').classList.toggle('on', gfx.playing);
  if (gfx.playing) { gfx.lastT = performance.now(); gfx.raf = requestAnimationFrame(gfxAnimate); } else gfxStop();
}
function gfxAnimate(now) {
  if (!gfx.playing) return;
  const fps = Math.max(gfx.scene.frameRate || 30, 1);
  if (now - gfx.lastT >= 1000 / fps) {
    gfx.lastT = now; const range = Math.max(gfxAnimRange(), 1);
    gfx.frame = (gfx.frame + 1) % range; $('gfxFrame').value = gfx.frame; gfxUpdateFrameLbl(); gfxDraw();
  }
  gfx.raf = requestAnimationFrame(gfxAnimate);
}
(function gfxInit() {
  gfx.canvas = $('gfxCanvas'); if (!gfx.canvas) return;
  gfx.ctx = gfx.canvas.getContext('2d');
  new ResizeObserver(() => { if (!$('gfxStage').hidden) gfxDraw(); }).observe(gfx.canvas);
  $('gfxFit').onclick = gfxFit;
  $('gfxPlay').onclick = gfxPlayToggle;
  $('gfxFrame').oninput = e => { gfxStop(); gfx.frame = parseInt(e.target.value) || 0; gfxUpdateFrameLbl(); gfxDraw(); };
  $('gfxFilter').oninput = e => {
    const q = e.target.value.trim().toLowerCase();
    for (const g of $('gfxTree').querySelectorAll('.gfx-group')) {
      const body = g.querySelector('.gfx-gbody'); let any = false;
      // each direct child is a leaf (.gfx-node) or an expandable sprite (.gfx-snode);
      // match on the row label only (collapsed children aren't in the DOM yet).
      for (const child of body.children) {
        const row = child.classList.contains('gfx-snode') ? child.querySelector(':scope > .gfx-node') : child;
        const txt = (row || child).textContent.toLowerCase();
        const show = !q || txt.includes(q); child.hidden = !show; if (show) any = true;
      }
      g.hidden = !!q && !any;
    }
  };
  gfx.canvas.addEventListener('wheel', e => {
    e.preventDefault(); const r = gfx.canvas.getBoundingClientRect(); const mx = e.clientX - r.left, my = e.clientY - r.top;
    const f = e.deltaY < 0 ? 1.15 : 1 / 1.15; const nz = Math.max(0.02, Math.min(80, gfx.zoom * f));
    gfx.panX = mx - (mx - gfx.panX) * (nz / gfx.zoom); gfx.panY = my - (my - gfx.panY) * (nz / gfx.zoom); gfx.zoom = nz; gfxDraw();
  }, { passive: false });
  let drag = false, sx = 0, sy = 0;
  gfx.canvas.addEventListener('mousedown', e => { drag = true; sx = e.clientX - gfx.panX; sy = e.clientY - gfx.panY; gfx.canvas.style.cursor = 'grabbing'; });
  window.addEventListener('mousemove', e => { if (drag) { gfx.panX = e.clientX - sx; gfx.panY = e.clientY - sy; gfxDraw(); } });
  window.addEventListener('mouseup', () => { if (drag) { drag = false; gfx.canvas.style.cursor = 'grab'; } });
})();

// ---------------------------------------------------------------- image / DDS converter
function fillImage(d) {
  const img = $('imgView');
  imgZoomReset();
  img.onload = () => { $('imgInfo').textContent = `${d.format || ''} · ${img.naturalWidth}×${img.naturalHeight}`; };
  img.src = d.dataUrl;
  $('imgInfo').textContent = d.format || '';
}

// Scroll-wheel zoom (toward the cursor) + drag-to-pan for the texture/DDS viewer.
// Double-click resets to fit. Zoom 1 = fit-to-pane (the default flex centring).
let imgZoom = 1, imgPanX = 0, imgPanY = 0;
function imgApply() {
  const img = $('imgView');
  img.style.transform = `translate(${imgPanX}px, ${imgPanY}px) scale(${imgZoom})`;
  img.style.cursor = imgZoom > 1 ? 'grab' : '';
}
function imgZoomReset() { imgZoom = 1; imgPanX = 0; imgPanY = 0; imgApply(); }
(function imageZoom() {
  const stage = document.querySelector('#pane-image .img-stage');
  if (!stage) return;
  stage.addEventListener('wheel', e => {
    e.preventDefault();
    const r = stage.getBoundingClientRect();
    const mx = e.clientX - (r.left + r.width / 2), my = e.clientY - (r.top + r.height / 2);
    const f = e.deltaY < 0 ? 1.15 : 1 / 1.15;
    const nz = Math.min(40, Math.max(1, imgZoom * f));
    if (nz === imgZoom) return;
    imgPanX = mx - (mx - imgPanX) * (nz / imgZoom);
    imgPanY = my - (my - imgPanY) * (nz / imgZoom);
    imgZoom = nz;
    if (imgZoom === 1) { imgPanX = 0; imgPanY = 0; }
    imgApply();
  }, { passive: false });
  let dragging = false, sx = 0, sy = 0;
  stage.addEventListener('mousedown', e => {
    if (imgZoom <= 1 || e.button !== 0) return;
    dragging = true; sx = e.clientX - imgPanX; sy = e.clientY - imgPanY;
    $('imgView').style.cursor = 'grabbing'; e.preventDefault();
  });
  window.addEventListener('mousemove', e => { if (dragging) { imgPanX = e.clientX - sx; imgPanY = e.clientY - sy; imgApply(); } });
  window.addEventListener('mouseup', () => { if (dragging) { dragging = false; imgApply(); } });
  stage.addEventListener('dblclick', imgZoomReset);
})();
function bytesToB64(bytes) {
  let bin = ''; const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  return btoa(bin);
}
async function saveDds(name) {
  const img = $('imgView');
  const w = img.naturalWidth, h = img.naturalHeight;
  if (!w || !h) { setStatus('Image not ready', true); return; }
  const cv = document.createElement('canvas'); cv.width = w; cv.height = h;
  cv.getContext('2d').drawImage(img, 0, 0);
  let data;
  try { data = cv.getContext('2d').getImageData(0, 0, w, h).data; }
  catch (e) { setStatus('Cannot read image pixels: ' + e.message, true); return; }
  const fmt = $('ddsFmt').value;
  setStatus(`Encoding ${fmt} DDS…`);
  const res = await call('encodeDds', { rgba: bytesToB64(new Uint8Array(data.buffer)), w, h, format: fmt, name });
  if (res.canceled) { setStatus('Save canceled'); return; }
  if (res.ok) setStatus(`Saved ${fmt} DDS → ${res.path} (${fmtSize(res.size)})`);
  else setStatus('DDS encode failed: ' + (res.message || ''), true);
}
$('btnSaveDds').onclick = () => {
  const base = (activeTab && activeTab.kind === 'image' ? activeTab.title : 'texture').replace(/\.[^.]+$/, '');
  saveDds(base + '.dds');
};

// drag & drop import. Per dropped file:
//   1. a CodeWalker XML export (name.<ext>.xml) -> convert back to the original
//      binary resource and import it into the current folder.
//   2. an image/.dds while a .ytd/.ypt texture view is active -> import as a texture
//      into that dictionary (replace-by-name, else add).
//   3. anything else -> import the file as-is into the current folder (replace same name).
const XML_REIMPORT_RE = /\.(ydr|ydd|yft|ytd|ypt|ymap|ytyp|ymt|ymf|meta|pso|rbf|rel|ynd|ynv|ycd|ybn|yld|yed|ywr|yvr|awc|fxc|ypdb|yfd|mrf|cut|dat)\.xml$/i;
const TEXTURE_RE = /\.(png|jpe?g|bmp|webp|gif|tga|dds)$/i;
function currentFolder() { return explorer.crumbs.length ? explorer.crumbs[explorer.crumbs.length - 1] : null; }

let dragDepth = 0;
window.addEventListener('dragover', e => e.preventDefault());
window.addEventListener('dragenter', e => {
  e.preventDefault();
  if (selfDropActive()) return;                  // our own drag-out hovering back over us
  dragDepth++;
  const dz = $('dropZone'), msg = dz.querySelector('div'), folder = currentFolder();
  msg.textContent = dropTargetNode() != null
    ? `Drop images → ${dropTargetName()} · .epic → install · other files → ${folder ? folder.name : 'folder'}`
    : (folder ? `Drop files to import into ${folder.name} · .epic extensions install` : 'Mount a folder to import files · .epic extensions install');
  dz.hidden = false;
});
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('dropZone').hidden = true; } });
window.addEventListener('drop', async e => {
  e.preventDefault(); dragDepth = 0; $('dropZone').hidden = true;
  if (selfDropActive()) { setStatus('Ignored drop of our own drag'); return; }   // never re-import a drag-out
  const files = [...e.dataTransfer.files]; if (!files.length) return;
  // Resolve the dropped files' REAL disk paths once via the host (WebView2 hands
  // them over natively) — imports then go by path, with no size limits.
  let paths = [];
  try { const pr = await callWithFiles('pathsOf', {}, files); paths = pr.paths || []; } catch { }
  for (let i = 0; i < files.length; i++) {
    try { await handleDroppedFile(files[i], paths[i] || null); }
    catch (err) { setStatus(`Import failed (${files[i].name}): ${err.message || err}`, true); }
  }
});

async function handleDroppedFile(file, srcPath) {
  const lower = file.name.toLowerCase();
  if (lower.endsWith('.epic')) {                          // drop an extension to install
    if (srcPath) epicPreviewThenInstall({ path: srcPath });
    else await epicInstallFromBytes(new Uint8Array(await file.arrayBuffer()));
    return;
  }
  if (XML_REIMPORT_RE.test(lower)) { await importDroppedFile(file, true); return; }      // XML export -> original binary (small, text)
  const tnode = dropTargetNode();
  if (tnode != null && TEXTURE_RE.test(lower)) { await importDroppedTexture(tnode, file); return; }  // texture into ytd/ypt
  if (srcPath) { await importByPath(srcPath, file.name); return; }                        // raw file by PATH (any size)
  await importDroppedFile(file, false);                                                   // fallback: small files via base64
}

// Path-based raw import — the host reads the source straight from disk.
async function importByPath(srcPath, name) {
  const folder = currentFolder();
  if (!folder) { setStatus('Mount a folder before importing files', true); return; }
  setStatus(`Importing ${name}…`);
  const res = await withProgress(call('importPath', { node: folder.id, path: folder.path, name, srcPath }), 0);
  if (res.ok) { setStatus(`Imported ${res.name} into ${folder.name} (${fmtSize(res.size)})${res.note ? ' — ' + res.note : ''}`); await refreshCurrent(); }
  else setStatus('Import failed: ' + (res.message || ''), true);
}

async function importDroppedFile(file, asXml) {
  const folder = currentFolder();
  if (!folder) { setStatus('Mount a folder before importing files', true); return; }
  if (!asXml && file.size > 200 * 1024 * 1024) {   // base64 fallback can't carry big files
    setStatus(`Import failed: ${file.name} is too large for this drop path — drag it from a real folder (not an archive inside another app).`, true);
    return;
  }
  try {
    const payload = asXml
      ? { node: folder.id, path: folder.path, name: file.name, as: 'xml', content: await file.text() }
      : { node: folder.id, path: folder.path, name: file.name, content: bytesToB64(new Uint8Array(await file.arrayBuffer())) };
    setStatus(`Importing ${file.name}…`);
    const res = await withProgress(call('importFile', payload), 250);
    if (res.ok) { setStatus(`Imported ${res.name} into ${folder.name} (${fmtSize(res.size)})`); await refreshCurrent(); }
    else setStatus('Import failed: ' + (res.message || ''), true);
  } catch (e) { setStatus('Import failed: ' + e.message, true); }
}

// ---------------------------------------------------------------- inspector
function row(k, v) { return `<div><span class="k">${k}:</span> <span class="v">${v}</span></div>`; }
function showInspectorForNode(n) {
  let h = `<div class="sect"><h4>${n.type || n.kind}</h4>${row('Name', n.name)}`;
  if (!n.container) { h += row('Viewer', n.viewer); if (n.size >= 0) h += row('Size', fmtSize(n.size)); if (n.attrs) h += row('Attributes', n.attrs); }
  h += '</div>';
  inspBody.innerHTML = h;
}
function showInspectorForSelection(focus) {
  if (selection.length > 1) {
    const bytes = selection.reduce((s, n) => s + (n.size > 0 ? n.size : 0), 0);
    inspBody.innerHTML = `<div class="sect"><h4>Selection</h4>${row('Items', selection.length)}${bytes > 0 ? row('Total size', fmtSize(bytes)) : ''}</div>`;
  } else showInspectorForNode(focus || selection[0]);
}
function showInspectorForTab(tab) {
  if (tab.kind === 'model') {
    const r = tab.data, s = r.stats || {};
    let h = `<div class="sect"><h4>Model</h4>${row('File', r.name)}${row('Parts', r.parts.length)}
      ${row('Geometries', s.geometryCount ?? '?')}${row('Rendered', s.rendered ?? '?')}${row('Skipped', s.skipped ?? 0)}</div>`;
    const mats = r.parts.flatMap(p => p.materials || []);
    if (mats.length) { h += `<div class="sect"><h4>Materials (${mats.length})</h4>`; for (const m of mats.slice(0, 24)) h += row(m.shader, m.diffuse || '—'); h += '</div>'; }
    if (s.skipSamples && s.skipSamples.length) { h += `<div class="sect"><h4>Skipped</h4>`; for (const x of s.skipSamples) h += `<div class="muted" style="font-size:11px">${x}</div>`; h += '</div>'; }
    inspBody.innerHTML = h;
  } else if (tab.kind === 'explorer') {
    inspBody.innerHTML = `<div class="muted">${explorer.items.length} item${explorer.items.length === 1 ? '' : 's'}</div>`;
  } else if (tab.kind === 'gfx') {
    const g = tab.data;
    if (!g.ok) { inspBody.innerHTML = `<div class="muted">GFX parse error</div>`; return; }
    inspBody.innerHTML = `<div class="sect"><h4>GFX</h4>${row('File', g.name)}${row('Format', g.signature + ' (' + g.compression + ')')}
      ${row('Size', Math.round(g.width) + '×' + Math.round(g.height))}${row('Frames', g.frameCount)}${row('Tags', g.tagCount)}
      ${row('Images', (g.images || []).length)}${row('Symbols', (g.symbols || []).length)}</div>`;
  }
}

// ---------------------------------------------------------------- context menu (with submenus)
let ctxMenus = [];
function hideContext() { for (const m of ctxMenus) m.remove(); ctxMenus = []; }
function closeAbove(level) { while (ctxMenus.length > level + 1) ctxMenus.pop().remove(); }
function openMenu(items, x, y, level) {
  const el = document.createElement('div'); el.className = 'ctx'; el.__level = level;
  document.body.append(el);
  for (const it of items) {
    if (it.sep) { const s = document.createElement('div'); s.className = 'ctx-sep'; el.append(s); continue; }
    const mi = document.createElement('div'); mi.className = 'mi' + (it.disabled ? ' disabled' : '');
    const right = it.submenu ? '<span class="arrow">▸</span>' : (it.accel ? `<span class="accel">${it.accel}</span>` : '');
    mi.innerHTML = `<span class="mlabel">${it.label}</span>${right}`;
    mi.onmouseenter = () => { closeAbove(level); if (it.submenu) { const r = mi.getBoundingClientRect(); openMenu(it.submenu, r.right - 3, r.top - 5, level + 1); } };
    if (!it.submenu && !it.disabled) mi.onclick = e => { e.stopPropagation(); hideContext(); it.action(); };
    el.append(mi);
  }
  const w = el.offsetWidth || 200, h = el.offsetHeight || 10;
  el.style.left = Math.max(2, Math.min(x, window.innerWidth - w - 4)) + 'px';
  el.style.top = Math.max(2, Math.min(y, window.innerHeight - h - 4)) + 'px';
  ctxMenus.push(el);
}
function showMenu(items, x, y) { hideContext(); openMenu(items, x, y, 0); }
document.addEventListener('click', hideContext);
document.addEventListener('scroll', hideContext, true);

function createTarget(node) { return (node && node.container) ? node : explorer.crumbs[explorer.crumbs.length - 1]; }
function menuFor(node) {
  const target = createTarget(node);
  const tick = on => on ? '● ' : '○ ';
  const items = [];
  if (node && !node.container) items.push({ label: 'Open', action: () => openFile(node) });
  if (node && node.container) items.push({ label: 'Open', action: () => onItemDbl(node) });
  if (node && !node.container && XML_EDITABLE.has(extOf(node.name)))
    items.push({ label: 'Edit as XML', action: () => openFile(node, 'xml') });
  items.push({ label: 'New folder', accel: 'Ctrl+D', action: () => newFolder(target) });
  items.push({ label: 'New RPF', accel: 'Ctrl+N', action: () => newRpf(target) });
  items.push({ label: 'New YTD', action: () => newYtd(target) });
  items.push({ sep: true });
  const multiSel = selection.length > 1 && node && isSelected(node);
  if (multiSel) items.push({ label: `Extract ${selection.length} items…`, action: () => extractMany(selection) });
  else if (node && (node.kind === 'file' || node.kind === 'archive')) items.push({ label: 'Extract…', action: () => extract(node) });
  // Export as XML (CodeWalker XML — re-importable by dropping the .xml back in).
  if (multiSel) items.push({ label: `Export ${selection.length} as XML…`, action: () => exportXmlMany(selection) });
  else if (node && node.kind === 'file') items.push({ label: 'Export as XML…', action: () => exportXml(node) });
  items.push({ label: 'Extract all…', action: () => extractAll(target) });
  if (node && node.id !== 0) {
    items.push({ sep: true });
    const multi = selection.length > 1 && isSelected(node);
    if (!multi) items.push({ label: 'Rename', accel: 'F2', action: () => rename(node) });
    items.push({ label: multi ? `Delete ${selection.length} items` : 'Delete', accel: 'Del', action: () => deleteNodes(multi ? selection : [node]) });
    if (!multi && node.kind === 'archive')
      items.push({ label: 'Convert to OPEN encryption', action: () => convertEncryption(node) });
  }
  items.push({ sep: true });
  items.push({ label: 'Sort by', submenu: [
    { label: tick(sortMode === 'name') + 'Name', action: () => setSort('name') },
    { label: tick(sortMode === 'type') + 'Type', action: () => setSort('type') },
    { label: tick(sortMode === 'size') + 'Size', action: () => setSort('size') },
  ] });
  items.push({ label: 'View', submenu: [
    { label: tick(viewMode === 'icons') + 'Large icons', action: () => setView('icons') },
    { label: tick(viewMode === 'list') + 'List', action: () => setView('list') },
    { label: tick(viewMode === 'details') + 'Details', action: () => setView('details') },
  ] });
  return items;
}
$('pane-explorer').addEventListener('contextmenu', e => { e.preventDefault(); showMenu(menuFor(null), e.clientX, e.clientY); });

async function extract(n) {
  setStatus(`Extracting ${n.name}…`);
  const res = await call('extract', { node: n.id });
  if (res.canceled) { setStatus('Extract canceled'); return; }
  if (res.ok) setStatus(`Extracted ${n.name} → ${res.path} (${fmtSize(res.size)})`);
  else setStatus('Extract failed: ' + (res.message || ''), true);
}
async function exportXml(n) {
  setStatus(`Converting ${n.name} to XML…`);
  const res = await call('exportXml', { node: n.id, path: n.path });
  if (res.canceled) { setStatus('Export canceled'); return; }
  if (res.ok) setStatus(`Exported ${n.name} as XML → ${res.path}`);
  else setStatus('Export as XML failed: ' + (res.message || ''), true);
}
async function exportXmlMany(nodes) {
  const sel = nodes.filter(n => n && n.id !== 0 && n.kind === 'file');
  if (!sel.length) { setStatus('Select files to export as XML.', true); return; }
  setStatus(`Exporting ${sel.length} files as XML…`);
  const res = await call('exportXmlMany', { nodes: sel.map(n => n.id), paths: sel.map(n => n.path || '') });
  if (res.canceled) { setStatus('Export canceled'); return; }
  if (res.ok) setStatus(`Exported ${res.count.toLocaleString()} as XML → ${res.path}` + (res.failed ? ` (${res.failed} failed: ${res.message || 'not convertible'})` : ''));
  else setStatus('Export failed: ' + (res.message || 'not convertible'), true);
}
async function extractMany(nodes) {
  const ids = nodes.filter(n => n && n.id !== 0 && (n.kind === 'file' || n.kind === 'archive' || n.kind === 'dir' || n.kind === 'folder')).map(n => n.id);
  if (!ids.length) { setStatus('Nothing extractable selected.', true); return; }
  setStatus(`Extracting ${ids.length} items…`);
  const res = await call('extractMany', { nodes: ids });
  if (res.canceled) { setStatus('Extract canceled'); return; }
  if (res.ok) setStatus(`Extracted ${res.count.toLocaleString()} files → ${res.path}` + (res.failed ? ` (${res.failed} failed)` : ''));
  else setStatus('Extract failed: ' + (res.message || ''), true);
}
async function extractAll(target) {
  if (!target || target.id === 0) { setStatus('Extract all works inside an archive.', true); return; }
  setStatus('Extracting all…');
  const res = await call('extractAll', { node: target.id });
  if (res.canceled) { setStatus('Extract canceled'); return; }
  if (res.ok) setStatus(`Extracted ${res.count.toLocaleString()} files → ${res.path}`);
  else setStatus('Extract all failed: ' + (res.message || ''), true);
}

// ---- create ----
function promptDialog({ title, label, value = '', checkboxLabel = null, okLabel = 'Create', selectBasename = false }) {
  return new Promise(resolve => {
    const ov = document.createElement('div'); ov.className = 'modal';
    ov.innerHTML = `<div class="modal-card"><h3>${title}</h3>
      <label class="dlg-l">${label}</label>
      <input class="dlg-in" type="text" spellcheck="false" value="${esc(value)}">
      ${checkboxLabel ? `<label class="dlg-cb"><input type="checkbox" class="dlg-chk"> ${checkboxLabel}</label>` : ''}
      <div class="modal-actions"><button class="btn ghost dlg-cancel">Cancel</button><button class="btn primary dlg-ok">${esc(okLabel)}</button></div></div>`;
    document.body.append(ov);
    const input = ov.querySelector('.dlg-in'); input.focus();
    // For rename, preselect just the base name (everything before the last dot), like Windows.
    const dot = value.lastIndexOf('.');
    if (selectBasename && dot > 0) input.setSelectionRange(0, dot); else input.select();
    const chk = ov.querySelector('.dlg-chk');
    const done = v => { ov.remove(); resolve(v); };
    ov.querySelector('.dlg-ok').onclick = () => { const n = input.value.trim(); if (n) done({ name: n, checked: chk ? chk.checked : false }); };
    ov.querySelector('.dlg-cancel').onclick = () => done(null);
    input.onkeydown = e => { if (e.key === 'Enter') ov.querySelector('.dlg-ok').click(); else if (e.key === 'Escape') done(null); };
  });
}
function badTarget(t) { if (!t) { setStatus('No location selected.', true); return true; } return false; }
async function newFolder(target) {
  target = target || createTarget(null); if (badTarget(target)) return;
  const r = await promptDialog({ title: 'New folder', label: 'Folder name', value: 'new_folder' });
  if (r) afterCreate(await call('createFolder', { node: target.id, path: target.path, name: r.name }));
}
async function newRpf(target) {
  target = target || createTarget(null); if (badTarget(target)) return;
  const r = await promptDialog({ title: 'New RPF', label: 'RPF name', value: 'new.rpf', checkboxLabel: 'Create content override (register in content.xml)' });
  if (r) afterCreate(await call('createRpf', { node: target.id, path: target.path, name: r.name, override: r.checked }));
}
async function newYtd(target) {
  target = target || createTarget(null); if (badTarget(target)) return;
  const r = await promptDialog({ title: 'New YTD', label: 'Texture dictionary name', value: 'new.ytd' });
  if (r) afterCreate(await call('createYtd', { node: target.id, path: target.path, name: r.name }));
}
function afterCreate(res) {
  if (res.ok) { setStatus(`Created ${res.kind} “${res.name}”` + (res.note ? ` — ${res.note}` : '')); refreshCurrent(); }
  else setStatus('Create failed: ' + (res.message || ''), true);
}

// ---- encryption converter (right-click an .rpf) ----
async function convertEncryption(node) {
  const yes = await confirmDialog({
    title: 'Convert to OPEN encryption',
    body: `Rewrite “${node.name}” (and every nested .rpf inside it) with OPEN encryption? ` +
          `The game loads OPEN archives normally, and future edits no longer need NG encryption — ` +
          `this is the safest state for modded archives. This rewrites the archive headers on disk.`,
    okLabel: 'Convert',
  });
  if (!yes) return;
  setStatus(`Converting ${node.name} to OPEN encryption…`);
  const res = await withProgress(call('convertEncryption', { node: node.id, path: node.path }), 0);
  if (res.ok) { setStatus(`${res.name}: ${res.before} → ${res.after} encryption`); await refreshCurrent(); }
  else setStatus('Convert failed: ' + (res.message || ''), true);
}

// ---- rename (right-click / F2) ----
async function rename(node) {
  if (!node || node.id === 0) return;
  const cur = node.name;
  const r = await promptDialog({ title: 'Rename', label: 'New name', value: cur, okLabel: 'Rename', selectBasename: true });
  if (!r || r.name === cur) return;
  setStatus(`Renaming ${cur}…`);
  const res = await call('rename', { node: node.id, path: node.path, name: r.name });
  if (res.ok) { node.name = res.name; setStatus(`Renamed to “${res.name}”`); refreshCurrent(); }
  else setStatus('Rename failed: ' + (res.message || ''), true);
}

// Re-read the folder currently shown (and the tree roots if at the top level),
// without yanking the user out of a non-explorer tab.
async function refreshCurrent() {
  const last = (explorer.crumbs[explorer.crumbs.length - 1]) || { id: 0 };
  if (last.id === 0) { const res = await call('expand', { node: 0 }); mountRoots = res.nodes || mountRoots; buildTree(mountRoots); }
  else childrenCache.delete(last.id);
  if (activeTab && activeTab.kind === 'explorer' && !searchActive) {
    explorer.items = await getChildren(last.id);
    renderList(explorer.items);
  }
}

// ---- delete (-> trash) ----
let batchSeq = 1;
async function deleteNode(n) { if (n && n.id !== 0) deleteNodes([n]); }

// Delete a set of nodes as one batch (so Ctrl+Z restores them all at once).
async function deleteNodes(nodes) {
  nodes = nodes.filter(n => n && n.id !== 0);
  if (!nodes.length) return;
  const batch = batchSeq++;
  let force = false, ok = 0;
  for (const n of nodes) {
    let res = await call('delete', { node: n.id, path: n.path, batch, force });
    if (res.needConfirm && !force) {
      const yes = await confirmDialog({
        title: 'Trash is full',
        body: `Trash is ${res.trashMb} MB (limit ${res.limitMb} MB). Permanently remove the oldest trashed items to make room?`,
        okLabel: 'Delete permanently',
      });
      if (!yes) { setStatus('Delete canceled'); break; }
      force = true;
      res = await call('delete', { node: n.id, path: n.path, batch, force: true });
    }
    if (res.ok) ok++; else setStatus('Delete failed: ' + (res.message || ''), true);
  }
  if (ok) setStatus(ok === 1 ? `Deleted “${nodes[0].name}” → trash (Ctrl+Z to undo)` : `Deleted ${ok} items → trash (Ctrl+Z to undo)`);
  clearSelection();
  refreshCurrent();
}

async function undoDelete() {
  const res = await call('undo');
  if (res.ok) { setStatus(res.restored === 1 ? `Restored “${res.name}”` : `Restored ${res.restored} items`); refreshCurrent(); }
  else setStatus(res.message || 'Nothing to undo');
}

function confirmDialog({ title, body, okLabel = 'OK' }) {
  return new Promise(resolve => {
    const ov = document.createElement('div'); ov.className = 'modal';
    ov.innerHTML = `<div class="modal-card"><h3>${title}</h3>
      <p class="muted" style="white-space:normal">${body}</p>
      <div class="modal-actions"><button class="btn ghost dlg-cancel">Cancel</button>
      <button class="btn primary dlg-ok">${okLabel}</button></div></div>`;
    document.body.append(ov);
    const done = v => { ov.remove(); resolve(v); };
    ov.querySelector('.dlg-ok').onclick = () => done(true);
    ov.querySelector('.dlg-cancel').onclick = () => done(false);
    ov.onkeydown = e => { if (e.key === 'Escape') done(false); };
  });
}

// ---- live file watching ----
async function onFsChange(m) {
  if (m.remount) {
    mountRoots = m.roots || mountRoots;
    childrenCache.clear();
    buildTree(mountRoots);
    if (activeTab && activeTab.kind === 'explorer' && !searchActive) navigate([{ id: 0, name: rootName }]);
    setStatus('Reloaded — an archive changed on disk');
  } else {
    refreshCurrent();
  }
}

// ---- shortcuts ----
window.addEventListener('keydown', e => {
  if (activeTab && activeTab.kind === 'edit') return; // don't steal keys from Monaco
  if (e.key === 'Delete' && !e.ctrlKey && !e.altKey) {
    if (selection.length) { e.preventDefault(); deleteNodes(selection); }
    else if (selectedRow && selectedRow.__node) { e.preventDefault(); deleteNode(selectedRow.__node); }
    return;
  }
  if (e.key === 'F2') {
    const n = (selection.length === 1 && selection[0]) ||
              (selectedTreeEl && selectedTreeEl.__node) ||
              (selectedRow && selectedRow.__node);
    if (n && n.id !== 0) { e.preventDefault(); rename(n); }
    return;
  }
  if (!e.ctrlKey || e.altKey) return;
  const k = e.key.toLowerCase();
  if (k === 'z' && !e.shiftKey) { e.preventDefault(); undoDelete(); }
  else if (e.shiftKey) return;
  else if (k === 'n') { e.preventDefault(); newRpf(); }
  else if (k === 'd') { e.preventDefault(); newFolder(); }
});

// ---------------------------------------------------------------- util
function fmtSize(b) {
  if (b < 0) return '';
  if (b < 1024) return b + ' B';
  if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
  return (b / 1048576).toFixed(1) + ' MB';
}

// ---------------------------------------------------------------- window controls
$('winMin').onclick = () => post('winMin');
$('winMax').onclick = () => post('winMax');
$('winClose').onclick = () => post('winClose');

// ---- frameless window resize -------------------------------------------------
// The OS title bar is gone, so the user couldn't resize the window (WindowChrome's
// resize border is covered by the WebView2 HWND). Detect a drag within 6px of any
// window edge and hand off to the native resize loop on the C# side. The toolbar is
// caption (app-region: drag) so its mousedowns don't reach JS — top-edge resize via
// the title bar is naturally skipped; sides, bottom and corners all work.
(function windowResize() {
  if (window.__POPOUT) { /* popouts resize fine too — same handler */ }
  const M = 6;
  const CUR = { l: 'ew-resize', r: 'ew-resize', t: 'ns-resize', b: 'ns-resize',
                tl: 'nwse-resize', br: 'nwse-resize', tr: 'nesw-resize', bl: 'nesw-resize' };
  const edgeAt = e => {
    const w = innerWidth, h = innerHeight, x = e.clientX, y = e.clientY;
    const l = x <= M, r = x >= w - M, t = y <= M, b = y >= h - M;
    if (t && l) return 'tl'; if (t && r) return 'tr'; if (b && l) return 'bl'; if (b && r) return 'br';
    if (l) return 'l'; if (r) return 'r'; if (t) return 't'; if (b) return 'b';
    return '';
  };
  let cur = '';
  window.addEventListener('mousemove', e => {
    if (e.buttons) return;                       // don't fight an active drag/pan/splitter
    const d = edgeAt(e);
    if (d !== cur) { cur = d; document.body.style.cursor = d ? CUR[d] : ''; }
  });
  window.addEventListener('mousedown', e => {
    if (e.button !== 0) return;
    const d = edgeAt(e);
    if (d) { e.preventDefault(); e.stopPropagation(); post('winResize', { edge: d }); }
  }, true);                                       // capture: beats splitter/pan handlers
})();

// ---------------------------------------------------------------- popout window mode
// A torn-off tab: hide the chrome and show just its viewer (state transferred from
// the originating window via the bridge).
async function enterPopout(info) {
  document.body.classList.add('popout');
  $('popoutTitle').textContent = info.title || '';
  const res = await call('popoutData', { node: info.id });
  let p; try { p = JSON.parse(res.payload || '{}'); } catch { p = {}; }
  if (!p.kind) { setStatus('Nothing to show.', true); return; }
  const tab = { key: (p.nodeId != null ? p.nodeId : 'popout'), kind: p.kind, title: p.title || info.title,
                data: p.data, nodeId: p.nodeId, noClose: true };
  tabs.push(tab);
  activate(tab);
}

// ---------------------------------------------------------------- .epic extensions
// Install (drag a .epic onto the app, or the Extension button) and a builder that
// assembles a manifest of operations and packs them into an encrypted .epic.
$('extBtn').onclick = e => {
  e.stopPropagation();   // don't let the global click-to-close fire on this same click
  const r = e.currentTarget.getBoundingClientRect();
  showMenu([
    { label: 'Install extension…', action: epicPickInstall },
    { label: 'Create extension…', action: openExtBuilder },
  ], r.left, r.bottom + 4);
};

// The GTA-root-relative vpath of the currently selected explorer file (for the builder).
function selectedVpath() {
  const n = (selection && selection[0]) || (selectedRow && selectedRow.__node);
  if (!n || n.container) return '';
  return [...explorer.crumbs.slice(1).map(c => c.name), n.name].join('/');
}

function modalShell(title, sub) {
  const ov = document.createElement('div'); ov.className = 'modal';
  ov.innerHTML = `<div class="modal-card ext-card"><h3>${esc(title)}</h3>${sub ? `<p class="muted" style="white-space:normal;margin:-4px 0 10px">${esc(sub)}</p>` : ''}<div class="ext-body"></div><div class="modal-actions"></div></div>`;
  document.body.append(ov);
  ov.onkeydown = ev => { if (ev.key === 'Escape') ov.remove(); };
  return { ov, body: ov.querySelector('.ext-body'), actions: ov.querySelector('.modal-actions'), close: () => ov.remove() };
}
function addBtn(host, label, cls, fn) { const b = document.createElement('button'); b.className = 'btn ' + cls; b.textContent = label; b.onclick = fn; host.append(b); return b; }

// ---- install ----
async function epicPickInstall() {
  const r = await call('pickEpic');
  if (!r.path) return;
  epicPreviewThenInstall({ path: r.path });
}
async function epicInstallFromBytes(bytes) { epicPreviewThenInstall({ content: bytesToB64(bytes) }); }

async function epicPreviewThenInstall(srcArg) {
  setStatus('Reading extension…');
  const info = await withProgress(call('inspectEpic', srcArg), 0);
  if (!info.ok) { setStatus('Not a valid .epic: ' + (info.message || ''), true); return; }
  const m = modalShell(`Install: ${info.name || 'extension'}`,
    `${info.version ? 'v' + info.version : ''}${info.author ? ' · by ' + info.author : ''}${info.target ? ' · ' + info.target : ''}`);
  let h = '';
  if (info.description) h += `<div class="ext-desc">${esc(info.description)}</div>`;
  h += `<div class="ext-plan-h">This will perform ${info.operations.length} operation${info.operations.length === 1 ? '' : 's'}:</div>`;
  h += `<div class="ext-plan">` + (info.plan || []).map(p => `<div>${esc(p)}</div>`).join('') + `</div>`;
  h += `<div class="muted" style="margin-top:8px;font-size:11px">Originals are backed up to GTAV\\EpicRpf_backups before any change.</div>`;
  m.body.innerHTML = h;
  addBtn(m.actions, 'Cancel', 'ghost', m.close);
  addBtn(m.actions, 'Install', 'primary', async () => {
    m.close(); setStatus('Installing extension…');
    const res = await withProgress(call('installEpic', srcArg), 0);
    if (!res.ok && res.message) { setStatus('Install failed: ' + res.message, true); }
    showEpicResults(res);
    await refreshCurrent();
  });
}
function showEpicResults(res) {
  const fails = (res.results || []).filter(r => !r.ok);
  setStatus(res.ok ? `Installed “${res.name}” — ${res.okCount} operation(s) applied`
                   : `Installed with ${res.failCount} failure(s) — ${res.okCount} ok`, !res.ok);
  const m = modalShell(res.ok ? 'Extension installed' : 'Extension installed with errors',
    res.backup ? 'Backups: ' + res.backup : '');
  m.body.innerHTML = `<div class="ext-plan">` + (res.results || []).map(r =>
    `<div class="${r.ok ? 'ext-ok' : 'ext-fail'}">[${r.ok ? 'OK' : 'FAIL'}] ${esc(r.op)} ${esc(r.target)} — ${esc(r.message)}</div>`).join('') + `</div>`;
  addBtn(m.actions, 'Close', 'primary', m.close);
}

// ---- builder ----
function openExtBuilder() {
  const m = modalShell('Create extension (.epic)', 'Assemble operations, then build an encrypted .epic others can drag-drop to install.');
  const ops = [];
  m.body.innerHTML = `
    <div class="ext-meta">
      <label>Name <input class="ext-in" id="exName" value="My Extension"></label>
      <label>Author <input class="ext-in" id="exAuthor"></label>
      <label>Version <input class="ext-in" id="exVer" value="1.0"></label>
      <label class="wide">Description <input class="ext-in" id="exDesc"></label>
    </div>
    <div class="ext-ops-h"><span>Operations</span><button class="btn ghost sm" id="exAdd">+ Add operation</button></div>
    <div class="ext-ops" id="exOps"></div>`;
  const opsEl = m.body.querySelector('#exOps');
  const render = () => {
    opsEl.innerHTML = '';
    ops.forEach((op, i) => opsEl.append(opRow(op, i, () => { ops.splice(i, 1); render(); })));
    if (!ops.length) opsEl.innerHTML = '<div class="muted" style="padding:8px">No operations yet — click “Add operation”.</div>';
  };
  m.body.querySelector('#exAdd').onclick = () => { ops.push({ op: 'replaceFile', target: selectedVpath(), source: '', createIfMissing: true }); render(); };
  render();
  addBtn(m.actions, 'Cancel', 'ghost', m.close);
  addBtn(m.actions, 'Build .epic…', 'primary', async () => {
    const manifest = {
      format: 'epic/1',
      name: m.body.querySelector('#exName').value.trim() || 'extension',
      author: m.body.querySelector('#exAuthor').value.trim(),
      version: m.body.querySelector('#exVer').value.trim() || '1.0',
      description: m.body.querySelector('#exDesc').value.trim(),
      target: 'update.rpf',
      operations: ops.filter(o => o.target),
    };
    if (!manifest.operations.length) { setStatus('Add at least one operation with a target.', true); return; }
    const bad = manifest.operations.find(o => o.op === 'replaceFile' && !o.source);
    if (bad) { setStatus('A replace-file operation has no source file picked.', true); return; }
    m.close(); setStatus('Building extension…');
    const res = await withProgress(call('createEpic', { manifest: JSON.stringify(manifest) }), 0);
    if (res.canceled) { setStatus('Build canceled'); return; }
    setStatus(res.ok ? `Built ${res.path} (${fmtSize(res.size)}, ${res.ops} ops)` : 'Build failed: ' + (res.message || ''), !res.ok);
  });
}

function opRow(op, idx, onRemove) {
  const row = document.createElement('div'); row.className = 'ext-op';
  const head = document.createElement('div'); head.className = 'ext-op-head';
  const typeSel = document.createElement('select'); typeSel.className = 'ext-in';
  for (const [v, l] of [['replaceFile', 'Replace / add file'], ['xml', 'XML edit'], ['text', 'Text edit'], ['deleteFile', 'Delete file']]) {
    const o = document.createElement('option'); o.value = v; o.textContent = l; if (op.op === v) o.selected = true; typeSel.append(o);
  }
  head.innerHTML = `<span class="ext-op-n">#${idx + 1}</span>`;
  head.append(typeSel);
  const rm = document.createElement('button'); rm.className = 'ext-x'; rm.textContent = '✕'; rm.title = 'Remove'; rm.onclick = onRemove; head.append(rm);
  const fields = document.createElement('div'); fields.className = 'ext-op-fields';
  row.append(head, fields);

  const field = (label, key, ph) => {
    const w = document.createElement('label'); w.className = 'ext-f';
    const inp = document.createElement('input'); inp.className = 'ext-in'; inp.value = op[key] || ''; inp.placeholder = ph || '';
    inp.oninput = () => op[key] = inp.value;
    w.append(document.createTextNode(label), inp); fields.append(w); return inp;
  };
  const select = (label, key, choices, def) => {
    const w = document.createElement('label'); w.className = 'ext-f';
    const sel = document.createElement('select'); sel.className = 'ext-in';
    for (const c of choices) { const o = document.createElement('option'); o.value = c; o.textContent = c; if ((op[key] || def) === c) o.selected = true; sel.append(o); }
    op[key] = op[key] || def; sel.onchange = () => { op[key] = sel.value; build(); };
    w.append(document.createTextNode(label), sel); fields.append(w); return sel;
  };
  const targetField = () => {
    const w = document.createElement('label'); w.className = 'ext-f wide';
    const inp = document.createElement('input'); inp.className = 'ext-in'; inp.value = op.target || ''; inp.placeholder = 'update/update.rpf/common/data/...';
    inp.oninput = () => op.target = inp.value;
    const use = document.createElement('button'); use.className = 'btn ghost sm'; use.textContent = 'Use selected'; use.title = 'Use the file selected in the explorer';
    use.onclick = () => { const v = selectedVpath(); if (v) { inp.value = v; op.target = v; } else setStatus('Select a file in the explorer first.', true); };
    w.append(document.createTextNode('Target vpath'), inp, use); fields.append(w); return inp;
  };

  function build() {
    fields.innerHTML = '';
    op.op = typeSel.value;
    const ti = targetField();
    if (op.op === 'replaceFile') {
      const sf = field('Source file', 'source', 'pick a file from disk');
      const pick = document.createElement('button'); pick.className = 'btn ghost sm'; pick.textContent = 'Pick…';
      pick.onclick = async () => {
        const r = await call('pickFile');
        if (!r.path) return;
        op.source = r.path; sf.value = r.path;
        // A folder-ish target (empty / trailing slash / no dot in the last segment)
        // gets the picked file's name appended, so the destination is a full path.
        const fname = r.path.split(/[\\/]/).pop();
        const t = (op.target || '').trim();
        const lastSeg = t.split(/[\\/]/).pop();
        if (!t || /[\\/]$/.test(t) || (lastSeg && !lastSeg.includes('.'))) {
          op.target = t ? t.replace(/[\\/]+$/, '') + '/' + fname : fname;
          ti.value = op.target;
        }
      };
      sf.parentElement.append(pick);
    } else if (op.op === 'xml') {
      const act = select('Action', 'action', ['add', 'replace', 'remove', 'setattr', 'settext'], 'add');
      field('XPath', 'xpath', 'e.g. //dataFiles  or  (//spec)[1]');
      const a = op.action || 'add';
      if (a === 'add' || a === 'replace') field('XML node', 'xml', '<Item>…</Item>');
      if (a === 'setattr') { field('Attribute', 'attr', 'value'); field('Value', 'value', ''); }
      if (a === 'settext') field('Text', 'value', '');
    } else if (op.op === 'text') {
      const act = select('Action', 'action', ['append', 'insertBefore', 'insertAfter', 'replace', 'delete'], 'append');
      const a = op.action || 'append';
      if (a !== 'append') field('Find (line/text)', 'find', '');
      if (a !== 'delete') field('Value', 'value', '');
    }
    const note = field('Note (optional)', 'note', 'shown in the install preview');
    note.parentElement.classList.add('wide');
  }
  typeSel.onchange = build;
  build();
  return row;
}

// ---------------------------------------------------------------- boot
viewport.init($('gl'));
editor.initEditor($('editor'));
setStatus('Ready');

async function autoload() {
  const a = window.__AUTOLOAD;
  if (a) {
    if (a.folder) folderInput.value = a.folder;
    if (a.gen9) gen9Chk.checked = true;
    await mount();
    if (a.open === 'menu') {
      setTimeout(() => {
        showMenu(menuFor(null), 380, 250);
        const sub = [...ctxMenus[0].querySelectorAll('.mi')].find(m => m.textContent.includes('Sort by'));
        if (sub) sub.dispatchEvent(new MouseEvent('mouseenter'));
      }, 400);
      return;
    }
    if (a.open === 'icons' || a.open === 'list') { setView(a.open); return; }
    if (a.open === 'search') { optExt.checked = true; filterInput.value = 'ydd'; await runSearch(); return; }
    if (a.open === 'root') return; // stay on the freshly-mounted root (dev/screenshot)
    if (a.open === 'sorttest') { setSort('size'); return; }
    if (a.open === 'gfx') { const res = await call('firstGfx'); if (res.type === 'gfx') openTab('auto', 'gfx', res.name, res); return; }
    if (a.open === 'renametest') {
      const res = await call('firstYpt'); if (res.type === 'model') openModelTab(res);
      await new Promise(r => setTimeout(r, 700));
      const hash = viewport.partHash(1);
      const rr = await call('renameModel', { file: curModel.file, hash, name: 'ZZ_CUSTOM_NAME' });
      viewport.setPartName(1, 'ZZ_CUSTOM_NAME'); fillModelList();
      setStatus(`renamed part1 hash=${hash} ok=${rr.ok} (file=${curModel.file})`);
      return;
    }
    if (a.open === 'mtest') {
      const up = mountRoots.find(k => k.name.toLowerCase() === 'update');
      const c1 = await getChildren(up.id); const rpf = c1.find(k => k.name.toLowerCase() === 'update.rpf');
      await navigate([{ id: 0, name: rootName }, { id: up.id, name: up.name }, { id: rpf.id, name: rpf.name }]);
      const picks = explorer.ordered.slice(0, 3).map(o => o.node);
      setSelection(picks); selAnchor = 2; if (explorer.ordered[2]) selectedRow = explorer.ordered[2].row;
      showInspectorForSelection();
      return;
    }
    if (a.open === 'undotest') {
      await call('createFolder', { node: 0, name: 'EpicRpf_UNDO' });
      let res = await call('expand', { node: 0 }); mountRoots = res.nodes || mountRoots;
      const n = mountRoots.find(k => k.name === 'EpicRpf_UNDO');
      if (n) {
        const d = await call('delete', { node: n.id, batch: 7 });
        await new Promise(r => setTimeout(r, 300));
        const u = await call('undo');
        setStatus(`delete ok=${d.ok}; undo ok=${u.ok} restored=${u.restored}`);
      }
      res = await call('expand', { node: 0 }); mountRoots = res.nodes || mountRoots; buildTree(mountRoots);
      await navigate([{ id: 0, name: rootName }]);
      return;
    }
    if (a.open === 'cretest') { const x = await call('createFolder', { node: 0, name: 'EpicRpf_TEST' }); setStatus(x.ok ? 'created EpicRpf_TEST' : ('create failed: ' + x.message), !x.ok); await refreshCurrent(); return; }
    if (a.open === 'deltest') {
      const res = await call('expand', { node: 0 }); mountRoots = res.nodes || mountRoots; buildTree(mountRoots);
      const n = mountRoots.find(k => k.name === 'EpicRpf_TEST'); if (n) await deleteNode(n); else setStatus('EpicRpf_TEST not found');
      return;
    }
    if (a.open === 'updaterpf') {
      const up = mountRoots.find(k => k.name.toLowerCase() === 'update');
      if (up) { const c1 = await getChildren(up.id); const rpf = c1.find(k => k.name.toLowerCase() === 'update.rpf');
        if (rpf) await navigate([{ id: 0, name: rootName }, { id: up.id, name: up.name }, { id: rpf.id, name: rpf.name }]); }
      return;
    }
    if (a.open && a.open.startsWith('named:')) {       // open any entry by exact file name
      const res = await call('openByName', { name: a.open.slice(6) });
      if (res.type === 'model') openModelTab(res, res.node);
      else if (res.type === 'texture') openTab('auto', 'texture', res.name, res);
      else if (res.type === 'edit') openTab('auto', 'edit', res.name, res);
      else if (res.type === 'gfx') openTab('auto', 'gfx', res.name, res);
      else if (res.message) setStatus(res.message, true);
      return;
    }
    const wantYpt = a.open === 'ypt' || a.open === 'yptxml' || a.open === 'yptexp' || a.open === 'yptpop';
    const cmd = a.open === 'meta' ? 'firstMeta' : a.open === 'texture' ? 'firstYtd' : a.open === 'prop' ? 'firstProp'
      : wantYpt ? 'firstYpt' : 'firstModel';
    const res = await call(cmd, a.open === 'yptxml' ? { as: 'xml' } : {});
    if (res.type === 'model') {
      openModelTab(res);
      if (a.open === 'yptexp') setTimeout(expandTextures, 800);
      if (a.open === 'yptpop') setTimeout(() => popoutTab(activeTab), 1200);
    }
    else if (res.type === 'edit') openTab('auto', 'edit', res.name, res);
    else if (res.type === 'texture') openTab('auto', 'texture', res.name, res);
    else if (res.type === 'image') openTab('auto', 'image', res.name, res);
    return;
  }
  detectAndMount();
}

// Open an arbitrary file on disk in a tab (used by the file-association single-instance
// forwarder, and by viewer mode). Exposed on window so C# can invoke it via ExecuteScript.
async function openExternalFile(path) {
  try {
    const r = await call('openPath', { path });
    if (!r || !r.ok || !r.node) { setStatus('Could not open ' + path + (r && r.message ? ': ' + r.message : ''), true); return; }
    openFile({ id: r.node, name: r.name, viewer: r.viewer });
    setStatus('Opened ' + r.name);
  } catch (e) { setStatus('Open failed: ' + e.message, true); }
}
window.__openExternalFile = openExternalFile;

if (window.__POPOUT) enterPopout(window.__POPOUT);
else if (window.__VIEWFILE) {            // launched to view a single file (no instance was running)
  document.body.classList.add('popout', 'viewfile');
  $('popoutTitle').textContent = window.__VIEWFILE.split(/[\\/]/).pop();
  openExternalFile(window.__VIEWFILE);
}
else autoload();
