import * as THREE from 'three';
import { OrbitControls } from './lib/three/OrbitControls.js';

// A single persistent WebGL viewport reused across model tabs. RAGE assets are
// Z-up; we drop them in a group rotated to three.js' Y-up convention.

let renderer, scene, camera, controls, grid, hemi, dir, ambient;
let root;                 // THREE.Group holding the current model (Z-up -> Y-up)
let boxHelper = null;
let current = null;       // current model message
const texLoader = new THREE.TextureLoader();
const texCache = new Map();   // data-url -> THREE.Texture
let lod = 'High';
let partIndex = -1;       // -1 = show every part; otherwise only that part (drawable)
let wire = false, showBox = false, showGrid = true, lit = true, vcolor = false;
let canvas;
const stats = { verts: 0, tris: 0, meshes: 0, rendered: 0, skipped: 0 };

export function init(glCanvas) {
  canvas = glCanvas;
  renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setClearColor(0x0d0f14, 1);

  scene = new THREE.Scene();

  camera = new THREE.PerspectiveCamera(45, 1, 0.05, 5000);
  camera.position.set(3, 2, 3);

  controls = new OrbitControls(camera, canvas);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  // Scroll wheel zooms (toward the cursor); keep a sane distance range.
  controls.enableZoom = true;
  controls.zoomToCursor = true;
  controls.zoomSpeed = 1.1;
  controls.minDistance = 0.01;
  controls.maxDistance = 8000;

  ambient = new THREE.AmbientLight(0xffffff, 0.35);
  hemi = new THREE.HemisphereLight(0xcfe3ff, 0x1a1d26, 0.7);
  dir = new THREE.DirectionalLight(0xffffff, 1.6);
  dir.position.set(1, 2.2, 1.4);
  scene.add(ambient, hemi, dir);

  grid = new THREE.GridHelper(20, 40, 0x2a3040, 0x1b2030);
  scene.add(grid);

  root = new THREE.Group();
  root.rotation.x = -Math.PI / 2;   // RAGE Z-up -> three Y-up
  scene.add(root);

  new ResizeObserver(resize).observe(canvas.parentElement);
  resize();
  animate();
}

function resize() {
  if (!canvas) return;
  const w = canvas.clientWidth || 1, h = canvas.clientHeight || 1;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}

function animate() {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
}

