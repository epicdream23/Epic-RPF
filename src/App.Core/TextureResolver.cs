using System;
using System.Collections.Generic;
using System.Linq;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Resolves textures a drawable references by name but doesn't embed, by indexing
/// nearby <c>.ytd</c> files on demand. GTA has ~88k ytds, so a global index is
/// impractical; instead, when a model is opened we index its sibling/same-folder/
/// parent-folder ytds (the common location of a model's txd) until the needed
/// names resolve. Everything found is cached process-wide, so later opens are cheap.
/// </summary>
public sealed class TextureResolver
{
    private readonly RpfManager _man;
    private readonly Dictionary<string, Texture> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _indexed = new(StringComparer.OrdinalIgnoreCase);

    public TextureResolver(RpfManager man) => _man = man;

    /// <summary>Look up a previously-indexed texture by name.</summary>
    public Texture? Resolve(string? name)
        => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var t) ? t : null;

    /// <summary>Load a .ytd (once) and index all its textures by name.</summary>
    public void IndexYtd(RpfFileEntry ytdEntry)
    {
        if (ytdEntry == null || !_indexed.Add(ytdEntry.Path)) return;
        try
        {
            var items = _man.GetFile<YtdFile>(ytdEntry)?.TextureDict?.Textures?.data_items;
            if (items == null) return;
            foreach (var t in items)
                if (t?.Name != null && t.Data?.FullData != null)
                    _byName.TryAdd(t.Name, t);
        }
        catch { }
    }

    /// <summary>
    /// Index ytds near <paramref name="modelEntry"/> until all <paramref name="neededNames"/>
    /// resolve or the budget is spent. The lookup is layered, strongest signal first:
    ///   1. txds named after the model (incl. <c>_hi</c>/<c>+hi</c>/<c>_lod</c> variants);
    ///   2. txds named after each *needed texture* — RAGE groups a texture into a same-named
    ///      txd, which is the decisive signal in big shared archives (e.g. weapons.rpf, where
    ///      a model's txd is alphabetically far down a list of hundreds);
    ///   3. a broad scan of this folder and a few parents, ordered by name relevance so the
    ///      right txd is hit early, continuing until resolved or the (generous) budget is spent.
    /// The old code capped the scan at 48 alphabetically-first ytds, so weapon body textures
    /// (e.g. <c>w_sr_*</c>) in weapons.rpf were never reached and models rendered untextured.
    /// </summary>
    public void IndexForModel(RpfFileEntry modelEntry, ICollection<string> neededNames, int maxYtds = 256)
    {
        if (neededNames.Count == 0) return;
        bool AllResolved() => neededNames.All(_byName.ContainsKey);
        if (AllResolved()) return;

        var dir = modelEntry.Parent;
        string baseLower = StripExt(modelEntry.NameLower);

        // 1. txds named after the model (and the base name with detail suffixes stripped,
        //    so a "_hi"/"_lod" model finds the base <model>.ytd that holds its textures).
        foreach (var cand in TxdCandidates(baseLower))
            if (TryIndexNamed(dir, cand) && AllResolved()) return;

        // 2. txds named after each needed texture (the dominant RAGE convention).
        foreach (var nm in neededNames.ToList())
        {
            string n = nm.ToLowerInvariant();
            bool any = TryIndexNamed(dir, n);
            any |= TryIndexNamed(dir, n + "+hi");
            if (any && AllResolved()) return;
        }

        // 3. broad scan of this folder then a few parents, ordered by relevance.
        int loaded = 0;
        var d = dir;
        for (int up = 0; d != null && up <= 3 && loaded < maxYtds && !AllResolved(); up++)
        {
            var ytds = d.Files?.Where(f => f.NameLower.EndsWith(".ytd", StringComparison.Ordinal)).ToList();
            if (ytds != null)
            {
                ytds.Sort((a, b) => Relevance(b.NameLower, baseLower, neededNames)
                                  - Relevance(a.NameLower, baseLower, neededNames));
                foreach (var f in ytds)
                {
                    IndexYtd(f);
                    if (++loaded >= maxYtds || AllResolved()) break;
                }
            }
            d = d.Parent;
        }
    }

    // Index a ytd named exactly "<baseName>.ytd" in this directory, if present.
    private bool TryIndexNamed(RpfDirectoryEntry? dir, string baseName)
    {
        var f = dir?.Files?.FirstOrDefault(x => x.NameLower == baseName + ".ytd");
        if (f == null) return false;
        IndexYtd(f);
        return true;
    }

    // Candidate txd base-names for a model: itself, its "+hi" high-detail twin, and the
    // same with a trailing detail suffix (_hi / +hi / _lod[n] / _l[n]) stripped.
    private static IEnumerable<string> TxdCandidates(string baseLower)
    {
        yield return baseLower;
        yield return baseLower + "+hi";
        string stripped = StripDetailSuffix(baseLower);
        if (stripped != baseLower) { yield return stripped; yield return stripped + "+hi"; }
    }

    private static string StripDetailSuffix(string s)
    {
        foreach (var suf in new[] { "+hi", "_hi", "_lod", "_lod1", "_lod2", "_lod3", "_l1", "_l2", "_l3" })
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length)
                return s.Substring(0, s.Length - suf.Length);
        return s;
    }

    // How likely a ytd holds what the model needs: the longest shared prefix between the
    // ytd's name and either the model base name or any needed texture name.
    private static int Relevance(string ytdNameLower, string baseLower, ICollection<string> needed)
    {
        string ytdBase = StripExt(ytdNameLower);
        int best = SharedPrefix(ytdBase, baseLower);
        foreach (var n in needed)
        {
            int p = SharedPrefix(ytdBase, n.ToLowerInvariant());
            if (p > best) best = p;
        }
        return best;
    }

    private static int SharedPrefix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static string StripExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(0, dot);
    }
}
