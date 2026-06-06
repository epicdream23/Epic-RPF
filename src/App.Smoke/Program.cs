using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using App.Core;
using CodeWalker.GameFiles;

// M0 smoke test: mount a Legacy GTA V install, print a slice of the archive
// tree, extract one .ydr, and (bonus) decode its drawable to prove the Gen8
// geometry path works end-to-end before any renderer exists.

string gta = args.Length > 0 ? args[0] : @"C:\Program Files\Epic Games\GTAV";

Console.WriteLine("== Epic RPF - M0 smoke test ==");
Console.WriteLine($"GTA folder: {gta}");
if (!Directory.Exists(gta))
{
    Console.Error.WriteLine("Folder not found.");
    return 1;
}

int errCount = 0;
var sw = Stopwatch.StartNew();
var ws = RpfWorkspace.Mount(
    gta,
    status: null,
    error: e => { if (errCount++ < 8) Console.WriteLine("  [mount err] " + e); });
sw.Stop();

var rpfs = ws.AllRpfs;
long fileEntries = rpfs.Sum(r => (long)(r.AllEntries?.Count(e => e is RpfFileEntry) ?? 0));
Console.WriteLine($"Mounted {rpfs.Count:N0} archives, {fileEntries:N0} file entries in " +
                  $"{sw.Elapsed.TotalSeconds:F1}s (mount errors: {errCount}).");

// --- Print the top of the tree for the first base archive ---
var baseRpf = rpfs.Where(r => r.Parent == null).OrderBy(r => r.Path.Length).FirstOrDefault()
              ?? rpfs.OrderBy(r => r.Path.Length).First();
Console.WriteLine();
Console.WriteLine($"Tree of base archive: {baseRpf.Path}");
var rootDir = baseRpf.Root;
if (rootDir != null)
{
    foreach (var d in rootDir.Directories.Take(12))
        Console.WriteLine($"  [dir]  {d.Name}/");
    foreach (var f in rootDir.Files.Take(8))
        Console.WriteLine($"  [file] {f.Name}  ({f.FileSize:N0} b)");
}

// --- Counts by a few interesting types ---
RpfFileEntry[] allFiles = rpfs.SelectMany(r => r.AllEntries).OfType<RpfFileEntry>().ToArray();
int Count(string ext) => allFiles.Count(e => e.NameLower.EndsWith(ext, StringComparison.Ordinal));
Console.WriteLine();
Console.WriteLine($"Resource counts:  .ydr={Count(".ydr"):N0}  .ydd={Count(".ydd"):N0}  " +
                  $".yft={Count(".yft"):N0}  .ytd={Count(".ytd"):N0}");

// --- Extract one .ydr and decode it ---
var ydrEntry = allFiles.FirstOrDefault(e => e.NameLower.EndsWith(".ydr", StringComparison.Ordinal));
if (ydrEntry == null)
{
    Console.WriteLine("No .ydr found to extract.");
    return 0;
}

byte[] bytes = RpfWorkspace.Extract(ydrEntry);
string outPath = Path.Combine(Path.GetTempPath(), ydrEntry.Name);
File.WriteAllBytes(outPath, bytes);
Console.WriteLine();
Console.WriteLine($"Extracted: {ydrEntry.Path}");
Console.WriteLine($"   -> {outPath}  ({bytes.Length:N0} bytes)");

var ydr = ws.Manager.GetFile<YdrFile>(ydrEntry);
var drw = ydr?.Drawable;
if (drw?.AllModels != null)
{
    int models = drw.AllModels.Length;
    int geoms = drw.AllModels.Sum(m => m.Geometries?.Length ?? 0);
    int shaders = drw.ShaderGroup?.Shaders?.data_items?.Length ?? 0;
    Console.WriteLine($"   decoded Drawable: {models} model(s), {geoms} geometr(y/ies), {shaders} shader(s).");

    var g = drw.AllModels.SelectMany(m => m.Geometries ?? Array.Empty<DrawableGeometry>())
                       .FirstOrDefault(x => x?.VertexBuffer?.Info != null);
    if (g != null)
    {
        var info = g.VertexBuffer.Info;
        Console.WriteLine($"   first geom: {g.VerticesCount} verts, stride {info.Stride}, " +
                          $"layout flags 0x{info.Flags:X}, shader '{g.Shader?.Name}'.");
    }
}
else
{
    Console.WriteLine("   (Drawable decoded but had no models.)");
}

// --- Meta round-trip (validates the editor's save-conversion path) ---
var ymtEntry = allFiles.FirstOrDefault(e => e.NameLower.EndsWith(".ymt", StringComparison.Ordinal));
if (ymtEntry != null)
{
    try
    {
        byte[] mb = RpfWorkspace.Extract(ymtEntry);
        string xml = MetaXml.GetXml(ymtEntry, mb, out string xmlName);
        Console.WriteLine();
        Console.WriteLine($"Meta round-trip: {ymtEntry.Name} ({mb.Length:N0} b)");
        if (string.IsNullOrEmpty(xml))
        {
            Console.WriteLine("   (no XML produced — treated as plain/binary)");
        }
        else
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var fmt = XmlMeta.GetXMLFormat(xmlName.ToLowerInvariant(), out _);
            byte[] back = XmlMeta.GetData(doc, fmt, "");
            Console.WriteLine($"   -> XML {xml.Length:N0} chars ({xmlName}) -> back as {fmt}: {(back?.Length ?? 0):N0} bytes");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Meta round-trip error: " + ex.Message);
    }
}

Console.WriteLine();
Console.WriteLine("M0 OK.");
return 0;
