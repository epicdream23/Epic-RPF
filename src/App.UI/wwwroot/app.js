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
window.chrome.webview.addEventListener('message', ev => {
  const m = ev.data;
  if (m && m.reqId && pending.has(m.reqId)) {
    const r = pending.get(m.reqId);
    pending.delete(m.reqId);
    r(m);
  }
});

// ---------------------------------------------------------------- dom
const $ = id => document.getElementById(id);
const folderInput = $('folder'), gen9Chk = $('gen9'), mountBtn = $('mount'), browseBtn = $('browse');
const mountInfo = $('mountInfo'), treeEl = $('tree'), filterInput = $('filter');
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
function iconName(node) {
  if (node.kind === 'folder' || node.kind === 'dir') return 'folder';
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

filterInput.addEventListener('input', () => {
  const term = filterInput.value.trim().toLowerCase();
  for (const el of treeEl.querySelectorAll('.node')) {
    if (!term) { el.style.display = ''; continue; }
    el.dataset.match = el.__node.name.toLowerCase().includes(term) ? '1' : '';
  }
  if (term) for (const el of treeEl.querySelectorAll('.node')) {
    const self = el.dataset.match === '1';
    const desc = !!el.querySelector('.node[data-match="1"]');
    el.style.display = (self || desc) ? '' : 'none';
    if (desc) el.classList.add('open');
  }
});

// ---------------------------------------------------------------- explorer (detail list)
const explorer = { crumbs: [], items: [] };

async function navigate(crumbs) {
  explorer.crumbs = crumbs;
  const last = crumbs[crumbs.length - 1];
  explorer.items = await getChildren(last.id);
  ensureExplorerTab();
  activate(explorerTab);
}

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
function renderList(items) {
  listBody.innerHTML = '';
  // Group by type (OpenIV-style): "Folder (4)", "XML (2)", etc.
  const groups = new Map();
  for (const it of items) {
    const t = it.type || 'Other';
    if (!groups.has(t)) groups.set(t, []);
    groups.get(t).push(it);
  }
  const rank = t => (t === 'Folder' ? 0 : t === 'Archive' ? 1 : 2);
  const order = [...groups.keys()].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  for (const t of order) {
    const arr = groups.get(t);
    const gh = document.createElement('div');
    gh.className = 'group-head';
    gh.innerHTML = `<span class="gt">${t}</span><span class="gc">(${arr.length})</span>`;
    listBody.appendChild(gh);
    for (const it of arr) listBody.appendChild(makeRow(it));
  }
}

function makeRow(it) {
  const row = document.createElement('div');
  row.className = 'row-item'; row.__node = it;
  const sizeText = it.container
    ? (it.count != null ? `${it.count} item${it.count === 1 ? '' : 's'}` : '')
    : (it.size >= 0 ? fmtSize(it.size) : '');
  row.innerHTML =
    `<span class="c-name">${iconImg(iconName(it))}<span class="label">${it.name}</span></span>` +
    `<span class="c-type">${it.type || ''}</span>` +
    `<span class="c-size">${sizeText}</span>` +
    `<span class="c-attr">${it.attrs || ''}</span>`;
  row.onclick = () => { if (selectedRow) selectedRow.classList.remove('selected'); selectedRow = row; row.classList.add('selected'); showInspectorForNode(it); };
  row.ondblclick = () => onItemDbl(it);
  row.oncontextmenu = e => { e.preventDefault(); showContext(e, it); };
  return row;
}

function onItemDbl(item) {
  if (item.container) navigate([...explorer.crumbs, { id: item.id, name: item.name }]);
  else openFile(item);
}

async function openFile(n) {
  setStatus(`Opening ${n.name}…`);
  const res = await call('open', { node: n.id });
  if (res.type === 'error') { setStatus('Open failed: ' + res.message, true); return; }
  if (res.type === 'model') openModelTab(res);
  else if (res.type === 'edit') openTab(n.id, 'edit', n.name, res);
  else if (res.type === 'hex') openTab(n.id, 'hex', n.name, res);
  else if (res.type === 'texture') openTab(n.id, 'texture', n.name, res);
  setStatus(`Opened ${n.name}`);
}

// ---------------------------------------------------------------- tabs / panes
const tabs = [];
let activeTab = null, explorerTab = null;

function ensureExplorerTab() {
  if (!explorerTab) { explorerTab = { key: 'explorer', kind: 'explorer', title: 'Explorer', noClose: true }; tabs.unshift(explorerTab); }
  renderTabs();
}

function openTab(key, kind, title, data) {
  let tab = tabs.find(t => t.key === key && t.kind === kind);
  if (!tab) { tab = { key, kind, title, data }; tabs.push(tab); renderTabs(); }
  else tab.data = data;
  activate(tab);
}
function openModelTab(res) { openTab('m' + res.path, 'model', res.name, res); }

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
    }
    el.onclick = () => activate(t);
    tabsEl.appendChild(el);
  }
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
  if (tab.kind === 'explorer') { renderCrumbs(); renderList(explorer.items); }
  else if (tab.kind === 'model') fillModel(tab.data);
  else if (tab.kind === 'edit') fillEdit(tab);
  else if (tab.kind === 'hex') renderHex(tab.data);
  else if (tab.kind === 'texture') renderTextures(tab.data);
  showInspectorForTab(tab);
}

