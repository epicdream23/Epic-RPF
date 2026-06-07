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
    /// resolve or the budget is spent. Order: same-named sibling, same folder, then up to a
    /// few parent folders (the model's own archive).
    /// </summary>
    public void IndexForModel(RpfFileEntry modelEntry, ICollection<string> neededNames, int maxYtds = 48)
    {
        if (neededNames.Count == 0) return;
        bool AllResolved() => neededNames.All(_byName.ContainsKey);
        if (AllResolved()) return;

        int loaded = 0;
        var dir = modelEntry.Parent;
        string baseLower = StripExt(modelEntry.NameLower);

        // 1. <model>.ytd sibling — the strongest heuristic for props.
        var sibling = dir?.Files?.FirstOrDefault(f => f.NameLower == baseLower + ".ytd");
        if (sibling != null) { IndexYtd(sibling); loaded++; if (AllResolved()) return; }

        // 2. every .ytd in the folder, then walk up a few parents.
        var d = dir;
        for (int up = 0; d != null && up <= 3 && loaded < maxYtds; up++)
        {
            if (d.Files != null)
                foreach (var f in d.Files)
                    if (f.NameLower.EndsWith(".ytd", StringComparison.Ordinal))
                    {
                        IndexYtd(f);
                        if (++loaded >= maxYtds || AllResolved()) return;
                    }
            d = d.Parent;
        }
    }

    private static string StripExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(0, dot);
    }
}
