using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using App.Core;
using App.Geometry;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace App.UI;

/// <summary>
/// The JS <-> C# message bridge. The front-end posts JSON commands; this class
/// runs them off the UI thread (serialized, so the CodeWalker manager is never
/// hit concurrently) and posts JSON responses back. Mesh buffers are base64-encoded
/// typed arrays the viewport decodes straight into three.js BufferGeometry.
/// </summary>
public sealed class Bridge
{
    private readonly Action<string> _post;
    private readonly Func<string?> _pickFolder;
    private readonly Func<string, string?> _pickSavePath;
    private readonly Action<string>? _windowAction;     // "min" | "max" | "close"
    private readonly Action<int, string>? _openPopout;  // (popoutId, title) -> new window
    private readonly Action<string[]>? _startDrag;      // begin a native OS file drag with these paths
    private readonly Func<string, string?>? _pickOpenPath;  // open-file dialog (filter) -> path

    // The mounted game is shared across all windows (main + torn-off popouts), so
    // these are static: a popout window's Bridge reuses the already-mounted set.
    private static RpfWorkspace? _ws;
    private static TextureResolver? _resolver;
    private static readonly Dictionary<int, object> _nodes = new();
    private static readonly Dictionary<int, RpfFileEntry> _searchResults = new();
    // For nodes opened via "Edit as XML": the temp folder the resource's textures
    // were extracted to, so a save-to-archive can read the .dds files back.
    private static readonly Dictionary<int, string> _xmlDdsDirs = new();
    // Viewer payloads stashed for tabs torn off into a new window.
    private static readonly Dictionary<int, string> _popouts = new();
    private static int _popoutSeq = 1;
    private static int _searchSeq = 1;
    private static int _nextNodeId = 1;
    // One global lock: every window's Bridge shares the same CodeWalker manager.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The GFX scene DTOs (shapes/sprites/fills/placements) expose their data as
        // public fields; without this they serialize as empty {} and nothing renders.
        IncludeFields = true,
    };

    public Bridge(Action<string> post, Func<string?> pickFolder, Func<string, string?> pickSavePath,
                  Action<string>? windowAction = null, Action<int, string>? openPopout = null,
                  Action<string[]>? startDrag = null, Func<string, string?>? pickOpenPath = null)
    {
        _post = post;
        _pickFolder = pickFolder;
        _pickSavePath = pickSavePath;
        _windowAction = windowAction;
        _openPopout = openPopout;
        _startDrag = startDrag;
        _pickOpenPath = pickOpenPath;
    }

    // Native OS drag-out. Called on the UI thread the instant the front-end detects a
    // drag gesture (so the mouse button is still down). Each selected entry is
    // materialised to a real file path — archive entries are extracted to a temp drag
    // folder (as valid standalone files), loose disk files / base .rpf / disk folders
    // are dragged in place — then handed to the host which runs the OLE drag loop.
    // Pure file reads (no manager mutation), so it runs without the command gate.
    public void HandleDrag(int[] ids)
    {
        if (_startDrag == null) return;
        var paths = PrepareDragPaths(ids);
        if (paths.Length > 0) _startDrag(paths);   // modal: returns after the drop/cancel
        // Tell the front-end the OLE drag finished. Dropping the dragged files back onto
        // our OWN window raises the normal HTML drop event — without this signal the app
        // would treat its own drag-out as an external import (and re-import the files).
        Send(new { type = "dragOutDone" });
    }

    public string[] PrepareDragPaths(int[] ids)
    {
        if (ids == null || ids.Length == 0) return Array.Empty<string>();
        string dir = Path.Combine(Path.GetTempPath(), "EpicRpf_drag");
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
                foreach (var d in Directory.GetDirectories(dir)) { try { Directory.Delete(d, true); } catch { } }
            }
            Directory.CreateDirectory(dir);
        }
        catch { }

        var paths = new List<string>();
        foreach (var id in ids)
        {
            if (!_nodes.TryGetValue(id, out var obj)) continue;
            try
            {
                switch (obj)
                {
                    case RpfFileEntry fe:
                        var p = Path.Combine(dir, SafeName(fe.Name));
                        File.WriteAllBytes(p, RpfWorkspace.ExtractForSave(fe));
                        paths.Add(p);
                        break;
                    case DiskItem di:                                   // loose file or folder — drag in place
                        if (File.Exists(di.Path) || Directory.Exists(di.Path)) paths.Add(di.Path);
                        break;
                    case RpfFile rf when SafePhysical(rf) is string phys && File.Exists(phys):
                        paths.Add(phys);                                // base .rpf — drag the archive file itself
                        break;
                    case RpfDirectoryEntry rd:                          // folder inside an archive — extract to temp
                        var sub = Path.Combine(dir, SafeName(rd.Name));
                        int c = 0; long b = 0; ExtractDir(rd, sub, ref c, ref b);
                        paths.Add(sub);
                        break;
                }
            }
            catch { }
        }
        return paths.ToArray();
    }

    private sealed class Req
    {
        public int Id { get; set; }
        public string? Cmd { get; set; }
        public string? Folder { get; set; }
        public bool Gen9 { get; set; }
        public int Node { get; set; }
        public string? Content { get; set; }
        public string? Format { get; set; }   // "text" | "meta" | dds format
        public string? MetaName { get; set; } // xml filename from MetaXml (drives reverse format)
        public string? Target { get; set; }   // "rpf" | "export"
        public string? Rgba { get; set; }     // base64 RGBA8 for encodeDds
        public int W { get; set; }
        public int H { get; set; }
        public string? Name { get; set; }     // filename for encodeDds / create commands
        public bool Override { get; set; }     // create content override when making an rpf
        public string? Query { get; set; }     // search text
        public bool Ext { get; set; }          // search by extension instead of name
        public string? Scope { get; set; }     // "none" | "archive" | "folder"
        public int Limit { get; set; }
        public string? As { get; set; }        // "xml" -> force the (optional) XML editor view
        public string? Title { get; set; }      // popout window title
        public string? Payload { get; set; }    // serialized viewer state for a torn-off tab
        public bool Force { get; set; }          // delete: confirmed to purge trash if full
        public long Batch { get; set; }          // delete: groups a multi-select delete for undo
        public int Index { get; set; }           // modelPart / texImage index
        public string? File { get; set; }        // custom-name LUT key (file name)
        public string? Hash { get; set; }        // custom-name LUT key (drawable hash hex)
        public string? Edge { get; set; }        // frameless window resize edge (l/r/t/b/tl/tr/bl/br)
        public string? Path { get; set; }        // stable file path, to re-resolve a save after a remount
        public int[]? Nodes { get; set; }         // multi-select (extract many)
        public string[]? Paths { get; set; }       // stable paths paired with Nodes (stale/loose fallback)
        public string? Manifest { get; set; }     // createEpic: the manifest JSON the builder assembled
        public int Mat { get; set; }              // setShaderParams: material (shader) index
        public int Ytd { get; set; }              // useTxd: node id of the .ytd to draw textures from
        public int Rid { get; set; }              // useTxd: search-result id (separate id space from nodes!)
        public string? SrcPath { get; set; }      // importPath: source file on disk (no base64 round-trip)
        public string[]? DroppedPaths { get; set; } // set host-side from postMessageWithAdditionalObjects
        public int Ycd { get; set; }              // animClips/animBake: node id of the clip dictionary
        public string? Clip { get; set; }         // animBake: clip hash (hex) inside the dictionary
        public bool Save { get; set; }            // setShaderParams: also write the file back
        public ParamEdit[]? Params { get; set; }  // setShaderParams: edited shader values
    }

    public sealed class ParamEdit
    {
        public string Name { get; set; } = "";
        public float[] Values { get; set; } = Array.Empty<float>();
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.Ordinal)
    { "dds", "png", "jpg", "jpeg", "bmp", "gif", "webp", "svg" };

    // Metadata-ish resource types CodeWalker can round-trip to/from editable XML.
    // Deliberately excludes models/textures (ydr/ydd/yft/ytd) and heavy/binary
    // resources (ynv/awc/fxc/...) which have their own viewers or aren't practical.
    private static readonly HashSet<string> MetaConvertExts = new(StringComparer.Ordinal)
    { "ymt", "ymf", "ymap", "ytyp", "pso", "cut", "rel", "ynd", "ycd", "ybn", "yld", "yed", "mrf", "ypdb", "yfd" };

    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    // A real on-disk node (directory or loose file). The tree now mirrors the
    // actual filesystem, not just mounted archives, so loose files/folders show.
    private sealed class DiskItem
    {
        public string Path = "";
        public bool IsDir;
    }
    private static DiskItem? _diskRoot;
    private static string _rootName = "GTA V";
    private static string _gtaFolder = "";

    // base (on-disk) archives keyed by full path, to link a disk .rpf -> its mount.
    private static readonly Dictionary<string, RpfFile> _baseRpfByPath = new(StringComparer.OrdinalIgnoreCase);

    // live file watching
    private static FileSystemWatcher? _watcher;
    private static Timer? _watchDebounce;
    private static volatile bool _watchRpfChanged;
    private static Action<object>? _broadcast;                 // notify the UI of fs changes
    private static readonly Dictionary<string, long> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

    // trash (delete moves here; permanent removal only when over the limit)
    private static string TrashDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicRpf", "trash");
    private const long TrashLimitBytes = 4L * 1024 * 1024 * 1024; // 4 GB

    // undo (Ctrl+Z): restore the most recent batch of deletions from trash.
    // The parent is stored as its stable PATH, never as a graph object — a background
    // remount swaps the graph, and restoring through a stale RpfDirectoryEntry would
    // write outdated entry tables into the archive (= corruption).
    private sealed class UndoEntry
    {
        public long Batch;
        public string Kind = "";       // "disk" | "rpfFile" | "rpfDir" | "baseRpf"
        public string TrashPath = "";
        public bool IsDir;
        public string OrigPath = "";
        public string ParentPath = "";
        public string Name = "";
    }
    private static readonly List<UndoEntry> _undoStack = new();
    private static long _batchSeq = 1;

    // Loaded-model cache so parts/textures load lazily (fast open of huge ydd/ypt).
    private sealed class ModelCache
    {
        public List<DrawableLoader.NamedDrawable> Drawables = new();
        public TextureDictionary? LocalDict;
        public RpfFileEntry? Entry;
        public string EntryPath = "";  // archive path of Entry, to re-resolve after a remount
        public string File = "";
        public string DiskPath = "";   // set for loose (on-disk) ytd/ypt, for writing texture edits back
        public List<TextureDictionary> ExtraDicts = new();   // user-picked .ytd texture sources (highest priority)
        public object? OwnerFile;      // the typed YdrFile/YddFile/YftFile/YptFile, for shader-param save-back
    }
    private static readonly Dictionary<int, ModelCache> _modelCache = new();
    private static readonly List<int> _modelCacheOrder = new();
    private static int _synthSeq = -1;

    // Cached .gfx bytes (+ source entry for image resolution) so the render scene can
    // be built lazily when the user switches to the visual view.
    private static readonly Dictionary<int, (byte[] bytes, RpfFileEntry? entry, string diskPath)> _gfxCache = new();
    private static readonly List<int> _gfxCacheOrder = new();

    // Custom drawable names (client-side only, never written into the file).
    // names.json: { "<filenameLower>": { "<hashHex>": "Custom Name" } }
    private static string NamesLutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicRpf", "names.json");
    private static Dictionary<string, Dictionary<string, string>>? _namesLut;

    public void HandleMessage(string json) => HandleMessage(json, null);

    /// <summary>
    /// <paramref name="droppedPaths"/> carries the REAL disk paths of files the front-end
    /// passed via postMessageWithAdditionalObjects (drag-drop). Shipping paths instead of
    /// base64 bytes is what makes multi-GB imports possible — a base64 string of a big
    /// file exceeds JavaScript's maximum string length ("Invalid string length").
    /// </summary>
    public void HandleMessage(string json, string[]? droppedPaths)
    {
        Req req;
        try { req = JsonSerializer.Deserialize<Req>(json, JsonOpts) ?? new Req(); }
        catch { return; }
        req.DroppedPaths = droppedPaths;

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { Dispatch(req); }
            catch (Exception ex) { Send(new { type = "error", reqId = req.Id, message = ex.Message }); }
            finally { _gate.Release(); }
        });
    }

    private void Dispatch(Req r)
    {
        switch (r.Cmd)
        {
            case "pickFolder": CmdPickFolder(r); break;
            case "detect": CmdDetect(r); break;
            case "mount": CmdMount(r); break;
            case "search": CmdSearch(r); break;
            case "reveal": CmdReveal(r); break;
            case "expand": CmdExpand(r); break;
            case "open": CmdOpen(r); break;
            case "firstModel": CmdFirstModel(r); break;
            case "firstMeta": CmdFirstMeta(r); break;
            case "firstYtd": CmdFirstYtd(r); break;
            case "firstGfx": CmdFirstGfx(r); break;
            case "gfxScene": CmdGfxScene(r); break;
            case "firstProp": CmdFirstProp(r); break;
            case "firstYpt": CmdFirstYpt(r); break;
            case "openByName": CmdOpenByName(r); break;
            case "save": CmdSave(r); break;
            case "encodeDds": CmdEncodeDds(r); break;
            case "decodeDds": CmdDecodeDds(r); break;
            case "extract": CmdExtract(r); break;
            case "extractMany": CmdExtractMany(r); break;
            case "exportXml": CmdExportXml(r); break;
            case "exportXmlMany": CmdExportXmlMany(r); break;
            case "extractAll": CmdExtractAll(r); break;
            case "createFolder": CmdCreateFolder(r); break;
            case "createRpf": CmdCreateRpf(r); break;
            case "createYtd": CmdCreateYtd(r); break;
            case "importFile": CmdImportFile(r); break;
            case "importPath": CmdImportPath(r); break;
            case "pathsOf": Send(new { type = "paths", reqId = r.Id, paths = r.DroppedPaths ?? Array.Empty<string>() }); break;
            case "inspectEpic": CmdInspectEpic(r); break;
            case "installEpic": CmdInstallEpic(r); break;
            case "createEpic": CmdCreateEpic(r); break;
            case "pickEpic": Send(new { type = "epicPicked", reqId = r.Id, path = _pickOpenPath?.Invoke("Epic RPF extension (*.epic)|*.epic") }); break;
            case "pickFile": Send(new { type = "filePicked", reqId = r.Id, path = _pickOpenPath?.Invoke("All files (*.*)|*.*") }); break;
            case "delete": CmdDelete(r); break;
            case "rename": CmdRename(r); break;
            case "convertEncryption": CmdConvertEncryption(r); break;
            case "undo": CmdUndo(r); break;
            case "modelPart": CmdModelPart(r); break;
            case "useTxd": CmdUseTxd(r); break;
            case "pickYtd": Send(new { type = "ytdPicked", reqId = r.Id, path = _pickOpenPath?.Invoke("Texture dictionary (*.ytd)|*.ytd") }); break;
            case "findAnims": CmdFindAnims(r); break;
            case "animClips": CmdAnimClips(r); break;
            case "animBake": CmdAnimBake(r); break;
            case "pedBody": CmdPedBody(r); break;
            case "pickYcd": Send(new { type = "ycdPicked", reqId = r.Id, path = _pickOpenPath?.Invoke("Clip dictionary (*.ycd)|*.ycd") }); break;
            case "setShaderParams": CmdSetShaderParams(r); break;
            case "texImage": CmdTexImage(r); break;
            case "replaceTexture": CmdReplaceTexture(r); break;
            case "deleteTexture": CmdDeleteTexture(r); break;
            case "openPath": CmdOpenPath(r); break;
            case "renameModel": CmdRenameModel(r); break;
            case "winMin": _windowAction?.Invoke("min"); break;
            case "winMax": _windowAction?.Invoke("max"); break;
            case "winClose": _windowAction?.Invoke("close"); break;
            case "winResize": _windowAction?.Invoke("resize:" + (r.Edge ?? "")); break;
            case "popout": CmdPopout(r); break;
            case "popoutData": CmdPopoutData(r); break;
            default: Send(new { type = "error", reqId = r.Id, message = $"unknown cmd '{r.Cmd}'" }); break;
        }
    }

    // ---- commands ---------------------------------------------------------

    private void CmdPickFolder(Req r)
    {
        string? path = _pickFolder();
        Send(new { type = "folderPicked", reqId = r.Id, path });
    }

    private void CmdDetect(Req r)
    {
        var installs = InstallDetector.Detect()
            .Select(i => new { path = i.Path, source = i.Source, gen9 = i.Gen9, label = i.Label })
            .ToArray();
        Send(new { type = "detected", reqId = r.Id, installs });
    }

    private void CmdMount(Req r)
    {
        string folder = r.Folder ?? "";
        int errs = 0;
        try
        {
            _ws = RpfWorkspace.Mount(folder, r.Gen9, error: _ => Interlocked.Increment(ref errs));
        }
        catch (Exception ex)
        {
            Send(new { type = "mounted", reqId = r.Id, ok = false, error = ex.Message });
            return;
        }

        _nodes.Clear();
        _nextNodeId = 1;
        _resolver = new TextureResolver(_ws.Manager);
        IndexBaseArchives();

        _gtaFolder = folder;
        try { var n = new DirectoryInfo(folder).Name; _rootName = string.IsNullOrEmpty(n) ? "GTA V" : n; } catch { _rootName = "GTA V"; }
        _diskRoot = new DiskItem { Path = folder, IsDir = true };
        var roots = EnumerateChildren(_diskRoot);
        long files = _ws.AllRpfs.Sum(x => (long)(x.AllEntries?.Count(e => e is RpfFileEntry) ?? 0));

        _broadcast = Send;     // the mounting window relays fs-change events
        StartWatcher(folder);

        // Warm the NG encrypt tables in the background so writes into NG-encrypted
        // archives (most base rpfs) don't fail with "tables not loaded" — first run
        // computes + caches them, later runs load the cache in well under a second.
        _ = Task.Run(() => { try { NgEncrypt.Ensure(); } catch { } });

        Send(new
        {
            type = "mounted",
            reqId = r.Id,
            ok = true,
            folder,
            gen9 = r.Gen9,
            rootName = _rootName,
            archives = _ws.AllRpfs.Count,
            files,
            mountErrors = errs,
            roots,
        });
    }

    // Map every base (on-disk) archive by its full path so the filesystem listing
    // can link a .rpf file to its mounted archive.
    private void IndexBaseArchives()
    {
        _baseRpfByPath.Clear();
        if (_ws == null) return;
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.Parent != null) continue;
            try { _baseRpfByPath[rpf.GetPhysicalFilePath()] = rpf; } catch { }
        }
    }

    // ---- live file watching ----------------------------------------------

    private void StartWatcher(string folder)
    {
        try
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watchDebounce = new Timer(_ => FsRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            FileSystemEventHandler onChange = (_, e) => OnFsEvent(e.FullPath);
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (_, e) => { OnFsEvent(e.FullPath); OnFsEvent(e.OldFullPath); };
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* watching is best-effort */ }
    }

    private void OnFsEvent(string path)
    {
        if (IsSelfWrite(path)) return;                       // ignore our own writes
        if (path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) _watchRpfChanged = true;
        _watchDebounce?.Change(250, Timeout.Infinite);       // coalesce a burst into one refresh
    }

    private void FsRefresh()
    {
        bool remount = _watchRpfChanged;
        _watchRpfChanged = false;
        if (!remount) { _broadcast?.Invoke(new { type = "fschange", remount = false }); return; }

        // an archive changed on disk -> re-scan, reset node registry, hand back fresh roots
        object[]? roots = null;
        try
        {
            _gate.Wait();
            try
            {
                _ws = RpfWorkspace.Mount(_gtaFolder, _ws?.Gen9 ?? false);
                _resolver = new TextureResolver(_ws.Manager);
                _nodes.Clear(); _nextNodeId = 1;
                IndexBaseArchives();
                _diskRoot = new DiskItem { Path = _gtaFolder, IsDir = true };
                roots = EnumerateChildren(_diskRoot);
            }
            finally { _gate.Release(); }
        }
        catch { }
        _broadcast?.Invoke(new { type = "fschange", remount = true, roots });
    }

    private static void MarkSelfWrite(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_selfWrites) _selfWrites[path] = DateTime.UtcNow.Ticks;
    }

    // An imported/restored .rpf is invisible until the mount re-scans it (self-write
    // marking suppresses the watcher, so nothing else would pick it up) — without this
    // a dragged-in archive opens as a plain hex file instead of an archive.
    private static void RescanIfArchive(string name)
    {
        if (!name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) return;
        _watchRpfChanged = true;
        _watchDebounce?.Change(400, Timeout.Infinite);
    }

    private static bool IsSelfWrite(string path)
    {
        lock (_selfWrites)
        {
            if (_selfWrites.TryGetValue(path, out var t))
            {
                if ((DateTime.UtcNow.Ticks - t) < TimeSpan.FromSeconds(4).Ticks) return true;
                _selfWrites.Remove(path);
            }
        }
        return false;
    }

    private void CmdExpand(Req r)
    {
        if (r.Node == 0)   // the GTA V root folder (re-enumerated live, e.g. after an fs change)
        {
            Send(new { type = "children", reqId = r.Id, node = 0, nodes = _diskRoot != null ? EnumerateChildren(_diskRoot) : Array.Empty<object>() });
            return;
        }
        if (!_nodes.TryGetValue(r.Node, out var obj))
        {
            Send(new { type = "children", reqId = r.Id, node = r.Node, nodes = Array.Empty<object>() });
            return;
        }
        Send(new { type = "children", reqId = r.Id, node = r.Node, nodes = EnumerateChildren(obj) });
    }

    private void CmdOpen(Req r)
    {
        if (!_nodes.TryGetValue(r.Node, out var obj))
        {
            Send(new { type = "error", reqId = r.Id, message = "invalid node" });
            return;
        }

        if (obj is RpfFileEntry fe)
        {
            // Optional "Edit as XML" path (right-click): for the visual resource types
            // (ydr/ydd/yft/ytd/ypt) the default is their viewer, but the CodeWalker XML
            // round-trip is still available on demand.
            if (r.As == "xml") { OpenAsXml(r, fe); return; }
            // A malformed/modded resource must never dead-end: if its typed viewer fails
            // to parse, fall back to the hex view instead of just erroring out (the
            // openers send their message only on success, so a throw means nothing was
            // sent yet — the fallback is safe). Mirrors OpenDiskFile's behaviour.
            try
            {
                switch (FileTypes.Route(fe.Name))
                {
                    case ViewerKind.Model: OpenModel(r, fe); break;
                    case ViewerKind.Texture: OpenTexture(r, fe); break;
                    case ViewerKind.Gfx: OpenGfxCore(r, fe.Name, RpfWorkspace.Extract(fe), fe); break;
                    default:
                        if (ImageExts.Contains(ExtOf(fe.NameLower))) OpenImage(r, fe);
                        else OpenEditableOrHex(r, fe);
                        break;
                }
            }
            catch { try { OpenHexCore(r, fe.Name, RpfWorkspace.Extract(fe)); } catch (Exception ex) { Send(new { type = "error", reqId = r.Id, message = ex.Message }); } }
            return;
        }

        if (obj is DiskItem di && !di.IsDir) { OpenDiskFile(r, di); return; }
        Send(new { type = "error", reqId = r.Id, message = "invalid node" });
    }

    // Open a loose file on disk (outside any archive).
    private void OpenDiskFile(Req r, DiskItem di)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(di.Path); }
        catch (Exception ex) { Send(new { type = "error", reqId = r.Id, message = ex.Message }); return; }

        string name = Path.GetFileName(di.Path);
        string ext = ExtOf(name.ToLowerInvariant());
        try
        {
            switch (FileTypes.Route(name))
            {
                case ViewerKind.Model:
                    var dws = DrawableLoader.LoadLoose(bytes, name, out var owner);
                    TextureDictionary? ld = (owner as YptFile)?.PtfxList?.TextureDictionary;
                    SendModelData(r, name, di.Path, dws, ld, null, owner);
                    break;
                case ViewerKind.Texture:
                    SendTextures(r, name, RpfFile.GetResourceFile<YtdFile>(bytes)?.TextureDict, null, di.Path);
                    break;
                case ViewerKind.Gfx:
                    OpenGfxCore(r, name, bytes, null, di.Path);
                    break;
                default:
                    if (ImageExts.Contains(ext)) OpenImageCore(r, name, bytes);
                    else OpenEditableOrHexCore(r, name, bytes, null, di.Path);
                    break;
            }
        }
        catch { OpenHexCore(r, name, bytes); }
    }

    // Dev / deep-link helper: open the first .ydr in the mounted set. Used by the
    // auto-load hook to exercise the whole pipeline without manual navigation.
    private void CmdFirstModel(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        RpfFileEntry? firstAny = null;
        int probed = 0;
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !fe.NameLower.EndsWith(".ydr", StringComparison.Ordinal)) continue;
                firstAny ??= fe;
                if (probed++ > 400) break;
                try
                {
                    var ydr = _ws.Manager.GetFile<YdrFile>(fe);
                    var td = ydr?.Drawable?.ShaderGroup?.TextureDictionary?.Textures?.data_items;
                    if (td != null && td.Any(t => t?.Data?.FullData != null)) { OpenModel(r, fe); return; }
                }
                catch { }
            }
            if (probed > 400) break;
        }
        if (firstAny != null) { OpenModel(r, firstAny); return; }
        Send(new { type = "error", reqId = r.Id, message = "no .ydr found" });
    }

    private void CmdFirstMeta(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".meta", StringComparison.Ordinal))
                {
                    OpenEditableOrHex(r, fe);
                    return;
                }
        }
        Send(new { type = "error", reqId = r.Id, message = "no .meta found" });
    }

    private void CmdFirstYtd(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".ytd", StringComparison.Ordinal))
                {
                    OpenTexture(r, fe);
                    return;
                }
        }
        Send(new { type = "error", reqId = r.Id, message = "no .ytd found" });
    }

    private void CmdFirstGfx(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        RpfFileEntry? first = null, minimap = null;
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".gfx", StringComparison.Ordinal))
                {
                    first ??= fe;
                    if (fe.NameLower.Contains("minimap")) { minimap = fe; break; }
                }
            if (minimap != null) break;
        }
        var pick = minimap ?? first;
        if (pick != null) { OpenGfxCore(r, pick.Name, RpfWorkspace.Extract(pick), pick); return; }
        Send(new { type = "error", reqId = r.Id, message = "no .gfx found" });
    }

    // Find a model with a same-named sibling .ytd and NO embedded textures, so it
    // can only render textured if external resolution works.
    private void CmdFirstProp(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        int loaded = 0;
        RpfFileEntry? fallback = null;
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !fe.NameLower.EndsWith(".ydr", StringComparison.Ordinal)) continue;
                string baseLower = fe.NameLower.Substring(0, fe.NameLower.Length - 4);
                var sib = fe.Parent?.Files?.FirstOrDefault(f => f.NameLower == baseLower + ".ytd");
                if (sib == null) continue; // cheap skip, no load
                if (loaded++ > 600) break;
                try
                {
                    var emb = _ws.Manager.GetFile<YdrFile>(fe)?.Drawable?.ShaderGroup?.TextureDictionary?.Textures?.data_items;
                    fallback ??= fe;
                    if (emb == null || !emb.Any(t => t?.Data?.FullData != null)) { OpenModel(r, fe); return; }
                }
                catch { }
            }
            if (loaded > 600) break;
        }
        if (fallback != null) { OpenModel(r, fallback); return; }
        Send(new { type = "error", reqId = r.Id, message = "no prop with sibling .ytd found" });
    }

    // Dev helper: open the first entry with this exact file name (routed like a
    // normal double-click). Drives deep-links / automated viewer tests by name.
    private void CmdOpenByName(Req r)
    {
        if (_ws == null || string.IsNullOrEmpty(r.Name))
        { Send(new { type = "error", reqId = r.Id, message = "not mounted / no name" }); return; }
        string nl = r.Name.ToLowerInvariant();
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && fe.NameLower == nl)
                { r.Node = Register(fe); CmdOpen(r); return; }
        }
        Send(new { type = "error", reqId = r.Id, message = "entry not found: " + r.Name });
    }

    // Dev helper: open core.ypt if present (has both drawables and textures),
    // otherwise the first .ypt that carries drawables.
    private void CmdFirstYpt(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        RpfFileEntry? fallback = null;
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !fe.NameLower.EndsWith(".ypt", StringComparison.Ordinal)) continue;
                fallback ??= fe;
                if (fe.NameLower == "core.ypt")
                {
                    int id = Register(fe);
                    if (r.As == "xml") { r.Node = id; OpenAsXml(r, fe); } else OpenModel(r, fe);
                    return;
                }
            }
        }
        if (fallback != null)
        {
            int id = Register(fallback);
            if (r.As == "xml") { r.Node = id; OpenAsXml(r, fallback); } else OpenModel(r, fallback);
            return;
        }
        Send(new { type = "error", reqId = r.Id, message = "no .ypt found" });
    }

    // Tear a tab off into a new OS window. The front-end serializes the tab's
    // already-rendered viewer state; we stash it and open a window that fetches it.
    // Open a loose file given by absolute path (file-association double-click). Registers
    // a disk node and returns it so the front-end opens it through the normal viewer flow
    // — works without a mounted game; the file is editable (saves back to disk).
    private void CmdOpenPath(Req r)
    {
        string path = r.Path ?? "";
        if (!File.Exists(path)) { Send(new { type = "pathNode", reqId = r.Id, ok = false, message = "file not found" }); return; }
        var di = new DiskItem { Path = path, IsDir = false };
        int id = Register(di);
        string name = Path.GetFileName(path);
        Send(new { type = "pathNode", reqId = r.Id, ok = true, node = id, name, viewer = FileTypes.Route(name).ToString().ToLowerInvariant() });
    }

    private void CmdPopout(Req r)
    {
        if (_openPopout == null) { Send(new { type = "popout", reqId = r.Id, ok = false }); return; }
        int id = _popoutSeq++;
        _popouts[id] = r.Payload ?? "";
        _openPopout(id, r.Title ?? "Epic RPF");
        Send(new { type = "popout", reqId = r.Id, ok = true, id });
    }

    private void CmdPopoutData(Req r)
    {
        _popouts.TryGetValue(r.Node, out var payload);
        _popouts.Remove(r.Node);
        Send(new { type = "popoutData", reqId = r.Id, payload });
    }

    private void CmdExtract(Req r)
    {
        if (!_nodes.TryGetValue(r.Node, out var obj))
        { Send(new { type = "error", reqId = r.Id, message = "invalid node" }); return; }

        string srcName;
        Func<byte[]> getBytes;
        if (obj is RpfFileEntry fe) { srcName = fe.Name; getBytes = () => RpfWorkspace.ExtractForSave(fe); }
        else if (obj is DiskItem di && !di.IsDir) { srcName = Path.GetFileName(di.Path); getBytes = () => File.ReadAllBytes(di.Path); }
        else { Send(new { type = "error", reqId = r.Id, message = "invalid node" }); return; }

        string? path = _pickSavePath(srcName);
        if (path == null) { Send(new { type = "extracted", reqId = r.Id, ok = false, canceled = true }); return; }
        byte[] bytes = getBytes();
        File.WriteAllBytes(path, bytes);
        Send(new { type = "extracted", reqId = r.Id, ok = true, path, size = bytes.Length });
    }

    // Extract several selected files at once into a single chosen folder. Each entry
    // is written as a valid standalone file (RSC7 header re-added for resources).
    private void CmdExtractMany(Req r)
    {
        var ids = r.Nodes ?? Array.Empty<int>();
        if (ids.Length == 0) { Send(new { type = "extractedMany", reqId = r.Id, ok = false, message = "nothing selected" }); return; }
        string? outDir = _pickFolder();
        if (outDir == null) { Send(new { type = "extractedMany", reqId = r.Id, ok = false, canceled = true }); return; }

        int count = 0, failed = 0; long bytes = 0;
        foreach (var id in ids)
        {
            if (!_nodes.TryGetValue(id, out var obj)) { failed++; continue; }
            try
            {
                string name; byte[] data;
                if (obj is RpfFileEntry fe) { name = fe.Name; data = RpfWorkspace.ExtractForSave(fe); }
                else if (obj is DiskItem di && !di.IsDir) { name = Path.GetFileName(di.Path); data = File.ReadAllBytes(di.Path); }
                else if (obj is RpfDirectoryEntry rd) { var sub = Path.Combine(outDir, SafeName(rd.Name)); int c = 0; long b = 0; ExtractDir(rd, sub, ref c, ref b); count += c; bytes += b; continue; }
                else { failed++; continue; }
                File.WriteAllBytes(Path.Combine(outDir, SafeName(name)), data);
                count++; bytes += data.Length;
            }
            catch { failed++; }
        }
        Send(new { type = "extractedMany", reqId = r.Id, ok = count > 0, path = outDir, count, failed, bytes });
    }

    // Convert an entry to CodeWalker XML, writing any embedded textures as .dds into
    // <baseDir>\<shortname>\ so the export round-trips. Returns the xml + its name (e.g.
    // carcols.ymt.pso.xml). For an already-text file the text passes through as
    // <name>.xml. Returns null xml only if it's binary with no XML mapping.
    // Convert an archive entry OR a loose disk file to CodeWalker XML.
    // <paramref name="err"/> receives the real reason on failure (so the UI can show it
    // instead of a generic "export failed").
    private static (string? xml, string xmlName) ToXml(object node, string baseDir, out string? err)
    {
        err = null;
        string name; byte[] bytes;
        RpfFileEntry entry;
        try
        {
            if (node is RpfFileEntry fe) { entry = fe; name = fe.Name; bytes = RpfWorkspace.Extract(fe); }
            else if (node is DiskItem di && !di.IsDir && File.Exists(di.Path))
            {
                name = Path.GetFileName(di.Path);
                // Build a resource entry from the loose RSC7 file so MetaXml can convert it
                // (the same synthetic-entry path GetResourceFile uses), or a binary entry.
                byte[] raw = File.ReadAllBytes(di.Path);
                if (raw.Length > 4 && BitConverter.ToUInt32(raw, 0) == 0x37435352u)   // 'RSC7'
                {
                    byte[] d = raw;
                    var re = RpfFile.CreateResourceFileEntry(ref d, 0u);
                    bytes = ResourceBuilder.Decompress(d);
                    re.Name = name; re.NameLower = name.ToLowerInvariant();
                    entry = re;
                }
                else { entry = new RpfBinaryFileEntry { Name = name, NameLower = name.ToLowerInvariant(), FileSize = (uint)raw.Length, FileUncompressedSize = (uint)raw.Length }; bytes = raw; }
            }
            else { err = "select a file"; return (null, "file.xml"); }
        }
        catch (Exception ex) { err = "read failed: " + ex.Message; return (null, "file.xml"); }

        try
        {
            string xml = MetaXml.GetXml(entry, bytes, out string xmlName, baseDir);
            if (!string.IsNullOrEmpty(xml) && xml.Length > 64) return (xml, xmlName);
        }
        catch (Exception ex) { err = ex.Message; }
        if (LooksText(bytes))
        {
            string nm = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? name : name + ".xml";
            return (DecodeText(bytes), nm);
        }
        if (err == null) err = "this file type has no XML conversion — extract it normally";
        return (null, name + ".xml");
    }

    // Export ONE file as CodeWalker XML via a Save dialog (embedded textures saved into
    // a sibling folder next to the chosen .xml so a later reimport works).
    private void CmdExportXml(Req r)
    {
        var obj = ResolveNode(r.Node, r.Path);
        if (obj == null)
        { Send(new { type = "exportedXml", reqId = r.Id, ok = false, message = "select a file" }); return; }
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "EpicRpf_xmlexport", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmp);
            var (xml, xmlName) = ToXml(obj, tmp, out var convErr);
            if (xml == null) { Send(new { type = "exportedXml", reqId = r.Id, ok = false, message = convErr ?? "could not convert to XML" }); return; }

            string? path = _pickSavePath(xmlName);
            if (path == null) { Send(new { type = "exportedXml", reqId = r.Id, ok = false, canceled = true }); return; }
            File.WriteAllText(path, xml, new UTF8Encoding(false));

            // copy the texture folder (if GetXml produced one) next to the .xml
            string shortName = Path.GetFileNameWithoutExtension(xmlName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? xmlName[..^4] : xmlName);
            string ddsSrc = Path.Combine(tmp, shortName);
            if (Directory.Exists(ddsSrc))
            {
                string ddsDst = Path.Combine(Path.GetDirectoryName(path)!, shortName);
                CopyDir(ddsSrc, ddsDst);
            }
            try { Directory.Delete(tmp, true); } catch { }
            Send(new { type = "exportedXml", reqId = r.Id, ok = true, path });
        }
        catch (Exception ex) { Send(new { type = "exportedXml", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Export several files as XML into one chosen folder (textures land in per-file subfolders).
    private void CmdExportXmlMany(Req r)
    {
        var ids = r.Nodes ?? Array.Empty<int>();
        if (ids.Length == 0) { Send(new { type = "exportedXmlMany", reqId = r.Id, ok = false, message = "nothing selected" }); return; }
        string? outDir = _pickFolder();
        if (outDir == null) { Send(new { type = "exportedXmlMany", reqId = r.Id, ok = false, canceled = true }); return; }
        int count = 0, failed = 0;
        string? lastErr = null;
        for (int k = 0; k < ids.Length; k++)
        {
            int id = ids[k];
            string? p = r.Paths != null && k < r.Paths.Length ? r.Paths[k] : null;
            var obj = ResolveNode(id, p);
            if (obj == null) { failed++; lastErr = "item no longer available"; continue; }
            try
            {
                var (xml, xmlName) = ToXml(obj, outDir, out var convErr);
                if (xml == null) { failed++; lastErr = convErr ?? lastErr; continue; }
                File.WriteAllText(Path.Combine(outDir, SafeName(xmlName)), xml, new UTF8Encoding(false));
                count++;
            }
            catch (Exception ex) { failed++; lastErr = ex.Message; }
        }
        Send(new { type = "exportedXmlMany", reqId = r.Id, ok = count > 0, path = outDir, count, failed, message = lastErr });
    }

    // ---- openers ----------------------------------------------------------

    private void OpenModel(Req r, RpfFileEntry fe)
    {
        var drawables = DrawableLoader.Load(_ws!.Manager, fe, out var owner);

        // A .ypt keeps its textures in the particle list's own dictionary (not inside
        // each drawable's shader group), so resolve diffuse textures against it and
        // surface the dictionary for the viewer's texture strip.
        TextureDictionary? localDict = (owner as YptFile)?.PtfxList?.TextureDictionary;
        SendModelData(r, fe.Name, fe.Path, drawables, localDict, fe, owner);
    }

    private void SendModelData(Req r, string name, string path,
        List<DrawableLoader.NamedDrawable> drawables, TextureDictionary? localDict, RpfFileEntry? modelEntry,
        object? ownerFile = null)
    {
        if (drawables.Count == 0 && localDict == null)
        {
            Send(new { type = "error", reqId = r.Id, message = "no drawable found in " + name });
            return;
        }

        // Cache the loaded drawables/dict so other parts and textures load on demand.
        // For a loose file (no archive entry) keep the disk path so texture edits can save.
        int cacheKey = r.Node > 0 ? r.Node : _synthSeq--;
        CacheModel(cacheKey, new ModelCache { Drawables = drawables, LocalDict = localDict, Entry = modelEntry, EntryPath = modelEntry?.Path ?? "", File = name, DiskPath = modelEntry == null ? path : "", OwnerFile = ownerFile });

        // Only decode the first drawable now (the viewer shows one at a time); the
        // rest are placeholders loaded when selected. Textures stream in lazily too.
        string fileLower = name.ToLowerInvariant();
        var parts = new List<object>();
        object stats = new { geometryCount = 0, rendered = 0, skipped = 0, skipSamples = Array.Empty<string>() };
        for (int i = 0; i < drawables.Count; i++)
        {
            if (i == 0) { parts.Add(BuildPart(drawables[i], modelEntry, localDict, fileLower, out stats, null, ownerFile)); }
            else parts.Add(LazyPart(drawables[i], fileLower));
        }

        Send(new
        {
            type = "model",
            reqId = r.Id,
            node = cacheKey,
            file = fileLower,
            name,
            path,
            parts,
            textures = TextureMeta(localDict),
            stats,
        });
    }

    private object LazyPart(DrawableLoader.NamedDrawable nd, string fileLower)
    {
        string hashHex = nd.Hash.ToString("X8");
        return new { name = DisplayName(fileLower, hashHex, nd.Name), hash = hashHex, lazy = true, materials = Array.Empty<object>(), lods = Array.Empty<object>() };
    }

    // Decode one drawable into a full part DTO (geometry + its diffuse textures).
    private object BuildPart(DrawableLoader.NamedDrawable nd, RpfFileEntry? modelEntry, TextureDictionary? localDict, string fileLower, out object stats,
        List<TextureDictionary>? extraDicts = null, object? ownerFile = null)
    {
        EnsureShaderNames();   // BEFORE Decode — material names resolve there
        var model = GeometryDecoder.Decode(nd.Drawable, nd.Name);
        stats = new { geometryCount = model.GeometryCount, rendered = model.RenderedCount, skipped = model.SkippedReasons.Count, skipSamples = model.SkippedReasons.Take(5).ToArray() };
        var dto = (Dictionary<string, object?>)BuildModelDto(model, nd.Drawable, modelEntry, localDict, extraDicts);
        string hashHex = nd.Hash.ToString("X8");
        dto["hash"] = hashHex;
        dto["lazy"] = false;
        dto["name"] = DisplayName(fileLower, hashHex, nd.Name);

        // Fragment children (wheels, breakable pieces, child extras): geometry that
        // lives OUTSIDE the main drawable, placed at its bone in the rig.
        if (ownerFile is YftFile yft && ReferenceEquals(yft.Fragment?.Drawable, nd.Drawable))
            dto["fragChildren"] = FragChildrenDto(yft, nd.Drawable, modelEntry, localDict, extraDicts);
        return dto;
    }

    // Children of PhysicsLOD1 that carry their own geometry (e.g. vehicle wheels —
    // ONE wheel drawable instanced at every wheel_* bone; extra_* child parts).
    private object? FragChildrenDto(YftFile yft, DrawableBase main, RpfFileEntry? modelEntry, TextureDictionary? localDict, List<TextureDictionary>? extras)
    {
        try
        {
            var kids = yft.Fragment?.PhysicsLODGroup?.PhysicsLOD1?.Children?.data_items;
            if (kids == null) return null;
            var bones = main.Skeleton?.Bones?.Items;
            int[] wheelTags = bones?.Where(b => b?.Name != null && b.Name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase))
                                    .Select(b => (int)b.Tag).ToArray() ?? Array.Empty<int>();
            bool wheelPlaced = false;
            var list = new List<object>();
            foreach (var c in kids)
            {
                var d1 = c?.Drawable1;
                if (c == null || d1?.DrawableModels?.High == null || d1.DrawableModels.High.Length == 0) continue;
                var model = GeometryDecoder.Decode(d1, c.GroupName ?? "child");
                if (model.Lods.Count == 0) continue;
                string group = c.GroupName ?? ("child_" + c.BoneTag);
                bool isWheel = group.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase);
                int[] inst = isWheel && !wheelPlaced && wheelTags.Length > 0 ? wheelTags : new[] { (int)c.BoneTag };
                if (isWheel) wheelPlaced = true;
                var cdto = (Dictionary<string, object?>)BuildModelDto(model, d1, modelEntry, localDict, extras);
                list.Add(new
                {
                    group,
                    boneTag = (int)c.BoneTag,
                    inst,
                    extra = group.StartsWith("extra_", StringComparison.OrdinalIgnoreCase),
                    materials = cdto["materials"],
                    lods = cdto["lods"],
                });
            }
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private void CmdModelPart(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc) || r.Index < 0 || r.Index >= mc.Drawables.Count)
        { Send(new { type = "modelPart", reqId = r.Id, ok = false }); return; }
        var part = BuildPart(mc.Drawables[r.Index], mc.Entry, mc.LocalDict, mc.File.ToLowerInvariant(), out var stats, mc.ExtraDicts, mc.OwnerFile);
        Send(new { type = "modelPart", reqId = r.Id, ok = true, index = r.Index, part, stats });
    }

    // ---- model details: user txd sources + shader parameter editing -------

    // Add a user-picked .ytd (archive node or loose disk path) as a texture source
    // for an open model. Picked dicts take priority over the automatic resolver,
    // so the user can fix any unresolved/wrong texture by pointing at the right txd.
    private void CmdUseTxd(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc))
        { Send(new { type = "txdUsed", reqId = r.Id, ok = false, message = "model not open" }); return; }
        try
        {
            TextureDictionary? dict = null;
            string src = "";
            if (!string.IsNullOrEmpty(r.Path) && File.Exists(r.Path))
            {
                dict = RpfFile.GetResourceFile<YtdFile>(File.ReadAllBytes(r.Path))?.TextureDict;
                src = Path.GetFileName(r.Path);
            }
            else if (r.Rid != 0 && _searchResults.TryGetValue(r.Rid, out var sfe)
                     && sfe.NameLower.EndsWith(".ytd", StringComparison.Ordinal))
            {
                // From the picker's search list. Search rids are a SEPARATE id space
                // from node ids (they'd collide), hence the dedicated field.
                dict = _ws?.Manager.GetFile<YtdFile>(sfe)?.TextureDict; src = sfe.Name;
            }
            else if (r.Ytd != 0 && _nodes.TryGetValue(r.Ytd, out var obj))
            {
                if (obj is RpfFileEntry fe && fe.NameLower.EndsWith(".ytd", StringComparison.Ordinal))
                { dict = _ws?.Manager.GetFile<YtdFile>(fe)?.TextureDict; src = fe.Name; }
                else if (obj is DiskItem di && !di.IsDir)
                { dict = RpfFile.GetResourceFile<YtdFile>(File.ReadAllBytes(di.Path))?.TextureDict; src = Path.GetFileName(di.Path); }
            }
            int count = dict?.Textures?.data_items?.Length ?? 0;
            if (dict == null || count == 0)
            { Send(new { type = "txdUsed", reqId = r.Id, ok = false, message = "no textures found in that .ytd" }); return; }
            mc.ExtraDicts.Insert(0, dict);   // newest picks win
            Send(new { type = "txdUsed", reqId = r.Id, ok = true, name = src, count });
        }
        catch (Exception ex) { Send(new { type = "txdUsed", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Edit shader value parameters (e.g. emissiveMultiple, bumpiness) on one material.
    // Always updates the in-memory drawable (so re-decodes see it); with Save=true the
    // owning ydr/ydd/yft/ypt is rebuilt and written back to its archive / disk file.
    private void CmdSetShaderParams(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc) || r.Index < 0 || r.Index >= mc.Drawables.Count)
        { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "model not open" }); return; }
        var edits = r.Params ?? Array.Empty<ParamEdit>();
        if (edits.Length == 0)
        { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "no parameters supplied" }); return; }

        try
        {
            var shaders = mc.Drawables[r.Index].Drawable?.ShaderGroup?.Shaders?.data_items;
            if (shaders == null || r.Mat < 0 || r.Mat >= shaders.Length)
            { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "material not found" }); return; }
            var ps = shaders[r.Mat]?.ParametersList?.Parameters;
            var hs = shaders[r.Mat]?.ParametersList?.Hashes;
            if (ps == null || hs == null)
            { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "shader has no parameters" }); return; }

            int applied = 0;
            foreach (var e in edits)
            {
                for (int i = 0; i < ps.Length && i < hs.Length; i++)
                {
                    if (!string.Equals(ParamName(hs[i]), e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    var v = new SharpDX.Vector4(
                        e.Values.Length > 0 ? e.Values[0] : 0,
                        e.Values.Length > 1 ? e.Values[1] : 0,
                        e.Values.Length > 2 ? e.Values[2] : 0,
                        e.Values.Length > 3 ? e.Values[3] : 0);
                    if (ps[i].Data is SharpDX.Vector4) { ps[i].Data = v; applied++; }
                    else if (ps[i].Data is SharpDX.Vector4[] arr && arr.Length > 0) { arr[0] = v; applied++; }
                    break;
                }
            }
            if (applied == 0)
            { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "no matching editable parameter" }); return; }

            if (!r.Save)
            { Send(new { type = "shaderParams", reqId = r.Id, ok = true, applied, saved = false }); return; }

            // Save-back: rebuild the owning resource and write it where it came from.
            byte[]? data = mc.OwnerFile switch
            {
                YdrFile y => y.Save(),
                YddFile y => y.Save(),
                YftFile y => y.Save(),
                YptFile y => y.Save(),
                _ => null,
            };
            if (data == null)
            { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "saving isn't supported for this file type" }); return; }

            // Re-resolve the entry against the live mount (same guard as texture replace).
            var fe = mc.Entry;
            if (fe != null && _ws != null && !string.IsNullOrEmpty(mc.EntryPath)
                && _ws.Manager.GetEntry(mc.EntryPath) is RpfFileEntry fresh) { fe = fresh; mc.Entry = fresh; }

            if (fe != null) { if (PrepareArchiveWrite(fe.File) is string werr2) throw new Exception(werr2); MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
            else if (!string.IsNullOrEmpty(mc.DiskPath)) { MarkSelfWrite(mc.DiskPath); File.WriteAllBytes(mc.DiskPath, data); }
            else { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = "no writable destination" }); return; }

            Send(new { type = "shaderParams", reqId = r.Id, ok = true, applied, saved = true, size = data.Length });
        }
        catch (Exception ex) { Send(new { type = "shaderParams", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // ---- animations (.ycd) -------------------------------------------------

    // Rank a vpath for the animation override rules: anything in an "epic" folder
    // wins, then update.rpf, then everything else (x64*/dlc packs).
    private static int RankAnimPath(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        if (p.Contains(@"\epic\") || p.StartsWith(@"epic\")) return 0;
        if (p.StartsWith(@"update\update.rpf")) return 1;
        return 2;
    }

    // Weapon-model → clip-dictionary hashes, resolved through the game's metas:
    // weapon*.meta (<Model> → <Name>), weaponanimations*.meta (Item key=WEAPON_X →
    // children named *ClipSet*), clip_sets* (clipset → clipDictionaryName). Keys and
    // dictionary names in clip_sets are often unresolved hashes ("hash_XXXXXXXX"),
    // so the whole pipeline matches by Jenkins hash. Cached per mount.
    private static Dictionary<string, string>? _weaponByModel;     // model lower -> WEAPON_NAME
    private static List<XmlDocument>? _weaponAnimDocs;
    private static Dictionary<uint, uint>? _clipSetToDict;         // jenk(clipset) -> jenk(dictionary)
    private static readonly object _animMetaGate = new();

    // "name" or "hash_XXXXXXXX" -> Jenkins hash (plain names hashed lowercased).
    private static uint AnimHashOf(string s)
    {
        s = s.Trim();
        if (s.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(s.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out uint h))
            return h;
        return JenkHash.GenHash(s.ToLowerInvariant());
    }

    private void EnsureWeaponAnimIndex()
    {
        if (_weaponByModel != null || _ws == null) return;
        lock (_animMetaGate)
        {
            if (_weaponByModel != null) return;
            var byModel = new Dictionary<string, (int rank, string name)>(OIC);
            var animDocs = new List<XmlDocument>();
            var setMap = new Dictionary<uint, uint>();
            foreach (var rpf in _ws.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe) continue;
                    string nl = fe.NameLower;
                    bool isWeaponMeta = (nl.StartsWith("weapon") || nl == "weapons.meta") && nl.EndsWith(".meta") && !nl.Contains("animation");
                    bool isWeaponAnim = nl.Contains("weaponanimation") && nl.EndsWith(".meta");
                    bool isClipSets = nl.StartsWith("clip_sets");
                    if (!isWeaponMeta && !isWeaponAnim && !isClipSets) continue;
                    string? xml = null;
                    try { xml = GameFs.ReadEditable(_ws.Manager, _gtaFolder, fe.Path, out _); } catch { }
                    if (string.IsNullOrEmpty(xml)) continue;
                    try
                    {
                        var doc = new XmlDocument();
                        doc.LoadXml(xml);
                        if (isWeaponAnim) { animDocs.Add(doc); continue; }
                        if (isClipSets)
                        {
                            foreach (XmlNode it in doc.SelectNodes("//Item[@key]") ?? (XmlNodeList)new XmlDocument().ChildNodes)
                            {
                                string key = (it as XmlElement)?.GetAttribute("key") ?? "";
                                var dictN = it.SelectSingleNode("clipDictionaryName")?.InnerText;
                                if (key.Length > 0 && !string.IsNullOrWhiteSpace(dictN))
                                    setMap[AnimHashOf(key)] = AnimHashOf(dictN!);
                            }
                            continue;
                        }
                        // weapon archetype meta: <Name>WEAPON_X</Name> + <Model>w_xx</Model> per Item
                        int rank = RankAnimPath(fe.Path);
                        foreach (XmlNode it in doc.SelectNodes("//Item[Name and Model]") ?? (XmlNodeList)new XmlDocument().ChildNodes)
                        {
                            string wn = it.SelectSingleNode("Name")?.InnerText ?? "";
                            string mdl = it.SelectSingleNode("Model")?.InnerText ?? "";
                            if (!wn.StartsWith("WEAPON_", StringComparison.OrdinalIgnoreCase) || mdl.Length == 0) continue;
                            string key = mdl.ToLowerInvariant();
                            if (!byModel.TryGetValue(key, out var cur) || rank < cur.rank) byModel[key] = (rank, wn);
                        }
                    }
                    catch { }
                }
            }
            _weaponAnimDocs = animDocs;
            _clipSetToDict = setMap;
            _weaponByModel = byModel.ToDictionary(kv => kv.Key, kv => kv.Value.name, OIC);
        }
    }

    // Clip-dictionary HASHES linked to a weapon model (w_ar_carbinerifle →
    // WEAPON_CARBINERIFLE → every non-empty *ClipSet* value → dictionary hash).
    private HashSet<uint> WeaponClipDictHashes(string modelBaseLower)
    {
        var dicts = new HashSet<uint>();
        try
        {
            EnsureWeaponAnimIndex();
            string? weapon = null;
            if (_weaponByModel != null) _weaponByModel.TryGetValue(modelBaseLower, out weapon);
            if (weapon == null && modelBaseLower.StartsWith("w_", StringComparison.Ordinal))
            {
                // naming convention fallback: w_ar_carbinerifle -> WEAPON_CARBINERIFLE
                int us = modelBaseLower.IndexOf('_', 2);
                if (us > 0 && us + 1 < modelBaseLower.Length) weapon = "WEAPON_" + modelBaseLower[(us + 1)..].ToUpperInvariant();
            }
            if (weapon == null) return dicts;
            foreach (var doc in _weaponAnimDocs ?? new List<XmlDocument>())
                foreach (XmlNode it in doc.SelectNodes($"//Item[@key='{weapon}']") ?? (XmlNodeList)new XmlDocument().ChildNodes)
                    foreach (XmlNode n in it.ChildNodes)
                        if (n.Name.Contains("ClipSet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(n.InnerText))
                        {
                            uint cs = AnimHashOf(n.InnerText);
                            dicts.Add(cs);   // many clipsets ARE their dictionary
                            if (_clipSetToDict != null && _clipSetToDict.TryGetValue(cs, out var d)) dicts.Add(d);
                        }
        }
        catch { }
        return dicts;
    }

    // Find clip dictionaries for the open model: weapon meta chain + name match,
    // ranked epic folder > update.rpf > rest; same-named dicts keep only the winner.
    private void CmdFindAnims(Req r)
    {
        if (_ws == null || !_modelCache.TryGetValue(r.Node, out var mc))
        { Send(new { type = "anims", reqId = r.Id, ok = false, message = "model not open" }); return; }

        string baseName = Path.GetFileNameWithoutExtension(mc.File).ToLowerInvariant();
        foreach (var suf in new[] { "+hi", "_hi" })
            if (baseName.EndsWith(suf, StringComparison.Ordinal)) baseName = baseName[..^suf.Length];

        var dictHashes = WeaponClipDictHashes(baseName);
        bool NameMatch(string shortName) =>
            dictHashes.Contains(JenkHash.GenHash(shortName))
            || (baseName.Length >= 4 && shortName.Contains(baseName, StringComparison.Ordinal));

        var best = new Dictionary<string, (int rank, RpfFileEntry? fe, string? disk)>(OIC);
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !fe.NameLower.EndsWith(".ycd", StringComparison.Ordinal)) continue;
                if (!NameMatch(fe.NameLower[..^4])) continue;
                int rank = RankAnimPath(fe.Path);
                if (!best.TryGetValue(fe.NameLower, out var cur) || rank < cur.rank) best[fe.NameLower] = (rank, fe, null);
            }
        }
        // loose .ycd files inside any "epic" folder beat everything
        try
        {
            foreach (var f in Directory.EnumerateFiles(_gtaFolder, "*.ycd", SearchOption.AllDirectories))
            {
                if (!f.ToLowerInvariant().Contains(@"\epic\")) continue;
                string nm = Path.GetFileName(f).ToLowerInvariant();
                if (NameMatch(nm[..^4])) best[nm] = (-1, null, f);
            }
        }
        catch { }

        var items = best.OrderBy(kv => kv.Value.rank).ThenBy(kv => kv.Key, OIC).Take(60).Select(kv => new
        {
            name = kv.Key,
            path = kv.Value.disk ?? kv.Value.fe!.Path,
            node = kv.Value.fe != null ? Register(kv.Value.fe) : 0,
            disk = kv.Value.disk,
            source = kv.Value.rank <= 0 ? "epic" : kv.Value.rank == 1 ? "update" : "base",
        }).ToArray();
        Send(new { type = "anims", reqId = r.Id, ok = true, items, weapon = dictHashes.Count > 0 });
    }

    // A full FREEMODE MALE character: the full 128-bone skeleton (every SKEL_* bone any
    // ped clip can animate — so run/walk/melee/aim all play) + all body component meshes
    // (head, torso+arms+hands=uppr, legs=lowr, feet), skinned. The weapon attaches to the
    // right hand. Cached after first build.
    private static (object skeleton, object[] meshes, int handTag)? _pedBodyDto;
    private void CmdPedBody(Req r)
    {
        try
        {
            if (_pedBodyDto == null)
            {
                if (_ws == null) { Send(new { type = "pedBody", reqId = r.Id, ok = false, message = "not mounted" }); return; }
                var all = _ws.AllRpfs.SelectMany(rp => rp.AllEntries ?? Enumerable.Empty<RpfEntry>()).OfType<RpfFileEntry>();
                var pedYft = all.FirstOrDefault(e => e.NameLower == "mp_m_freemode_01.yft");
                var drawable = pedYft != null ? _ws.Manager.GetFile<YftFile>(pedYft)?.Fragment?.Drawable : null;
                var skel = drawable?.Skeleton;
                if (skel?.Bones?.Items == null) { Send(new { type = "pedBody", reqId = r.Id, ok = false, message = "ped skeleton not found" }); return; }

                // each body component (variation _000), skinned to the shared skeleton
                RpfFileEntry? Comp(string prefix) =>
                    all.FirstOrDefault(e => e.NameLower.StartsWith(prefix) && e.NameLower.EndsWith(".ydd")
                        && e.Path.ToLowerInvariant().Contains("mp_m_freemode_01"))
                    ?? all.FirstOrDefault(e => e.NameLower.StartsWith(prefix) && e.NameLower.EndsWith(".ydd"));

                var meshDtos = new List<object>();
                foreach (var prefix in new[] { "head_000", "uppr_000", "lowr_000", "feet_000" })
                {
                    var comp = Comp(prefix);
                    if (comp == null) continue;
                    var dws = _ws.Manager.GetFile<YddFile>(comp)?.DrawableDict?.Drawables?.data_items;
                    var cd = dws?.FirstOrDefault(d => d?.DrawableModels?.High?.Length > 0);
                    if (cd == null) continue;
                    var md = GeometryDecoder.Decode(cd, comp.Name);
                    foreach (var l in md.Lods)
                        if (l.Level == "High")
                            foreach (var mesh in l.Meshes) meshDtos.Add(MeshDto(mesh));
                }
                // GTA attaches weapons to the PHYSICS hand bone (PH_R_Hand) — it sits in
                // the palm (~5cm past the wrist toward the fingers), which is exactly where
                // the grip belongs. Fall back to IK_R_Hand, then the wrist.
                Bone? FindBone(params string[] ns) => skel.Bones.Items.FirstOrDefault(b => b?.Name != null && ns.Any(n => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
                var hand = FindBone("PH_R_Hand", "IK_R_Hand", "SKEL_R_Hand");
                _pedBodyDto = (SkeletonDto(drawable)!, meshDtos.ToArray(), hand != null ? (int)hand.Tag : 28422);
            }
            var dto = _pedBodyDto.Value;
            Send(new { type = "pedBody", reqId = r.Id, ok = true, skeleton = dto.skeleton, meshes = dto.meshes, handTag = dto.handTag });
        }
        catch (Exception ex) { Send(new { type = "pedBody", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private YcdFile? LoadYcd(Req r, out string name)
    {
        name = "";
        var obj = ResolveNode(r.Ycd, r.Path);
        if (obj is RpfFileEntry fe && fe.NameLower.EndsWith(".ycd", StringComparison.Ordinal))
        { name = fe.Name; return _ws?.Manager.GetFile<YcdFile>(fe); }
        string? p = (obj as DiskItem)?.Path ?? r.Path;
        if (!string.IsNullOrEmpty(p) && File.Exists(p) && p.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase))
        { name = Path.GetFileName(p); return RpfFile.GetResourceFile<YcdFile>(File.ReadAllBytes(p)); }
        return null;
    }

    private void CmdAnimClips(Req r)
    {
        try
        {
            var ycd = LoadYcd(r, out string name);
            if (ycd?.ClipMapEntries == null)
            { Send(new { type = "animClips", reqId = r.Id, ok = false, message = "could not read the clip dictionary" }); return; }
            var clips = new List<(string hash, string name, float dur)>();
            foreach (var cme in ycd.ClipMapEntries)
            {
                var clip = cme?.Clip;
                if (clip == null) continue;
                float dur = clip switch
                {
                    ClipAnimation ca when ca.Rate > 0 => (ca.EndTime - ca.StartTime) / ca.Rate,
                    ClipAnimationList cl => cl.Duration,
                    _ => 0f,
                };
                string cn = clip.ShortName;
                if (string.IsNullOrEmpty(cn)) cn = clip.Name ?? ((uint)cme!.Hash).ToString("X8");
                clips.Add((((uint)cme!.Hash).ToString("X8"), cn, dur));
            }
            Send(new
            {
                type = "animClips", reqId = r.Id, ok = true, name,
                clips = clips.OrderBy(c => c.name, OIC).Select(c => new { hash = c.hash, name = c.name, duration = c.dur }).ToArray(),
            });
        }
        catch (Exception ex) { Send(new { type = "animClips", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Bake a clip into per-bone keyframe tracks the renderer can play directly.
    // A clip is usually a LIST of animations (e.g. a root-mover anim + the real body
    // anim, or upper/lower-body splits) — each covers a DIFFERENT subset of bones, so
    // we bake them ALL and merge, or whole-body bones go missing. Each track carries
    // its own fps (animations in one clip can have different rates/lengths).
    private void CmdAnimBake(Req r)
    {
        try
        {
            var ycd = LoadYcd(r, out string name);
            var cme = ycd?.ClipMapEntries?.FirstOrDefault(c =>
                c != null && ((uint)c.Hash).ToString("X8").Equals(r.Clip, StringComparison.OrdinalIgnoreCase));
            var clip = cme?.Clip;
            if (clip == null)
            { Send(new { type = "animBaked", reqId = r.Id, ok = false, message = "clip not found" }); return; }

            var anims = clip switch
            {
                ClipAnimation ca when ca.Animation != null => new[] { ca.Animation },
                ClipAnimationList cl => cl.Animations?.Data?.Where(a => a?.Animation != null).Select(a => a!.Animation).ToArray() ?? Array.Empty<Animation>(),
                _ => Array.Empty<Animation>(),
            };
            if (anims.Length == 0)
            { Send(new { type = "animBaked", reqId = r.Id, ok = false, message = "clip has no animation data" }); return; }

            // merge by (tag, track); when an animation repeats a bone, keep the richer
            // (more frames) one — typically only one anim drives a given bone anyway.
            var baked = new Dictionary<(int tag, int track), (int n, float fps, string data)>();
            float maxDur = 0f;
            foreach (var anim in anims)
            {
                if (anim?.Sequences?.data_items == null || anim.BoneIds?.data_items == null || anim.Frames == 0) continue;
                int frames = anim.Frames;
                int chunk = anim.SequenceFrameLimit > 0 ? anim.SequenceFrameLimit : frames;
                var seqs = anim.Sequences.data_items;
                var bids = anim.BoneIds.data_items;
                float fps = anim.Duration > 0.001f ? frames / anim.Duration : 30f;
                maxDur = Math.Max(maxDur, anim.Duration > 0.001f ? anim.Duration : frames / 30f);

                for (int bidx = 0; bidx < bids.Length; bidx++)
                {
                    byte track = bids[bidx].Track;
                    if (track != 0 && track != 1) continue;   // bone position / rotation only (skip mover/IK/facial)
                    var key = ((int)bids[bidx].BoneId, (int)track);
                    if (baked.TryGetValue(key, out var ex2) && ex2.n >= frames) continue;
                    bool rot = track == 1;
                    var data = new float[frames * (rot ? 4 : 3)];
                    bool ok = true;
                    for (int f = 0; f < frames && ok; f++)
                    {
                        int si = Math.Min(f / chunk, seqs.Length - 1);
                        var sub = seqs[si]?.Sequences;
                        if (sub == null || bidx >= sub.Length || sub[bidx] == null) { ok = false; break; }
                        int sf = f - si * chunk;
                        try
                        {
                            if (rot)
                            {
                                var q = sub[bidx].EvaluateQuaternion(sf);
                                data[f * 4] = q.X; data[f * 4 + 1] = q.Y; data[f * 4 + 2] = q.Z; data[f * 4 + 3] = q.W;
                            }
                            else
                            {
                                var v = sub[bidx].EvaluateVector(sf);
                                data[f * 3] = v.X; data[f * 3 + 1] = v.Y; data[f * 3 + 2] = v.Z;
                            }
                        }
                        catch { ok = false; }
                    }
                    if (ok) baked[key] = (frames, fps, B64(data));
                }
            }

            var tracks = baked.Select(kv => new { tag = kv.Key.tag, track = kv.Key.track, n = kv.Value.n, fps = kv.Value.fps, data = kv.Value.data }).ToArray();
            Send(new
            {
                type = "animBaked", reqId = r.Id, ok = tracks.Length > 0,
                name, clip = r.Clip, duration = maxDur > 0 ? maxDur : 1f, tracks,
                message = tracks.Length > 0 ? null : "no usable bone tracks in this clip",
            });
        }
        catch (Exception ex) { Send(new { type = "animBaked", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdTexImage(Req r)
    {
        var texs = (_modelCache.TryGetValue(r.Node, out var mc) ? mc.LocalDict : null)?.Textures?.data_items;
        if (texs == null || r.Index < 0 || r.Index >= texs.Length || texs[r.Index] == null)
        { Send(new { type = "texImage", reqId = r.Id, index = r.Index, img = (string?)null }); return; }
        Send(new { type = "texImage", reqId = r.Id, index = r.Index, img = DecodeTexUrl(texs[r.Index]) });
    }

    // Replace (or add) a texture inside an open .ytd / .ypt and write the resource
    // back into its archive (or to disk). The new texture keeps the replaced one's
    // name + usage so the model keeps referencing it; only the pixels/format change.
    // Import source is either a .dds (Content, base64) or raw RGBA (Rgba/W/H) that we
    // encode to the original texture's format.
    private void CmdReplaceTexture(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc))
        { Send(new { type = "replaced", reqId = r.Id, ok = false, message = "texture source not open" }); return; }

        // Re-resolve the entry against the CURRENT mount: a background remount (the file
        // watcher fires on any .rpf change) replaces the archive graph and leaves mc.Entry
        // pointing at a stale archive with stale offsets — reading/writing through it would
        // corrupt the file. GetEntry rebuilds it from the live graph by its stable path.
        var fe = mc.Entry;
        if (fe != null && _ws != null && !string.IsNullOrEmpty(mc.EntryPath)
            && _ws.Manager.GetEntry(mc.EntryPath) is RpfFileEntry fresh)
        {
            fe = fresh;
            mc.Entry = fresh;
        }
        string fileLower = (fe?.NameLower ?? Path.GetFileName(mc.DiskPath)).ToLowerInvariant();
        bool isYpt = fileLower.EndsWith(".ypt", StringComparison.Ordinal);
        bool isYtd = fileLower.EndsWith(".ytd", StringComparison.Ordinal);
        if (!isYpt && !isYtd)
        { Send(new { type = "replaced", reqId = r.Id, ok = false, message = "only .ytd / .ypt textures can be replaced" }); return; }
        if (fe == null && string.IsNullOrEmpty(mc.DiskPath))
        { Send(new { type = "replaced", reqId = r.Id, ok = false, message = "no writable source for this texture" }); return; }

        try
        {
            // Re-load a fresh, authoritative copy (GetFile doesn't cache) to modify + save.
            YtdFile? ytd = null; YptFile? ypt = null;
            TextureDictionary? dict;
            if (fe != null)
            {
                if (isYtd) { ytd = _ws!.Manager.GetFile<YtdFile>(fe); dict = ytd?.TextureDict; }
                else { ypt = _ws!.Manager.GetFile<YptFile>(fe); dict = ypt?.PtfxList?.TextureDictionary; }
            }
            else
            {
                byte[] raw = File.ReadAllBytes(mc.DiskPath);
                if (isYtd) { ytd = RpfFile.GetResourceFile<YtdFile>(raw); dict = ytd?.TextureDict; }
                else { ypt = RpfFile.GetResourceFile<YptFile>(raw); dict = ypt?.PtfxList?.TextureDictionary; }
            }
            if (dict == null)
            { Send(new { type = "replaced", reqId = r.Id, ok = false, message = "no texture dictionary in file" }); return; }

            var list = (dict.Textures?.data_items ?? Array.Empty<Texture>()).Where(t => t != null).ToList();

            // Find the texture being replaced (by name first, index as fallback).
            string targetName = r.Name ?? "";
            Texture? oldTex = list.FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (oldTex == null && r.Index >= 0 && r.Index < list.Count) oldTex = list[r.Index];

            // Build the replacement texture from the imported DDS (or encode RGBA first).
            byte[] ddsBytes;
            if (!string.IsNullOrEmpty(r.Content))
                ddsBytes = Convert.FromBase64String(r.Content);
            else
            {
                byte[] rgba = Convert.FromBase64String(r.Rgba ?? "");
                if (r.W <= 0 || r.H <= 0 || rgba.Length < (long)r.W * r.H * 4) throw new Exception("bad image data");
                string fmt = !string.IsNullOrEmpty(r.Format) ? r.Format! : EncodeFormatFor(oldTex?.Format);
                ddsBytes = TextureCodec.EncodeDds(rgba, r.W, r.H, fmt, true);
            }
            // TextureFromDds normalizes sRGB DX10 formats and rejects anything that maps to
            // format 0, so a bad import fails cleanly instead of writing a file that crashes
            // the game and can no longer be opened.
            var newTex = TextureCodec.TextureFromDds(ddsBytes);

            string keepName = !string.IsNullOrEmpty(oldTex?.Name) ? oldTex!.Name
                            : (!string.IsNullOrEmpty(targetName) ? targetName : "texture");
            newTex.Name = keepName;
            newTex.NameHash = JenkHash.GenHash(keepName.ToLowerInvariant());
            if (oldTex != null) { newTex.Usage = oldTex.Usage; newTex.UsageFlags = oldTex.UsageFlags; }

            if (oldTex != null) list[list.IndexOf(oldTex)] = newTex;
            else list.Add(newTex);                          // "import new" -> add a texture
            dict.BuildFromTextureList(list);

            byte[] data = isYtd ? ytd!.Save() : ypt!.Save();
            if (fe != null) { if (PrepareArchiveWrite(fe.File) is string werr2) throw new Exception(werr2); MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
            else { MarkSelfWrite(mc.DiskPath); File.WriteAllBytes(mc.DiskPath, data); }

            // Point the display cache at the new dict so thumbnails reflect the edit.
            mc.LocalDict = dict;
            Send(new { type = "replaced", reqId = r.Id, ok = true, node = r.Node, name = keepName, size = data.Length, textures = TextureMeta(dict) });
        }
        catch (Exception ex)
        {
            Send(new { type = "replaced", reqId = r.Id, ok = false, message = ex.Message });
        }
    }

    // Delete a texture from an open .ytd / .ypt and write the dictionary back.
    private void CmdDeleteTexture(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc))
        { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = "texture source not open" }); return; }

        var fe = mc.Entry;
        if (fe != null && _ws != null && !string.IsNullOrEmpty(mc.EntryPath)
            && _ws.Manager.GetEntry(mc.EntryPath) is RpfFileEntry fresh) { fe = fresh; mc.Entry = fresh; }

        string fileLower = (fe?.NameLower ?? Path.GetFileName(mc.DiskPath)).ToLowerInvariant();
        bool isYpt = fileLower.EndsWith(".ypt", StringComparison.Ordinal);
        bool isYtd = fileLower.EndsWith(".ytd", StringComparison.Ordinal);
        if (!isYpt && !isYtd) { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = "only .ytd / .ypt textures can be deleted" }); return; }
        if (fe == null && string.IsNullOrEmpty(mc.DiskPath)) { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = "no writable source" }); return; }

        try
        {
            YtdFile? ytd = null; YptFile? ypt = null; TextureDictionary? dict;
            if (fe != null)
            {
                if (isYtd) { ytd = _ws!.Manager.GetFile<YtdFile>(fe); dict = ytd?.TextureDict; }
                else { ypt = _ws!.Manager.GetFile<YptFile>(fe); dict = ypt?.PtfxList?.TextureDictionary; }
            }
            else
            {
                byte[] raw = File.ReadAllBytes(mc.DiskPath);
                if (isYtd) { ytd = RpfFile.GetResourceFile<YtdFile>(raw); dict = ytd?.TextureDict; }
                else { ypt = RpfFile.GetResourceFile<YptFile>(raw); dict = ypt?.PtfxList?.TextureDictionary; }
            }
            if (dict == null) { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = "no texture dictionary in file" }); return; }

            var list = (dict.Textures?.data_items ?? Array.Empty<Texture>()).Where(t => t != null).ToList();
            string targetName = r.Name ?? "";
            Texture? tex = list.FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (tex == null && r.Index >= 0 && r.Index < list.Count) tex = list[r.Index];
            if (tex == null) { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = "texture not found" }); return; }

            list.Remove(tex);
            dict.BuildFromTextureList(list);

            byte[] data = isYtd ? ytd!.Save() : ypt!.Save();
            if (fe != null) { if (PrepareArchiveWrite(fe.File) is string werr2) throw new Exception(werr2); MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
            else { MarkSelfWrite(mc.DiskPath); File.WriteAllBytes(mc.DiskPath, data); }

            mc.LocalDict = dict;
            Send(new { type = "texDeleted", reqId = r.Id, ok = true, node = r.Node, name = targetName, count = list.Count, size = data.Length, textures = TextureMeta(dict) });
        }
        catch (Exception ex) { Send(new { type = "texDeleted", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Map a RAGE texture format to an encoder format string (for image imports —
    // keep the original compression so the game reads it as expected).
    private static string EncodeFormatFor(TextureFormat? f) => f switch
    {
        TextureFormat.D3DFMT_DXT1 => "DXT1",
        TextureFormat.D3DFMT_DXT3 => "DXT3",
        TextureFormat.D3DFMT_DXT5 => "DXT5",
        TextureFormat.D3DFMT_ATI1 => "BC4",
        TextureFormat.D3DFMT_ATI2 => "BC5",
        TextureFormat.D3DFMT_BC7 => "BC7",
        TextureFormat.D3DFMT_A8R8G8B8 or TextureFormat.D3DFMT_A8B8G8R8 or TextureFormat.D3DFMT_X8R8G8B8 => "RGBA",
        _ => "DXT5",
    };

    private void CacheModel(int key, ModelCache mc)
    {
        _modelCache[key] = mc;
        _modelCacheOrder.Remove(key); _modelCacheOrder.Add(key);
        while (_modelCacheOrder.Count > 6) { var old = _modelCacheOrder[0]; _modelCacheOrder.RemoveAt(0); _modelCache.Remove(old); }
    }

    // ---- custom drawable names (persisted LUT) ---------------------------

    private static Dictionary<string, Dictionary<string, string>> NamesLut()
    {
        if (_namesLut != null) return _namesLut;
        try
        {
            if (File.Exists(NamesLutPath))
                _namesLut = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(NamesLutPath));
        }
        catch { }
        return _namesLut ??= new();
    }

    // Custom name keyed by (file name, drawable hash) — survives moving the file
    // since it's keyed by name, not full path. Falls back to the resolved hash name.
    private static string DisplayName(string fileLower, string hashHex, string fallback)
    {
        var lut = NamesLut();
        if (lut.TryGetValue(fileLower, out var m) && m.TryGetValue(hashHex, out var custom) && !string.IsNullOrEmpty(custom))
            return custom;
        return fallback;
    }

    private void CmdRenameModel(Req r)
    {
        string file = (r.File ?? "").ToLowerInvariant();
        string hash = r.Hash ?? "";
        string name = (r.Name ?? "").Trim();
        if (file.Length == 0 || hash.Length == 0) { Send(new { type = "renamed", reqId = r.Id, ok = false }); return; }
        try
        {
            var lut = NamesLut();
            if (!lut.TryGetValue(file, out var m)) { m = new(); lut[file] = m; }
            if (name.Length == 0) m.Remove(hash); else m[hash] = name;       // empty -> revert to default
            if (m.Count == 0) lut.Remove(file);
            Directory.CreateDirectory(Path.GetDirectoryName(NamesLutPath)!);
            File.WriteAllText(NamesLutPath, JsonSerializer.Serialize(lut, new JsonSerializerOptions { WriteIndented = true }));
            Send(new { type = "renamed", reqId = r.Id, ok = true, name });
        }
        catch (Exception ex) { Send(new { type = "renamed", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Texture metadata only (no decoded image) — thumbnails stream in on demand via
    // texImage so opening a dictionary with many textures is instant.
    private static List<object> TextureMeta(TextureDictionary? dict)
    {
        var list = new List<object>();
        var texs = dict?.Textures?.data_items;
        if (texs != null)
            for (int i = 0; i < texs.Length; i++)
            {
                var t = texs[i]; if (t == null) continue;
                list.Add(new { index = i, name = t.Name, width = (int)t.Width, height = (int)t.Height, format = t.Format.ToString(), levels = (int)t.Levels });
            }
        return list;
    }

    private void OpenTexture(Req r, RpfFileEntry fe)
        => SendTextures(r, fe.Name, _ws!.Manager.GetFile<YtdFile>(fe)?.TextureDict, fe);

    private void SendTextures(Req r, string name, TextureDictionary? dict, RpfFileEntry? entry = null, string diskPath = "")
    {
        int cacheKey = r.Node > 0 ? r.Node : _synthSeq--;
        CacheModel(cacheKey, new ModelCache { LocalDict = dict, File = name, Entry = entry, EntryPath = entry?.Path ?? "", DiskPath = diskPath });
        Send(new { type = "texture", reqId = r.Id, node = cacheKey, name, textures = TextureMeta(dict) });
    }

    // Read-only Scaleform GFx (.gfx) structure view: parse the SWF/GFx tag list and
    // surface its shapes/sprites/fonts/images/text + referenced external textures.
    // The raw bytes are cached so the visual render scene can be built on demand.
    private void OpenGfxCore(Req r, string name, byte[] bytes, RpfFileEntry? entry = null, string diskPath = "")
    {
        var g = GfxParser.Parse(bytes);
        int key = r.Node > 0 ? r.Node : _synthSeq--;
        _gfxCache[key] = (bytes, entry, diskPath);
        _gfxCacheOrder.Remove(key); _gfxCacheOrder.Add(key);
        while (_gfxCacheOrder.Count > 4) { var old = _gfxCacheOrder[0]; _gfxCacheOrder.RemoveAt(0); _gfxCache.Remove(old); }
        Send(new
        {
            type = "gfx", reqId = r.Id, node = key, name,
            ok = g.Ok, error = g.Error,
            signature = g.Signature, compression = g.Compression, version = g.Version,
            fileLength = g.FileLength, width = g.Width, height = g.Height,
            frameRate = g.FrameRate, frameCount = g.FrameCount, tagCount = g.TagCount,
            counts = g.Counts,
            images = g.Images.ConvertAll(i => new { i.Id, i.File, i.Width, i.Height }),
            symbols = g.Symbols.ConvertAll(s => new { s.Id, s.Name }),
            tags = g.Tags.ConvertAll(t => new { t.Code, t.Name, t.Category, id = t.Id, t.Detail }),
        });
    }

    // Build the visual render scene (shapes/sprites/timeline) for an open .gfx and
    // resolve its referenced images to PNG data URLs so the canvas can draw them.
    private void CmdGfxScene(Req r)
    {
        if (!_gfxCache.TryGetValue(r.Node, out var g))
        { Send(new { type = "gfxScene", reqId = r.Id, ok = false, error = "gfx not open" }); return; }

        var scene = GfxSceneParser.Parse(g.bytes);
        var images = new List<object>();
        foreach (var im in scene.Images)
        {
            string? url = null; int w = im.W, h = im.H;
            try
            {
                if (im.Kind == "external" && !string.IsNullOrEmpty(im.File))
                    url = ResolveGfxImageUrl(im.File!, g.entry, out w, out h);
            }
            catch { }
            images.Add(new { id = im.Id, dataUrl = url, w, h, file = im.File, resolved = url != null });
        }

        Send(new
        {
            type = "gfxScene", reqId = r.Id, ok = scene.Ok, error = scene.Error,
            width = scene.Width, height = scene.Height, frameRate = scene.FrameRate, frameCount = scene.FrameCount,
            shapes = scene.Shapes, sprites = scene.Sprites, main = scene.Main,
            symbols = scene.Symbols.ConvertAll(s => new { s.Id, s.Name }),
            images,
        });
    }

    // Resolve a GFx external image filename (e.g. "blips_texturesheet.dds") to a PNG
    // data URL: a sibling loose .dds, else a same-named texture in nearby .ytd files.
    private string? ResolveGfxImageUrl(string file, RpfFileEntry? entry, out int w, out int h)
    {
        w = 0; h = 0;
        if (_ws == null) return null;
        string lower = file.ToLowerInvariant();
        string baseName = file; int dot = baseName.LastIndexOf('.'); if (dot > 0) baseName = baseName[..dot];

        var dds = entry?.Parent?.Files?.FirstOrDefault(f => f.NameLower == lower);
        if (dds != null)
        {
            var rgba = TextureCodec.DecodeDds(RpfWorkspace.Extract(dds), out w, out h);
            if (rgba != null) return ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h));
        }

        _resolver ??= new TextureResolver(_ws.Manager);
        if (entry != null) try { _resolver.IndexForModel(entry, new[] { baseName }); } catch { }
        var tex = _resolver.Resolve(baseName);
        if (tex != null)
        {
            var rgba = TextureCodec.DecodeTexture(tex, out w, out h);
            if (rgba != null) return ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h));
        }
        return null;
    }

    private void OpenImage(Req r, RpfFileEntry fe) => OpenImageCore(r, fe.Name, RpfWorkspace.Extract(fe));

    private void OpenImageCore(Req r, string name, byte[] bytes)
    {
        string ext = ExtOf(name.ToLowerInvariant());
        string url; int w = 0, h = 0; string fmt = ext.ToUpperInvariant();

        if (ext == "dds")
        {
            var rgba = TextureCodec.DecodeDds(bytes, out w, out h);
            if (rgba == null) { OpenHexCore(r, name, bytes); return; }
            url = ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h));
            fmt = "DDS";
        }
        else if (ext == "svg")
        {
            url = "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
        }
        else
        {
            string mime = ext switch
            {
                "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "bmp" => "image/bmp",
                "gif" => "image/gif", "webp" => "image/webp", _ => "application/octet-stream",
            };
            url = $"data:{mime};base64," + Convert.ToBase64String(bytes);
        }
        Send(new { type = "image", reqId = r.Id, name, format = fmt, width = w, height = h, dataUrl = url });
    }

    private void OpenEditableOrHex(Req r, RpfFileEntry fe)
        => OpenEditableOrHexCore(r, fe.Name, RpfWorkspace.Extract(fe), fe, fe.Path);

    // fe is null for loose disk files (no binary-meta XML conversion available there).
    // savePath is the stable identity (archive entry path or on-disk path) the front-end
    // echoes back on save so the write survives a background remount invalidating node ids.
    private void OpenEditableOrHexCore(Req r, string name, byte[] bytes, RpfFileEntry? fe, string savePath = "")
    {
        string ext = ExtOf(name.ToLowerInvariant());

        // 1. Convertible binary meta -> editable XML (archive entries only).
        if (fe != null && MetaConvertExts.Contains(ext) && TrySendMeta(r, fe, bytes)) return;

        // 2. Plain text (xml/txt/lua/many .meta/.dat that are actually text).
        if (LooksText(bytes))
        {
            Send(new
            {
                type = "edit", reqId = r.Id, name, editable = true, path = savePath,
                format = "text", language = LangForExt(ext), content = DecodeText(bytes),
            });
            return;
        }

        // 3. Last resort: try meta conversion for binary .meta/.dat/etc.
        if (fe != null && TrySendMeta(r, fe, bytes)) return;

        // 4. Hex (always-available fallback, read-only).
        OpenHexCore(r, name, bytes);
    }

    private bool TrySendMeta(Req r, RpfFileEntry fe, byte[] bytes)
    {
        try
        {
            string xml = MetaXml.GetXml(fe, bytes, out string xmlName);
            if (string.IsNullOrEmpty(xml)) return false;
            Send(new
            {
                type = "edit", reqId = r.Id, name = fe.Name, editable = true, path = fe.Path,
                format = "meta", metaName = xmlName, language = "xml", content = xml,
            });
            return true;
        }
        catch { return false; }
    }

    // "Edit as XML" (right-click). Converts a resource to CodeWalker XML. For the
    // texture-bearing resources (ydr/ydd/yft/ytd/ypt) the embedded textures are
    // extracted to a temp folder as .dds so the XML round-trips on save.
    private void OpenAsXml(Req r, RpfFileEntry fe)
    {
        byte[] bytes = RpfWorkspace.Extract(fe);
        try
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "EpicRpf_xml", r.Node.ToString());
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { }
            Directory.CreateDirectory(baseDir);

            string xml = MetaXml.GetXml(fe, bytes, out string xmlName, baseDir);
            if (string.IsNullOrEmpty(xml)) { OpenEditableOrHex(r, fe); return; }

            // GetXml writes the .dds into <baseDir>\<shortname>\ — remember that exact
            // folder so a later save can read them back.
            _xmlDdsDirs[r.Node] = Path.Combine(baseDir, fe.GetShortName());

            Send(new
            {
                type = "edit", reqId = r.Id, name = fe.Name, editable = true, path = fe.Path,
                format = "meta", metaName = xmlName, language = "xml", content = xml,
            });
        }
        catch (Exception ex)
        {
            Send(new { type = "error", reqId = r.Id, message = "XML convert failed: " + ex.Message });
        }
    }

    private void CmdSave(Req r)
    {
        // Resolve the save target. Node ids are invalidated whenever the file watcher
        // remounts (any background .rpf change in the install clears the registry), which
        // is why edits to anything but a just-opened file silently failed with "invalid
        // node". The editor now also echoes the file's stable path, so fall back to
        // re-resolving by path against the live mount (archive entry) or the disk.
        RpfFileEntry? fe = null;
        DiskItem? di = null;
        if (_nodes.TryGetValue(r.Node, out var obj)) { fe = obj as RpfFileEntry; di = obj as DiskItem; }
        if (fe == null && (di == null || di.IsDir) && !string.IsNullOrEmpty(r.Path))
        {
            if (_ws?.Manager.GetEntry(r.Path!) is RpfFileEntry ent) fe = ent;
            else if (File.Exists(r.Path!)) di = new DiskItem { Path = r.Path!, IsDir = false };
        }
        if (fe == null && (di == null || di.IsDir))
        { Send(new { type = "saved", reqId = r.Id, ok = false, message = "invalid node" }); return; }

        string content = r.Content ?? "";
        string target = r.Target ?? "export";
        string fileName = fe?.Name ?? Path.GetFileName(di!.Path);

        if (target == "export")
        {
            // For a binary-meta-as-XML view, use CodeWalker's conversion name (e.g.
            // carcols.ymt.pso.xml) so a later drag-drop reimport can detect the format.
            string suggested = r.Format == "meta" ? (string.IsNullOrEmpty(r.MetaName) ? fileName + ".xml" : r.MetaName!) : fileName;
            string? path = _pickSavePath(suggested);
            if (path == null) { Send(new { type = "saved", reqId = r.Id, ok = false, canceled = true }); return; }
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Send(new { type = "saved", reqId = r.Id, ok = true, target = "export", path });
            return;
        }

        // target == "rpf": convert (if meta) and write back in place (archive or disk).
        byte[] data;
        try
        {
            if (r.Format == "meta")
            {
                var doc = new XmlDocument();
                doc.LoadXml(content);
                var fmt = XmlMeta.GetXMLFormat((r.MetaName ?? fileName + ".xml").ToLowerInvariant(), out _);
                string ddsDir = _xmlDdsDirs.TryGetValue(r.Node, out var dd) ? dd : "";
                data = XmlMeta.GetData(doc, fmt, ddsDir);
                if (data == null || data.Length == 0) throw new Exception("conversion produced no data");
            }
            else data = new UTF8Encoding(false).GetBytes(content);
        }
        catch (Exception ex)
        {
            Send(new { type = "saved", reqId = r.Id, ok = false, target = "rpf", message = "convert failed: " + ex.Message });
            return;
        }

        try
        {
            if (fe != null) { if (PrepareArchiveWrite(fe.File) is string werr2) throw new Exception(werr2); MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
            else { MarkSelfWrite(di!.Path); File.WriteAllBytes(di.Path, data); }
            Send(new { type = "saved", reqId = r.Id, ok = true, target = "rpf", size = data.Length });
        }
        catch (Exception ex)
        {
            Send(new { type = "saved", reqId = r.Id, ok = false, target = "rpf", message = ex.Message });
        }
    }

    // ---- search / reveal -------------------------------------------------

    private void CmdSearch(Req r)
    {
        if (_ws == null) { Send(new { type = "search", reqId = r.Id, results = Array.Empty<object>(), total = 0 }); return; }
        string q = (r.Query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) { Send(new { type = "search", reqId = r.Id, results = Array.Empty<object>(), total = 0 }); return; }

        int limit = r.Limit > 0 ? r.Limit : 1000;
        string extNeedle = "." + q.TrimStart('.');
        _searchResults.Clear();
        _searchSeq = 1;

        var results = new List<object>();
        int total = 0;
        foreach (var fe in EntryUniverse(r))
        {
            bool match = r.Ext ? fe.NameLower.EndsWith(extNeedle, StringComparison.Ordinal) : fe.NameLower.Contains(q);
            if (!match) continue;
            total++;
            if (results.Count >= limit) continue;
            int rid = _searchSeq++;
            _searchResults[rid] = fe;
            results.Add(new
            {
                rid, name = fe.Name, path = fe.Path, type = FriendlyType(fe.Name), size = SafeSize(fe),
                viewer = FileTypes.Route(fe.Name).ToString().ToLowerInvariant(),
                kind = fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? "archive" : "file",
            });
        }
        Send(new { type = "search", reqId = r.Id, query = q, results, total, capped = total > limit });
    }

    private IEnumerable<RpfFileEntry> EntryUniverse(Req r)
    {
        IEnumerable<RpfFileEntry> Global() =>
            _ws!.AllRpfs.Where(x => x.AllEntries != null).SelectMany(x => x.AllEntries).OfType<RpfFileEntry>();

        string scope = r.Scope ?? "none";
        if (scope == "none" || !_nodes.TryGetValue(r.Node, out var obj)) return Global();

        if (scope == "archive")
        {
            var rpf = NodeArchive(obj);
            if (rpf == null) return Global();
            return ArchiveTree(rpf).Where(a => a.AllEntries != null).SelectMany(a => a.AllEntries).OfType<RpfFileEntry>();
        }
        if (scope == "folder")
        {
            var dir = ResolveDir(obj);
            if (dir == null) return Global();
            var list = new List<RpfFileEntry>();
            CollectDir(dir, list);
            return list;
        }
        return Global();
    }

    private static RpfFile? NodeArchive(object obj) => obj switch
    {
        RpfFile f => f,
        RpfDirectoryEntry d => d.File,
        RpfFileEntry fe => fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? fe.File?.FindChildArchive(fe) : fe.File,
        _ => null,
    };

    private static IEnumerable<RpfFile> ArchiveTree(RpfFile rpf)
    {
        yield return rpf;
        if (rpf.Children != null)
            foreach (var c in rpf.Children)
                foreach (var x in ArchiveTree(c))
                    yield return x;
    }

    private static void CollectDir(RpfDirectoryEntry dir, List<RpfFileEntry> list)
    {
        if (dir.Files != null)
            foreach (var fe in dir.Files)
            {
                list.Add(fe);
                if (fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal))
                {
                    var ch = SafeChildRoot(fe);
                    if (ch != null) CollectDir(ch, list);
                }
            }
        if (dir.Directories != null)
            foreach (var sub in dir.Directories)
                CollectDir(sub, list);
    }

    private void CmdReveal(Req r)
    {
        if (!_searchResults.TryGetValue(r.Node, out var fe))
        { Send(new { type = "revealed", reqId = r.Id, ok = false }); return; }

        // Walk file -> root collecting nodes (inner dirs, each archive + the dir it
        // lives in within its parent, then the base archive's disk folders), reverse.
        var stack = new List<(object node, string name)>();
        var rpf = fe.File!;
        var d = fe.Parent;
        while (d != null && d != rpf.Root) { stack.Add((d, d.Name)); d = d.Parent; }
        for (var cur = rpf; cur != null;)
        {
            stack.Add((cur, cur.Name));
            var parent = cur.Parent;
            if (parent == null)
            {
                var vf = VFoldersFor(cur);
                for (int i = vf.Count - 1; i >= 0; i--) stack.Add(vf[i]);
                break;
            }
            var entry = parent.AllEntries?.OfType<RpfFileEntry>().FirstOrDefault(e => e.Path == cur.Path);
            var dd = entry?.Parent;
            while (dd != null && dd != parent.Root) { stack.Add((dd, dd.Name)); dd = dd.Parent; }
            cur = parent;
        }
        stack.Reverse();

        var crumbs = new List<object> { new { id = 0, name = _rootName, path = (string?)null } };
        foreach (var (node, name) in stack)
            crumbs.Add(new
            {
                id = Register(node), name,
                path = node switch { RpfEntry e => e.Path, RpfFile f => f.Path, DiskItem di => di.Path, _ => null },
            });

        Send(new
        {
            type = "revealed", reqId = r.Id, ok = true, crumbs,
            fileId = Register(fe), name = fe.Name,
            viewer = FileTypes.Route(fe.Name).ToString().ToLowerInvariant(),
        });
    }

    // Disk-folder crumbs leading to a base archive (e.g. update\ for update.rpf).
    private List<(object node, string name)> VFoldersFor(RpfFile baseRpf)
    {
        var res = new List<(object, string)>();
        var segs = (baseRpf.Path ?? baseRpf.Name).Split('\\', '/');
        string cur = _gtaFolder;
        for (int i = 0; i < segs.Length - 1; i++)
        {
            cur = Path.Combine(cur, segs[i]);
            res.Add((new DiskItem { Path = cur, IsDir = true }, segs[i]));
        }
        return res;
    }

    // ---- create / extract-all -------------------------------------------

    private static RpfDirectoryEntry? ResolveDir(object? node) => node switch
    {
        RpfFile f => f.Root,
        RpfDirectoryEntry d => d,
        RpfFileEntry fe when fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) => SafeChildRoot(fe),
        _ => null,
    };

    // ---- write safety ------------------------------------------------------
    // (1) A background remount (file watcher) swaps the archive graph and clears
    //     _nodes; writing through a STALE graph object rewrites the archive with
    //     outdated entry tables and CORRUPTS it. Commands re-resolve their target
    //     by its stable PATH against the live graph whenever the node id is gone.
    // (2) Before any archive write, probe the physical file for write access so a
    //     locked archive (GTA V / launcher running) fails fast with a clear message
    //     instead of dying midway through CodeWalker's write sequence.
    // (3) NG-encrypted archives need the NG ENCRYPT tables before ANY header write
    //     (create/rename/delete/import/undo) — EnsureFor is part of the same gate.

    private object? ResolveByPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Length > 1 && path[1] == ':')   // absolute disk path (loose file/folder)
            return Directory.Exists(path) ? new DiskItem { Path = path, IsDir = true }
                 : File.Exists(path) ? new DiskItem { Path = path, IsDir = false } : null;
        if (_ws == null) return null;
        object? entry = _ws.Manager.GetEntry(path);
        if (entry != null) return entry;
        return _ws.AllRpfs.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    // Node lookup with stale-fallback: a live node id wins (same mount generation);
    // a cleared/recycled id re-resolves through the stable path.
    private object? ResolveNode(int nodeId, string? path)
        => _nodes.TryGetValue(nodeId, out var obj) ? obj : ResolveByPath(path);

    // Friendly pre-check that an archive's physical file is writable right now.
    private static string? WriteLockError(RpfFile? archive)
    {
        string? phys = SafePhysical(archive);
        if (phys == null) return null;
        try { using var fs = File.Open(phys, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); return null; }
        catch (IOException) { return Path.GetFileName(phys) + " is locked by another program — if GTA V or a launcher is running, close it and try again."; }
        catch (UnauthorizedAccessException) { return "No write permission for " + Path.GetFileName(phys) + "."; }
    }

    // Everything that must hold before writing into an archive: not locked + NG ready.
    // Returns an error message, or null when it's safe to proceed.
    private static string? PrepareArchiveWrite(RpfFile? archive)
    {
        var err = WriteLockError(archive);
        if (err != null) return err;
        NgEncrypt.EnsureFor(archive);
        return null;
    }
    private static string? PrepareArchiveWrite(RpfDirectoryEntry dir) => PrepareArchiveWrite(dir.File);

    // A create target is either a real disk directory or a directory inside an
    // archive. nodeId 0 = the GTA V root folder (so you can create at the top level).
    private bool ResolveTarget(Req r, out string? diskDir, out RpfDirectoryEntry? rpfDir, out string? err)
    {
        diskDir = null; rpfDir = null; err = null;
        if (r.Node == 0 && string.IsNullOrEmpty(r.Path)) { diskDir = _gtaFolder; return diskDir.Length > 0; }
        var obj = ResolveNode(r.Node, r.Path);
        if (obj == null) { err = "That location is no longer available (the archives were re-scanned) — go back to the folder and try again."; return false; }
        if (obj is DiskItem di && di.IsDir) { diskDir = di.Path; return true; }
        rpfDir = ResolveDir(obj);
        if (rpfDir != null) return true;
        err = "Can't create here.";
        return false;
    }

    private void CmdCreateFolder(Req r)
    {
        if (!ResolveTarget(r, out var disk, out var dir, out var err))
        { Send(new { type = "created", reqId = r.Id, ok = false, message = err }); return; }
        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "created", reqId = r.Id, ok = false, message = "name required" }); return; }
        try
        {
            if (disk != null) { var p = Path.Combine(disk, name); MarkSelfWrite(p); Directory.CreateDirectory(p); }
            else
            {
                if (PrepareArchiveWrite(dir!) is string werr) throw new Exception(werr);
                MarkSelfWrite(SafePhysical(dir!.File));
                RpfFile.CreateDirectory(dir!, name);
            }
            Send(new { type = "created", reqId = r.Id, ok = true, kind = "folder", name });
        }
        catch (Exception ex) { Send(new { type = "created", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdCreateRpf(Req r)
    {
        if (!ResolveTarget(r, out var disk, out var dir, out var err))
        { Send(new { type = "created", reqId = r.Id, ok = false, message = err }); return; }
        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "created", reqId = r.Id, ok = false, message = "name required" }); return; }
        if (!name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) name += ".rpf";
        try
        {
            string? note;
            if (disk != null)
            {
                var full = Path.Combine(disk, name);
                MarkSelfWrite(full);
                var rpf = RpfFile.CreateNew(disk, name, RpfEncryption.OPEN);
                AddRpfToManager(rpf);
                note = r.Override ? "content sync only applies inside update.rpf" : null;
            }
            else
            {
                if (PrepareArchiveWrite(dir!) is string werr) throw new Exception(werr);
                MarkSelfWrite(SafePhysical(dir!.File));
                RpfFile.CreateNew(dir!, name, RpfEncryption.OPEN);
                note = r.Override ? TryAddContentOverride(dir, name) : null;
            }
            Send(new { type = "created", reqId = r.Id, ok = true, kind = "rpf", name, note });
        }
        catch (Exception ex) { Send(new { type = "created", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdCreateYtd(Req r)
    {
        if (!ResolveTarget(r, out var disk, out var dir, out var err))
        { Send(new { type = "created", reqId = r.Id, ok = false, message = err }); return; }
        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "created", reqId = r.Id, ok = false, message = "name required" }); return; }
        if (!name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)) name += ".ytd";
        try
        {
            var ytd = new YtdFile { TextureDict = new TextureDictionary() };
            ytd.TextureDict.BuildFromTextureList(new List<Texture>());
            byte[] data = ytd.Save();
            if (disk != null) { var p = Path.Combine(disk, name); MarkSelfWrite(p); File.WriteAllBytes(p, data); }
            else
            {
                if (PrepareArchiveWrite(dir!) is string werr) throw new Exception(werr);
                MarkSelfWrite(SafePhysical(dir!.File));
                RpfFile.CreateFile(dir!, name, data, true);
            }
            Send(new { type = "created", reqId = r.Id, ok = true, kind = "ytd", name });
        }
        catch (Exception ex) { Send(new { type = "created", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Import an arbitrary dropped file into a folder/archive. As-is by default (raw
    // bytes in Content as base64, replacing any same-name file). When As=="xml" the
    // file is a CodeWalker XML export (name.<ext>.xml) and is converted back to its
    // binary resource, saved as name.<ext> — the reverse of "Edit as XML"/extract.
    private void CmdImportFile(Req r)
    {
        if (!ResolveTarget(r, out var disk, out var dir, out var err))
        { Send(new { type = "imported", reqId = r.Id, ok = false, message = err }); return; }
        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "imported", reqId = r.Id, ok = false, message = "name required" }); return; }
        try
        {
            byte[] data; string outName;
            if (r.As == "xml")
            {
                // trimlength recovers the original name: ".ydr.xml"->4 (name.ydr),
                // ".pso.xml"->8 (a ymt exported as name.ymt.pso.xml -> name.ymt).
                var fmt = XmlMeta.GetXMLFormat(name.ToLowerInvariant(), out int trim);
                outName = name.Length > trim ? name.Substring(0, name.Length - trim) : name;
                var doc = new XmlDocument();
                doc.LoadXml(r.Content ?? "");
                data = XmlMeta.GetData(doc, fmt, "");
                if (data == null || data.Length == 0)
                    throw new Exception("XML conversion produced no data (embedded-texture resources also need their .dds folder)");
            }
            else
            {
                outName = name;
                data = Convert.FromBase64String(r.Content ?? "");
                if (data.Length == 0) throw new Exception("empty file");
            }

            if (disk != null) { var p = Path.Combine(disk, outName); MarkSelfWrite(p); File.WriteAllBytes(p, data); }
            else
            {
                if (PrepareArchiveWrite(dir!) is string werr) throw new Exception(werr);
                MarkSelfWrite(SafePhysical(dir!.File));
                RpfFile.CreateFile(dir!, outName, data, true);
            }
            RescanIfArchive(outName);
            Send(new { type = "imported", reqId = r.Id, ok = true, name = outName, size = data.Length });
        }
        catch (Exception ex) { Send(new { type = "imported", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Import a file straight from its disk path (drag-drop hands us the real path via
    // postMessageWithAdditionalObjects). No base64 round-trip through the JS bridge, so
    // size is unlimited for disk targets (streamed copy) and bounded only by the
    // archive-entry format for .rpf targets.
    private void CmdImportPath(Req r)
    {
        if (!ResolveTarget(r, out var disk, out var dir, out var err))
        { Send(new { type = "imported", reqId = r.Id, ok = false, message = err }); return; }
        string src = r.SrcPath ?? "";
        if (!File.Exists(src))
        { Send(new { type = "imported", reqId = r.Id, ok = false, message = "source file not found: " + src }); return; }
        string name = string.IsNullOrWhiteSpace(r.Name) ? Path.GetFileName(src) : r.Name.Trim();
        try
        {
            long len = new FileInfo(src).Length;
            if (disk != null)
            {
                string p = Path.Combine(disk, name);
                if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                { Send(new { type = "imported", reqId = r.Id, ok = true, name, size = len, note = "already here" }); return; }
                MarkSelfWrite(p);
                File.Copy(src, p, true);   // streamed by the OS — any size
            }
            else
            {
                if (len > 1_900_000_000)
                    throw new Exception($"{name} is {len / 1048576:N0} MB — too large for an archive entry. Import it into a normal folder instead.");
                if (PrepareArchiveWrite(dir!) is string werr) throw new Exception(werr);
                MarkSelfWrite(SafePhysical(dir!.File));
                RpfFile.CreateFile(dir!, name, File.ReadAllBytes(src), true);
            }
            RescanIfArchive(name);
            Send(new { type = "imported", reqId = r.Id, ok = true, name, size = len });
        }
        catch (Exception ex) { Send(new { type = "imported", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // ---- .epic extensions -------------------------------------------------

    private static readonly JsonSerializerOptions EpicJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // The .epic bytes for a request: dropped file (base64 in Content) or a picked path.
    // Open a .epic the UI referenced either by file path (streamed — no giant in-memory
    // buffer) or by base64 bytes (drag-drop). Caller disposes the returned package.
    private static EpicPackage OpenEpic(Req r)
    {
        if (!string.IsNullOrEmpty(r.Path) && File.Exists(r.Path)) return EpicPackage.OpenFile(r.Path);
        if (!string.IsNullOrEmpty(r.Content)) return EpicPackage.Open(Convert.FromBase64String(r.Content));
        throw new Exception("no extension supplied");
    }

    private void CmdInspectEpic(Req r)
    {
        EpicPackage pkg;
        try { pkg = OpenEpic(r); }
        catch (Exception ex) { Send(new { type = "epicInfo", reqId = r.Id, ok = false, message = ex.Message }); return; }
        using (pkg)
        {
            var m = pkg.Manifest;
            var plan = _ws != null ? EpicInstaller.Plan(pkg, _ws.Manager, _gtaFolder) : new List<string> { "(mount a game folder to preview targets)" };
            Send(new
            {
                type = "epicInfo", reqId = r.Id, ok = true,
                name = m.Name, author = m.Author, version = m.Version, description = m.Description, target = m.Target,
                operations = m.Operations.ConvertAll(o => new { op = o.Op, target = o.Target, action = o.Action, note = o.Note }),
                plan,
            });
        }
    }

    private void CmdInstallEpic(Req r)
    {
        if (_ws == null) { Send(new { type = "epicInstalled", reqId = r.Id, ok = false, message = "mount a GTA folder first" }); return; }
        EpicPackage pkg;
        try { pkg = OpenEpic(r); }
        catch (Exception ex) { Send(new { type = "epicInstalled", reqId = r.Id, ok = false, message = ex.Message }); return; }
        using (pkg)
        {
            string backup = Path.Combine(_gtaFolder, "EpicRpf_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            List<EpicOpResult> results;
            try { results = EpicInstaller.Apply(pkg, _ws, backup); }
            catch (Exception ex) { Send(new { type = "epicInstalled", reqId = r.Id, ok = false, message = ex.Message }); return; }

            int ok = results.Count(x => x.Ok), fail = results.Count - ok;
            Send(new
            {
                type = "epicInstalled", reqId = r.Id, ok = fail == 0, okCount = ok, failCount = fail,
                name = pkg.Manifest.Name, backup,
                results = results.ConvertAll(x => new { op = x.Op, target = x.Target, ok = x.Ok, message = x.Message }),
            });
        }
    }

    private void CmdCreateEpic(Req r)
    {
        EpicManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<EpicManifest>(r.Manifest ?? "", EpicJson); }
        catch (Exception ex) { Send(new { type = "epicCreated", reqId = r.Id, ok = false, message = "bad manifest: " + ex.Message }); return; }
        if (manifest == null) { Send(new { type = "epicCreated", reqId = r.Id, ok = false, message = "empty manifest" }); return; }

        // Resolve payload sources to disk PATHS (streamed at pack time, never read fully into RAM).
        var payload = new List<(string name, string path)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var op in manifest.Operations.Where(o => o.Op == "replaceFile" && !string.IsNullOrEmpty(o.Source)))
            {
                if (!File.Exists(op.Source!)) throw new Exception("payload file not found: " + op.Source);
                string name = Path.GetFileName(op.Source!);
                if (!used.Add(name)) { name = Guid.NewGuid().ToString("N")[..8] + "_" + name; used.Add(name); }
                payload.Add((name, op.Source!));
                op.Source = name;   // store in-package name
            }
        }
        catch (Exception ex) { Send(new { type = "epicCreated", reqId = r.Id, ok = false, message = ex.Message }); return; }

        // Ask where to save BEFORE packing, so a cancel doesn't waste the (potentially large) pack.
        string suggested = (string.IsNullOrWhiteSpace(manifest.Name) ? "extension" : SafeName(manifest.Name)) + ".epic";
        string? path = _pickSavePath(suggested);
        if (path == null) { Send(new { type = "epicCreated", reqId = r.Id, ok = false, canceled = true }); return; }
        if (!path.EndsWith(".epic", StringComparison.OrdinalIgnoreCase)) path += ".epic";

        try
        {
            using (var outFs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                EpicPackage.PackToStream(manifest, payload, outFs);
            long size = new FileInfo(path).Length;
            Send(new { type = "epicCreated", reqId = r.Id, ok = true, path, size, ops = manifest.Operations.Count });
        }
        catch (Exception ex)
        {
            try { File.Delete(path); } catch { }
            Send(new { type = "epicCreated", reqId = r.Id, ok = false, message = ex.Message });
        }
    }

    private void AddRpfToManager(RpfFile rpf)
    {
        try { _ws?.Manager.AllRpfs.Add(rpf); } catch { }
        try { _baseRpfByPath[rpf.GetPhysicalFilePath()] = rpf; } catch { }
    }

    private static string? SafePhysical(RpfFile? f) { try { return f?.GetPhysicalFilePath(); } catch { return null; } }

    // ---- delete / trash --------------------------------------------------

    private void CmdDelete(Req r)
    {
        var obj = ResolveNode(r.Node, r.Path);
        if (obj == null)
        { Send(new { type = "deleted", reqId = r.Id, ok = false, message = "That item is no longer available — refresh and try again." }); return; }

        string name; long incoming;
        switch (obj)
        {
            case DiskItem di: name = Path.GetFileName(di.Path); incoming = di.IsDir ? DirSize(di.Path) : FileLen(di.Path); break;
            case RpfFile rf: name = rf.Name; incoming = SafePhysical(rf) is string p ? FileLen(p) : 0; break;
            case RpfFileEntry fe: name = fe.Name; incoming = SafeSize(fe); break;
            case RpfDirectoryEntry rd: name = rd.Name; incoming = 0; break;
            default: Send(new { type = "deleted", reqId = r.Id, ok = false, message = "can't delete this" }); return;
        }

        // Pre-flight: deleting an archive entry rewrites the archive — fail fast and
        // clearly when the archive is locked (game running) or NG tables are missing.
        string? werr = obj switch
        {
            RpfFileEntry fe => PrepareArchiveWrite(fe.File),
            RpfDirectoryEntry rd => PrepareArchiveWrite(rd.File),
            RpfFile rf => WriteLockError(rf),
            _ => null,
        };
        if (werr != null) { Send(new { type = "deleted", reqId = r.Id, ok = false, message = werr }); return; }

        long trash = TrashSize();
        if (!r.Force && trash + incoming > TrashLimitBytes)
        {
            Send(new { type = "deleted", reqId = r.Id, ok = false, needConfirm = true, name,
                trashMb = trash / 1048576, limitMb = TrashLimitBytes / 1048576 });
            return;
        }

        try
        {
            Directory.CreateDirectory(TrashDir);
            if (r.Force) PurgeTrashToFit(incoming);
            long batch = r.Batch > 0 ? r.Batch : _batchSeq++;
            switch (obj)
            {
                case DiskItem di: MoveDiskToTrash(di, batch); break;
                case RpfFile rf: DeleteBaseRpf(rf, batch); break;
                case RpfFileEntry fe: MoveRpfFileToTrash(fe, batch); break;
                case RpfDirectoryEntry rd: MoveRpfDirToTrash(rd, batch); break;
            }
            if (_undoStack.Count > 200) _undoStack.RemoveRange(0, _undoStack.Count - 200);
            Send(new { type = "deleted", reqId = r.Id, ok = true, name });
        }
        catch (Exception ex) { Send(new { type = "deleted", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Rename a disk file/folder or an entry inside an archive. We refuse to rename
    // .rpf archives themselves: NG-encrypted archives are keyed by their filename, so
    // renaming one breaks mounting (documented gotcha) — entries inside are fine.
    private void CmdRename(Req r)
    {
        var obj = ResolveNode(r.Node, r.Path);
        if (obj == null)
        { Send(new { type = "renamed", reqId = r.Id, ok = false, message = "That item is no longer available — refresh and try again." }); return; }

        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "renamed", reqId = r.Id, ok = false, message = "name required" }); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { Send(new { type = "renamed", reqId = r.Id, ok = false, message = "name contains invalid characters" }); return; }

        try
        {
            switch (obj)
            {
                case DiskItem di:
                {
                    if (!di.IsDir && di.Path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Renaming an .rpf archive can break it (archives are keyed by filename).");
                    string dir = Path.GetDirectoryName(di.Path) ?? "";
                    string dest = Path.Combine(dir, name);
                    if (string.Equals(dest, di.Path, StringComparison.Ordinal)) break;   // no change
                    if (File.Exists(dest) || Directory.Exists(dest)) throw new Exception("a file or folder with that name already exists here");
                    MarkSelfWrite(di.Path); MarkSelfWrite(dest);
                    if (di.IsDir) Directory.Move(di.Path, dest); else File.Move(di.Path, dest);
                    di.Path = dest;
                    break;
                }
                case RpfFile:
                    throw new Exception("Renaming an .rpf archive can break it (archives are keyed by filename).");
                case RpfFileEntry fe when fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal):
                    throw new Exception("Renaming an .rpf archive can break it (archives are keyed by filename).");
                case RpfFileEntry fe:
                    if (PrepareArchiveWrite(fe.File) is string we1) throw new Exception(we1);
                    MarkSelfWrite(SafePhysical(fe.File));
                    RpfFile.RenameEntry(fe, name);
                    break;
                case RpfDirectoryEntry rd:
                    if (PrepareArchiveWrite(rd.File) is string we2) throw new Exception(we2);
                    MarkSelfWrite(SafePhysical(rd.File));
                    RpfFile.RenameEntry(rd, name);
                    break;
                default:
                    throw new Exception("can't rename this");
            }
            Send(new { type = "renamed", reqId = r.Id, ok = true, name });
        }
        catch (Exception ex) { Send(new { type = "renamed", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    // Convert an .rpf archive (plus all nested child archives, and any encrypted
    // parents) to OPEN encryption — CodeWalker's own pre-edit conversion. OPEN
    // archives load fine in the game and sidestep NG encryption on every future
    // write, which is the safest state for modded archives.
    private void CmdConvertEncryption(Req r)
    {
        var obj = ResolveNode(r.Node, r.Path);
        RpfFile? rpf = obj switch
        {
            RpfFile f => f,
            RpfFileEntry fe when fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) => fe.File?.FindChildArchive(fe),
            _ => null,
        };
        if (rpf == null)
        { Send(new { type = "encryptionConverted", reqId = r.Id, ok = false, message = "select an .rpf archive" }); return; }

        try
        {
            if (WriteLockError(rpf) is string lockErr)
            { Send(new { type = "encryptionConverted", reqId = r.Id, ok = false, message = lockErr }); return; }

            string before = rpf.Encryption.ToString();
            int converted = 0;
            MarkSelfWrite(SafePhysical(rpf));
            bool ok = RpfFile.EnsureValidEncryption(rpf, f => { converted++; return true; }, recursive: true);
            MarkSelfWrite(SafePhysical(rpf));   // refresh the watcher window after the (possibly long) run
            Send(new
            {
                type = "encryptionConverted", reqId = r.Id, ok,
                name = rpf.Name, before, after = rpf.Encryption.ToString(),
            });
        }
        catch (Exception ex) { Send(new { type = "encryptionConverted", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private static void PushUndo(UndoEntry e) { lock (_undoStack) _undoStack.Add(e); }

    private void MoveDiskToTrash(DiskItem di, long batch)
    {
        string dest = UniqueTrashPath(Path.GetFileName(di.Path));
        MarkSelfWrite(di.Path);
        try { if (di.IsDir) Directory.Move(di.Path, dest); else File.Move(di.Path, dest); }
        catch
        {
            if (di.IsDir) { CopyDir(di.Path, dest); Directory.Delete(di.Path, true); }
            else { File.Copy(di.Path, dest, true); File.Delete(di.Path); }
        }
        PushUndo(new UndoEntry { Batch = batch, Kind = "disk", TrashPath = dest, IsDir = di.IsDir, OrigPath = di.Path });
    }

    private void DeleteBaseRpf(RpfFile rf, long batch)
    {
        string? phys = SafePhysical(rf);
        if (phys == null || !File.Exists(phys)) throw new Exception("archive file not found");
        string dest = UniqueTrashPath(rf.Name);
        MarkSelfWrite(phys);
        File.Move(phys, dest);
        try { _ws?.Manager.AllRpfs.Remove(rf); } catch { }
        _baseRpfByPath.Remove(phys);
        PushUndo(new UndoEntry { Batch = batch, Kind = "baseRpf", TrashPath = dest, OrigPath = phys });
    }

    private void MoveRpfFileToTrash(RpfFileEntry fe, long batch)
    {
        string dest = UniqueTrashPath(fe.Name);
        // Save a valid standalone copy (RSC7 header re-added for resources) so undo can
        // re-import it as the correct entry kind instead of a corrupt binary file.
        try { File.WriteAllBytes(dest, RpfWorkspace.ExtractForSave(fe)); } catch { }
        MarkSelfWrite(SafePhysical(fe.File));
        string parentPath = fe.Parent?.Path ?? "";
        RpfFile.DeleteEntry(fe);
        PushUndo(new UndoEntry { Batch = batch, Kind = "rpfFile", TrashPath = dest, ParentPath = parentPath, Name = fe.Name });
    }

    private void MoveRpfDirToTrash(RpfDirectoryEntry rd, long batch)
    {
        string dest = UniqueTrashPath(rd.Name);
        try { int c = 0; long b = 0; ExtractDir(rd, dest, ref c, ref b); } catch { }
        MarkSelfWrite(SafePhysical(rd.File));
        string parentPath = rd.Parent?.Path ?? "";
        RpfFile.DeleteEntry(rd);
        PushUndo(new UndoEntry { Batch = batch, Kind = "rpfDir", TrashPath = dest, ParentPath = parentPath, Name = rd.Name });
    }

    private void CmdUndo(Req r)
    {
        UndoEntry[] batch;
        lock (_undoStack)
        {
            if (_undoStack.Count == 0) { Send(new { type = "undone", reqId = r.Id, ok = false, message = "nothing to undo" }); return; }
            long top = _undoStack[^1].Batch;
            int i = _undoStack.Count;
            while (i > 0 && _undoStack[i - 1].Batch == top) i--;
            batch = _undoStack.GetRange(i, _undoStack.Count - i).ToArray();
            _undoStack.RemoveRange(i, _undoStack.Count - i);
        }

        int restored = 0; string? last = null;
        foreach (var e in batch)
        {
            try { RestoreUndo(e); restored++; last = Path.GetFileName(e.OrigPath.Length > 0 ? e.OrigPath : e.Name); }
            catch { }
        }
        Send(new { type = "undone", reqId = r.Id, ok = restored > 0, restored, name = last });
    }

    private void RestoreUndo(UndoEntry e)
    {
        // Restore targets re-resolve through the LIVE graph by path — never through a
        // graph object captured at delete time (stale after a remount = corruption).
        RpfDirectoryEntry? Parent()
        {
            var p = ResolveByPath(e.ParentPath) as RpfDirectoryEntry
                 ?? (ResolveByPath(e.ParentPath) is RpfFile rf ? rf.Root : null);
            if (p == null) throw new Exception("restore target gone: " + e.ParentPath);
            if (PrepareArchiveWrite(p) is string werr) throw new Exception(werr);
            return p;
        }
        switch (e.Kind)
        {
            case "disk":
                Directory.CreateDirectory(Path.GetDirectoryName(e.OrigPath)!);
                MarkSelfWrite(e.OrigPath); MarkSelfWrite(e.TrashPath);
                if (e.IsDir) Directory.Move(e.TrashPath, e.OrigPath); else File.Move(e.TrashPath, e.OrigPath);
                RescanIfArchive(e.OrigPath);
                break;
            case "rpfFile":
            {
                var parent = Parent()!;
                MarkSelfWrite(SafePhysical(parent.File));
                RpfFile.CreateFile(parent, e.Name, File.ReadAllBytes(e.TrashPath), true);
                try { File.Delete(e.TrashPath); } catch { }
                RescanIfArchive(e.Name);
                break;
            }
            case "rpfDir":
            {
                var parent = Parent()!;
                MarkSelfWrite(SafePhysical(parent.File));
                var dir = RpfFile.CreateDirectory(parent, e.Name);
                ImportDir(dir, e.TrashPath);
                try { Directory.Delete(e.TrashPath, true); } catch { }
                break;
            }
            case "baseRpf":
                // move the archive back; let the watcher re-scan it (no self-write mark)
                if (File.Exists(e.TrashPath)) File.Move(e.TrashPath, e.OrigPath);
                _watchRpfChanged = true; _watchDebounce?.Change(250, Timeout.Infinite);
                break;
        }
    }

    private static void ImportDir(RpfDirectoryEntry dir, string diskFolder)
    {
        foreach (var f in Directory.GetFiles(diskFolder))
            try { RpfFile.CreateFile(dir, Path.GetFileName(f), File.ReadAllBytes(f), true); } catch { }
        foreach (var d in Directory.GetDirectories(diskFolder))
            try { ImportDir(RpfFile.CreateDirectory(dir, Path.GetFileName(d)), d); } catch { }
    }

    private static string UniqueTrashPath(string name)
    {
        string baseName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeName(name)}";
        string p = Path.Combine(TrashDir, baseName);
        int i = 1;
        while (File.Exists(p) || Directory.Exists(p)) p = Path.Combine(TrashDir, $"{baseName}_{i++}");
        return p;
    }

    private static long TrashSize() => DirSize(TrashDir);

    private static long DirSize(string path)
    {
        long total = 0;
        try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch { } } }
        catch { }
        return total;
    }

    private static long FileLen(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    private static void PurgeTrashToFit(long incoming)
    {
        try
        {
            if (!Directory.Exists(TrashDir)) return;
            var items = new DirectoryInfo(TrashDir).GetFileSystemInfos().OrderBy(i => i.LastWriteTimeUtc).ToList();
            foreach (var it in items)
            {
                if (TrashSize() + incoming <= TrashLimitBytes) break;
                try { if (it is DirectoryInfo) Directory.Delete(it.FullName, true); else it.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private void CmdExtractAll(Req r)
    {
        _nodes.TryGetValue(r.Node, out var obj);
        var dir = ResolveDir(obj);
        var diskDir = obj as DiskItem;
        if (dir == null && (diskDir == null || !diskDir.IsDir))
        { Send(new { type = "extractedAll", reqId = r.Id, ok = false, message = "Extract all works on a folder." }); return; }

        string? outDir = _pickFolder();
        if (outDir == null) { Send(new { type = "extractedAll", reqId = r.Id, ok = false, canceled = true }); return; }
        int count = 0; long bytes = 0;
        try
        {
            if (dir != null) ExtractDir(dir, outDir, ref count, ref bytes);
            else
            {
                var d = Path.Combine(outDir, Path.GetFileName(diskDir!.Path));
                CopyDir(diskDir.Path, d);
                try { count = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length; } catch { count = 0; }
            }
        }
        catch (Exception ex) { Send(new { type = "extractedAll", reqId = r.Id, ok = false, message = ex.Message }); return; }
        Send(new { type = "extractedAll", reqId = r.Id, ok = true, path = outDir, count, bytes });
    }

    private static void ExtractDir(RpfDirectoryEntry dir, string outBase, ref int count, ref long bytes)
    {
        Directory.CreateDirectory(outBase);
        if (dir.Files != null)
            foreach (var fe in dir.Files)
                try { var d = RpfWorkspace.ExtractForSave(fe); File.WriteAllBytes(Path.Combine(outBase, SafeName(fe.Name)), d); count++; bytes += d.Length; }
                catch { }
        if (dir.Directories != null)
            foreach (var sub in dir.Directories)
                ExtractDir(sub, Path.Combine(outBase, SafeName(sub.Name)), ref count, ref bytes);
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // Register the new rpf in this archive's content.xml as an overlay override
    // (matches how update.rpf registers epic/epic.rpf — see the dataFiles + the
    // CCS_TITLE_UPDATE_STREAMING change set).
    private string TryAddContentOverride(RpfDirectoryEntry dir, string rpfName)
    {
        try
        {
            var host = dir.File;
            var cxEntry = host?.Root?.Files?.FirstOrDefault(f => f.NameLower == "content.xml");
            if (cxEntry == null) return "no content.xml here — created the RPF only";

            var doc = new XmlDocument();
            doc.LoadXml(Encoding.UTF8.GetString(RpfWorkspace.Extract(cxEntry)));

            string hostPath = host!.Path ?? "";
            string dirPath = dir.Path ?? "";
            string sub = dirPath.Length > hostPath.Length ? dirPath.Substring(hostPath.Length).TrimStart('\\', '/') : "";
            string cpath = "update:/" + (sub.Length > 0 ? sub.Replace('\\', '/') + "/" : "") + rpfName;

            var dataFiles = doc.SelectSingleNode("//dataFiles");
            if (dataFiles != null)
            {
                var item = doc.CreateElement("Item");
                item.AppendChild(El(doc, "filename", cpath));
                item.AppendChild(El(doc, "fileType", "RPF_FILE"));
                foreach (var (n, v) in new[] { ("locked", "true"), ("disabled", "true"), ("persistent", "true"), ("overlay", "true") })
                {
                    var e = doc.CreateElement(n); e.SetAttribute("value", v); item.AppendChild(e);
                }
                dataFiles.AppendChild(item);
            }

            var sets = doc.SelectNodes("//contentChangeSets/Item");
            if (sets != null)
                foreach (XmlNode c in sets)
                    if (c.SelectSingleNode("changeSetName")?.InnerText == "CCS_TITLE_UPDATE_STREAMING")
                    {
                        var fte = c.SelectSingleNode("filesToEnable");
                        if (fte != null) { var it = doc.CreateElement("Item"); it.InnerText = cpath; fte.AppendChild(it); }
                        break;
                    }

            using var ms = new MemoryStream();
            using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                doc.Save(w);
            RpfFile.CreateFile(host.Root, cxEntry.Name, ms.ToArray(), true);
            return "added content override → " + cpath;
        }
        catch (Exception ex) { return "content override failed: " + ex.Message; }
    }

    private static XmlElement El(XmlDocument doc, string name, string text)
    {
        var e = doc.CreateElement(name); e.InnerText = text; return e;
    }

    private static string ExtOf(string nameLower)
    {
        int dot = nameLower.LastIndexOf('.');
        return dot < 0 ? "" : nameLower.Substring(dot + 1);
    }

    private static string LangForExt(string ext) => ext switch
    {
        "xml" or "meta" or "ymt" or "ymap" or "ytyp" or "pso" or "rel" or "cut" => "xml",
        "lua" => "lua",
        "json" => "json",
        "ini" or "cfg" => "ini",
        _ => "plaintext",
    };

    private void OpenHexCore(Req r, string name, byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 64 * 1024);
        Send(new
        {
            type = "hex",
            reqId = r.Id,
            name,
            total = bytes.Length,
            shown = n,
            data = Convert.ToBase64String(bytes, 0, n),
        });
    }

    // ---- helpers ----------------------------------------------------------

    private object[] EnumerateChildren(object obj)
    {
        var list = new List<object>();
        switch (obj)
        {
            case DiskItem di when di.IsDir:
                AddDiskDir(list, di.Path);
                break;
            case RpfFile rfile:
                AddDir(list, rfile.Root);
                break;
            case RpfDirectoryEntry d:
                AddDir(list, d);
                break;
            case RpfFileEntry fe when fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal):
                AddDir(list, fe.File?.FindChildArchive(fe)?.Root);
                break;
        }
        return list.ToArray();
    }

    // Enumerate a real on-disk directory: subfolders, mounted .rpf archives, and
    // every loose file (so the user's own folders/files all show up).
    private void AddDiskDir(List<object> list, string path)
    {
        string[] dirs, files;
        try { dirs = Directory.GetDirectories(path); } catch { dirs = Array.Empty<string>(); }
        try { files = Directory.GetFiles(path); } catch { files = Array.Empty<string>(); }

        foreach (var d in dirs.OrderBy(x => x, OIC)) list.Add(DiskDirNode(d));
        foreach (var f in files.OrderBy(x => x, OIC))
        {
            if (f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && _baseRpfByPath.TryGetValue(f, out var rpf))
                list.Add(ArchiveNode(rpf));
            else
                list.Add(DiskFileNode(f));
        }
    }

    private object DiskDirNode(string path)
    {
        int count = 0;
        try { count = Directory.EnumerateFileSystemEntries(path).Count(); } catch { }
        return new { id = Register(new DiskItem { Path = path, IsDir = true }), name = Path.GetFileName(path),
            kind = "dir", container = true, expandable = true, type = "Folder", size = -1L, count, attrs = "", path };
    }

    private object DiskFileNode(string path)
    {
        long size = -1; try { size = new FileInfo(path).Length; } catch { }
        string name = Path.GetFileName(path);
        return new { id = Register(new DiskItem { Path = path, IsDir = false }), name,
            kind = "file", container = false, expandable = false, type = FriendlyType(name), size, attrs = "Loose",
            viewer = FileTypes.Route(name).ToString().ToLowerInvariant(), path };
    }

    private void AddDir(List<object> list, RpfDirectoryEntry? dir)
    {
        if (dir == null) return;
        foreach (var d in dir.Directories.OrderBy(x => x.Name, OIC)) list.Add(DirNode(d));
        foreach (var fe in dir.Files.OrderBy(x => x.Name, OIC))
            list.Add(fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? ArchiveNode(fe) : FileNode(fe));
    }

    private object DirNode(RpfDirectoryEntry d) => new
    { id = Register(d), name = d.Name, kind = "dir", container = true, expandable = true, type = "Folder", size = -1L, count = d.Directories.Count + d.Files.Count, attrs = "", path = d.Path };

    private object ArchiveNode(RpfFile rf) => new
    {
        id = Register(rf), name = rf.Name, kind = "archive", container = true, expandable = true,
        type = "Archive", size = rf.FileSize, count = ChildCount(rf.Root),
        attrs = rf.Encryption is RpfEncryption.NONE or RpfEncryption.OPEN ? "" : rf.Encryption.ToString(),
        path = rf.Path,
    };

    private object ArchiveNode(RpfFileEntry fe) => new
    {
        id = Register(fe), name = fe.Name, kind = "archive", container = true, expandable = true,
        type = "Archive", size = SafeSize(fe), count = ChildCount(SafeChildRoot(fe)), attrs = Attrs(fe),
        path = fe.Path,
    };

    private static int ChildCount(RpfDirectoryEntry? dir)
        => dir == null ? 0 : dir.Directories.Count + dir.Files.Count;

    private static RpfDirectoryEntry? SafeChildRoot(RpfFileEntry fe)
    {
        try { return fe.File?.FindChildArchive(fe)?.Root; } catch { return null; }
    }

    private object FileNode(RpfFileEntry fe) => new
    {
        id = Register(fe), name = fe.Name, kind = "file", container = false, expandable = false,
        type = FriendlyType(fe.Name), size = SafeSize(fe), attrs = Attrs(fe),
        viewer = FileTypes.Route(fe.Name).ToString().ToLowerInvariant(),
        path = fe.Path,
    };

    private static string FriendlyType(string name) => ExtOf(name.ToLowerInvariant()) switch
    {
        "ydr" => "Drawable", "ydd" => "Drawable dictionary", "yft" => "Fragment", "ytd" => "Texture dictionary",
        "ypt" => "Particle effect", "ycd" => "Clip dictionary", "ybn" => "Collision", "ynv" => "Nav mesh",
        "ymap" => "Map", "ytyp" => "Type definition", "ymt" => "Metadata", "ymf" => "Manifest", "meta" => "Metadata",
        "xml" => "XML", "rel" => "Audio data", "awc" => "Audio", "gxt2" => "Text table", "dat" => "Data",
        "gfx" => "Scaleform (Flash)",
        "rpf" => "Archive", "txt" => "Text", "ini" => "Config", "lua" => "Script", "" => "File",
        var e => e.ToUpperInvariant() + " file",
    };

    private static string Attrs(RpfFileEntry fe)
    {
        if (fe is RpfResourceFileEntry) return "Resource";
        if (fe is RpfBinaryFileEntry b)
        {
            var parts = new List<string>();
            if (b.FileUncompressedSize > 0 && fe.FileSize > 0 && fe.FileSize != b.FileUncompressedSize) parts.Add("Compressed");
            if (b.EncryptionType != 0) parts.Add("Encrypted");
            return string.Join(";", parts);
        }
        return "";
    }

    private int Register(object o)
    {
        int id = _nextNodeId++;
        _nodes[id] = o;
        return id;
    }

    private Dictionary<string, object?> BuildModelDto(ModelData m, DrawableBase d, RpfFileEntry? modelEntry,
        TextureDictionary? localDict = null, List<TextureDictionary>? extraDicts = null)
    {
        EnsureShaderNames();
        var shaders = d.ShaderGroup?.Shaders?.data_items;

        // Pre-index external textures this model needs (those not embedded with data) —
        // ALL samplers (diffuse + normal + spec…), so lighting maps resolve too.
        // Only possible for archive entries (loose disk files resolve embedded only).
        if (shaders != null && _resolver != null && modelEntry != null)
        {
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in shaders)
                foreach (var (_, tb) in SamplerRefs(s))
                    if (tb != null && !(tb is Texture tx && tx.Data?.FullData != null) && !string.IsNullOrEmpty(tb.Name))
                        needed.Add(tb.Name);
            if (needed.Count > 0) _resolver.IndexForModel(modelEntry, needed);
        }

        var texCache = new Dictionary<(Texture t, bool nrm), string?>();
        var mats = new List<object>();
        for (int i = 0; i < m.Materials.Count; i++)
        {
            var s = (shaders != null && i < shaders.Length) ? shaders[i] : null;
            mats.Add(MatDto(s, m.Materials[i], localDict, extraDicts, texCache));
        }
        // "Additions": meshes living on extra_* bones (toggleable spawn parts on
        // vehicles etc.) — rigid bone binding or, on skinned models, the geometry's
        // dominant blend bone. Hidden by default; the UI lists them as checkboxes.
        var boneNames = d.Skeleton?.Bones?.Items?.Select(b => b?.Name ?? "").ToArray();
        var additions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (boneNames != null)
            foreach (var lod in m.Lods)
                foreach (var mesh in lod.Meshes)
                {
                    int bIdx = mesh.HasSkin ? mesh.DominantBone : mesh.BoneIndex;
                    if (bIdx > 0 && bIdx < boneNames.Length && boneNames[bIdx].StartsWith("extra_", StringComparison.OrdinalIgnoreCase))
                        additions.Add(boneNames[bIdx]);
                }

        return new Dictionary<string, object?>
        {
            ["name"] = m.Name,
            ["bmin"] = new[] { m.BoundsMin.X, m.BoundsMin.Y, m.BoundsMin.Z },
            ["bmax"] = new[] { m.BoundsMax.X, m.BoundsMax.Y, m.BoundsMax.Z },
            ["materials"] = mats,
            ["skeleton"] = SkeletonDto(d),
            ["additions"] = additions.Count > 0 ? additions.ToArray() : null,
            ["lods"] = m.Lods.Select(l => new { level = l.Level, meshes = l.Meshes.Select(MeshDto).ToArray() }).ToArray(),
        };
    }

    // Full material DTO: shader identity, every texture sampler (with its resolved
    // image where found), and the editable value parameters. The renderer maps the
    // diffuse/normal/spec roles to its lighting model; everything is shown in the UI.
    private object MatDto(ShaderFX? s, MaterialData md, TextureDictionary? localDict,
        List<TextureDictionary>? extras, Dictionary<(Texture t, bool nrm), string?> cache)
    {
        string shader = md.ShaderName;
        string sps = "";
        try { sps = s?.FileName.ToString() ?? ""; } catch { }
        string shaderLower = shader.ToLowerInvariant() + " " + sps.ToLowerInvariant();

        var texs = new List<object>();
        string? diffuse = md.DiffuseTextureName, tex = null, nrmTex = null, spcTex = null, firstUrl = null;
        if (s != null)
        {
            foreach (var (sampler, tb) in SamplerRefs(s))
            {
                var t = ResolveTexture(tb, localDict, extras);
                string role = RoleFor(sampler);
                bool asNormal = role == "normal";
                string? url = null;
                if (t != null && !cache.TryGetValue((t, asNormal), out url))
                { url = DecodeTexUrl(t, asNormal); cache[(t, asNormal)] = url; }

                texs.Add(new
                {
                    sampler, role,
                    name = tb?.Name,
                    found = url != null,
                    w = t?.Width ?? 0, h = t?.Height ?? 0,
                    fmt = t != null ? t.Format.ToString().Replace("D3DFMT_", "") : "",
                    embedded = tb is Texture et && et.Data?.FullData != null,
                });
                firstUrl ??= url;
                if (role == "diffuse" && tex == null) { tex = url; diffuse = tb?.Name ?? diffuse; }
                else if (role == "normal" && nrmTex == null) nrmTex = url;
                else if (role == "spec" && spcTex == null) spcTex = url;
            }
        }
        tex ??= firstUrl;   // no diffuse-named sampler -> first resolved texture (old behaviour)

        // Editable value parameters (Vector4 / first element of Vector4 arrays).
        var prms = new List<object>();
        var ps = s?.ParametersList?.Parameters;
        var hs = s?.ParametersList?.Hashes;
        float emissiveMult = 1f;
        if (ps != null && hs != null)
        {
            for (int i = 0; i < ps.Length && i < hs.Length; i++)
            {
                float[]? vals = ps[i].Data switch
                {
                    SharpDX.Vector4 v => new[] { v.X, v.Y, v.Z, v.W },
                    SharpDX.Vector4[] { Length: > 0 } arr => new[] { arr[0].X, arr[0].Y, arr[0].Z, arr[0].W },
                    _ => null,
                };
                if (vals == null) continue;
                string pname = ParamName(hs[i]);
                int count = ps[i].Data is SharpDX.Vector4[] a ? a.Length : 1;
                prms.Add(new { name = pname, values = vals, count });
                if (pname.Equals("emissiveMultiple", StringComparison.OrdinalIgnoreCase)) emissiveMult = vals[0];
            }
        }

        bool emissive = shaderLower.Contains("emissive") || shaderLower.Contains("glow");
        return new
        {
            shader, sps,
            bucket = (int)(s?.RenderBucket ?? 0),
            diffuse, tex, nrmTex, spcTex,
            emissive, emissiveMult,
            texs,
            @params = prms,
        };
    }

    // Shader parameter names are ShaderParamNames-enum hashes (the same resolution
    // CodeWalker's XML writer uses); fall back to the MetaName/Jenkins string.
    private static string ParamName(MetaName h)
    {
        string s = ((ShaderParamNames)(uint)h).ToString();
        if (s.Length > 0 && !char.IsDigit(s[0])) return s;
        return h.ToString();
    }

    // All texture-sampler parameters of a shader, with their (resolved-or-not) refs.
    private static IEnumerable<(string sampler, TextureBase? tb)> SamplerRefs(ShaderFX s)
    {
        ShaderParameter[]? ps = null; MetaName[]? hs = null;
        try { ps = s.ParametersList?.Parameters; hs = s.ParametersList?.Hashes; } catch { }
        if (ps == null) yield break;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i]?.Data is TextureBase tb)
                yield return ((hs != null && i < hs.Length) ? ParamName(hs[i]) : "Sampler" + i, tb);
    }

    // Classify a sampler name into a renderer role.
    private static string RoleFor(string sampler)
    {
        string s = sampler.ToLowerInvariant();
        if (s.Contains("bump") || s.Contains("normal")) return "normal";
        if (s.Contains("spec")) return "spec";
        if (s.Contains("detail")) return "detail";
        if (s.Contains("tint") || s.Contains("palette")) return "tint";
        if (s.Contains("diffuse") || s.Contains("texturesamp") || s.Contains("platebg")) return "diffuse";
        return "other";
    }

    // Bone hierarchy for the details panel, 3D overlay, rigid-part placement, skinning
    // and animation. World positions come from the skeleton's absolute transforms;
    // local translation/rotation/scale rebuild the same hierarchy renderer-side.
    private static object? SkeletonDto(DrawableBase? d)
    {
        try
        {
            var bones = d?.Skeleton?.Bones?.Items;
            if (bones == null || bones.Length == 0) return null;
            var list = new List<object>(bones.Length);
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                var p = b.AbsTransform.TranslationVector;
                var lt = b.Translation; var lr = b.Rotation; var ls = b.Scale;
                list.Add(new
                {
                    i,
                    name = string.IsNullOrEmpty(b.Name) ? "bone_" + i : b.Name,
                    tag = (int)b.Tag,
                    parent = (int)b.ParentIndex,
                    pos = new[] { p.X, p.Y, p.Z },
                    lp = new[] { lt.X, lt.Y, lt.Z },
                    lr = new[] { lr.X, lr.Y, lr.Z, lr.W },
                    ls = new[] { ls.X, ls.Y, ls.Z },
                });
            }
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private Texture? ResolveTexture(TextureBase? tb, TextureDictionary? localDict = null, List<TextureDictionary>? extras = null)
    {
        if (tb == null) return null;

        // User-picked txds win (the user pointed at them explicitly).
        if (extras != null)
            foreach (var dict in extras)
            {
                var ut = LookupTex(dict, tb);
                if (ut?.Data?.FullData != null) return ut;
            }

        if (tb is Texture t && t.Data?.FullData != null) return t;   // embedded

        // The file's own dictionary (e.g. a .ypt's particle texture dict).
        var lt = LookupTex(localDict, tb);
        if (lt?.Data?.FullData != null) return lt;

        if (_resolver != null)
        {
            var ext = _resolver.Resolve(tb.Name);
            if (ext?.Data?.FullData != null) return ext;             // external .ytd
        }
        return null;
    }

    private static Texture? LookupTex(TextureDictionary? dict, TextureBase tb)
    {
        var items = dict?.Textures?.data_items;
        if (items == null) return null;
        foreach (var t in items)
            if (t != null && (t.NameHash == tb.NameHash ||
                              (!string.IsNullOrEmpty(t.Name) && t.Name == tb.Name)))
                return t;
        return null;
    }

    private static string? DecodeTexUrl(Texture t, bool normalMap = false)
    {
        try
        {
            var rgba = TextureCodec.DecodeTexture(t, out int w, out int h);
            if (rgba == null) return null;
            if (normalMap) PrepareNormalMap(rgba, t.Format.ToString());
            return ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h));
        }
        catch { return null; }
    }

    // RAGE BC5/ATI2 normal maps carry only X+Y; reconstruct Z so the renderer can use
    // them as standard RGB normal maps (other formats already store full RGB normals).
    private static void PrepareNormalMap(byte[] rgba, string format)
    {
        string f = format.ToUpperInvariant();
        if (!f.Contains("ATI2") && !f.Contains("BC5")) return;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float x = rgba[i] / 255f * 2f - 1f;
            float y = rgba[i + 1] / 255f * 2f - 1f;
            float z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
            rgba[i + 2] = (byte)Math.Clamp((int)MathF.Round((z * 0.5f + 0.5f) * 255f), 0, 255);
            rgba[i + 3] = 255;
        }
    }

    // Shader names live as Jenkins hashes; seed the index with the common RAGE shader
    // names once so ShaderFX.Name/FileName resolve to readable strings (e.g.
    // "ped_emissive" instead of 2401522793).
    private static bool _shaderNamesSeeded;
    private static void EnsureShaderNames()
    {
        if (_shaderNamesSeeded) return;
        _shaderNamesSeeded = true;
        string[] names =
        {
            "default", "default_detail", "default_spec", "default_terrain_cb", "default_tnt",
            "normal", "normal_alpha", "normal_cutout", "normal_decal", "normal_detail", "normal_diffspec",
            "normal_diffspec_detail", "normal_pxm", "normal_pxm_tnt", "normal_reflect", "normal_reflect_alpha",
            "normal_reflect_decal", "normal_spec", "normal_spec_alpha", "normal_spec_cutout", "normal_spec_decal",
            "normal_spec_decal_detail", "normal_spec_decal_pxm", "normal_spec_detail", "normal_spec_detail_dpm",
            "normal_spec_detail_tnt", "normal_spec_dpm", "normal_spec_emissive", "normal_spec_pxm",
            "normal_spec_reflect", "normal_spec_reflect_decal", "normal_spec_reflect_emissivenight",
            "normal_spec_tnt", "normal_spec_um", "normal_spec_wrinkle", "normal_terrain_wet", "normal_tnt",
            "normal_um", "normal_um_tnt", "spec", "spec_alpha", "spec_decal", "spec_reflect", "spec_reflect_alpha",
            "spec_reflect_decal", "spec_tnt", "spec_twiddle_tnt", "alpha", "cutout_fence", "cutout_fence_normal",
            "decal", "decal_amb_only", "decal_diff_only_um", "decal_dirt", "decal_emissive_only",
            "decal_emissivenight_only", "decal_glue", "decal_normal_only", "decal_shadow_only", "decal_spec_only",
            "decal_tnt", "custom_default", "cloth_default", "cloth_normal_spec", "cloth_normal_spec_alpha",
            "cloth_normal_spec_cutout", "cloth_normal_spec_tnt", "cloth_spec_alpha", "cloth_spec_cutout",
            "emissive", "emissive_additive_alpha", "emissive_additive_uv_alpha", "emissive_alpha",
            "emissive_alpha_tnt", "emissive_clip", "emissive_speclum", "emissive_tnt", "emissivenight",
            "emissivenight_alpha", "emissivenight_geomnightonly", "emissivestrong", "emissivestrong_alpha",
            "glass", "glass_breakable", "glass_breakable_screendooralpha", "glass_displacement",
            "glass_emissive", "glass_emissive_alpha", "glass_emissivenight", "glass_emissivenight_alpha",
            "glass_env", "glass_normal_spec_reflect", "glass_pv", "glass_pv_env", "glass_reflect",
            "glass_spec", "mirror_crack", "mirror_decal", "mirror_default",
            "ped", "ped_alpha", "ped_cloth", "ped_cloth_enveff", "ped_decal", "ped_decal_decoration",
            "ped_decal_expensive", "ped_decal_nodiff", "ped_default", "ped_default_cloth", "ped_default_enveff",
            "ped_default_mp", "ped_default_palette", "ped_emissive", "ped_enveff", "ped_fur", "ped_hair_cutout_alpha",
            "ped_hair_spiked", "ped_nopeddamagedecals", "ped_palette", "ped_wrinkle", "ped_wrinkle_cloth",
            "ped_wrinkle_cloth_enveff", "ped_wrinkle_cs", "ped_wrinkle_enveff",
            "vehicle_badges", "vehicle_basic", "vehicle_blurredrotor", "vehicle_blurredrotor_emissive",
            "vehicle_cloth", "vehicle_cloth2", "vehicle_dash_emissive", "vehicle_dash_emissive_opaque",
            "vehicle_decal", "vehicle_decal2", "vehicle_detail", "vehicle_detail2", "vehicle_emissive_alpha",
            "vehicle_emissive_opaque", "vehicle_generic", "vehicle_interior", "vehicle_interior2",
            "vehicle_licenseplate", "vehicle_lightsemissive", "vehicle_mesh", "vehicle_mesh2_enveff",
            "vehicle_mesh_enveff", "vehicle_paint1", "vehicle_paint1_enveff", "vehicle_paint2",
            "vehicle_paint2_enveff", "vehicle_paint3", "vehicle_paint3_enveff", "vehicle_paint3_lvr",
            "vehicle_paint4", "vehicle_paint4_emissive", "vehicle_paint4_enveff", "vehicle_paint5_enveff",
            "vehicle_paint6", "vehicle_paint6_enveff", "vehicle_paint7", "vehicle_paint7_enveff",
            "vehicle_paint8", "vehicle_paint9", "vehicle_shuts", "vehicle_tire", "vehicle_tire_emissive",
            "vehicle_track", "vehicle_track2", "vehicle_track2_emissive", "vehicle_track_ammo",
            "vehicle_track_emissive", "vehicle_vehglass", "vehicle_vehglass_inner",
            "weapon_emissivestrong_alpha", "weapon_emissive_tnt", "weapon_normal_spec_alpha",
            "weapon_normal_spec_cutout_palette", "weapon_normal_spec_detail_palette",
            "weapon_normal_spec_detail_tnt", "weapon_normal_spec_palette", "weapon_normal_spec_tnt",
            "terrain_cb_4lyr", "terrain_cb_4lyr_2tex", "terrain_cb_4lyr_2tex_blend",
            "terrain_cb_4lyr_2tex_blend_lod", "terrain_cb_4lyr_2tex_blend_pxm",
            "terrain_cb_4lyr_2tex_blend_pxm_spm", "terrain_cb_4lyr_2tex_pxm", "terrain_cb_4lyr_cm",
            "terrain_cb_4lyr_cm_pxm", "terrain_cb_4lyr_cm_pxm_tnt", "terrain_cb_4lyr_cm_tnt",
            "terrain_cb_4lyr_lod", "terrain_cb_4lyr_pxm", "terrain_cb_4lyr_pxm_spm", "terrain_cb_4lyr_spec",
            "terrain_cb_4lyr_spec_int", "terrain_cb_4lyr_spec_int_pxm", "terrain_cb_4lyr_spec_pxm",
            "trees", "trees_lod", "trees_lod2", "trees_lod2d", "trees_lod_tnt", "trees_normal",
            "trees_normal_diffspec", "trees_normal_diffspec_tnt", "trees_normal_spec", "trees_normal_spec_tnt",
            "trees_shadow_proxy", "trees_tnt", "grass", "grass_batch", "grass_fur", "grass_fur_mask",
            "water_terrainfoam", "minimap", "gta_radar", "radar", "ptfx_model", "sky_system",
            "cable", "cpv_only", "clouds_altitude", "clouds_anim", "clouds_animsoft", "clouds_fast",
            "clouds_fog", "clouds_soft", "parallax", "parallax_specmap", "reflect", "reflect_alpha",
            "reflect_decal", "distance_map", "gta_default", "rope_default", "rope_normal",
        };
        foreach (var n in names) { JenkIndex.Ensure(n); JenkIndex.Ensure(n + ".sps"); }
    }

    private void CmdEncodeDds(Req r)
    {
        try
        {
            byte[] rgba = Convert.FromBase64String(r.Rgba ?? "");
            if (r.W <= 0 || r.H <= 0 || rgba.Length < (long)r.W * r.H * 4)
            { Send(new { type = "encoded", reqId = r.Id, ok = false, message = "bad image data" }); return; }

            byte[] dds = TextureCodec.EncodeDds(rgba, r.W, r.H, r.Format ?? "DXT5", true);
            string suggested = r.Name ?? "texture.dds";
            if (!suggested.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) suggested += ".dds";
            string? path = _pickSavePath(suggested);
            if (path == null) { Send(new { type = "encoded", reqId = r.Id, ok = false, canceled = true }); return; }
            if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) path += ".dds";
            File.WriteAllBytes(path, dds);
            Send(new { type = "encoded", reqId = r.Id, ok = true, path, size = dds.Length, format = r.Format ?? "DXT5" });
        }
        catch (Exception ex) { Send(new { type = "encoded", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdDecodeDds(Req r)
    {
        try
        {
            byte[] dds = Convert.FromBase64String(r.Content ?? "");
            var rgba = TextureCodec.DecodeDds(dds, out int w, out int h);
            if (rgba == null) { Send(new { type = "image", reqId = r.Id, message = "decode failed" }); return; }
            Send(new
            {
                type = "image", reqId = r.Id, name = r.Name ?? "texture.dds", format = "DDS",
                width = w, height = h, dataUrl = ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h)),
            });
        }
        catch (Exception ex) { Send(new { type = "image", reqId = r.Id, message = ex.Message }); }
    }

    private static object MeshDto(MeshData m) => new
    {
        vcount = m.VertexCount,
        icount = m.Indices.Length,
        mat = m.MaterialIndex,
        pos = B64(m.Positions),
        nrm = m.Normals != null ? B64(m.Normals) : null,
        uv0 = m.TexCoords0 != null ? B64(m.TexCoords0) : null,
        col = m.Colors0 != null ? B64(m.Colors0) : null,
        bw = m.HasSkin && m.BlendWeights != null ? B64(m.BlendWeights) : null,
        bi = m.HasSkin && m.BlendIndices != null ? Convert.ToBase64String(m.BlendIndices) : null,
        idx = B64(m.Indices),
        bone = m.BoneIndex,
        skin = m.HasSkin,
        domBone = m.DominantBone,
        bmin = new[] { m.BoundsMin.X, m.BoundsMin.Y, m.BoundsMin.Z },
        bmax = new[] { m.BoundsMax.X, m.BoundsMax.Y, m.BoundsMax.Z },
    };

    private static string B64(float[] a)
    {
        if (a.Length == 0) return "";
        var b = new byte[a.Length * 4];
        Buffer.BlockCopy(a, 0, b, 0, b.Length);
        return Convert.ToBase64String(b);
    }

    private static string B64(uint[] a)
    {
        if (a.Length == 0) return "";
        var b = new byte[a.Length * 4];
        Buffer.BlockCopy(a, 0, b, 0, b.Length);
        return Convert.ToBase64String(b);
    }

    private static long SafeSize(RpfFileEntry fe)
    {
        try { return fe.GetFileSize(); } catch { return fe.FileSize; }
    }

    private static bool LooksText(byte[] b)
    {
        int n = Math.Min(b.Length, 2048);
        int bad = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 0) return false;
            if (c < 9 || (c > 13 && c < 32)) bad++;
        }
        return bad < n / 20 + 1;
    }

    private static string DecodeText(byte[] b)
    {
        try { return Encoding.UTF8.GetString(b); }
        catch { return Encoding.Latin1.GetString(b); }
    }

    private void Send(object o) => _post(JsonSerializer.Serialize(o, JsonOpts));
}
