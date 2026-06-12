using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using App.Core;
using CodeWalker.GameFiles;

// rpfcli — headless file access to a GTA V install, INCLUDING inside .rpf archives.
// Built so a coding agent (or any script) can view and edit game files directly,
// no manual extraction needed. Virtual paths are GTA-root-relative and may cross
// archive boundaries, e.g.  update/update.rpf/common/data/timecycle/w_clear.xml
//
//   rpfcli ls   [vpath]                      list a folder / archive directory
//   rpfcli find <text> [--ext] [--limit N]   search all entries by name
//   rpfcli info <vpath>                      entry details
//   rpfcli cat  <vpath> [-o out] [--dds dir] read as text (binary meta/resource -> CodeWalker XML)
//   rpfcli get  <vpath> <outfile>            extract raw bytes (valid standalone file)
//   rpfcli put  <vpath> <infile> [--dds dir] write into archive/disk (xml input vs binary
//                                            target is converted back automatically)
//   --gta <folder>   GTA install (default: EPICRPF_GTA env or the Epic path)
//
// Exit codes: 0 ok, 1 usage, 2 not found, 3 operation failed.

Console.OutputEncoding = Encoding.UTF8;

string gta = Environment.GetEnvironmentVariable("EPICRPF_GTA") ?? @"C:\Program Files\Epic Games\GTAV";
var rest = new List<string>();
string? outFile = null, ddsDir = null;
bool extSearch = false;
int limit = 200;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--gta": gta = args[++i]; break;
        case "-o": case "--out": outFile = args[++i]; break;
        case "--dds": ddsDir = args[++i]; break;
        case "--ext": extSearch = true; break;
        case "--limit": limit = int.Parse(args[++i]); break;
        default: rest.Add(args[i]); break;
    }
}
if (rest.Count == 0) return Usage();
string cmd = rest[0].ToLowerInvariant();

// `epic` subcommands: create/inspect don't need a mount; install does.
if (cmd == "epic")
{
    string sub = (rest.ElementAtOrDefault(1) ?? "").ToLowerInvariant();
    if (sub == "create") return EpicCreate(rest.ElementAtOrDefault(2), rest.ElementAtOrDefault(3));
    if (sub == "inspect") return EpicInspect(rest.ElementAtOrDefault(2));
    if (sub == "install") { /* falls through to mount below */ }
    else return Usage();
}

if (!Directory.Exists(gta)) { Console.Error.WriteLine($"GTA folder not found: {gta}"); return 2; }
var ws = RpfWorkspace.Mount(gta);

return cmd switch
{
    "ls" => Ls(rest.ElementAtOrDefault(1) ?? ""),
    "find" => Find(rest.ElementAtOrDefault(1) ?? ""),
    "info" => Info(rest.ElementAtOrDefault(1) ?? ""),
    "cat" => Cat(rest.ElementAtOrDefault(1) ?? ""),
    "get" => Get(rest.ElementAtOrDefault(1) ?? "", rest.ElementAtOrDefault(2)),
    "put" => Put(rest.ElementAtOrDefault(1) ?? "", rest.ElementAtOrDefault(2)),
    "epic" => EpicInstall(rest.ElementAtOrDefault(2)),
    _ => Usage(),
};

// --- .epic extension packaging / install ---
int EpicCreate(string? manifestPath, string? outPath)
{
    if (manifestPath == null || outPath == null || !File.Exists(manifestPath)) { Console.Error.WriteLine("usage: rpfcli epic create <manifest.json> <out.epic>"); return 1; }
    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var manifest = System.Text.Json.JsonSerializer.Deserialize<EpicManifest>(File.ReadAllText(manifestPath), opts);
    if (manifest == null) { Console.Error.WriteLine("bad manifest json"); return 1; }
    string baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    var payload = new Dictionary<string, byte[]>();
    foreach (var op in manifest.Operations.Where(o => o.Op == "replaceFile" && !string.IsNullOrEmpty(o.Source)))
    {
        string srcPath = Path.IsPathRooted(op.Source!) ? op.Source! : Path.Combine(baseDir, op.Source!);
        if (!File.Exists(srcPath)) { Console.Error.WriteLine($"payload source not found: {op.Source}"); return 2; }
        string name = Path.GetFileName(srcPath);
        if (payload.ContainsKey(name)) name = Guid.NewGuid().ToString("N")[..8] + "_" + name;  // de-dup
        payload["payload/" + name] = File.ReadAllBytes(srcPath);
        op.Source = name;   // store the in-package name
    }
    File.WriteAllBytes(outPath, EpicPackage.Pack(manifest, payload));
    Console.WriteLine($"built {outPath}  ({new FileInfo(outPath).Length:N0} b, {manifest.Operations.Count} ops, {payload.Count} payload file(s))");
    return 0;
}

