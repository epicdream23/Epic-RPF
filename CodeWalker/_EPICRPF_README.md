# CodeWalker (source clone) — separate world-editor tool

This folder is a **full source clone of the real CodeWalker** (dexyfex/CodeWalker, `master`),
dropped in so you can **add your own functions** to it. It is the actual fly-around GTA V
world editor: roam the map, select objects and see all their info, and create/edit
`.ymap` / `.ytyp` / `.ynd` / `.ynv` etc. through the Project editor.

It is **completely separate** from Epic RPF — its own `CodeWalker.sln`, its own
`CodeWalker.Core` build, **not** referenced by `EpicRpf.slnx`. Epic RPF still uses
`libs\CodeWalker.Core.dll` as before; nothing here touches it.

## Build

```
_build.bat
```

That builds the world-editor project (`CodeWalker.exe`, net48 + SharpDX, Release), restores
NuGet, and copies the runtime `Shaders\` + `icons\` next to the exe. Or open
`CodeWalker.sln` in Visual Studio and build the **CodeWalker** project.

Output: `CodeWalker\bin\Release\net48\CodeWalker.exe`

(Building does **not** require the C++ `CodeWalker.Shaders` project — the compiled `.cso`
shaders are checked in under `Shaders\`. You only need that project if you change HLSL.)

## Run

```
Run CodeWalker.bat        (or double-click CodeWalker\bin\Release\net48\CodeWalker.exe)
```

First launch: CodeWalker shows **"Auto-detected game folder"** — it finds your install
(verified: it picks up `C:\Program Files\Epic Games\GTAV` via the OpenIV registry entry).
Click **Yes**, then **World** to enter the map. Everything — models, textures, lighting,
ymaps — streams from that real game directory. The folder is remembered after the first run
(change it later via the menu).

## Where to add your own functions

| Area | File | What it is |
|------|------|-----------|
| App entry / modes | `CodeWalker\Program.cs` | startup; `world` / `explorer` / `project` / `peds` / `vehicles` CLI modes |
| Launcher menu | `CodeWalker\MenuForm.cs` | the big-button launcher — add a new tool button here |
| **World map editor** | `CodeWalker\WorldForm.cs` (~8k lines) | camera, input, the menu bar + toolbar, render loop. **Add map tools / menu items here.** |
| Object selection | `CodeWalker\World\MapSelection.cs` | mouse picking + the selected-object info shown in the panel |
| Renderer (D3D11) | `CodeWalker\Rendering\Renderer.cs`, `Renderable*.cs`, `ShaderManager.cs`, `VertexTypes.cs` | the SharpDX render pipeline |
| Shaders (HLSL) | `CodeWalker.Shaders\*.hlsl` → `Shaders\*.cso` | only if you change shading; needs VS C++ + the FXC compiler |
| **Project editor** | `CodeWalker\Project\ProjectForm.cs` (~10k lines) + `Project\Panels\Edit*Panel.cs` | create/edit `.ymap`/`.ytyp`/`.ynd`/... ; each `Edit*Panel` is one entity editor |
| Data layer | `CodeWalker.Core\` | RPF + all file-format parsing (same family as Epic RPF's DLL, built fresh here) |

Good first insertion points for custom tools: a new toolbar button / menu item in
`WorldForm.cs`, a new launcher button in `MenuForm.cs`, or a custom panel in `ProjectForm.cs`.

## Custom additions (Epic RPF)

Our own code lives in [`CodeWalker/MloTools.cs`](CodeWalker/MloTools.cs) (a `partial class
WorldForm`, wired in by one line in the `WorldForm` constructor). It adds two commands to
the world editor's **Tools** menu (the Tools button on the toolbar):

- **Select MLO Objects** — with an object inside an MLO (interior) selected, this selects
  *every* entity of that MLO at once (all rooms + entity sets). Works whether you have an
  interior prop or the MLO entity itself selected.
- **Export MLO Objects…** — extracts the model + texture files (`.ydr` / `.ydd` / `.yft`
  + `.ytd`) of every entity in that MLO into one chosen folder, plus a `_manifest.txt`
  listing each archetype with its local position / rotation / scale. This makes it easy to
  delete an existing MLO and rebuild your own from its parts.

  Two things worth knowing:
  - The export pulls every entity (all rooms **and all entity sets**, including ones not
    currently visible) **directly from the archives** — it does *not* depend on what has
    streamed into the viewport. So you don't need to wait for the whole interior to load:
    select any one object in it and export immediately, you still get the complete set.
  - Exported resources get a proper RSC7 header rebuilt (compress + header), so the files
    are valid standalone `.ydr/.ydd/.yft/.ytd` that Epic RPF / OpenIV / CodeWalker open
    normally.

- **Load All MLOs (void gallery)** — unloads the real map and lays **every MLO archetype in
  the game** out in a single line in an empty void, **sorted smallest → largest** and spaced
  by each MLO's own footprint so small ones sit close together and nothing overlaps. The
  MLO exterior/building shells are hidden (`Renderer.rendermloshells=false`) so you only see
  the interiors, not low-LOD towers. Click again to exit back to the map. Implementation: a
  synthetic in-memory ymap holds one MLO instance per archetype (all entity sets forced
  visible), rendered through CodeWalker's normal LOD/world path with map streaming off; only
  MLOs near the camera fully render (LOD culling). Building it the first time can take a few
  seconds (it instantiates every interior).

  Known gaps: some interiors are missing a roof, and street-facing **store doors** can be
  absent — those doors are placed as world entities at the store's real map location (not
  inside the MLO data), so they aren't part of the gallery.

- **Performance / loading (RAM, GPU, speed)…** — a Tools-menu dialog to throw more hardware
  at CodeWalker so the world (and the MLO gallery) loads from further away and runs smoother:
  - *Loading speed (items per loop)* — how many models/textures are uploaded per content-loop.
    Stock CodeWalker is **1** (why things only appear when you get close); the default is now
    **8** and you can push it higher. Applied live, uses more CPU.
  - *RAM file cache (GB)* / *GPU geometry cache (GB)* / *GPU texture cache (GB)* — how much
    stays resident so it isn't constantly re-streamed. Applied live and saved (re-applied at
    the next launch). Defaults are small (2 GB / 0.5 GB / 1 GB) — raise them for big sessions.

  Code-level change: `RenderableCache.MaxItemsPerLoop` and `GameFileCache.MaxItemsPerLoop`
  defaults were raised 1→8; `GameFileCache.SetCacheSize()` and `RenderableCache.SetCacheLimits()`
  were added so the caches can be resized at runtime.

To extend further, follow the same pattern: a new `partial class WorldForm` file + one call
from `InitMloTools()` (or the constructor). `SelectedItem` is the current selection,
`SelectMulti(...)` sets a multi-selection, and `GameFileCache.GetYdrEntry/GetYddEntry/
GetYftEntry/GetYtdEntry(hash)` resolve a file entry you can extract with
`entry.File.ExtractFile(entry)`.

## Notes

- This is a **shallow clone** (`--depth 1`). To pull upstream updates or get full history:
  `git fetch --unshallow` then `git pull`.
- License: CodeWalker source is released by dexyfex for educational purposes (see
  `Notice.txt` / `Readme_Src.txt`). Keep that in mind before redistributing.
- You already had a prebuilt copy at `Desktop\CodeWalker30_dev46\`; this clone is the same
  app but as **source you can modify and rebuild**.
