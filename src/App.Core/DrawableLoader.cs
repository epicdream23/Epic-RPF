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
        }
        return result;
    }

    private static string NameOf(Drawable d, string fallback)
        => string.IsNullOrWhiteSpace(d.Name) ? fallback : d.Name!;
}
