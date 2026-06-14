using System.Collections.Generic;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Loads the drawable(s) out of a model file entry (.ydr/.ydd/.yft/.ypt). A .ydr is
/// a single drawable; a .ydd/.ypt is a dictionary of them; a .yft exposes its
/// fragment's drawable. Each loaded drawable carries a display name and its
/// dictionary hash (0 for single-drawable files) — the hash is the stable id used
/// for custom names and as the fallback label (hash_xxxxxxxx) when unresolved.
/// </summary>
public static class DrawableLoader
{
    public readonly struct NamedDrawable
    {
        public readonly string Name;
        public readonly DrawableBase Drawable;
        public readonly uint Hash;
        public NamedDrawable(string name, DrawableBase drawable, uint hash = 0) { Name = name; Drawable = drawable; Hash = hash; }
    }

    public static List<NamedDrawable> Load(RpfManager man, RpfFileEntry entry) => Load(man, entry, out _);

    /// <summary>
    /// Load drawables and also surface the typed game file that owns them
    /// (YdrFile/YddFile/YftFile/YptFile) so callers can mutate + <c>Save()</c> it
    /// (e.g. shader-parameter edits written back into the archive).
    /// </summary>
    public static List<NamedDrawable> Load(RpfManager man, RpfFileEntry entry, out object? ownerFile)
    {
        var result = new List<NamedDrawable>();
        ownerFile = null;
        switch (FileTypes.Detect(entry.NameLower))
        {
            case RpfFileKind.Drawable:
                var ydr = man.GetFile<YdrFile>(entry);
                ownerFile = ydr;
                if (ydr?.Drawable != null) result.Add(new NamedDrawable(entry.Name, ydr.Drawable));
                break;
            case RpfFileKind.DrawableDictionary:
                var ydd = man.GetFile<YddFile>(entry);
                ownerFile = ydd;
                AddDict(result, ydd?.DrawableDict?.Drawables?.data_items, ydd?.DrawableDict?.Hashes, entry.Name);
                break;
            case RpfFileKind.Fragment:
                var yft = man.GetFile<YftFile>(entry);
                ownerFile = yft;
                if (yft?.Fragment?.Drawable != null) result.Add(new NamedDrawable(entry.Name, yft.Fragment.Drawable));
                break;
            case RpfFileKind.ParticleEffect:
                var ypt = man.GetFile<YptFile>(entry);
                ownerFile = ypt;
                AddYptDrawables(result, ypt);
                break;
        }
        return result;
    }

    /// <summary>Like <see cref="Load"/> but for a loose file on disk (raw RSC7 bytes).</summary>
    public static List<NamedDrawable> LoadLoose(byte[] data, string name) => LoadLoose(data, name, out _);

    public static List<NamedDrawable> LoadLoose(byte[] data, string name, out object? ownerFile)
    {
        var result = new List<NamedDrawable>();
        ownerFile = null;
        try
        {
            switch (FileTypes.Detect(name.ToLowerInvariant()))
            {
                case RpfFileKind.Drawable:
                    var ydr = RpfFile.GetResourceFile<YdrFile>(data);
                    ownerFile = ydr;
                    if (ydr?.Drawable != null) result.Add(new NamedDrawable(name, ydr.Drawable));
                    break;
                case RpfFileKind.DrawableDictionary:
                    var ydd = RpfFile.GetResourceFile<YddFile>(data);
                    ownerFile = ydd;
                    AddDict(result, ydd?.DrawableDict?.Drawables?.data_items, ydd?.DrawableDict?.Hashes, name);
                    break;
                case RpfFileKind.Fragment:
                    var yft = RpfFile.GetResourceFile<YftFile>(data);
                    ownerFile = yft;
                    if (yft?.Fragment?.Drawable != null) result.Add(new NamedDrawable(name, yft.Fragment.Drawable));
                    break;
                case RpfFileKind.ParticleEffect:
                    var ypt = RpfFile.GetResourceFile<YptFile>(data);
                    ownerFile = ypt;
                    AddYptDrawables(result, ypt);
                    break;
            }
        }
        catch { }
        return result;
    }

    public static void AddYptDrawables(List<NamedDrawable> result, YptFile? ypt)
    {
        var dd = ypt?.PtfxList?.DrawableDictionary;
        AddDict(result, dd?.Drawables?.data_items, dd?.Hashes, "model");
    }

    // Pair each drawable with its dictionary hash; label by the resolved string or
    // hash_xxxxxxxx (never blank/dots).
    private static void AddDict(List<NamedDrawable> result, DrawableBase[]? items, uint[]? hashes, string fallback)
    {
        if (items == null) return;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) continue;
            uint h = (hashes != null && i < hashes.Length) ? hashes[i] : 0u;
            string nm = h != 0 ? MetaXmlBase.HashString((MetaHash)h) : fallback;
            if (string.IsNullOrWhiteSpace(nm)) nm = $"model_{i}";
            result.Add(new NamedDrawable(nm, items[i], h));
        }
    }
}
