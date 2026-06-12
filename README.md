Epic RPF is a Codewalker.Core.dll based program for viweing and editing .rpf7 Archieves. It brings new tools that are missing in Codewalker as well as OpenIV and combines some into one. It significantly imrpoves speed since the front end UI is build using HTML. 


There will be aded more in the future updates, please check ou tmy Discord:

https://discord.gg/FZw6ChbFbp

There is a designated channel for bugs and new features you would like to see.

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

TODO:

 Add ydr übergreifende files (auto mods optional auf einem auto anmachen)
 
 3d Viewer überarbeiten skeletons, materials
 
 ydr anims mit ycd (animations) verknüfen