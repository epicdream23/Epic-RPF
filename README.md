Epic RPF is a CodeWalker.Core.dll–based program for viewing and editing .rpf7 archives. It brings new tools that are missing in CodeWalker and OpenIV, and combines several of them into one. It's significantly faster because the front-end UI is built with HTML.

More will be added in future updates — please check out my Discord:

https://discord.gg/FZw6ChbFbp

There's a designated channel for bugs and new features you'd like to see.

## 3D viewer, textures & animations

Open a `.ydr` / `.ydd` / `.yft` / `.ypt` and it renders in a WebGL viewport — orbit
camera, LOD switching, and per-part navigation. **`.ybn` collision** opens in 3D too — the
bounds (triangle meshes, box polys, and primitive shapes) render as a flat-shaded model, with
each piece palette-coloured (toggle vertex colours to tell them apart). Right-click → **Edit as
XML** still gives the textual view.

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

## Archive protection (locking)

Stop people lifting your custom models/files out of a `.rpf` with CodeWalker or OpenIV.
Right-click any root `.rpf` → **Lock (protect)** and pick a strength (optionally with a
password). Everything is fully reversible with **Unlock**.

- **Light** — tampers a few header bytes so CodeWalker/OpenIV refuse the file, while GTA V
  still loads it directly. Verified to block CodeWalker; reversible; the `.rpf` keeps its exact
  size (the small revert data lives in a hidden `*.rpf.epclk` sidecar). In-game tolerance is
  best-effort — test it, and Unlock if a build won't load it.
- **Full** — encrypts the whole archive (AES-256) at rest, so **neither tools nor the game**
  can read it directly. To use it, the game is launched with on-the-fly decryption (below):
  the file on disk stays encrypted the entire time, even while playing.

**Password (optional).** A password gates **opening/extracting the contents in the tool** — not
running the file in-game. So you can hand a password-locked `update.rpf` to someone: their Epic RPF
will still **decrypt it for the game/launcher** (auto-inject, below) so it runs perfectly, but they
**can't open or extract** your models without the password. The file never decrypts on disk. (Any
Epic RPF can run any locked file; only *extracting* needs the password or your admin key.)

> Protection is *obfuscation-grade* by design: a determined reverse-engineer could defeat it.
> It's meant to stop casual theft, not a funded attacker.

### Playing a Full-locked archive (auto-inject)

A Full-locked `.rpf` can't be read directly, so Epic RPF feeds decrypted bytes at runtime via a
small helper DLL (`EpicRpfHook.dll`) injected into the processes that read it. The **💉 Auto-inject**
toolbar button (next to CodeWalker / Settings) toggles this: while it's ON, Epic RPF watches for
`GTA5.exe` **and your RP server launcher**, and the moment they appear injects on-the-fly
decryption for **every Full-locked archive under your GTA folder that they load** — even when a
launcher you don't control starts the game. The files stay encrypted on disk the whole time. The
setting persists and re-arms after each mount.

Many RP launchers run a **sanity check** over the `.rpf` files before starting the game and would
trip over an encrypted one — so the launcher is injected too, and sees the files decrypted (valid),
then the game is injected when it starts. The launcher often runs as several processes; every
matching one is injected. Set the launcher's process name(s) in **Settings → Advanced → "Auto-inject
also into launchers"** (defaults to `Majestic Launcher`; `GTA5` is always included). Note: a 32-bit
launcher can't be injected by the 64-bit hook (you'll see a clear message).

> ⚠ **Only enable auto-inject on servers that allow non-cheat DLL injection.** The hook is a
> decryption helper, not a cheat — but a server that forbids any injection could ban you for it.
> Confirm with the server's rules/admins first. (It only decrypts your own files; it adds nothing
> to gameplay.) Archives the game never opens are ignored, and password-protected locks are skipped
> (lock without a password, or use the admin key, to include them).

Note: if you lock the core `update.rpf` itself, a *normal* launch won't load it until you unlock
it — that's the point. Keep an unlocked backup while you work.

## Opening cracked / modified / "protected" archives

Some `.rpf` files are deliberately tampered so GTA V still runs them but CodeWalker and OpenIV
refuse to open them (a common trick is setting the header's encryption flag to a bogus value like
`CFXP` — the game treats an unknown flag as plaintext, those tools default it to NG-decrypt and
choke). **Epic RPF opens them anyway:** when an archive won't parse the normal way, it brute-forces
the real table-of-contents encryption and recovers the file list — *including* nested archives
that are protected the same way — without modifying the file on disk. If GTA can read it, the tool
aims to read it too. (`rpfcli tolerant <file.rpf>` does the same from the command line.)

### Creator admin key — how it works

When you lock a file, its random per-file key is also wrapped to an **admin public key** baked
into Epic RPF. The matching **private key** lives only in a `.epickey` file *you* keep (it is
**not** in the program), so:

- **You** can open/Unlock any locked file with **no password** using your `.epickey` — even one
  someone else made and forgot the password to.
- **Nobody else** can: reverse-engineering Epic RPF never reveals the private half, because it
  isn't in the binary.

It's an escrow/master key purely for you, the creator. Make one and bake its public half once
(via the headless tool):

```
rpfcli admin-keygen MyAdmin.epickey     # prints the public key to bake into AppSecret.cs, then rebuild
rpfcli lock   <file.rpf> --mode full|light [--password P]
rpfcli unlock <file.rpf> --key MyAdmin.epickey     # admin override: no password needed
rpfcli lockinfo <file.rpf> [--reveal]              # status (+ embedded password with --reveal)
```

**Keep the `.epickey` private and backed up.** Building the injection helper needs a one-time
`powershell -ExecutionPolicy Bypass -File native\build-hook.ps1`; see `native/README.md` for the
full details.

## Archive Fix (repairing archives after heavy editing)

Editing a `.rpf` a lot in one session — replacing/deleting many files, especially large ones —
leaves gaps and stale space behind that can eventually stop GTA V from loading the archive. The
**🛠 Archive Fix** toolbar button rebuilds (defragments) every archive you've edited this
session — innermost nested archive first, parents last, since a parent physically embeds its
children's bytes — the same job as the ArchiveFix-for-GTA tool. Click it once to fix everything
edited so far and turn on **Archive Fix: ON**, which then keeps auto-fixing on every further
change for the rest of the session. Right-click any single `.rpf` → **Archive Fix (this archive +
nested)** to rebuild just that one on demand. The headless equivalent is `rpfcli fixall`, which
walks the entire mounted install.

Every rebuild is wrapped in a safety net: the archive is backed up first, and if the rebuilt file
doesn't re-open cleanly afterwards — e.g. a huge archive would exceed the RPF block-offset limit —
the original is automatically restored and the file is left exactly as it was. If the restore
itself can't complete (the archive is locked by another program the whole time, for instance), the
backup is kept on disk instead of being deleted, and the failure message names the backup file so
you can restore it by hand — a rebuild attempt never silently leaves an archive broken with no way
back.

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

Shipped in v5.0.0 (see `changelog.txt`):
 - ✅ Archive protection (locking) — Full AES-256 encryption at rest, auto-inject for
   in-game play, password gate, creator admin escrow key
 - ✅ Opening cracked/tampered archives that CodeWalker/OpenIV refuse to touch
 - ✅ Archive Fix — rebuild/defragment archives after heavy editing, with backup +
   verify + auto-rollback so a bad rebuild is never left on disk
 - ✅ `rpfcli` headless command-line tool for scripts and coding agents
 - ✅ `.ybn` collision files open in the 3D viewer

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