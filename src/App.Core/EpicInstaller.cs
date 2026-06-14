using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;

namespace App.Core;

public sealed class EpicOpResult
{
    public string Op = "";
    public string Target = "";
    public bool Ok;
    public string Message = "";
}

/// <summary>
/// Applies a <see cref="EpicPackage"/>'s operations to a mounted game install. Every
/// target is backed up (its pre-change bytes) before the first edit, so an install is
/// reversible. <see cref="Plan"/> previews without writing.
/// </summary>
public static class EpicInstaller
{
    /// <summary>Human-readable preview of what the package will do (no writes).</summary>
    public static List<string> Plan(EpicPackage pkg, RpfManager man, string gtaFolder)
    {
        var lines = new List<string>();
        foreach (var op in pkg.Manifest.Operations)
        {
            string repTarget = op.Target;
            if (op.Op == "replaceFile") { try { repTarget = ResolveReplaceTarget(man, gtaFolder, op); } catch { } }
            bool exists = GameFs.Exists(man, gtaFolder, op.Op == "replaceFile" ? repTarget : op.Target);
            string note = string.IsNullOrEmpty(op.Note) ? "" : "  — " + op.Note;
            lines.Add(op.Op switch
            {
                "replaceFile" => $"{(exists ? "replace" : "add")} file  {repTarget}  ({pkg.PayloadLength(op.Source)} bytes){note}",
                "deleteFile" => $"delete file  {op.Target}{(exists ? "" : "  (missing)")}{note}",
                "xml" => $"xml {op.Action}  {op.Target}  [{op.Xpath}]{note}",
                "text" => $"text {op.Action}  {op.Target}{note}",
                _ => $"?? {op.Op}  {op.Target}{note}",
            });
        }
        return lines;
    }

    /// <summary>
    /// Apply every operation. Backs each touched target up to <paramref name="backupDir"/>
    /// (once) first. Returns a per-op result; keeps going past a failed op.
    /// </summary>
    public static List<EpicOpResult> Apply(EpicPackage pkg, RpfWorkspace ws, string backupDir)
    {
        var man = ws.Manager;
        string gta = ws.GtaFolder;
        var results = new List<EpicOpResult>();
        var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Backup(string vpath)
        {
            if (!backedUp.Add(GameFs.Norm(vpath))) return;
            try
            {
                if (GameFs.Entry(man, vpath) is RpfFileEntry fe)
                {
                    Directory.CreateDirectory(backupDir);
                    File.WriteAllBytes(Path.Combine(backupDir, SafeName(vpath)), RpfWorkspace.ExtractForSave(fe));
                }
                else
                {
                    string dp = GameFs.DiskPath(gta, vpath);
                    if (File.Exists(dp)) { Directory.CreateDirectory(backupDir); File.Copy(dp, Path.Combine(backupDir, SafeName(vpath)), true); }
                }
            }
            catch { /* backup is best-effort; do not block the install */ }
        }

        foreach (var op in pkg.Manifest.Operations)
        {
            var r = new EpicOpResult { Op = op.Op, Target = op.Target };
            try
            {
                Backup(op.Target);
                switch (op.Op)
                {
                    case "replaceFile":
                    {
                        var data = pkg.Payload(op.Source) ?? throw new Exception($"payload '{op.Source}' missing");
                        string target = ResolveReplaceTarget(man, gta, op);
                        r.Target = target;   // show the real destination in the results
                        if (!op.CreateIfMissing && !GameFs.Exists(man, gta, target)) throw new Exception("target missing and createIfMissing=false");
                        r.Ok = GameFs.WriteBytes(man, gta, target, data, out var e1); r.Message = r.Ok ? $"{data.Length} bytes" : e1;
                        break;
                    }
                    case "deleteFile":
                        r.Ok = GameFs.Delete(man, gta, op.Target, out var e2); r.Message = r.Ok ? "deleted" : e2;
                        break;
                    case "xml":
                        r.Ok = ApplyXml(man, gta, op, out var e3); r.Message = r.Ok ? (op.Action ?? "edited") : e3;
                        break;
                    case "text":
                        r.Ok = ApplyText(man, gta, op, out var e4); r.Message = r.Ok ? (op.Action ?? "edited") : e4;
                        break;
                    default:
                        r.Message = "unknown op"; break;
                }
            }
            catch (Exception ex) { r.Ok = false; r.Message = ex.Message; }
            results.Add(r);
        }
        return results;
    }