// ---------------------------------------------------------------- model pane
function fillModel(res) {
  viewport.loadModel(res);
  const sel = $('lodSel'); sel.innerHTML = '';
  for (const l of viewport.availableLods()) { const o = document.createElement('option'); o.value = l; o.textContent = l; sel.append(o); }
  requestAnimationFrame(() => requestAnimationFrame(() => viewport.fit()));
  updateVpStats();
}
function updateVpStats() {
  const s = viewport.getStats();
  statusRight.textContent = `${s.meshes} mesh · ${s.verts.toLocaleString()} verts · ${s.tris.toLocaleString()} tris`
    + (s.skipped ? ` · ${s.skipped} skipped` : '');
}
function chip(id, fn, initial) { const el = $(id); if (initial) el.classList.add('on'); el.onclick = () => fn(el.classList.toggle('on')); }
chip('tgWire', viewport.setWire, false);
chip('tgBox', viewport.setBox, false);
chip('tgGrid', viewport.setGrid, true);
chip('tgLight', viewport.setLit, true);
$('btnFit').onclick = () => viewport.fit();
$('lodSel').onchange = e => { viewport.setLod(e.target.value); updateVpStats(); };

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
    c.innerHTML = `<div class="tn">${t.name}</div><div class="td">${t.width}×${t.height} · ${t.format.replace('D3DFMT_', '')} · ${t.levels} mip${t.levels === 1 ? '' : 's'}</div>`;
    g.appendChild(c);
  }
}

// ---------------------------------------------------------------- inspector
function row(k, v) { return `<div><span class="k">${k}:</span> <span class="v">${v}</span></div>`; }
function showInspectorForNode(n) {
  let h = `<div class="sect"><h4>${n.type || n.kind}</h4>${row('Name', n.name)}`;
  if (!n.container) { h += row('Viewer', n.viewer); if (n.size >= 0) h += row('Size', fmtSize(n.size)); if (n.attrs) h += row('Attributes', n.attrs); }
  h += '</div>';
  inspBody.innerHTML = h;
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

// ---------------------------------------------------------------- context menu
let ctxEl = null;
function showContext(e, n) {
  hideContext();
  const el = document.createElement('div'); el.className = 'ctx';
  el.style.left = e.clientX + 'px'; el.style.top = e.clientY + 'px';
  const items = [];
  if (!n.container) items.push(['Open', () => openFile(n)]);
  if (n.container) items.push(['Open folder', () => navigate([...explorer.crumbs, { id: n.id, name: n.name }])]);
  if (n.kind === 'file' || n.kind === 'archive') items.push(['Extract…', () => extract(n)]);
  for (const [label, fn] of items) {
    const mi = document.createElement('div'); mi.className = 'mi'; mi.textContent = label;
    mi.onclick = () => { hideContext(); fn(); };
    el.append(mi);
  }
  document.body.append(el); ctxEl = el;
}
function hideContext() { if (ctxEl) { ctxEl.remove(); ctxEl = null; } }
document.addEventListener('click', hideContext);
document.addEventListener('scroll', hideContext, true);

async function extract(n) {
  setStatus(`Extracting ${n.name}…`);
  const res = await call('extract', { node: n.id });
  if (res.canceled) { setStatus('Extract canceled'); return; }
  if (res.ok) setStatus(`Extracted ${n.name} → ${res.path} (${fmtSize(res.size)})`);
  else setStatus('Extract failed: ' + (res.message || ''), true);
}

// ---------------------------------------------------------------- util
function fmtSize(b) {
  if (b < 0) return '';
  if (b < 1024) return b + ' B';
  if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
  return (b / 1048576).toFixed(1) + ' MB';
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
    const cmd = a.open === 'meta' ? 'firstMeta' : 'firstModel';
    const res = await call(cmd);
    if (res.type === 'model') openModelTab(res);
    else if (res.type === 'edit') openTab('auto', 'edit', res.name, res);
    return;
  }
  detectAndMount();
}
autoload();