int EpicInspect(string? pkgPath)
{
    if (pkgPath == null || !File.Exists(pkgPath)) { Console.Error.WriteLine("usage: rpfcli epic inspect <pkg.epic>"); return 1; }
    EpicPackage pkg;
    try { pkg = EpicPackage.Open(File.ReadAllBytes(pkgPath)); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    var m = pkg.Manifest;
    Console.WriteLine($"{m.Name} v{m.Version}  by {m.Author}");
    if (!string.IsNullOrEmpty(m.Description)) Console.WriteLine($"  {m.Description}");
    Console.WriteLine($"  target: {m.Target}   operations: {m.Operations.Count}");
    foreach (var op in m.Operations)
        Console.WriteLine("   - " + op.Op switch
        {
            "replaceFile" => $"replaceFile {op.Target}",
            "deleteFile" => $"deleteFile {op.Target}",
            "xml" => $"xml {op.Action} {op.Target} [{op.Xpath}]",
            "text" => $"text {op.Action} {op.Target}",
            _ => $"{op.Op} {op.Target}",
        });
    return 0;
}

int EpicInstall(string? pkgPath)
{
    if (pkgPath == null || !File.Exists(pkgPath)) { Console.Error.WriteLine("usage: rpfcli epic install <pkg.epic> [--gta ...]"); return 1; }
    EpicPackage pkg;
    try { pkg = EpicPackage.Open(File.ReadAllBytes(pkgPath)); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    Console.WriteLine($"Installing: {pkg.Manifest.Name} v{pkg.Manifest.Version}");
    foreach (var l in EpicInstaller.Plan(pkg, ws.Manager, gta)) Console.WriteLine("   plan: " + l);
    string backup = Path.Combine(gta, "EpicRpf_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    var results = EpicInstaller.Apply(pkg, ws, backup);
    int ok = results.Count(r => r.Ok), fail = results.Count - ok;
    foreach (var r in results) Console.WriteLine($"   [{(r.Ok ? "OK" : "FAIL")}] {r.Op} {r.Target} — {r.Message}");
    Console.WriteLine($"-- {ok} ok / {fail} failed. backups: {backup}");
    return fail == 0 ? 0 : 3;
}

int Usage()
{
    Console.Error.WriteLine("usage: rpfcli <ls|find|info|cat|get|put> [args]  (see source header)");
    return 1;
}

string Norm(string vpath) => vpath.Replace('/', '\\').Trim('\\');

// Resolve a vpath to an archive entry (file or dir), or null.
RpfEntry? Entry(string vpath) => ws.Manager.GetEntry(Norm(vpath));

// A vpath that is a real on-disk path under the GTA root (loose file/folder).
string Disk(string vpath) => Path.Combine(gta, Norm(vpath));

int Ls(string vpath)
{
    string norm = Norm(vpath);
    var rows = new List<(string kind, string name, long size)>();

    RpfDirectoryEntry? dir = null;
    var e = norm.Length == 0 ? null : Entry(norm);
    if (e is RpfDirectoryEntry d) dir = d;
    else if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".rpf")) dir = fe.File?.FindChildArchive(fe)?.Root;
    else if (e is RpfFileEntry) { Console.Error.WriteLine("that's a file — use cat/info"); return 1; }

    if (dir != null)
    {
        foreach (var sd in dir.Directories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(("dir", sd.Name, -1));
        foreach (var f in dir.Files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add((f.NameLower.EndsWith(".rpf") ? "rpf" : "file", f.Name, SafeSize(f)));
    }
    else
    {
        // disk folder (GTA root, loose folders) — base .rpf files are still browsable
        string dp = norm.Length == 0 ? gta : Disk(norm);
        if (Directory.Exists(dp))
        {
            foreach (var sd in Directory.GetDirectories(dp).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                rows.Add(("dir", Path.GetFileName(sd), -1));
            foreach (var f in Directory.GetFiles(dp).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                rows.Add((f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) ? "rpf" : "file", Path.GetFileName(f), new FileInfo(f).Length));
        }
        else if (norm.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && File.Exists(dp))
        {
            // a base archive on disk -> list its root
            var rpf = ws.AllRpfs.FirstOrDefault(r => r.Parent == null && string.Equals(r.GetPhysicalFilePath(), dp, StringComparison.OrdinalIgnoreCase));
            if (rpf?.Root != null) return LsDir(rpf.Root);
            Console.Error.WriteLine("archive not mounted"); return 2;
        }
        else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }
    }

    foreach (var (kind, name, size) in rows)
        Console.WriteLine($"{kind,-5} {(size >= 0 ? size.ToString() : ""),12}  {name}");
    Console.WriteLine($"-- {rows.Count} item(s)");
    return 0;

    int LsDir(RpfDirectoryEntry root)
    {
        foreach (var sd in root.Directories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"dir   {"",12}  {sd.Name}");
        foreach (var f in root.Files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{(f.NameLower.EndsWith(".rpf") ? "rpf" : "file"),-5} {SafeSize(f),12}  {f.Name}");
        return 0;
    }
}

int Find(string text)
{
    if (text.Length == 0) return Usage();
    string q = text.ToLowerInvariant();
    string extNeedle = "." + q.TrimStart('.');
    int total = 0;
    foreach (var rpf in ws.AllRpfs)
    {
        if (rpf.AllEntries == null) continue;
        foreach (var en in rpf.AllEntries)
        {
            if (en is not RpfFileEntry fe) continue;
            bool match = extSearch ? fe.NameLower.EndsWith(extNeedle, StringComparison.Ordinal) : fe.NameLower.Contains(q);
            if (!match) continue;
            total++;
            if (total <= limit) Console.WriteLine($"{SafeSize(fe),12}  {fe.Path}");
        }
    }
    Console.WriteLine($"-- {total} match(es){(total > limit ? $", showing first {limit}" : "")}");
    return 0;
}

int Info(string vpath)
{
    var e = Entry(vpath);
    if (e is RpfFileEntry fe)
    {
        Console.WriteLine($"path:  {fe.Path}");
        Console.WriteLine($"size:  {SafeSize(fe)}");
        Console.WriteLine($"kind:  {(fe is RpfResourceFileEntry ? "resource" : "binary")}  archive: {fe.File?.Path}");
        return 0;
    }
    if (e is RpfDirectoryEntry de) { Console.WriteLine($"dir: {de.Path} ({de.Directories.Count} dirs, {de.Files.Count} files)"); return 0; }
    string dp = Disk(vpath);
    if (File.Exists(dp)) { Console.WriteLine($"loose file: {dp} ({new FileInfo(dp).Length} b)"); return 0; }
    if (Directory.Exists(dp)) { Console.WriteLine($"disk dir: {dp}"); return 0; }
    Console.Error.WriteLine($"not found: {vpath}"); return 2;
}

int Cat(string vpath)
{
    byte[] bytes;
    var e = Entry(vpath);
    if (e is RpfFileEntry fe) bytes = RpfWorkspace.Extract(fe);
    else if (File.Exists(Disk(vpath))) bytes = File.ReadAllBytes(Disk(vpath));
    else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }

    string? text = null;
    if (LooksText(bytes)) text = Encoding.UTF8.GetString(bytes);
    else if (e is RpfFileEntry fent)
    {
        // binary meta / resource -> CodeWalker XML (embedded textures go to --dds dir)
        try { text = MetaXml.GetXml(fent, bytes, out _, ddsDir ?? ""); } catch { }
        if (string.IsNullOrEmpty(text)) { Console.Error.WriteLine("binary file with no XML conversion — use `get`"); return 3; }
    }
    else { Console.Error.WriteLine("binary loose file — use `get`"); return 3; }

    if (outFile != null) { File.WriteAllText(outFile, text, new UTF8Encoding(false)); Console.WriteLine($"wrote {text!.Length:N0} chars -> {outFile}"); }
    else Console.Write(text);
    return 0;
}

int Get(string vpath, string? dest)
{
    if (dest == null) return Usage();
    var e = Entry(vpath);
    byte[] bytes;
    if (e is RpfFileEntry fe) bytes = RpfWorkspace.ExtractForSave(fe);          // valid standalone file
    else if (File.Exists(Disk(vpath))) bytes = File.ReadAllBytes(Disk(vpath));
    else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }
    File.WriteAllBytes(dest, bytes);
    Console.WriteLine($"extracted {bytes.Length:N0} b -> {dest}");
    return 0;
}

int Put(string vpath, string? src)
{
    if (src == null || !File.Exists(src)) { Console.Error.WriteLine("input file required"); return 1; }
    string norm = Norm(vpath);
    byte[] input = File.ReadAllBytes(src);

    var e = Entry(norm);
    if (e is RpfFileEntry fe)
    {
        byte[] data = input;
        // XML input against a binary target -> convert back to the binary format.
        // The format is derived from the target itself (GetXml names it, e.g.
        // carcols.ymt -> "carcols.ymt.pso.xml"), so callers never guess PSO vs RSC.
        string inputHead = Encoding.UTF8.GetString(input, 0, Math.Min(input.Length, 64)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        bool inputIsXml = src.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || inputHead.StartsWith('<');
        byte[] current = RpfWorkspace.Extract(fe);
        if (inputIsXml && !LooksText(current))
        {
            string xmlName;
            try { _ = MetaXml.GetXml(fe, current, out xmlName); }
            catch (Exception ex) { Console.Error.WriteLine("target has no XML mapping: " + ex.Message); return 3; }
            var fmt = XmlMeta.GetXMLFormat(xmlName.ToLowerInvariant(), out _);
            var doc = new XmlDocument();
            doc.LoadXml(Encoding.UTF8.GetString(input));
            data = XmlMeta.GetData(doc, fmt, ddsDir ?? "");
            if (data == null || data.Length == 0) { Console.Error.WriteLine("XML conversion produced no data (resources with embedded textures need --dds)"); return 3; }
        }
        try
        {
            NgEncrypt.EnsureFor(fe.File, s => Console.Error.WriteLine(s));   // NG archives need encrypt tables
            RpfFile.CreateFile(fe.Parent, fe.Name, data, true);
            Console.WriteLine($"wrote {data.Length:N0} b -> {fe.Path}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("write failed: " + ex.Message); return 3; }
    }

    // new file inside an archive dir, or loose on disk
    int cut = norm.LastIndexOf('\\');
    string parent = cut < 0 ? "" : norm[..cut], leaf = cut < 0 ? norm : norm[(cut + 1)..];
    var pe = parent.Length > 0 ? Entry(parent) : null;
    RpfDirectoryEntry? dir = pe as RpfDirectoryEntry
        ?? (pe is RpfFileEntry prf && prf.NameLower.EndsWith(".rpf") ? prf.File?.FindChildArchive(prf)?.Root : null)
        // a BASE archive on disk isn't an entry in anything — resolve it from the mounted set
        ?? ws.AllRpfs.FirstOrDefault(r => r.Parent == null && string.Equals(r.Path, parent, StringComparison.OrdinalIgnoreCase))?.Root;
    if (dir != null)
    {
        try
        {
            NgEncrypt.EnsureFor(dir.File, s => Console.Error.WriteLine(s));
            RpfFile.CreateFile(dir, leaf, input, true);
            Console.WriteLine($"created {input.Length:N0} b -> {dir.Path}\\{leaf}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("write failed: " + ex.Message); return 3; }
    }
    string dp = Disk(norm);
    if (Directory.Exists(Path.GetDirectoryName(dp)!)) { File.WriteAllBytes(dp, input); Console.WriteLine($"wrote loose {input.Length:N0} b -> {dp}"); return 0; }
    Console.Error.WriteLine($"no such target or parent: {vpath}");
    return 2;
}

static long SafeSize(RpfFileEntry fe) { try { return fe.GetFileSize(); } catch { return fe.FileSize; } }

static bool LooksText(byte[] b)
{
    int n = Math.Min(b.Length, 2048), bad = 0;
    for (int i = 0; i < n; i++) { byte c = b[i]; if (c == 0) return false; if (c < 9 || (c > 13 && c < 32)) bad++; }
    return bad < n / 20 + 1;
}