    /// <summary>
    /// A replaceFile target may name a FOLDER (trailing slash, an archive directory, a
    /// disk directory, or empty = the GTA root) — the payload then keeps its own file
    /// name and lands inside that folder. A target whose last segment already equals
    /// the payload's name is used as-is (e.g. target "update/update.rpf" + payload
    /// update.rpf replaces the archive FILE, not a file inside it).
    /// </summary>
    private static string ResolveReplaceTarget(RpfManager man, string gta, EpicOp op)
    {
        string norm = GameFs.Norm(op.Target);
        string name = Path.GetFileName(op.Source ?? "");
        // payload names may carry the builder's dedupe prefix ("a1b2c3d4_name.ext")
        if (name.Length > 9 && name[8] == '_' && name[..8].All(Uri.IsHexDigit)) name = name[9..];

        if (norm.Length == 0)
            return name.Length > 0 ? name : throw new Exception("empty target needs a payload filename");

        bool explicitFolder = op.Target.EndsWith("/") || op.Target.EndsWith("\\");
        string last = norm[(norm.LastIndexOf('\\') + 1)..];
        if (!explicitFolder && string.Equals(last, name, StringComparison.OrdinalIgnoreCase))
            return norm;   // already the full file path

        bool folder = explicitFolder
            || GameFs.Entry(man, norm) is RpfDirectoryEntry
            || Directory.Exists(GameFs.DiskPath(gta, norm));
        if (!folder) return norm;
        if (name.Length == 0) throw new Exception($"target '{op.Target}' is a folder — the payload needs a filename");
        return norm + "\\" + name;
    }

    private static bool ApplyXml(RpfManager man, string gta, EpicOp op, out string error)
    {
        error = "";
        string? text = GameFs.ReadEditable(man, gta, op.Target, out _);
        if (text == null) { error = "target not found or not XML-convertible"; return false; }
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(text);
        var nodes = string.IsNullOrEmpty(op.Xpath) ? new List<XmlNode> { doc.DocumentElement! }
                                                   : doc.SelectNodes(op.Xpath)?.Cast<XmlNode>().ToList() ?? new();
        if (nodes.Count == 0) { error = $"xpath matched nothing: {op.Xpath}"; return false; }

        foreach (var node in nodes)
        {
            switch ((op.Action ?? "").ToLowerInvariant())
            {
                case "add":
                {
                    var frag = doc.CreateDocumentFragment();
                    frag.InnerXml = op.Xml ?? "";
                    if (op.Append) node.AppendChild(frag); else node.InsertBefore(frag, node.FirstChild);
                    break;
                }
                case "replace":
                {
                    var frag = doc.CreateDocumentFragment();
                    frag.InnerXml = op.Xml ?? "";
                    node.ParentNode?.ReplaceChild(frag, node);
                    break;
                }
                case "remove":
                    node.ParentNode?.RemoveChild(node);
                    break;
                case "settext":
                    node.InnerText = op.Value ?? "";
                    break;
                case "setattr":
                    if (node is not XmlElement el) { error = "setattr target is not an element"; return false; }
                    el.SetAttribute(op.Attr ?? "value", op.Value ?? "");
                    break;
                default:
                    error = "unknown xml action: " + op.Action; return false;
            }
        }
        return GameFs.WriteEditable(man, gta, op.Target, doc.OuterXml, out error);
    }

    private static bool ApplyText(RpfManager man, string gta, EpicOp op, out string error)
    {
        error = "";
        string? text = GameFs.ReadEditable(man, gta, op.Target, out var metaName);
        if (text == null) { error = "target not found or not text"; return false; }
        if (!string.IsNullOrEmpty(metaName)) { error = "text ops only apply to plain-text files (use an xml op for binary metas)"; return false; }

        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        string find = op.Find ?? "", value = op.Value ?? "";
        switch ((op.Action ?? "").ToLowerInvariant())
        {
            case "append": lines.Add(value); break;
            case "insertbefore":
            {
                int i = lines.FindIndex(l => l.Contains(find));
                if (i < 0) { error = $"anchor not found: {find}"; return false; }
                lines.Insert(i, value); break;
            }
            case "insertafter":
            {
                int i = lines.FindIndex(l => l.Contains(find));
                if (i < 0) { error = $"anchor not found: {find}"; return false; }
                lines.Insert(i + 1, value); break;
            }
            case "replace":
            {
                if (!text.Contains(find)) { error = $"text not found: {find}"; return false; }
                text = string.Join(nl, lines).Replace(find, value);
                return GameFs.WriteEditable(man, gta, op.Target, text, out error);
            }
            case "delete":
            {
                int removed = lines.RemoveAll(l => l.Contains(find));
                if (removed == 0) { error = $"nothing matched: {find}"; return false; }
                break;
            }
            default: error = "unknown text action: " + op.Action; return false;
        }
        return GameFs.WriteEditable(man, gta, op.Target, string.Join(nl, lines), out error);
    }

    public static string SafeName(string vpath)
    {
        var sb = new StringBuilder();
        foreach (char c in GameFs.Norm(vpath)) sb.Append(c == '\\' ? '~' : (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        return sb.ToString();
    }
}
