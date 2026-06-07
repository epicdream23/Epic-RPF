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
window.chrome.webview.addEventListener('message', ev => {
  const m = ev.data;
  if (m && m.reqId && pending.has(m.reqId)) {
    const r = pending.get(m.reqId);
    pending.delete(m.reqId);
    r(m);
  } else if (m && m.type === 'fschange') {
    onFsChange(m);
  }
});

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
  statusLeft.textContent = msg;
  statusLeft.style.color = isErr ? 'var(--danger)' : '';
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
};
function iconName(node) {
  if (node.kind === 'folder' || node.kind === 'dir')
    return FOLDER_ICONS[node.name.toLowerCase()] || 'folder';
  if (node.kind === 'archive') return 'archive';
  return (node.name.split('.').pop() || 'file').toLowerCase();
}
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
  const res = await call('mount', { folder, gen9: gen9Chk.checked });
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
  lab.className = 'label'; lab.textContent = n.name;

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
    chain.unshift({ id: cur.__node.id, name: cur.__node.name });
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
let sortMode = 'name';   // name | type | size
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

function sortItems(arr) {
  return arr.slice().sort((a, b) => {
    if (sortMode === 'size') {
      const sa = a.container ? -1 : (a.size || 0), sb = b.container ? -1 : (b.size || 0);
      return (sa - sb) || a.name.localeCompare(b.name, undefined, { numeric: true });
    }
    if (sortMode === 'type') return (a.type || '').localeCompare(b.type || '') || a.name.localeCompare(b.name, undefined, { numeric: true });
    return a.name.localeCompare(b.name, undefined, { numeric: true });
  });
}

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
    row.innerHTML =
      `<span class="c-name">${iconImg(iconName(it))}<span class="label">${it.name}</span></span>` +
      `<span class="c-type">${it.type || ''}</span>` +
      `<span class="c-size">${sizeText}</span>` +
      `<span class="c-attr">${it.attrs || ''}</span>`;
  } else {
    row.innerHTML = `${iconImg(iconName(it))}<span class="label">${it.name}</span>`;
  }
  row.onclick = e => onRowClick(it, row, e);
  row.ondblclick = () => onItemDbl(it);
  row.oncontextmenu = e => {
    e.preventDefault(); e.stopPropagation();
    if (!isSelected(it)) { setSelection([it]); selAnchor = row.__idx; selectedRow = row; showInspectorForSelection(it); }
    showMenu(menuFor(it), e.clientX, e.clientY);
  };
  return row;
}

function setSort(m) { sortMode = m; if (activeTab && activeTab.kind === 'explorer') renderList(explorer.items); }
function setView(m) { viewMode = m; if (activeTab && activeTab.kind === 'explorer') renderList(explorer.items); }

function onItemDbl(item) {
  if (item.container) navigate([...explorer.crumbs, { id: item.id, name: item.name }]);
  else openFile(item);
}

async function openFile(n, as) {
  setStatus(`Opening ${n.name}…`);
  const res = await call('open', { node: n.id, as });
  if (res.type === 'error') { setStatus('Open failed: ' + res.message, true); return; }
  if (res.type === 'model') openModelTab(res, n.id);
  else if (res.type === 'edit') openTab(n.id, 'edit', n.name, res, n.id);
  else if (res.type === 'hex') openTab(n.id, 'hex', n.name, res, n.id);
  else if (res.type === 'texture') openTab(n.id, 'texture', n.name, res, n.id);
  else if (res.type === 'image') openTab(n.id, 'image', n.name, res, n.id);
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
  showInspectorForTab(tab);
}

