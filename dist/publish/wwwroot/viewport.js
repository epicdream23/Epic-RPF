import * as THREE from 'three';
import { OrbitControls } from './lib/three/OrbitControls.js';

// A single persistent WebGL viewport reused across model tabs. RAGE assets are
// Z-up; we drop them in a group rotated to three.js' Y-up convention.

let renderer, scene, camera, controls, grid, hemi, dir, ambient;
let root;                 // THREE.Group holding the current model (Z-up -> Y-up)
let boxHelper = null;
let current = null;       // current model message
const texLoader = new THREE.TextureLoader();
const texCache = new Map();   // data-url|kind -> THREE.Texture
let lod = 'High';
let partIndex = -1;       // -1 = show every part; otherwise only that part (drawable)
let wire = false, showBox = false, showGrid = true, lit = true, vcolor = false, showSkel = false;
let canvas;
let skelGroup = null;     // skeleton overlay (lines + joints), in model space
let boneMarker = null;    // highlight marker for the selected bone
let boneMarkerEnd = 0;    // when (ms) the marker auto-hides
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
  const dt = clock.getDelta();
  if (mixer && !animPaused) {
    mixer.update(dt);
    if (showSkel) rebuildSkeletonFromRig();   // overlay follows the animated pose
  }
  if (boneMarker) {
    const left = boneMarkerEnd - performance.now();
    if (left <= 0) { root.remove(boneMarker); boneMarker = null; }
    else {
      const s = 1 + 0.25 * Math.sin(performance.now() / 110);   // gentle pulse
      boneMarker.scale.setScalar(s);
    }
  }
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
function u8(s) { return s ? b64bytes(s) : null; }

function clearRoot() {
  for (const c of [...root.children]) {
    c.traverse(o => {
      o.geometry?.dispose?.();
      if (Array.isArray(o.material)) o.material.forEach(m => m.dispose());
      else o.material?.dispose?.();
      o.skeleton?.dispose?.();
    });
    root.remove(c);
  }
  if (boxHelper) { scene.remove(boxHelper); boxHelper.geometry?.dispose?.(); boxHelper = null; }
  _attached = [];
  clearSkeleton();
}

// RAGE/DirectX UV convention: V = 0 at the TOP of the image. three.js' loader default
// (flipY = true) is the OpenGL convention — it flipped every texture vertically, which
// goes unnoticed on tiling surfaces but scrambles real unwrapped UV atlases (peds,
// weapons, props). flipY = false + untouched UVs is the correct DirectX-style setup.
function getTexture(url, srgb = true) {
  if (!url) return null;
  const key = url + (srgb ? '|s' : '|l');
  if (texCache.has(key)) return texCache.get(key);
  const t = texLoader.load(url);
  t.flipY = false;
  if (srgb) t.colorSpace = THREE.SRGBColorSpace;   // data maps (normal/spec) stay linear
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.anisotropy = 8;
  texCache.set(key, t);
  return t;
}
function clearTextures() {
  for (const t of texCache.values()) t.dispose();
  texCache.clear();
}

// Value-parameter lookup on a material DTO (shader params from the bridge).
function pval(mi, name, dflt) {
  const p = (mi && mi.params || []).find(p => (p.name || '').toLowerCase() === name);
  return p ? p.values[0] : dflt;
}

// Map RAGE shader value params onto the three.js material (live-editable).
function applyParams(m, mi) {
  if (m.normalMap) {
    const b = pval(mi, 'bumpiness', 1);
    const s = Math.max(0, Math.min(4, b));
    m.normalScale.set(s, -s);             // -Y: DirectX-style normal maps
  }
  const si = pval(mi, 'specularintensitymult', null);
  if (si != null) m.specular.setScalar(Math.min(1, 0.18 * Math.max(0, si)));
  const sf = pval(mi, 'specularfalloffmult', null);
  if (sf != null) m.shininess = Math.max(2, Math.min(512, sf));
  if (m.emissiveMap) m.emissiveIntensity = Math.max(0, pval(mi, 'emissivemultiple', mi.emissiveMult != null ? mi.emissiveMult : 1));
  m.needsUpdate = true;
}

