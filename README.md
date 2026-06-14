Epic RPF is a CodeWalker.Core.dll–based program for viewing and editing .rpf7 archives. It brings new tools that are missing in CodeWalker and OpenIV, and combines several of them into one. It's significantly faster because the front-end UI is built with HTML.

More will be added in future updates — please check out my Discord:

https://discord.gg/FZw6ChbFbp

There's a designated channel for bugs and new features you'd like to see.

## 3D viewer, textures & animations

Open a `.ydr` / `.ydd` / `.yft` / `.ypt` and it renders in a WebGL viewport — orbit
camera, LOD switching, and per-part navigation.

- **Materials** — Phong shading with normal / specular / emissive maps. The **Materials**
  tab lists every shader with its parameters; edit them and the changes save straight back
  into the model. You can also point the viewer at your own `.ytd` files for textures.
- **Skeleton** — the **Skeleton** tab shows a model's bone hierarchy with a live overlay
  that follows the current pose.
- **Vehicle additions** — toggle a car's optional extra parts (bull-bars, roof racks and
  the like) on and off from the Materials tab.
- **Animations** — the **Anims** tab plays GTA animations (`.ycd`). It automatically finds
  the animation dictionaries that belong to a model, lists every clip, and plays them with
  play/pause, a scrub bar and speed control; or browse to any `.ycd` yourself. Tick
  **Show character** on a weapon to load a full freemode character holding it, so movement /
  aim / reload / idle clips play on a whole body — with a live **Grip adjust** panel
  (position + rotation, remembered between sessions) to seat the weapon exactly in the hand.

Textures (`.ytd` / `.ypt`) open in a grid you can preview, replace/import (images or DDS,
drag-and-drop), and delete from. Metas and XML open in a built-in editor with binary↔XML
round-trip, and Scaleform (`.gfx`) files have their own viewer.

## rpfcli — headless file access (for scripts & coding agents)

`src/App.Cli` builds `rpfcli.exe`, a command-line tool that views and edits any file in
the GTA V install **including inside .rpf archives** — no manual extraction. Paths are
GTA-root-relative and cross archive boundaries:

```
rpfcli ls   update/update.rpf/common/data/timecycle      # list a folder (even inside rpfs)
rpfcli find timecyc                                      # search all entries by name (--ext for extension)
rpfcli info <vpath>                                      # entry details
rpfcli cat  <vpath> [-o out.xml]                         # read as text; binary metas (.ymt/.ymap/...)
                                                         #   are converted to CodeWalker XML automatically
rpfcli get  <vpath> <outfile>                            # extract raw (valid standalone file)
rpfcli put  <vpath> <infile>                             # write back; XML input against a binary target
                                                         #   is converted back to the binary format
rpfcli ... --gta <folder>                                # default: EPICRPF_GTA env or the Epic install
```

Typical edit cycle: `cat -o tmp.xml` → edit the XML → `put <vpath> tmp.xml`. Writes into
NG-encrypted archives work: the NG encrypt tables are computed once (≈1 min) and cached in
`%LOCALAPPDATA%\EpicRpf`, after which writes take seconds. Note: NG-encrypted .rpf files are
keyed by their filename — never rename one.

## .epic extensions (one-click mod install)

Epic RPF can package mods into a single **`.epic`** file that anyone installs by dragging
it onto the app (or **◆ Extension → Install extension…**). It's an *encrypted* container
holding a manifest of operations plus any payload files — conceptually like OpenIV's
`.oiv`, but you build it inside Epic RPF (**◆ Extension → Create extension…**), no
third-party tools, and installs auto-back-up every changed file to `GTAV\EpicRpf_backups`.

Operations a `.epic` can perform (each targets a GTA-root-relative vpath, including
inside `.rpf` archives):
- **replaceFile** — add/replace a whole file (meta, ytd, ydr, anything).
- **deleteFile** — remove a file.
- **xml** — edit an XML/meta file by XPath: `add` a node, `replace`/`remove` a node,
  `setattr` (meta scalars use `value="…"`), or `settext`. Binary metas (.ymt/.ymap/…)
  are converted to/from CodeWalker XML automatically.
- **text** — line edits on a text file: `append`, `insertBefore`/`insertAfter` an anchor,
  `replace`, `delete`.

Headless equivalents in `rpfcli`: `epic create <manifest.json> <out.epic>`,
`epic inspect <pkg.epic>`, `epic install <pkg.epic>`.

## Open files by double-clicking (file associations)

Epic RPF registers the file types it understands (`.ytd`, `.ydr`, `.ydd`, `.yft`, `.ypt`,
`.ymap`, `.ytyp`, metas, `.gfx`, `.epic`, …) under the current user on first run — no admin
needed. After that:

- **Double-click a supported file while the app is open** → it opens in a **new tab**, just
  like any file from inside an archive, and the window jumps to the front. (A second launch
  detects the running instance over a named pipe, hands it the file, and exits.)
- **Double-click while the app is closed** → it launches in **viewer mode**: only the right
  renderer for that file (3D viewer, texture grid, GFX, text/XML, hex) fills the window — no
  sidebar, tabs or mount bar. The file is still fully **editable** (replace/delete textures,
  edit text/XML) and saves straight back to the file on disk.

`.epic` files are set as the default for that extension (it's our own format); game
extensions are only added to the *Open with* list so the user's other tools aren't hijacked.

You can also **delete an individual texture** from a `.ytd`/`.ypt` — right-click it in the
texture grid → **Delete texture**; the dictionary is rebuilt and saved in place.

## Roadmap

Shipped in v3.0.0 (see `changelog.txt`):
 - ✅ Toggleable vehicle additions (optional car mods you can switch on per car)
 - ✅ Reworked 3D viewer — skeletons & materials
 - ✅ Linked `.ydr`/weapon models to `.ycd` animations (playback)

Got an idea? The Discord has a channel for feature requests.