// ---------------------------------------------------------------- model pane
let modelTextures = [];
function fillModel(res) {
  collapseTextures();
  viewport.loadModel(res);
  fillModelList();
  fillLodSelector();
  fillModelTextures(res.textures || []);
  requestAnimationFrame(() => requestAnimationFrame(() => viewport.fit()));
  updateVpStats();
}
function fillLodSelector() {
  const sel = $('lodSel'); sel.innerHTML = '';
  for (const l of viewport.availableLods()) { const o = document.createElement('option'); o.value = l; o.textContent = l; sel.append(o); }
}
// A .ydd / .ypt holds several independent drawables — list them down the left so
// the user clicks one (or "All") instead of using a dropdown.
function fillModelList() {
  const wrap = $('vpModels');
  const names = viewport.partNames();
  wrap.innerHTML = '';
  if (names.length <= 1) { wrap.hidden = true; return; }
  wrap.hidden = false;
  const head = document.createElement('div'); head.className = 'vp-mhead'; head.textContent = `Models (${names.length})`;
  wrap.append(head);
  const add = (label, idx) => {
    const el = document.createElement('div');
    el.className = 'vp-model'; el.dataset.idx = String(idx); el.textContent = label; el.title = label;
    if (viewport.currentPart() === idx) el.classList.add('sel');
    el.onclick = () => selectPart(idx);
    wrap.append(el);
  };
  add(`All (${names.length})`, -1);
  names.forEach((n, i) => add(n, i));
}
function selectPart(idx) {
  viewport.setPart(idx);
  for (const el of $('vpModels').querySelectorAll('.vp-model')) el.classList.toggle('sel', parseInt(el.dataset.idx, 10) === idx);
  fillLodSelector();
  updateVpStats();
}
// Embedded textures (currently from .ypt) shown in a toggleable strip — visible
// by default when the file carries textures so both models and textures are seen.
function fillModelTextures(texs) {
  modelTextures = texs || [];
  const wrap = $('vpTextures'), row = $('vpTexRow'), chip = $('tgTex');
  row.innerHTML = '';
  const has = modelTextures.length > 0;
  chip.hidden = !has;
  chip.classList.toggle('on', has);
  wrap.hidden = !has;
  if (!has) return;
  for (const t of modelTextures) row.append(makeTexThumb(t));
}
function openTexImage(t, fmt) {
  openTab('img:' + t.name, 'image', t.name + '.dds', { dataUrl: t.img, format: fmt, name: t.name });
}
function makeTexThumb(t) {
  const fmt = (t.format || '').replace('D3DFMT_', '');
  const c = document.createElement('div'); c.className = 'vp-tex';
  c.innerHTML = (t.img ? `<img class="checker" src="${t.img}" alt="">` : `<div class="noimg checker">no preview</div>`)
    + `<span class="vt-name">${t.name}</span><span class="vt-dim">${t.width}×${t.height} · ${fmt}</span>`;
  if (t.img) c.onclick = () => openTexImage(t, fmt);
  return c;
}
// "Expand" -> textures fill the pane (model list + 3D viewer hidden).
function expandTextures() {
  const grid = $('vpTexFullGrid'); grid.innerHTML = '';
  $('vpTexFullTitle').textContent = `Textures (${modelTextures.length})`;
  for (const t of modelTextures) {
    const fmt = (t.format || '').replace('D3DFMT_', '');
    const c = document.createElement('div'); c.className = 'tex-card';
    c.innerHTML = (t.img ? `<img class="preview checker" src="${t.img}" alt="">` : `<div class="preview noimg checker">no preview</div>`)
      + `<div class="tn">${t.name}</div><div class="td">${t.width}×${t.height} · ${fmt}</div>`;
    if (t.img) c.onclick = () => openTexImage(t, fmt);
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
$('tgTex').onclick = () => { const on = $('tgTex').classList.toggle('on'); $('vpTextures').hidden = !on; };
$('btnFit').onclick = () => viewport.fit();
$('lodSel').onchange = e => { viewport.setLod(e.target.value); updateVpStats(); };
$('btnTexExpand').onclick = expandTextures;
$('btnTexCollapse').onclick = collapseTextures;

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
  const res = await call('save', { node: t.key, content, format: t.data.format, metaName: t.data.metaName, target });
  if (res.canceled) { setStatus('Save canceled'); return; }
  if (res.ok) {
    if (target === 'rpf') { editor.markSaved(t); updateDirty(t); setStatus(`Saved ${t.title} into archive (${fmtSize(res.size)})`); }
    else setStatus(`Exported ${t.title} → ${res.path}`);
  } else setStatus('Save failed: ' + (res.message || ''), true);
}
$('btnSaveRpf').onclick = () => saveActive('rpf');
$('btnExport').onclick = () => saveActive('export');

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
    const c = document.createElement('div'); c.className = 'tex-card';
    const fmt = t.format.replace('D3DFMT_', '');
    const prev = t.img
      ? `<img class="preview checker" src="${t.img}" alt="">`
      : `<div class="preview noimg checker">no preview</div>`;
    c.innerHTML = prev + `<div class="tn">${t.name}</div><div class="td">${t.width}×${t.height} · ${fmt} · ${t.levels} mip${t.levels === 1 ? '' : 's'}</div>`;
    if (t.img) c.onclick = () => openTab('img:' + t.name, 'image', t.name + '.dds', { dataUrl: t.img, format: fmt, name: t.name });
    g.appendChild(c);
  }
}

// ---------------------------------------------------------------- image / DDS converter
function fillImage(d) {
  const img = $('imgView');
  img.onload = () => { $('imgInfo').textContent = `${d.format || ''} · ${img.naturalWidth}×${img.naturalHeight}`; };
  img.src = d.dataUrl;
  $('imgInfo').textContent = d.format || '';
}
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