// Build a lit material from the bridge's material DTO: diffuse + normal + specular
// maps, emissive shaders glow (emissiveMap = diffuse), alpha/cutout shaders clip.
function buildMaterial(matInfo) {
  const map = matInfo && matInfo.tex ? getTexture(matInfo.tex, true) : null;
  const m = new THREE.MeshPhongMaterial({
    color: map ? 0xffffff : 0x9aa3af,
    map: map || null,
    specular: 0x1c1c1c, shininess: 22,
    side: THREE.DoubleSide, wireframe: wire,
  });
  if (matInfo) {
    if (matInfo.nrmTex) m.normalMap = getTexture(matInfo.nrmTex, false);
    if (matInfo.spcTex) { m.specularMap = getTexture(matInfo.spcTex, false); m.specular = new THREE.Color(0x9a9a9a); }
    const sl = ((matInfo.shader || '') + ' ' + (matInfo.sps || '')).toLowerCase();
    if (matInfo.emissive) {
      m.emissive = new THREE.Color(0xffffff);
      m.emissiveMap = map;
      m.emissiveIntensity = matInfo.emissiveMult != null ? matInfo.emissiveMult : 1;
    }
    if (/alpha|decal|cutout|glass|hair|fence/.test(sl)) {
      m.alphaTest = 0.32;
      if (/glass|decal/.test(sl)) { m.transparent = true; m.opacity = /glass/.test(sl) ? 0.7 : 1; }
    }
    applyParams(m, matInfo);
  }
  return m;
}

