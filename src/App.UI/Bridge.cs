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

    private RpfWorkspace? _ws;
    private readonly Dictionary<int, object> _nodes = new();
    private int _nextNodeId = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Bridge(Action<string> post, Func<string?> pickFolder, Func<string, string?> pickSavePath)
    {
        _post = post;
        _pickFolder = pickFolder;
        _pickSavePath = pickSavePath;
    }

    private sealed class Req
    {
        public int Id { get; set; }
        public string? Cmd { get; set; }
        public string? Folder { get; set; }
        public bool Gen9 { get; set; }
        public int Node { get; set; }
        public string? Content { get; set; }
        public string? Format { get; set; }   // "text" | "meta"
        public string? MetaName { get; set; } // xml filename from MetaXml (drives reverse format)
        public string? Target { get; set; }   // "rpf" | "export"
    }

    // Metadata-ish resource types CodeWalker can round-trip to/from editable XML.
    // Deliberately excludes models/textures (ydr/ydd/yft/ytd) and heavy/binary
    // resources (ynv/awc/fxc/...) which have their own viewers or aren't practical.
    private static readonly HashSet<string> MetaConvertExts = new(StringComparer.Ordinal)
    { "ymt", "ymf", "ymap", "ytyp", "pso", "cut", "rel", "ynd", "ycd", "ybn", "yld", "yed", "mrf", "ypdb", "yfd" };

    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    // A virtual on-disk folder node, so base archives appear under their real
    // folders (e.g. update\x64\dlcpacks\<name>\dlc.rpf) instead of flat at root.
    private sealed class VFolder
    {
        public string Name = "";
        public SortedDictionary<string, VFolder> Subs = new(StringComparer.OrdinalIgnoreCase);
        public List<RpfFile> Rpfs = new();
    }
    private VFolder? _vroot;

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
            case "expand": CmdExpand(r); break;
            case "open": CmdOpen(r); break;
            case "firstModel": CmdFirstModel(r); break;
            case "firstMeta": CmdFirstMeta(r); break;
            case "save": CmdSave(r); break;
            case "extract": CmdExtract(r); break;
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
        BuildTree(folder);
        var roots = EnumerateChildren(_vroot!);
        long files = _ws.AllRpfs.Sum(x => (long)(x.AllEntries?.Count(e => e is RpfFileEntry) ?? 0));

        Send(new
        {
            type = "mounted",
            reqId = r.Id,
            ok = true,
            folder,
            gen9 = r.Gen9,
            rootName = _vroot!.Name,
            archives = _ws.AllRpfs.Count,
            files,
            mountErrors = errs,
            roots,
        });
    }

    // Reconstruct the on-disk folder hierarchy from each base archive's path so
    // the tree mirrors the real folder structure instead of a flat archive list.
    private void BuildTree(string folder)
    {
        string rootName = "GTA V";
        try { var n = new DirectoryInfo(folder).Name; if (!string.IsNullOrEmpty(n)) rootName = n; } catch { }
        _vroot = new VFolder { Name = rootName };
        if (_ws == null) return;

        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.Parent != null) continue; // only base (on-disk) archives
            var path = rpf.Path ?? rpf.Name;
            var segs = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var cur = _vroot;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (!cur.Subs.TryGetValue(segs[i], out var nx)) { nx = new VFolder { Name = segs[i] }; cur.Subs[segs[i]] = nx; }
                cur = nx;
            }
            cur.Rpfs.Add(rpf);
        }
    }

    private void CmdExpand(Req r)
    {
        if (!_nodes.TryGetValue(r.Node, out var obj))
        {
            Send(new { type = "children", reqId = r.Id, node = r.Node, nodes = Array.Empty<object>() });
            return;
        }
        Send(new { type = "children", reqId = r.Id, node = r.Node, nodes = EnumerateChildren(obj) });
    }

    private void CmdOpen(Req r)
    {
        if (_ws == null || !_nodes.TryGetValue(r.Node, out var obj) || obj is not RpfFileEntry fe)
        {
            Send(new { type = "error", reqId = r.Id, message = "invalid node" });
            return;
        }

        switch (FileTypes.Route(fe.Name))
        {
            case ViewerKind.Model: OpenModel(r, fe); break;
            case ViewerKind.Texture: OpenTexture(r, fe); break;
            default: OpenEditableOrHex(r, fe); break;
        }
    }

    // Dev / deep-link helper: open the first .ydr in the mounted set. Used by the
    // auto-load hook to exercise the whole pipeline without manual navigation.
    private void CmdFirstModel(Req r)
    {
        if (_ws == null) { Send(new { type = "error", reqId = r.Id, message = "not mounted" }); return; }
        foreach (var rpf in _ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".ydr", StringComparison.Ordinal))
                {
                    OpenModel(r, fe);
                    return;
                }
        }
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

    private void CmdExtract(Req r)
    {
        if (!_nodes.TryGetValue(r.Node, out var obj) || obj is not RpfFileEntry fe)
        {
            Send(new { type = "error", reqId = r.Id, message = "invalid node" });
            return;
        }
        string? path = _pickSavePath(fe.Name);
        if (path == null)
        {
            Send(new { type = "extracted", reqId = r.Id, ok = false, canceled = true });
            return;
        }
        byte[] bytes = RpfWorkspace.Extract(fe);
        File.WriteAllBytes(path, bytes);
        Send(new { type = "extracted", reqId = r.Id, ok = true, path, size = bytes.Length });
    }

    // ---- openers ----------------------------------------------------------

    private void OpenModel(Req r, RpfFileEntry fe)
    {
        var drawables = DrawableLoader.Load(_ws!.Manager, fe);
        if (drawables.Count == 0)
        {
            Send(new { type = "error", reqId = r.Id, message = "no drawable found in " + fe.Name });
            return;
        }

        var parts = new List<object>();
        int geom = 0, rendered = 0, skipped = 0;
        var skipSamples = new List<string>();
        foreach (var nd in drawables)
        {
            var model = GeometryDecoder.Decode(nd.Drawable, nd.Name);
            geom += model.GeometryCount;
            rendered += model.RenderedCount;
            skipped += model.SkippedReasons.Count;
            if (skipSamples.Count < 5) skipSamples.AddRange(model.SkippedReasons.Take(5 - skipSamples.Count));
            parts.Add(ModelDto(model));
        }

        Send(new
        {
            type = "model",
            reqId = r.Id,
            name = fe.Name,
            path = fe.Path,
            parts,
            stats = new { geometryCount = geom, rendered, skipped, skipSamples },
        });
    }

    private void OpenTexture(Req r, RpfFileEntry fe)
    {
        var ytd = _ws!.Manager.GetFile<YtdFile>(fe);
        var texs = ytd?.TextureDict?.Textures?.data_items;
        var list = texs?.Where(t => t != null).Select(t => new
        {
            name = t.Name,
            width = (int)t.Width,
            height = (int)t.Height,
            format = t.Format.ToString(),
            levels = (int)t.Levels,
        }).ToArray() ?? Array.Empty<object>();
        Send(new { type = "texture", reqId = r.Id, name = fe.Name, textures = list });
    }

    private void OpenEditableOrHex(Req r, RpfFileEntry fe)
    {
        byte[] bytes = RpfWorkspace.Extract(fe);
        string ext = ExtOf(fe.NameLower);

        // 1. Convertible binary meta -> editable XML.
        if (MetaConvertExts.Contains(ext) && TrySendMeta(r, fe, bytes)) return;

        // 2. Plain text (xml/txt/lua/many .meta/.dat that are actually text).
        if (LooksText(bytes))
        {
            Send(new
            {
                type = "edit", reqId = r.Id, name = fe.Name, editable = true,
                format = "text", language = LangForExt(ext), content = DecodeText(bytes),
            });
            return;
        }

        // 3. Last resort: try meta conversion for binary .meta/.dat/etc.
        if (TrySendMeta(r, fe, bytes)) return;

        // 4. Hex (always-available fallback, read-only).
        OpenHex(r, fe, bytes);
    }

    private bool TrySendMeta(Req r, RpfFileEntry fe, byte[] bytes)
    {
        try
        {
            string xml = MetaXml.GetXml(fe, bytes, out string xmlName);
            if (string.IsNullOrEmpty(xml)) return false;
            Send(new
            {
                type = "edit", reqId = r.Id, name = fe.Name, editable = true,
                format = "meta", metaName = xmlName, language = "xml", content = xml,
            });
            return true;
        }
        catch { return false; }
    }

    private void CmdSave(Req r)
    {
        if (!_nodes.TryGetValue(r.Node, out var obj) || obj is not RpfFileEntry fe)
        {
            Send(new { type = "saved", reqId = r.Id, ok = false, message = "invalid node" });
            return;
        }
        string content = r.Content ?? "";
        string target = r.Target ?? "export";

        if (target == "export")
        {
            // Export exactly what's in the editor (the XML/text the user sees).
            string suggested = r.Format == "meta" ? fe.Name + ".xml" : fe.Name;
            string? path = _pickSavePath(suggested);
            if (path == null) { Send(new { type = "saved", reqId = r.Id, ok = false, canceled = true }); return; }
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Send(new { type = "saved", reqId = r.Id, ok = true, target = "export", path });
            return;
        }

        // target == "rpf": convert (if meta) and write back into the archive.
        byte[] data;
        try
        {
            if (r.Format == "meta")
            {
                var doc = new XmlDocument();
                doc.LoadXml(content);
                var fmt = XmlMeta.GetXMLFormat((r.MetaName ?? fe.Name + ".xml").ToLowerInvariant(), out _);
                data = XmlMeta.GetData(doc, fmt, "");
                if (data == null || data.Length == 0) throw new Exception("conversion produced no data");
            }
            else
            {
                data = new UTF8Encoding(false).GetBytes(content);
            }
        }
        catch (Exception ex)
        {
            Send(new { type = "saved", reqId = r.Id, ok = false, target = "rpf", message = "convert failed: " + ex.Message });
            return;
        }

        try
        {
            RpfFile.CreateFile(fe.Parent, fe.Name, data, true);
            Send(new { type = "saved", reqId = r.Id, ok = true, target = "rpf", size = data.Length });
        }
        catch (Exception ex)
        {
            Send(new { type = "saved", reqId = r.Id, ok = false, target = "rpf", message = ex.Message });
        }
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

    private void OpenHex(Req r, RpfFileEntry fe, byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 64 * 1024);
        Send(new
        {
            type = "hex",
            reqId = r.Id,
            name = fe.Name,
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
            case VFolder vf:
                foreach (var sub in vf.Subs.Values) list.Add(FolderNode(sub));
                foreach (var rf in vf.Rpfs.OrderBy(x => x.Name, OIC)) list.Add(ArchiveNode(rf));
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

    private void AddDir(List<object> list, RpfDirectoryEntry? dir)
    {
        if (dir == null) return;
        foreach (var d in dir.Directories.OrderBy(x => x.Name, OIC)) list.Add(DirNode(d));
        foreach (var fe in dir.Files.OrderBy(x => x.Name, OIC))
            list.Add(fe.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? ArchiveNode(fe) : FileNode(fe));
    }

    private object FolderNode(VFolder vf) => new
    { id = Register(vf), name = vf.Name, kind = "folder", container = true, expandable = true, type = "Folder", size = -1L, count = vf.Subs.Count + vf.Rpfs.Count, attrs = "" };

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

    private static object ModelDto(ModelData m) => new
    {
        name = m.Name,
        bmin = new[] { m.BoundsMin.X, m.BoundsMin.Y, m.BoundsMin.Z },
        bmax = new[] { m.BoundsMax.X, m.BoundsMax.Y, m.BoundsMax.Z },
        materials = m.Materials.Select(x => new { shader = x.ShaderName, diffuse = x.DiffuseTextureName }).ToArray(),
        lods = m.Lods.Select(l => new { level = l.Level, meshes = l.Meshes.Select(MeshDto).ToArray() }).ToArray(),
    };

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
