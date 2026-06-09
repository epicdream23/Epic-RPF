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
                  Action<string>? windowAction = null, Action<int, string>? openPopout = null)
    {
        _post = post;
        _pickFolder = pickFolder;
        _pickSavePath = pickSavePath;
        _windowAction = windowAction;
        _openPopout = openPopout;
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
    private sealed class UndoEntry
    {
        public long Batch;
        public string Kind = "";       // "disk" | "rpfFile" | "rpfDir" | "baseRpf"
        public string TrashPath = "";
        public bool IsDir;
        public string OrigPath = "";
        public RpfDirectoryEntry? Parent;
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

    public void HandleMessage(string json)
    {
        Req req;
        try { req = JsonSerializer.Deserialize<Req>(json, JsonOpts) ?? new Req(); }
        catch { return; }

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
            case "save": CmdSave(r); break;
            case "encodeDds": CmdEncodeDds(r); break;
            case "decodeDds": CmdDecodeDds(r); break;
            case "extract": CmdExtract(r); break;
            case "extractAll": CmdExtractAll(r); break;
            case "createFolder": CmdCreateFolder(r); break;
            case "createRpf": CmdCreateRpf(r); break;
            case "createYtd": CmdCreateYtd(r); break;
            case "importFile": CmdImportFile(r); break;
            case "delete": CmdDelete(r); break;
            case "undo": CmdUndo(r); break;
            case "modelPart": CmdModelPart(r); break;
            case "texImage": CmdTexImage(r); break;
            case "replaceTexture": CmdReplaceTexture(r); break;
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
                    TextureDictionary? ld = null;
                    var dws = DrawableLoader.LoadLoose(bytes, name);
                    if (ext == "ypt") { try { ld = RpfFile.GetResourceFile<YptFile>(bytes)?.PtfxList?.TextureDictionary; } catch { } }
                    SendModelData(r, name, di.Path, dws, ld, null);
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

    // ---- openers ----------------------------------------------------------

    private void OpenModel(Req r, RpfFileEntry fe)
    {
        var drawables = DrawableLoader.Load(_ws!.Manager, fe);

        // A .ypt keeps its textures in the particle list's own dictionary (not inside
        // each drawable's shader group), so resolve diffuse textures against it and
        // surface the dictionary for the viewer's texture strip.
        TextureDictionary? localDict = null;
        if (fe.NameLower.EndsWith(".ypt", StringComparison.Ordinal))
        {
            try { localDict = _ws.Manager.GetFile<YptFile>(fe)?.PtfxList?.TextureDictionary; } catch { }
        }
        SendModelData(r, fe.Name, fe.Path, drawables, localDict, fe);
    }

    private void SendModelData(Req r, string name, string path,
        List<DrawableLoader.NamedDrawable> drawables, TextureDictionary? localDict, RpfFileEntry? modelEntry)
    {
        if (drawables.Count == 0 && localDict == null)
        {
            Send(new { type = "error", reqId = r.Id, message = "no drawable found in " + name });
            return;
        }

        // Cache the loaded drawables/dict so other parts and textures load on demand.
        // For a loose file (no archive entry) keep the disk path so texture edits can save.
        int cacheKey = r.Node > 0 ? r.Node : _synthSeq--;
        CacheModel(cacheKey, new ModelCache { Drawables = drawables, LocalDict = localDict, Entry = modelEntry, EntryPath = modelEntry?.Path ?? "", File = name, DiskPath = modelEntry == null ? path : "" });

        // Only decode the first drawable now (the viewer shows one at a time); the
        // rest are placeholders loaded when selected. Textures stream in lazily too.
        string fileLower = name.ToLowerInvariant();
        var parts = new List<object>();
        object stats = new { geometryCount = 0, rendered = 0, skipped = 0, skipSamples = Array.Empty<string>() };
        for (int i = 0; i < drawables.Count; i++)
        {
            if (i == 0) { parts.Add(BuildPart(drawables[i], modelEntry, localDict, fileLower, out stats)); }
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
    private object BuildPart(DrawableLoader.NamedDrawable nd, RpfFileEntry? modelEntry, TextureDictionary? localDict, string fileLower, out object stats)
    {
        var model = GeometryDecoder.Decode(nd.Drawable, nd.Name);
        stats = new { geometryCount = model.GeometryCount, rendered = model.RenderedCount, skipped = model.SkippedReasons.Count, skipSamples = model.SkippedReasons.Take(5).ToArray() };
        var dto = (Dictionary<string, object?>)BuildModelDto(model, nd.Drawable, modelEntry, localDict);
        string hashHex = nd.Hash.ToString("X8");
        dto["hash"] = hashHex;
        dto["lazy"] = false;
        dto["name"] = DisplayName(fileLower, hashHex, nd.Name);
        return dto;
    }

    private void CmdModelPart(Req r)
    {
        if (!_modelCache.TryGetValue(r.Node, out var mc) || r.Index < 0 || r.Index >= mc.Drawables.Count)
        { Send(new { type = "modelPart", reqId = r.Id, ok = false }); return; }
        var part = BuildPart(mc.Drawables[r.Index], mc.Entry, mc.LocalDict, mc.File.ToLowerInvariant(), out var stats);
        Send(new { type = "modelPart", reqId = r.Id, ok = true, index = r.Index, part, stats });
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
            if (fe != null) { MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
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
            string suggested = r.Format == "meta" ? fileName + ".xml" : fileName;
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
            if (fe != null) { MarkSelfWrite(SafePhysical(fe.File)); RpfFile.CreateFile(fe.Parent, fe.Name, data, true); }
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

        var crumbs = new List<object> { new { id = 0, name = _rootName } };
        foreach (var (node, name) in stack) crumbs.Add(new { id = Register(node), name });

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

    // A create target is either a real disk directory or a directory inside an
    // archive. nodeId 0 = the GTA V root folder (so you can create at the top level).
    private bool ResolveTarget(int nodeId, out string? diskDir, out RpfDirectoryEntry? rpfDir, out string? err)
    {
        diskDir = null; rpfDir = null; err = null;
        if (nodeId == 0) { diskDir = _gtaFolder; return diskDir.Length > 0; }
        if (!_nodes.TryGetValue(nodeId, out var obj)) { err = "invalid location"; return false; }
        if (obj is DiskItem di && di.IsDir) { diskDir = di.Path; return true; }
        rpfDir = ResolveDir(obj);
        if (rpfDir != null) return true;
        err = "Can't create here.";
        return false;
    }

    private void CmdCreateFolder(Req r)
    {
        if (!ResolveTarget(r.Node, out var disk, out var dir, out var err))
        { Send(new { type = "created", reqId = r.Id, ok = false, message = err }); return; }
        string name = (r.Name ?? "").Trim();
        if (name.Length == 0) { Send(new { type = "created", reqId = r.Id, ok = false, message = "name required" }); return; }
        try
        {
            if (disk != null) { var p = Path.Combine(disk, name); MarkSelfWrite(p); Directory.CreateDirectory(p); }
            else RpfFile.CreateDirectory(dir!, name);
            Send(new { type = "created", reqId = r.Id, ok = true, kind = "folder", name });
        }
        catch (Exception ex) { Send(new { type = "created", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdCreateRpf(Req r)
    {
        if (!ResolveTarget(r.Node, out var disk, out var dir, out var err))
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
                RpfFile.CreateNew(dir!, name, RpfEncryption.OPEN);
                MarkSelfWrite(SafePhysical(dir!.File));
                note = r.Override ? TryAddContentOverride(dir, name) : null;
            }
            Send(new { type = "created", reqId = r.Id, ok = true, kind = "rpf", name, note });
        }
        catch (Exception ex) { Send(new { type = "created", reqId = r.Id, ok = false, message = ex.Message }); }
    }

    private void CmdCreateYtd(Req r)
    {
        if (!ResolveTarget(r.Node, out var disk, out var dir, out var err))
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
            else { RpfFile.CreateFile(dir!, name, data, true); MarkSelfWrite(SafePhysical(dir!.File)); }
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
        if (!ResolveTarget(r.Node, out var disk, out var dir, out var err))
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
            else { MarkSelfWrite(SafePhysical(dir!.File)); RpfFile.CreateFile(dir!, outName, data, true); }
            Send(new { type = "imported", reqId = r.Id, ok = true, name = outName, size = data.Length });
        }
        catch (Exception ex) { Send(new { type = "imported", reqId = r.Id, ok = false, message = ex.Message }); }
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
        if (!_nodes.TryGetValue(r.Node, out var obj))
        { Send(new { type = "deleted", reqId = r.Id, ok = false, message = "invalid node" }); return; }

        string name; long incoming;
        switch (obj)
        {
            case DiskItem di: name = Path.GetFileName(di.Path); incoming = di.IsDir ? DirSize(di.Path) : FileLen(di.Path); break;
            case RpfFile rf: name = rf.Name; incoming = SafePhysical(rf) is string p ? FileLen(p) : 0; break;
            case RpfFileEntry fe: name = fe.Name; incoming = SafeSize(fe); break;
            case RpfDirectoryEntry rd: name = rd.Name; incoming = 0; break;
            default: Send(new { type = "deleted", reqId = r.Id, ok = false, message = "can't delete this" }); return;
        }

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
        var parent = fe.Parent;
        RpfFile.DeleteEntry(fe);
        PushUndo(new UndoEntry { Batch = batch, Kind = "rpfFile", TrashPath = dest, Parent = parent, Name = fe.Name });
    }

    private void MoveRpfDirToTrash(RpfDirectoryEntry rd, long batch)
    {
        string dest = UniqueTrashPath(rd.Name);
        try { int c = 0; long b = 0; ExtractDir(rd, dest, ref c, ref b); } catch { }
        MarkSelfWrite(SafePhysical(rd.File));
        var parent = rd.Parent;
        RpfFile.DeleteEntry(rd);
        PushUndo(new UndoEntry { Batch = batch, Kind = "rpfDir", TrashPath = dest, Parent = parent, Name = rd.Name });
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
        switch (e.Kind)
        {
            case "disk":
                Directory.CreateDirectory(Path.GetDirectoryName(e.OrigPath)!);
                MarkSelfWrite(e.OrigPath); MarkSelfWrite(e.TrashPath);
                if (e.IsDir) Directory.Move(e.TrashPath, e.OrigPath); else File.Move(e.TrashPath, e.OrigPath);
                break;
            case "rpfFile":
                if (e.Parent == null) return;
                MarkSelfWrite(SafePhysical(e.Parent.File));
                RpfFile.CreateFile(e.Parent, e.Name, File.ReadAllBytes(e.TrashPath), true);
                try { File.Delete(e.TrashPath); } catch { }
                break;
            case "rpfDir":
                if (e.Parent == null) return;
                MarkSelfWrite(SafePhysical(e.Parent.File));
                var dir = RpfFile.CreateDirectory(e.Parent, e.Name);
                ImportDir(dir, e.TrashPath);
                try { Directory.Delete(e.TrashPath, true); } catch { }
                break;
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
            kind = "dir", container = true, expandable = true, type = "Folder", size = -1L, count, attrs = "" };
    }

    private object DiskFileNode(string path)
    {
        long size = -1; try { size = new FileInfo(path).Length; } catch { }
        string name = Path.GetFileName(path);
        return new { id = Register(new DiskItem { Path = path, IsDir = false }), name,
            kind = "file", container = false, expandable = false, type = FriendlyType(name), size, attrs = "Loose",
            viewer = FileTypes.Route(name).ToString().ToLowerInvariant() };
    }

    private void AddDir(List<object> list, RpfDirectoryEntry? dir)
    {
        if (dir == null) return;
        foreach (var d in dir.Directories.OrderBy(x => x.Name, OIC)) list.Add(DirNode(d));
        foreach (var fe in dir.Files.OrderBy(x => x.Name, OIC))
            list.Add(fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? ArchiveNode(fe) : FileNode(fe));
    }

    private object DirNode(RpfDirectoryEntry d) => new
    { id = Register(d), name = d.Name, kind = "dir", container = true, expandable = true, type = "Folder", size = -1L, count = d.Directories.Count + d.Files.Count, attrs = "" };

    private object ArchiveNode(RpfFile rf) => new
    {
        id = Register(rf), name = rf.Name, kind = "archive", container = true, expandable = true,
        type = "Archive", size = rf.FileSize, count = ChildCount(rf.Root),
        attrs = rf.Encryption is RpfEncryption.NONE or RpfEncryption.OPEN ? "" : rf.Encryption.ToString(),
    };

    private object ArchiveNode(RpfFileEntry fe) => new
    {
        id = Register(fe), name = fe.Name, kind = "archive", container = true, expandable = true,
        type = "Archive", size = SafeSize(fe), count = ChildCount(SafeChildRoot(fe)), attrs = Attrs(fe),
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

    private Dictionary<string, object?> BuildModelDto(ModelData m, DrawableBase d, RpfFileEntry? modelEntry, TextureDictionary? localDict = null)
    {
        var shaders = d.ShaderGroup?.Shaders?.data_items;

        // Pre-index external textures this model needs (those not embedded with data).
        // Only possible for archive entries (loose disk files resolve embedded only).
        if (shaders != null && _resolver != null && modelEntry != null)
        {
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in shaders)
            {
                var tb = GetDiffuseRef(s);
                if (tb != null && !(tb is Texture tx && tx.Data?.FullData != null) && !string.IsNullOrEmpty(tb.Name))
                    needed.Add(tb.Name);
            }
            if (needed.Count > 0) _resolver.IndexForModel(modelEntry, needed);
        }

        var texCache = new Dictionary<object, string?>();
        var mats = new List<object>();
        for (int i = 0; i < m.Materials.Count; i++)
        {
            var md = m.Materials[i];
            string? tex = null;
            if (shaders != null && i < shaders.Length)
            {
                var t = ResolveTexture(GetDiffuseRef(shaders[i]), localDict);
                if (t != null && !texCache.TryGetValue(t, out tex)) { tex = DecodeTexUrl(t); texCache[t] = tex; }
            }
            mats.Add(new { shader = md.ShaderName, diffuse = md.DiffuseTextureName, tex });
        }
        return new Dictionary<string, object?>
        {
            ["name"] = m.Name,
            ["bmin"] = new[] { m.BoundsMin.X, m.BoundsMin.Y, m.BoundsMin.Z },
            ["bmax"] = new[] { m.BoundsMax.X, m.BoundsMax.Y, m.BoundsMax.Z },
            ["materials"] = mats,
            ["lods"] = m.Lods.Select(l => new { level = l.Level, meshes = l.Meshes.Select(MeshDto).ToArray() }).ToArray(),
        };
    }

    private static TextureBase? GetDiffuseRef(ShaderFX s)
    {
        try
        {
            var ps = s.ParametersList?.Parameters;
            var hs = s.ParametersList?.Hashes;
            if (ps == null) return null;
            if (hs != null)
                for (int i = 0; i < ps.Length && i < hs.Length; i++)
                    if (hs[i].ToString().ToLowerInvariant().Contains("diffuse") && ps[i].Data is TextureBase tb)
                        return tb;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].Data is TextureBase tb2)
                    return tb2;
        }
        catch { }
        return null;
    }

    private Texture? ResolveTexture(TextureBase? tb, TextureDictionary? localDict = null)
    {
        if (tb is Texture t && t.Data?.FullData != null) return t;   // embedded
        if (tb == null) return null;

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

    private static string? DecodeTexUrl(Texture t)
    {
        try
        {
            var rgba = TextureCodec.DecodeTexture(t, out int w, out int h);
            return rgba == null ? null : ImageUtil.DataUrlPng(ImageUtil.PngFromRgba(rgba, w, h));
        }
        catch { return null; }
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
        idx = B64(m.Indices),
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