/** Load a model message ({parts:[{lods:[{level,meshes:[...]}]}]}). */
export function loadModel(msg) {
  current = msg;
  armsOn = false;   // a fresh model starts without the borrowed arms rig
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

// ---- bone rig -----------------------------------------------------------------
// THREE.Bone hierarchy rebuilt from the skeleton's parent-local transforms. Rigid
// meshes hang off their bone (RAGE stores their geometry bone-local), skinned meshes
// bind to the whole rig, and animations drive the bones (names are 'b<tag>').
let rigs = new Map();          // part index (or 'arms') -> { bones, byTag, rigRoot, sk }
let additionsOn = new Set();   // enabled extra_* names (lowercase); default = none shown
let armsOn = false, armsDto = null;   // character arms+hands rig for weapon animations
// Corrective rotation aligning the weapon's Gun_GripR bone frame to the hand's frame.
// RAGE weapon grip bones and the ped hand bone differ by a fixed rotation, found
// empirically (verified carbine + pistol): Euler (90,180,0) gets the roll (magazine
// down) + level, then a 180° yaw about the hand axis points the muzzle FORWARD (the
// way the ped faces/aims) instead of backward. Shared across weapons.
function gripFixMat(ex, ey, ez) {
  return new THREE.Matrix4().makeRotationFromEuler(
    new THREE.Euler(ex * Math.PI / 180, ey * Math.PI / 180, ez * Math.PI / 180, 'XYZ'));
}
// base orientation correction (weapon grip-bone frame vs hand frame), then the user's
// live rotation on top. GRIP_FIX = R_user · GRIP_BASE.
const GRIP_BASE = new THREE.Matrix4().makeRotationAxis(new THREE.Vector3(0, 1, 0), Math.PI).multiply(gripFixMat(90, 180, 0));
let GRIP_ROT = new THREE.Vector3(0, 0, 0);   // user rotation (deg) on top of base
let GRIP_FIX = GRIP_BASE.clone();
function rebuildGripFix() { GRIP_FIX = gripFixMat(GRIP_ROT.x, GRIP_ROT.y, GRIP_ROT.z).multiply(GRIP_BASE); }
let _attached = [];   // [{partGroup, gripLocal}] — weapons held in the hand
export function setGripRot(ex, ey, ez) { GRIP_ROT.set(ex || 0, ey || 0, ez || 0); rebuildGripFix(); if (armsOn) applyGrip(); }
// Hard-coded nudge (hand-local) so the pistol grip lands in the CURLED FINGERS, not at
// the wrist/PH bone. +X = toward the fingertips (PH_R_Hand alone leaves the hand on the
// MAGAZINE; the fingers wrap a grip well past it), -Y = down so the weapon sits level.
// Tuned empirically against the carbine aim pose.
let GRIP_OFFSET = new THREE.Vector3(0.15, -0.04, 0.012);
export function setGripOffset(x, y, z) { GRIP_OFFSET.set(x || 0, y || 0, z || 0); if (armsOn) applyGrip(); }

function buildRig(part) {
  const sk = part.skeleton;
  if (!sk || !sk.length || !sk[0].lp) return null;
  const rigRoot = new THREE.Group(); rigRoot.name = 'rig';
  const bones = sk.map(b => {
    const bone = new THREE.Bone();
    bone.name = 'b' + b.tag;
    bone.position.set(b.lp[0], b.lp[1], b.lp[2]);
    bone.quaternion.set(b.lr[0], b.lr[1], b.lr[2], b.lr[3]);
    bone.scale.set(b.ls[0] || 1, b.ls[1] || 1, b.ls[2] || 1);
    return bone;
  });
  sk.forEach((b, i) => { (b.parent >= 0 && bones[b.parent] ? bones[b.parent] : rigRoot).add(bones[i]); });
  const byTag = new Map(); sk.forEach((b, i) => byTag.set(b.tag, bones[i]));
  return { bones, byTag, rigRoot, sk };
}

// Which addition (extra_*) a mesh belongs to, or null (always visible).
function additionNameFor(m, sk) {
  const bIdx = m.skin ? m.domBone : m.bone;
  if (bIdx > 0 && sk && sk[bIdx] && /^extra_/i.test(sk[bIdx].name)) return sk[bIdx].name.toLowerCase();
  return null;
}

function geomFromDto(m, material) {
  const g = new THREE.BufferGeometry();
  const pos = f32(m.pos);
  if (!pos) return null;
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  const nrm = f32(m.nrm);
  if (nrm) g.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
  const uv = f32(m.uv0);
  if (uv) g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
  const idx = u32(m.idx);
  if (idx) g.setIndex(new THREE.BufferAttribute(idx, 1));
  if (!nrm) g.computeVertexNormals();
  const col = f32(m.col);
  if (vcolor && col && material) {
    const c3 = new Float32Array(m.vcount * 3);
    for (let k = 0; k < m.vcount; k++) { c3[k * 3] = col[k * 4]; c3[k * 3 + 1] = col[k * 4 + 1]; c3[k * 3 + 2] = col[k * 4 + 2]; }
    g.setAttribute('color', new THREE.BufferAttribute(c3, 3));
    material.vertexColors = true;
  }
  return g;
}

function rebuild() {
  clearRoot();
  if (mixer) { mixer.stopAllAction(); mixer = null; action = null; clipDuration = 0; }   // rigs change -> old mixer is stale
  stats.verts = stats.tris = stats.meshes = 0;
  rigs = new Map();
  if (!current) return;

  const levels = new Set();
  const weaponGroups = [];   // (partGroup, part) to attach to the hand when arms are on
  current.parts.forEach((part, pi) => {
    if (partIndex >= 0 && pi !== partIndex) return;
    for (const l of part.lods) levels.add(l.level);
    const chosen = pickLodForPart(part);
    if (!chosen) return;

    const partGroup = new THREE.Group();
    partGroup.userData.part = pi;
    root.add(partGroup);
    weaponGroups.push({ partGroup, part, pi });
    const rig = buildRig(part);
    if (rig) { rigs.set(pi, rig); partGroup.add(rig.rigRoot); }
    scene.updateMatrixWorld(true);
    let skeletonObj = null;
    const ensureSkeleton = () => skeletonObj || (skeletonObj = new THREE.Skeleton(rig.bones));

    const addMesh = (m, matInfo, host, addition) => {
      const material = buildMaterial(matInfo);
      const g = geomFromDto(m, material);
      if (!g) return;
      let mesh;
      const bw = m.skin ? f32(m.bw) : null;
      const bi = m.skin ? u8(m.bi) : null;
      if (bw && bi && rig) {
        g.setAttribute('skinWeight', new THREE.BufferAttribute(bw, 4));
        g.setAttribute('skinIndex', new THREE.BufferAttribute(bi, 4));
        mesh = new THREE.SkinnedMesh(g, material);
        partGroup.add(mesh);
        scene.updateMatrixWorld(true);
        mesh.bind(ensureSkeleton(), mesh.matrixWorld.clone());
      } else {
        mesh = new THREE.Mesh(g, material);
        host.add(mesh);
      }
      mesh.userData = { part: pi, mat: m.mat, addition };
      mesh.frustumCulled = false;   // skinned/bone-local bounds are unreliable
      stats.verts += m.vcount;
      stats.tris += (m.icount / 3) | 0;
      stats.meshes++;
    };

    for (const m of chosen.meshes) {
      // rigid models bound to a bone carry bone-local geometry — parent them there
      const host = (!m.skin && m.bone > 0 && rig && rig.bones[m.bone]) ? rig.bones[m.bone] : partGroup;
      addMesh(m, (part.materials && part.materials[m.mat]) || null, host, additionNameFor(m, part.skeleton));
    }

    // fragment children (vehicle wheels etc.) — instanced at their bones
    for (const ch of (part.fragChildren || [])) {
      const byLevel = Object.fromEntries((ch.lods || []).map(l => [l.level, l]));
      const clod = byLevel[lod] || (ch.lods && ch.lods[0]) || null;
      if (!clod) continue;
      const addName = ch.extra ? String(ch.group || '').toLowerCase() : null;
      for (const tag of (ch.inst || [ch.boneTag])) {
        const host = (rig && rig.byTag.get(tag)) || partGroup;
        const grp = new THREE.Group();
        grp.userData.addition = addName;
        host.add(grp);
        for (const m of clod.meshes)
          addMesh(m, (ch.materials && ch.materials[m.mat]) || null, grp, addName);
      }
    }
  });
  if (armsOn && armsDto) buildArms(weaponGroups);
  if (current.stats) { stats.rendered = current.stats.rendered; stats.skipped = current.stats.skipped; }
  applyAdditions();
  rebuildSkeleton();
  updateBox();
  updateStats();
  return [...levels];
}

// ---- character arms+hands (for weapon animations) -----------------------------
const SKIN_MAT = () => new THREE.MeshPhongMaterial({ color: 0xb98a6a, specular: 0x141414, shininess: 12 });

// Build the ped arms rig and attach each weapon group to the right-hand bone so the
// weapon is gripped; the ped rig is registered so animations drive arms + weapon.
function buildArms(weaponGroups) {
  const sk = armsDto.skeleton;
  if (!sk || !sk.length) return;
  const armsRoot = new THREE.Group(); armsRoot.name = 'arms';
  const bones = sk.map(b => {
    const bone = new THREE.Bone(); bone.name = 'b' + b.tag;
    bone.position.set(b.lp[0], b.lp[1], b.lp[2]);
    bone.quaternion.set(b.lr[0], b.lr[1], b.lr[2], b.lr[3]);
    bone.scale.set(b.ls[0] || 1, b.ls[1] || 1, b.ls[2] || 1);
    return bone;
  });
  sk.forEach((b, i) => { (b.parent >= 0 && bones[b.parent] ? bones[b.parent] : armsRoot).add(bones[i]); });
  const byTag = new Map(); sk.forEach((b, i) => byTag.set(b.tag, bones[i]));
  rigs.set('arms', { bones, byTag, rigRoot: armsRoot, sk });
  root.add(armsRoot);
  scene.updateMatrixWorld(true);

  const skel = new THREE.Skeleton(bones);
  for (const m of (armsDto.meshes || [])) {
    const mat = SKIN_MAT();
    const g = geomFromDto(m, null);
    if (!g) continue;
    const bw = f32(m.bw), bi = u8(m.bi);
    if (!bw || !bi) continue;
    g.setAttribute('skinWeight', new THREE.BufferAttribute(bw, 4));
    g.setAttribute('skinIndex', new THREE.BufferAttribute(bi, 4));
    const mesh = new THREE.SkinnedMesh(g, mat);
    mesh.frustumCulled = false; mesh.userData = { arms: true };
    armsRoot.add(mesh);
    scene.updateMatrixWorld(true);
    mesh.bind(skel, mesh.matrixWorld.clone());
    stats.verts += m.vcount; stats.tris += (m.icount / 3) | 0; stats.meshes++;
  }

  // attach weapon(s) to the right hand: align the weapon's grip bone to the hand frame
  const hand = byTag.get(armsDto.handTag);
  if (!hand) return;
  scene.updateMatrixWorld(true);

  _attached = [];
  for (const { partGroup, part } of weaponGroups) {
    const wrig = [...rigs.values()].find(r => r.rigRoot.parent === partGroup);
    let grip = null;
    if (wrig && part.skeleton) {
      const gb = part.skeleton.find(b => /^(gun_gripr|weapon_grip|gun_root)$/i.test(b.name))
              || part.skeleton.find(b => /grip/i.test(b.name)) || part.skeleton[0];
      grip = gb && wrig.byTag.get(gb.tag);
    }
    // grip-bone frame in the WEAPON GROUP's local space (computed BEFORE reparenting,
    // while group + grip share a consistent world).
    let gripLocal = null;
    if (grip) {
      grip.updateWorldMatrix(true, false);
      gripLocal = new THREE.Matrix4().copy(partGroup.matrixWorld).invert().multiply(grip.matrixWorld);
    }
    root.remove(partGroup);
    hand.add(partGroup);
    partGroup.matrixAutoUpdate = false;
    _attached.push({ partGroup, gripLocal });
  }
  applyGrip();
}

// Position every attached weapon in its hand: place the grip bone at the hand, apply
// the orientation fix (GRIP_FIX) and the hand-local nudge (GRIP_OFFSET). Called live
// from the grip sliders WITHOUT a full rebuild so adjustment is instant.
function applyGrip() {
  const palmT = new THREE.Matrix4().makeTranslation(GRIP_OFFSET.x, GRIP_OFFSET.y, GRIP_OFFSET.z);
  for (const { partGroup, gripLocal } of _attached) {
    if (gripLocal) {
      const m = gripLocal.clone().invert();
      if (GRIP_FIX) m.premultiply(GRIP_FIX);
      m.premultiply(palmT);
      partGroup.matrix.copy(m);
    } else partGroup.matrix.identity();
    partGroup.matrixWorldNeedsUpdate = true;
  }
  scene.updateMatrixWorld(true);
}

export function setArms(on, dto) {
  armsOn = on;
  if (dto) armsDto = dto;
  rebuild();
  if (on) fit();
}
export function hasArms() { return armsOn; }

// ---- additions (extra_* parts), hidden unless enabled -------------------------
function applyAdditions() {
  root.traverse(o => {
    const a = o.userData && o.userData.addition;
    if (a != null) o.visible = additionsOn.has(a);
  });
}
export function setAdditions(names) {
  additionsOn = new Set([...(names || [])].map(n => String(n).toLowerCase()));
  applyAdditions();
}
export function partAdditions() {
  // every addition name present in the current scene (meshes + frag children)
  const s = new Set();
  root.traverse(o => { const a = o.userData && o.userData.addition; if (a != null) s.add(a); });
  return [...s].sort();
}

// ---- animation playback --------------------------------------------------------
// A baked clip from the bridge: { duration, fps, tracks: [{tag, track, n, data}] }
// (track 0 = bone position, 1 = bone rotation; data = base64 float32 frames).
// Tracks bind to rig bones by tag; bones drive skinned and rigid meshes alike.
const clock = new THREE.Clock();
let mixer = null, action = null, clipDuration = 0, animPaused = false;

export function playClip(bake) {
  stopClip();
  const dur = Math.max(0.001, bake.duration || 1);
  const tracks = [];
  const armsRig = rigs.get('arms');
  // Bind each (tag,track) to exactly ONE bone. The ped/arms rig wins for any tag it
  // owns — so the ped's root-motion track (tag 0) drives the ped, NOT the weapon's
  // tag-0 root bone (which stays anchored to the hand). The weapon rig only gets
  // tags the ped lacks (Gun_*/WAP* recoil bones). Each track has its OWN fps (a clip
  // composites animations of different lengths).
  for (const [key, rig] of rigs) {
    for (const t of (bake.tracks || [])) {
      if (key !== 'arms' && armsRig && armsRig.byTag.has(t.tag)) continue;   // ped owns this tag
      const bone = rig.byTag.get(t.tag);
      if (!bone) continue;
      const data = f32(t.data);
      if (!data || !t.n) continue;
      const fps = t.fps || bake.fps || 30;
      const times = new Float32Array(t.n);
      for (let i = 0; i < t.n; i++) times[i] = i / fps;
      if (t.track === 0) tracks.push(new THREE.VectorKeyframeTrack(bone.uuid + '.position', times, data));
      else if (t.track === 1) tracks.push(new THREE.QuaternionKeyframeTrack(bone.uuid + '.quaternion', times, data));
    }
  }
  if (!tracks.length) return 0;
  const clip = new THREE.AnimationClip('clip', dur, tracks);
  mixer = new THREE.AnimationMixer(root);
  action = mixer.clipAction(clip);
  action.setLoop(THREE.LoopRepeat, Infinity);
  action.play();
  clipDuration = dur;
  animPaused = false;
  return tracks.length;
}

export function stopClip() {
  if (mixer) { mixer.stopAllAction(); mixer.uncacheRoot(root); mixer = null; action = null; }
  clipDuration = 0; animPaused = false;
  // back to bind pose
  for (const [, rig] of rigs)
    rig.sk.forEach((b, i) => {
      const bone = rig.bones[i];
      bone.position.set(b.lp[0], b.lp[1], b.lp[2]);
      bone.quaternion.set(b.lr[0], b.lr[1], b.lr[2], b.lr[3]);
      bone.scale.set(b.ls[0] || 1, b.ls[1] || 1, b.ls[2] || 1);
    });
}
export function pauseClip(p) { animPaused = !!p; }
export function setClipSpeed(s) { if (mixer) mixer.timeScale = Math.max(0.05, s); }
export function clipState() {
  return { playing: !!mixer && !animPaused, time: action ? action.time % Math.max(0.001, clipDuration) : 0, duration: clipDuration };
}
export function setClipTime(t) { if (action && mixer) { action.time = Math.max(0, Math.min(clipDuration, t)); mixer.update(0); } }

// ---- skeleton overlay --------------------------------------------------------
function clearSkeleton() {
  if (skelGroup) { root.remove(skelGroup); skelGroup.traverse(o => { o.geometry?.dispose?.(); o.material?.dispose?.(); }); skelGroup = null; }
  if (boneMarker) { root.remove(boneMarker); boneMarker.traverse(o => { o.geometry?.dispose?.(); o.material?.dispose?.(); }); boneMarker = null; }
}

// The skeleton shown belongs to the displayed part (or the first part that has one).
function currentSkeleton() {
  if (!current) return null;
  if (partIndex >= 0) return current.parts[partIndex]?.skeleton || null;
  for (const p of current.parts) if (p && p.skeleton) return p.skeleton;
  return null;
}

function rebuildSkeleton() {
  clearSkeleton();
  const bones = currentSkeleton();
  if (!bones || !showSkel) return;
  skelGroup = new THREE.Group();
  skelGroup.renderOrder = 999;

  const pairCount = bones.filter(b => b.parent >= 0 && bones[b.parent]).length;
  const lineArr = new Float32Array(pairCount * 6);
  const jointArr = new Float32Array(bones.length * 3);
  // static fill (bind pose) — replaced by live rig positions while animating
  let li = 0;
  bones.forEach((b, i) => {
    jointArr[i * 3] = b.pos[0]; jointArr[i * 3 + 1] = b.pos[1]; jointArr[i * 3 + 2] = b.pos[2];
    if (b.parent >= 0 && bones[b.parent]) {
      const p = bones[b.parent].pos;
      lineArr[li++] = b.pos[0]; lineArr[li++] = b.pos[1]; lineArr[li++] = b.pos[2];
      lineArr[li++] = p[0]; lineArr[li++] = p[1]; lineArr[li++] = p[2];
    }
  });

  const lg = new THREE.BufferGeometry();
  const lineAttr = new THREE.BufferAttribute(lineArr, 3);
  lg.setAttribute('position', lineAttr);
  const lines = new THREE.LineSegments(lg, new THREE.LineBasicMaterial({ color: 0x4fd1c5, depthTest: false, transparent: true, opacity: 0.9 }));
  lines.renderOrder = 999; lines.frustumCulled = false;
  skelGroup.add(lines);

  const pg = new THREE.BufferGeometry();
  const jointAttr = new THREE.BufferAttribute(jointArr, 3);
  pg.setAttribute('position', jointAttr);
  const joints = new THREE.Points(pg, new THREE.PointsMaterial({ color: 0xffe08a, size: 5, sizeAttenuation: false, depthTest: false, transparent: true }));
  joints.renderOrder = 1000; joints.frustumCulled = false;
  skelGroup.add(joints);

  skelGroup.userData = { lineAttr, jointAttr };
  root.add(skelGroup);
}

// While a clip plays, the overlay tracks the live bone positions.
const _wv = new THREE.Vector3();
const _winv = new THREE.Matrix4();
function rebuildSkeletonFromRig() {
  if (!skelGroup || !skelGroup.userData.lineAttr) return;
  const rig = rigs.get(partIndex >= 0 ? partIndex : (rigs.keys().next().value ?? -1));
  if (!rig) return;
  _winv.copy(root.matrixWorld).invert();
  const { lineAttr, jointAttr } = skelGroup.userData;
  const local = i => { rig.bones[i].getWorldPosition(_wv); return _wv.applyMatrix4(_winv); };
  let li = 0;
  rig.sk.forEach((b, i) => {
    const p = local(i);
    jointAttr.setXYZ(i, p.x, p.y, p.z);
    if (b.parent >= 0 && rig.bones[b.parent]) {
      lineAttr.setXYZ(li++, p.x, p.y, p.z);
      const q = local(b.parent);
      lineAttr.setXYZ(li++, q.x, q.y, q.z);
    }
  });
  lineAttr.needsUpdate = true;
  jointAttr.needsUpdate = true;
}

export function setSkeleton(on) { showSkel = on; rebuildSkeleton(); }
export function hasSkeleton() { return !!currentSkeleton(); }

/** Highlight one bone in the 3D view (marker + pulse), e.g. from the skeleton tree. */
export function highlightBone(i) {
  const bones = currentSkeleton();
  const b = bones && bones[i];
  if (!b) return;
  if (!showSkel) { showSkel = true; rebuildSkeleton(); }
  if (boneMarker) { root.remove(boneMarker); boneMarker.traverse(o => { o.geometry?.dispose?.(); o.material?.dispose?.(); }); boneMarker = null; }

  const box = new THREE.Box3().setFromObject(root);
  const radius = Math.max(0.012, box.isEmpty() ? 0.05 : box.getSize(new THREE.Vector3()).length() * 0.012);
  boneMarker = new THREE.Group();
  const sph = new THREE.Mesh(
    new THREE.SphereGeometry(radius, 18, 14),
    new THREE.MeshBasicMaterial({ color: 0xff5b7c, depthTest: false, transparent: true, opacity: 0.9 }));
  const axes = new THREE.AxesHelper(radius * 3.2);
  axes.material.depthTest = false;
  boneMarker.add(sph, axes);
  boneMarker.position.set(b.pos[0], b.pos[1], b.pos[2]);
  boneMarker.renderOrder = 1001;
  root.add(boneMarker);
  boneMarkerEnd = performance.now() + 2600;
}

// ---- live material editing ----------------------------------------------------
/** Re-apply edited shader params (and emissive toggle) to every mesh of a material. */
export function updateMaterialParams(pi, mi, params) {
  if (!current || !current.parts[pi]) return;
  const matInfo = current.parts[pi].materials && current.parts[pi].materials[mi];
  if (!matInfo) return;
  if (params) matInfo.params = params;
  root.traverse(c => {
    if (!c.isMesh || c.userData.part !== pi || c.userData.mat !== mi) return;
    applyParams(c.material, matInfo);
  });
}

/** Briefly tint all meshes using a material so the user sees what it covers. */
export function flashMaterial(pi, mi) {
  const until = performance.now() + 900;
  const targets = [];
  root.traverse(c => { if (c.isMesh && c.userData.part === pi && c.userData.mat === mi) targets.push(c); });
  for (const c of targets) {
    const m = c.material;
    if (m.__flashBack) continue;
    m.__flashBack = { emissive: m.emissive.clone(), intensity: m.emissiveIntensity, map: m.emissiveMap };
    m.emissive = new THREE.Color(0x4fd1c5);
    m.emissiveMap = null;
    m.emissiveIntensity = 0.9;
    m.needsUpdate = true;
    setTimeout(() => {
      const bk = m.__flashBack; if (!bk) return;
      m.emissive = bk.emissive; m.emissiveIntensity = bk.intensity; m.emissiveMap = bk.map;
      delete m.__flashBack; m.needsUpdate = true;
    }, Math.max(0, until - performance.now()));
  }
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
export function partName(i) { return current && current.parts[i] ? current.parts[i].name : null; }
export function partHash(i) { return current && current.parts[i] ? current.parts[i].hash : null; }
export function setPartName(i, name) { if (current && current.parts[i]) current.parts[i].name = name; }
export function setPartData(i, part) { if (current && current.parts[i]) current.parts[i] = part; }
export function setVertexColors(on) { vcolor = on; rebuild(); }
export function setWire(on) { wire = on; root.traverse(m => { if (!m.isMesh) return; if (Array.isArray(m.material)) m.material.forEach(x => x.wireframe = on); else if (m.material) m.material.wireframe = on; }); }
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
