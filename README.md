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

## CodeWalker world editor

New in **v4.0** — a **🗺 CodeWalker** button in the toolbar launches the full
[CodeWalker](https://github.com/dexyfex/CodeWalker) world editor, **bundled and ready to
go** (no separate download or build). Fly around the entire GTA V map — streamed live from
your real game files — and select and export world objects and interiors (MLOs). A few
extras are layered on top: one-click tools to gather and lay out every MLO side-by-side for
inspection, and a performance dialog to tune how much RAM and how many CPU/GPU cores it uses.

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

## Explorer — keyboard, mouse & clipboard

The file browser works like Windows Explorer, and it's fully drivable without a mouse:

- **Arrow keys** move the selection — Up/Down **wrap around** (past the bottom jumps back to
  the top, and vice-versa). **Enter** opens the item, **Backspace** goes up a folder,
  **Home/End** and **Page Up/Page Down** jump, and **type-ahead** (just start typing a name)
  skips to it. **Shift+arrows** extend the selection; **Ctrl+A** selects everything.
- **Cut / copy / paste**, the Windows way — **Ctrl+X / Ctrl+C / Ctrl+V** (or the right-click
  menu) move or copy files and folders, even **across archives** and between an archive and a
  loose disk folder. Cut items dim until you paste; a paste that would clash is auto-renamed
  (`name - Copy`) so nothing is ever overwritten, and a cut is **undoable** (Ctrl+Z).
- The mouse's **Back / Forward** side buttons navigate folder history (as do **Alt+← / Alt+→**
  and the toolbar arrows), and you can **rubber-band-select** by dragging a box over empty space.

## Open files by double-clicking (file associations)

Epic RPF registers the file types it understands (`.rpf`, `.ytd`, `.ydr`, `.ydd`, `.yft`,
`.ypt`, `.ymap`, `.ytyp`, metas, `.gfx`, `.epic`, …) under the current user — no admin needed.
Each type gets its **own icon** on the desktop and in Explorer (the same glyph it has inside
the tool), and Epic RPF is set as its default handler so those icons appear. After that:

- **Double-click a supported file while the app is open** → it opens in a **new tab**, just
  like any file from inside an archive, and the window jumps to the front. (A second launch
  detects the running instance over a named pipe, hands it the file, and exits.)
- **Double-click while the app is closed** → it launches in **viewer mode**: only the right
  renderer for that file (3D viewer, texture grid, GFX, text/XML, hex) fills the window — no
  sidebar, tabs or mount bar. The file is still fully **editable** (replace/delete textures,
  edit text/XML) and saves straight back to the file on disk.
- **`.rpf` archives** get a distinct gold archive icon, and double-clicking one opens the full
  app and **mounts its folder** so you can browse the archive right away (an `.rpf` is far too
  large to open as a single file).

The icons are applied **right after installation** (no need to launch the app first) and
removed again on **uninstall**, refreshing the Windows icon cache both times. `.epic` is set
as the default for its extension (it's our own format); the GTA game extensions get their icon
and open in Epic RPF by default, while leaving generic types (`.xml`/`.txt`/`.ini`/…) alone.

You can also **delete textures** from a `.ytd`/`.ypt` — select one or several in the grid
(click, Ctrl-click, Shift-click) and press Delete, or right-click → **Delete texture**; the
dictionary is rebuilt and saved in place. In-archive edits — even inside the ~2 GB
`update.rpf` — are written reliably, the file list updates live as soon as a change lands,
and a **⟳ Reload** button force-re-reads everything from disk if you ever want it.

## Roadmap

Shipped in v4.2.x (see `changelog.txt`):
 - ✅ Full keyboard control of the file list + Windows cut/copy/paste (across archives)
 - ✅ Per-type desktop icons (incl. a `.rpf` archive icon), applied on install & removed on uninstall
 - ✅ Settings: themes, accent colour, 10-language UI, rounded corners & window outline
 - ✅ Uninstall fully removes the program folder (force-closes the app first)

Shipped in v4.0.0:
 - ✅ Bundled CodeWalker world editor (fly the map, select/export objects & MLOs)
 - ✅ Rock-solid in-archive texture editing — multi-select delete, live view updates
 - ✅ Rename `.rpf` archives; much faster drag-in; `.rpf` file sizes shown in the list

Earlier (v3.x):
 - ✅ Toggleable vehicle additions (optional car mods you can switch on per car)
 - ✅ Reworked 3D viewer — skeletons & materials
 - ✅ Linked `.ydr`/weapon models to `.ycd` animations (playback)

Got an idea? The Discord has a channel for feature requests.