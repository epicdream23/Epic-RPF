using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// "Archive fix" = rebuild (defragment) RPF archives so GTA V loads them after editing — the same
/// job as the ArchiveFix-for-GTA tool, built on CodeWalker's <see cref="RpfFile.Defragment"/>.
///
/// A parent archive embeds its children's bytes, so children MUST be rebuilt before their parents
/// (<see cref="Subtree"/> / <see cref="OrderDeepestFirst"/> give that order). All archives nested in
/// one on-disk <c>.rpf</c> share that single physical file, so a fix is wrapped per-root with a
/// <b>backup → rebuild → re-scan verify → auto-rollback</b> guard: if the rebuilt file doesn't
/// re-open cleanly (e.g. a huge archive hits the RPF block-offset limit), the original is restored
/// and the file is left exactly as it was. A bad rebuild is never left on disk.
/// </summary>
public static class ArchiveFixer
{
    /// <summary>The top-level on-disk archive that physically contains <paramref name="f"/>.</summary>
    public static RpfFile Root(RpfFile f) { var r = f; while (r.Parent != null) r = r.Parent; return r; }

    /// <summary>Nesting depth (0 = a root on disk).</summary>
    public static int Depth(RpfFile f) { int d = 0; for (var p = f.Parent; p != null; p = p.Parent) d++; return d; }

    /// <summary><paramref name="target"/> plus every archive nested inside it, deepest first.</summary>
    public static List<RpfFile> Subtree(RpfFile target)
    {
        var all = new List<RpfFile>();
        var stack = new Stack<RpfFile>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var a = stack.Pop();
            if (a?.AllEntries == null) continue;
            all.Add(a);
            if (a.Children != null) foreach (var c in a.Children) stack.Push(c);
        }
        return OrderDeepestFirst(all);
    }

    /// <summary>Children before parents (deepest nesting first).</summary>
    public static List<RpfFile> OrderDeepestFirst(IEnumerable<RpfFile> archives)
        => archives.Where(a => a?.AllEntries != null).Distinct().OrderByDescending(Depth).ToList();

    public sealed class FixResult
    {
        public string Root = "";
        public int Fixed;
        public bool Ok;
        public bool RolledBack;
        public string Message = "";
    }

    /// <summary>
    /// Defragment every archive in <paramref name="ordered"/> — which MUST all live in
    /// <paramref name="root"/>'s physical file — deepest first. With <paramref name="backup"/> on,
    /// the root file is copied aside first and, if it fails to re-open cleanly afterwards (any scan
    /// error, including a nested archive), the original is restored. Never throws.
    /// </summary>
    public static FixResult FixRoot(RpfFile root, IReadOnlyList<RpfFile> ordered,
        bool backup = true, Action<string, float>? progress = null)
    {
        var res = new FixResult { Root = SafeName(root) };
        string disk;
        try { disk = root.GetPhysicalFilePath(); }
        catch (Exception ex) { res.Message = "no physical path: " + ex.Message; return res; }

        string bak = disk + ".epicfixbak";
        if (backup)
        {
            try { if (File.Exists(bak)) File.Delete(bak); File.Copy(disk, bak, true); }
            catch (Exception ex) { res.Message = "couldn't back up " + Path.GetFileName(disk) + ": " + ex.Message; return res; }
        }

        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i];
                progress?.Invoke(SafePath(a), (float)i / Math.Max(1, ordered.Count));
                try { NgEncrypt.EnsureFor(a); } catch { /* OPEN archives need no encrypt tables */ }
                RpfFile.Defragment(a, null, recursive: false);
                res.Fixed++;
            }

            // Verify the rebuilt file (and every archive nested in it) re-opens cleanly from disk.
            if (backup && !ReopensClean(disk, root.Path))
                throw new Exception("archive did not re-open cleanly after rebuild "
                    + "(a very large archive can exceed the RPF block-offset limit and can't be rebuilt in place)");

            res.Ok = true;
            if (backup) { try { File.Delete(bak); } catch { /* leave the backup if we can't delete it */ } }
            return res;
        }
        catch (Exception ex)
        {
            // Only delete the backup once it's actually been copied back — if the restore
            // itself throws (disk still locked/read-only/full), that backup is the ONLY
            // clean copy left; deleting it here would turn a failed rebuild into real data
            // loss instead of a recoverable one. RolledBack distinguishes "restored" from
            // "restore also failed" so the caller never mistakes the latter for the former.
            string restoreFailReason = "";
            if (backup)
            {
                try { File.Copy(bak, disk, true); res.RolledBack = true; }
                catch (Exception rbEx) { restoreFailReason = " (" + rbEx.Message + ")"; }
                if (res.RolledBack) { try { File.Delete(bak); } catch { /* harmless leftover; next fix attempt replaces it */ } }
            }
            res.Ok = false;
            res.Fixed = 0;
            res.Message = ex.Message + (res.RolledBack
                ? " — ROLLED BACK (file left unchanged)"
                : backup
                    ? $" — ROLLBACK FAILED{restoreFailReason}: the archive may be corrupted. A clean backup was "
                      + $"kept at {Path.GetFileName(bak)} next to it — rename it over the original to restore manually."
                    : "");
            return res;
        }
    }

    // Re-scan the archive from disk; any scan error (top-level or a nested archive) means the
    // rebuild produced something the game/tools can't read.
    private static bool ReopensClean(string disk, string? relpath)
    {
        try
        {
            bool err = false;
            var rpf = new RpfFile(disk, relpath ?? Path.GetFileName(disk));
            rpf.ScanStructure(_ => { }, _ => err = true);
            if (err || rpf.AllEntries == null || rpf.AllEntries.Count == 0 || rpf.LastException != null) return false;
            return AllChildrenClean(rpf);
        }
        catch { return false; }
    }

    private static bool AllChildrenClean(RpfFile f)
    {
        if (f.LastException != null) return false;
        if (f.Children != null) foreach (var c in f.Children) if (!AllChildrenClean(c)) return false;
        return true;
    }

    private static string SafeName(RpfFile f) { try { return f.Name ?? f.Path ?? "?"; } catch { return "?"; } }
    private static string SafePath(RpfFile f) { try { return f.Path ?? f.Name ?? "?"; } catch { return "?"; } }
}
