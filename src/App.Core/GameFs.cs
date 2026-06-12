using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Shared read/write access to a mounted GTA install by GTA-root-relative "vpath"
/// (forward or back slashes; may cross archive boundaries, e.g.
/// <c>update/update.rpf/common/data/levels/gta5/weather.xml</c>). Resolves archive
/// entries and loose disk files uniformly, converts binary metas to/from CodeWalker
/// XML, and writes back into NG-encrypted archives (warming the encrypt tables).
/// This is the engine the .epic installer and the CLI build on.
/// </summary>
public static class GameFs
{
    public static string Norm(string vpath) => (vpath ?? "").Replace('/', '\\').Trim('\\');

    /// <summary>Archive entry (file or directory) for a vpath, or null.</summary>
    public static RpfEntry? Entry(RpfManager man, string vpath) => man.GetEntry(Norm(vpath));

    /// <summary>The on-disk path a vpath maps to under the GTA root.</summary>
    public static string DiskPath(string gtaFolder, string vpath) => Path.Combine(gtaFolder, Norm(vpath));

    /// <summary>True if the vpath resolves to something (archive entry or loose file).</summary>
    public static bool Exists(RpfManager man, string gtaFolder, string vpath)
        => Entry(man, vpath) is RpfFileEntry || File.Exists(DiskPath(gtaFolder, vpath));

    /// <summary>Raw bytes (entry-paired/decompressed for resources). Null if missing.</summary>
    public static byte[]? ReadBytes(RpfManager man, string gtaFolder, string vpath)
    {
        if (Entry(man, vpath) is RpfFileEntry fe) return RpfWorkspace.Extract(fe);
        string dp = DiskPath(gtaFolder, vpath);
        return File.Exists(dp) ? File.ReadAllBytes(dp) : null;
    }

    public static bool LooksText(byte[] b)
    {
        int n = Math.Min(b.Length, 2048), bad = 0;
        for (int i = 0; i < n; i++) { byte c = b[i]; if (c == 0) return false; if (c < 9 || (c > 13 && c < 32)) bad++; }
        return bad < n / 20 + 1;
    }

    /// <summary>
    /// Read a file as editable text. Plain text is returned as-is; a binary meta
    /// (.ymt/.ymap/.ytyp/.pso/…) is converted to CodeWalker XML. <paramref name="metaName"/>
    /// is the conversion name (e.g. "carcols.ymt.pso.xml") needed to convert back, or
    /// "" when the file is already text. Null if the file is binary with no XML mapping.
    /// </summary>
    public static string? ReadEditable(RpfManager man, string gtaFolder, string vpath, out string metaName, string ddsDir = "")
    {
        metaName = "";
        var bytes = ReadBytes(man, gtaFolder, vpath);
        if (bytes == null) return null;
        if (LooksText(bytes)) return Encoding.UTF8.GetString(bytes);
        if (Entry(man, vpath) is RpfFileEntry fe)
        {
            try { var xml = MetaXml.GetXml(fe, bytes, out metaName, ddsDir); if (!string.IsNullOrEmpty(xml)) return xml; }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Write raw bytes to a vpath — into the owning archive (creating the entry if it
    /// doesn't exist, given the parent dir/archive exists) or as a loose disk file.
    /// Handles NG-encrypted archives. Returns false + reason on failure.
    /// </summary>
    public static bool WriteBytes(RpfManager man, string gtaFolder, string vpath, byte[] data, out string error)
    {
        error = "";
        string norm = Norm(vpath);
        try
        {
            if (Entry(man, vpath) is RpfFileEntry fe)
            {
                NgEncrypt.EnsureFor(fe.File);
                var created = RpfFile.CreateFile(fe.Parent, fe.Name, data, true);
                Register(man, created);   // CreateFile replaces the entry object; keep GetEntry in sync
                return true;
            }
            // new file: resolve the parent (archive dir, nested rpf root, base rpf root, or disk dir)
            int cut = norm.LastIndexOf('\\');
            string parent = cut < 0 ? "" : norm[..cut], leaf = cut < 0 ? norm : norm[(cut + 1)..];
            var pe = parent.Length > 0 ? Entry(man, parent) : null;
            RpfDirectoryEntry? dir = pe as RpfDirectoryEntry
                ?? (pe is RpfFileEntry prf && prf.NameLower.EndsWith(".rpf", StringComparison.Ordinal) ? prf.File?.FindChildArchive(prf)?.Root : null)
                ?? man.AllRpfs.FirstOrDefault(r => r.Parent == null && string.Equals(r.Path, parent, StringComparison.OrdinalIgnoreCase))?.Root;
            if (dir != null)
            {
                NgEncrypt.EnsureFor(dir.File);
                var created = RpfFile.CreateFile(dir, leaf, data, true);
                Register(man, created);   // make the new entry visible to GetEntry within this session
                return true;
            }
            string dp = DiskPath(gtaFolder, norm);
            var ddir = Path.GetDirectoryName(dp);
            if (ddir != null && Directory.Exists(ddir)) { File.WriteAllBytes(dp, data); return true; }
            error = "no such target or parent folder: " + vpath;
            return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Write text back to a vpath. If the existing target is a binary meta, the text is
    /// treated as CodeWalker XML and converted back to the binary format first (using
    /// the target's own conversion name, so PSO vs RSC etc. is never guessed).
    /// </summary>
    public static bool WriteEditable(RpfManager man, string gtaFolder, string vpath, string text, out string error, string ddsDir = "")
    {
        error = "";
        try
        {
            var current = ReadBytes(man, gtaFolder, vpath);
            bool targetBinaryMeta = current != null && !LooksText(current) && Entry(man, vpath) is RpfFileEntry;
            byte[] data;
            if (targetBinaryMeta)
            {
                var fe = (RpfFileEntry)Entry(man, vpath)!;
                string metaName;
                try { _ = MetaXml.GetXml(fe, current!, out metaName, ddsDir); }
                catch (Exception ex) { error = "target has no XML mapping: " + ex.Message; return false; }
                var fmt = XmlMeta.GetXMLFormat(metaName.ToLowerInvariant(), out _);
                var doc = new XmlDocument();
                doc.LoadXml(text);
                data = XmlMeta.GetData(doc, fmt, ddsDir);
                if (data == null || data.Length == 0) { error = "XML->binary conversion produced no data"; return false; }
            }
            else data = new UTF8Encoding(false).GetBytes(text);
            return WriteBytes(man, gtaFolder, vpath, data, out error);
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // Keep RpfManager.GetEntry in sync with a just-created/replaced entry, so later
    // operations in the same session resolve it (CreateFile updates the dir tree but
    // not the manager's path index).
    private static void Register(RpfManager man, RpfFileEntry? created)
    {
        if (created?.Path == null) return;
        try { man.EntryDict[created.Path.ToLowerInvariant()] = created; } catch { }
    }

    /// <summary>Delete a file at a vpath (archive entry or loose). False if not found.</summary>
    public static bool Delete(RpfManager man, string gtaFolder, string vpath, out string error)
    {
        error = "";
        try
        {
            if (Entry(man, vpath) is RpfFileEntry fe) { NgEncrypt.EnsureFor(fe.File); RpfFile.DeleteEntry(fe); return true; }
            string dp = DiskPath(gtaFolder, vpath);
            if (File.Exists(dp)) { File.Delete(dp); return true; }
            error = "not found: " + vpath; return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
