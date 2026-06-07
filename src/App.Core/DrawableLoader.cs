using System.Collections.Generic;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Loads the drawable(s) out of a model file entry (.ydr/.ydd/.yft). A .ydr is a
/// single drawable, a .ydd is a dictionary of them, and a .yft exposes its
/// fragment's drawable. Each loaded drawable is paired with a display name.
/// </summary>
public static class DrawableLoader
{
    public readonly struct NamedDrawable
    {
        public readonly string Name;
        public readonly DrawableBase Drawable;
        public NamedDrawable(string name, DrawableBase drawable) { Name = name; Drawable = drawable; }
    }

    public static List<NamedDrawable> Load(RpfManager man, RpfFileEntry entry)
    {
        var result = new List<NamedDrawable>();
        switch (FileTypes.Detect(entry.NameLower))
        {
            case RpfFileKind.Drawable:
            {
                var ydr = man.GetFile<YdrFile>(entry);
                if (ydr?.Drawable != null)
                    result.Add(new NamedDrawable(entry.Name, ydr.Drawable));
                break;
            }
            case RpfFileKind.DrawableDictionary:
            {
                var ydd = man.GetFile<YddFile>(entry);
                var drawables = ydd?.Drawables;
                if (drawables != null)
                    foreach (var d in drawables)
                        if (d != null)
                            result.Add(new NamedDrawable(NameOf(d, entry.Name), d));
                break;
            }
            case RpfFileKind.Fragment:
            {
                var yft = man.GetFile<YftFile>(entry);
                var frag = yft?.Fragment;
                if (frag?.Drawable != null)
                    result.Add(new NamedDrawable(entry.Name, frag.Drawable));
                break;
            }
            case RpfFileKind.ParticleEffect:
                AddYptDrawables(result, man.GetFile<YptFile>(entry));
                break;
        }
        return result;
    }

    /// <summary>
    /// Same as <see cref="Load"/> but for a loose file on disk (raw resource bytes,
    /// not inside an archive). Uses CodeWalker's RSC7 loader.
    /// </summary>
    public static List<NamedDrawable> LoadLoose(byte[] data, string name)
    {
        var result = new List<NamedDrawable>();
        try
        {
            switch (FileTypes.Detect(name.ToLowerInvariant()))
            {
                case RpfFileKind.Drawable:
                    var ydr = RpfFile.GetResourceFile<YdrFile>(data);
                    if (ydr?.Drawable != null) result.Add(new NamedDrawable(name, ydr.Drawable));
                    break;
                case RpfFileKind.DrawableDictionary:
                    var ydd = RpfFile.GetResourceFile<YddFile>(data);
                    if (ydd?.Drawables != null)
                        foreach (var d in ydd.Drawables) if (d != null) result.Add(new NamedDrawable(NameOf(d, name), d));
                    break;
                case RpfFileKind.Fragment:
                    var yft = RpfFile.GetResourceFile<YftFile>(data);
                    if (yft?.Fragment?.Drawable != null) result.Add(new NamedDrawable(name, yft.Fragment.Drawable));
                    break;
                case RpfFileKind.ParticleEffect:
                    AddYptDrawables(result, RpfFile.GetResourceFile<YptFile>(data));
                    break;
            }
        }
        catch { }
        return result;
    }

    // A .ypt's particle list owns a dictionary of drawables (the meshes a particle
    // system renders). Each is named by its dictionary hash.
    public static void AddYptDrawables(List<NamedDrawable> result, YptFile? ypt)
    {
        var dd = ypt?.PtfxList?.DrawableDictionary;
        var items = dd?.Drawables?.data_items;
        var hashes = dd?.Hashes;
        if (items == null) return;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) continue;
            string nm = (hashes != null && i < hashes.Length) ? ((MetaHash)hashes[i]).ToCleanString() : $"drawable_{i}";
            result.Add(new NamedDrawable(nm, items[i]));
        }
    }

    private static string NameOf(Drawable d, string fallback)
        => string.IsNullOrWhiteSpace(d.Name) ? fallback : d.Name!;
}