// ---- base64 typed-array decoders ----
function b64bytes(s) {
  const bin = atob(s);
  const len = bin.length;
  const bytes = new Uint8Array(len);
  for (let i = 0; i < len; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}
function f32(s) { return s ? new Float32Array(b64bytes(s).buffer) : null; }
function u32(s) { return s ? new Uint32Array(b64bytes(s).buffer) : null; }

function clearRoot() {
  for (const c of [...root.children]) {
    root.remove(c);
    c.geometry?.dispose?.();
    if (Array.isArray(c.material)) c.material.forEach(m => m.dispose());
    else c.material?.dispose?.();
  }
  if (boxHelper) { scene.remove(boxHelper); boxHelper.geometry?.dispose?.(); boxHelper = null; }
}

function getTexture(url) {
  if (!url) return null;
  if (texCache.has(url)) return texCache.get(url);
  const t = texLoader.load(url);
  t.colorSpace = THREE.SRGBColorSpace;
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.anisotropy = 4;
  texCache.set(url, t);
  return t;
}
function clearTextures() {
  for (const t of texCache.values()) t.dispose();
  texCache.clear();
}
function buildMaterial(matInfo) {
  const map = matInfo && matInfo.tex ? getTexture(matInfo.tex) : null;
  return new THREE.MeshStandardMaterial({
    color: map ? 0xffffff : 0x9aa3af,
    map: map || null,
    roughness: 0.82, metalness: 0.04,
    side: THREE.DoubleSide, wireframe: wire,
  });
}

/** Load a model message ({parts:[{lods:[{level,meshes:[...]}]}]}). */
export function loadModel(msg) {
  current = msg;
  clearTextures();
  // A dictionary (.ydd) / particle file (.ypt) holds independent drawables that
  // each live at their own origin — showing them all at once just overlaps them.
  // Default to the first drawable when there's more than one; single-drawable
  // files (.ydr/.yft) show everything.
  partIndex = (msg.parts && msg.parts.length > 1) ? 0 : -1;
  rebuild();
  fit();
}

function pickLodForPart(part) {
  const byLevel = Object.fromEntries(part.lods.map(l => [l.level, l]));
  return byLevel[lod] || part.lods[0] || null;
}

function rebuild() {
  clearRoot();
  stats.verts = stats.tris = stats.meshes = 0;
  if (!current) return;

  const levels = new Set();
  current.parts.forEach((part, pi) => {
    if (partIndex >= 0 && pi !== partIndex) return;
    for (const l of part.lods) levels.add(l.level);
    const chosen = pickLodForPart(part);
    if (!chosen) return;
    for (const m of chosen.meshes) {
      const g = new THREE.BufferGeometry();
      const pos = f32(m.pos);
      if (!pos) continue;
      g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      const nrm = f32(m.nrm);
      if (nrm) g.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
      const uv = f32(m.uv0);
      if (uv) g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
      const idx = u32(m.idx);
      if (idx) g.setIndex(new THREE.BufferAttribute(idx, 1));
      if (!nrm) g.computeVertexNormals();

      const matInfo = (part.materials && part.materials[m.mat]) || null;
      const material = buildMaterial(matInfo);
      const col = f32(m.col);
      if (vcolor && col) {
        const c3 = new Float32Array(m.vcount * 3);
        for (let k = 0; k < m.vcount; k++) { c3[k * 3] = col[k * 4]; c3[k * 3 + 1] = col[k * 4 + 1]; c3[k * 3 + 2] = col[k * 4 + 2]; }
        g.setAttribute('color', new THREE.BufferAttribute(c3, 3));
        material.vertexColors = true;
      }
      const mesh = new THREE.Mesh(g, material);
      root.add(mesh);
      stats.verts += m.vcount;
      stats.tris += (m.icount / 3) | 0;
      stats.meshes++;
    }
  });
  if (current.stats) { stats.rendered = current.stats.rendered; stats.skipped = current.stats.skipped; }
  updateBox();
  updateStats();
  return [...levels];
}

function updateBox() {
  if (boxHelper) { scene.remove(boxHelper); boxHelper = null; }
  if (showBox && root.children.length) {
    const box = new THREE.Box3().setFromObject(root);
    boxHelper = new THREE.Box3Helper(box, 0x4fd1c5);
    scene.add(boxHelper);
  }
}

export function fit() {
  if (!root.children.length) return;
  const box = new THREE.Box3().setFromObject(root);
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const radius = Math.max(size.x, size.y, size.z) * 0.5 || 1;

  const fov = camera.fov * Math.PI / 180;
  let dist = (radius / Math.sin(fov / 2)) * 1.4;
  camera.near = Math.max(0.01, radius * 0.01);
  camera.far = dist * 8 + 100;
  camera.updateProjectionMatrix();

  controls.target.copy(center);
  const dirv = new THREE.Vector3(1, 0.65, 1).normalize();
  camera.position.copy(center).add(dirv.multiplyScalar(dist));
  controls.update();

  // sit the grid under the model
  grid.position.set(center.x, box.min.y, center.z);
}

export function setLod(level) { lod = level; rebuild(); }
export function setPart(i) { partIndex = i; rebuild(); fit(); }
export function currentPart() { return partIndex; }
export function partNames() { return current ? current.parts.map((p, i) => p.name || ('model ' + i)) : []; }
export function setVertexColors(on) { vcolor = on; rebuild(); }
export function setWire(on) { wire = on; root.children.forEach(m => { if (Array.isArray(m.material)) m.material.forEach(x => x.wireframe = on); else m.material.wireframe = on; }); }
export function setBox(on) { showBox = on; updateBox(); }
export function setGrid(on) { showGrid = on; grid.visible = on; }
export function setLit(on) {
  lit = on;
  dir.intensity = on ? 1.6 : 0.0;
  hemi.intensity = on ? 0.7 : 0.0;
  ambient.intensity = on ? 0.35 : 1.0;
}

export function availableLods() {
  if (!current) return [];
  const s = new Set();
  current.parts.forEach((p, i) => { if (partIndex < 0 || i === partIndex) p.lods.forEach(l => s.add(l.level)); });
  return ['High', 'Med', 'Low', 'VLow'].filter(x => s.has(x));
}

export function getStats() { return { ...stats }; }

function updateStats() {
  const el = document.getElementById('vpStats');
  if (!el) return;
  el.textContent =
    `${stats.meshes} mesh · ${stats.verts.toLocaleString()} verts · ${stats.tris.toLocaleString()} tris` +
    (stats.skipped ? `  ·  ${stats.skipped} skipped` : '');
}