// drag & drop any image -> convert to DDS
let dragDepth = 0;
window.addEventListener('dragover', e => e.preventDefault());
window.addEventListener('dragenter', e => { e.preventDefault(); dragDepth++; $('dropZone').hidden = false; });
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('dropZone').hidden = true; } });
window.addEventListener('drop', async e => {
  e.preventDefault(); dragDepth = 0; $('dropZone').hidden = true;
  const f = e.dataTransfer.files[0]; if (!f) return;
  const ext = (f.name.split('.').pop() || '').toLowerCase();
  if (ext === 'dds') {
    const buf = new Uint8Array(await f.arrayBuffer());
    const res = await call('decodeDds', { content: bytesToB64(buf), name: f.name });
    if (res.dataUrl) openTab('drop:' + f.name, 'image', f.name, { dataUrl: res.dataUrl, format: 'DDS', name: f.name });
    else setStatus('Could not decode ' + f.name, true);
  } else {
    const rd = new FileReader();
    rd.onload = () => openTab('drop:' + f.name, 'image', f.name, { dataUrl: rd.result, format: ext.toUpperCase(), name: f.name });
    rd.readAsDataURL(f);
  }
});

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
  if (node && (node.kind === 'file' || node.kind === 'archive')) items.push({ label: 'Extract…', action: () => extract(node) });
  items.push({ label: 'Extract all…', action: () => extractAll(target) });
  if (node && node.id !== 0) {
    items.push({ sep: true });
    const multi = selection.length > 1 && isSelected(node);
    items.push({ label: multi ? `Delete ${selection.length} items` : 'Delete', accel: 'Del', action: () => deleteNodes(multi ? selection : [node]) });
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
async function extractAll(target) {
  if (!target || target.id === 0) { setStatus('Extract all works inside an archive.', true); return; }
  setStatus('Extracting all…');
  const res = await call('extractAll', { node: target.id });
  if (res.canceled) { setStatus('Extract canceled'); return; }
  if (res.ok) setStatus(`Extracted ${res.count.toLocaleString()} files → ${res.path}`);
  else setStatus('Extract all failed: ' + (res.message || ''), true);
}

// ---- create ----
function promptDialog({ title, label, value = '', checkboxLabel = null }) {
  return new Promise(resolve => {
    const ov = document.createElement('div'); ov.className = 'modal';
    ov.innerHTML = `<div class="modal-card"><h3>${title}</h3>
      <label class="dlg-l">${label}</label>
      <input class="dlg-in" type="text" spellcheck="false" value="${value}">
      ${checkboxLabel ? `<label class="dlg-cb"><input type="checkbox" class="dlg-chk"> ${checkboxLabel}</label>` : ''}
      <div class="modal-actions"><button class="btn ghost dlg-cancel">Cancel</button><button class="btn primary dlg-ok">Create</button></div></div>`;
    document.body.append(ov);
    const input = ov.querySelector('.dlg-in'); input.focus(); input.select();
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
  if (r) afterCreate(await call('createFolder', { node: target.id, name: r.name }));
}
async function newRpf(target) {
  target = target || createTarget(null); if (badTarget(target)) return;
  const r = await promptDialog({ title: 'New RPF', label: 'RPF name', value: 'new.rpf', checkboxLabel: 'Create content override (register in content.xml)' });
  if (r) afterCreate(await call('createRpf', { node: target.id, name: r.name, override: r.checked }));
}
async function newYtd(target) {
  target = target || createTarget(null); if (badTarget(target)) return;
  const r = await promptDialog({ title: 'New YTD', label: 'Texture dictionary name', value: 'new.ytd' });
  if (r) afterCreate(await call('createYtd', { node: target.id, name: r.name }));
}
function afterCreate(res) {
  if (res.ok) { setStatus(`Created ${res.kind} “${res.name}”` + (res.note ? ` — ${res.note}` : '')); refreshCurrent(); }
  else setStatus('Create failed: ' + (res.message || ''), true);
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
    let res = await call('delete', { node: n.id, batch, force });
    if (res.needConfirm && !force) {
      const yes = await confirmDialog({
        title: 'Trash is full',
        body: `Trash is ${res.trashMb} MB (limit ${res.limitMb} MB). Permanently remove the oldest trashed items to make room?`,
        okLabel: 'Delete permanently',
      });
      if (!yes) { setStatus('Delete canceled'); break; }
      force = true;
      res = await call('delete', { node: n.id, batch, force: true });
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

if (window.__POPOUT) enterPopout(window.__POPOUT);
else autoload();
