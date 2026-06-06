using System;

namespace App.Core;

/// <summary>Coarse RAGE file classification, derived from the file extension.</summary>
public enum RpfFileKind
{
    Unknown,
    Drawable,            // .ydr
    DrawableDictionary,  // .ydd
    Fragment,            // .yft
    TextureDictionary,   // .ytd
    ParticleEffect,      // .ypt
    ClipDictionary,      // .ycd
    Bounds,              // .ybn
    Navmesh,             // .ynv
    Meta,                // .ymt/.meta/.pso/...
    Xml,                 // .xml
    Text,                // .txt/.lua/...
    Audio,               // .awc
    Archive,             // .rpf
}

/// <summary>Which viewer tab a file should open in. Hex is the always-available fallback.</summary>
public enum ViewerKind { Model, Texture, Text, Hex }

/// <summary>Extension-based file-type detection and viewer routing.</summary>
public static class FileTypes
{
    public static RpfFileKind Detect(string name) => Ext(name) switch
    {
        ".ydr" => RpfFileKind.Drawable,
        ".ydd" => RpfFileKind.DrawableDictionary,
        ".yft" => RpfFileKind.Fragment,
        ".ytd" => RpfFileKind.TextureDictionary,
        ".ypt" => RpfFileKind.ParticleEffect,
        ".ycd" => RpfFileKind.ClipDictionary,
        ".ybn" => RpfFileKind.Bounds,
        ".ynv" => RpfFileKind.Navmesh,
        ".rpf" => RpfFileKind.Archive,
        ".xml" => RpfFileKind.Xml,
        ".meta" or ".ymt" or ".ymf" or ".pso" or ".ymap" or ".ytyp" => RpfFileKind.Meta,
        ".txt" or ".log" or ".ini" or ".cfg" or ".lua" or ".nametable" or ".dat" => RpfFileKind.Text,
        ".awc" => RpfFileKind.Audio,
        _ => RpfFileKind.Unknown,
    };

    /// <summary>Routes a file to a viewer. Unknown/binary types fall back to hex — never a dead end.</summary>
    public static ViewerKind Route(string name) => Detect(name) switch
    {
        RpfFileKind.Drawable or RpfFileKind.DrawableDictionary or RpfFileKind.Fragment => ViewerKind.Model,
        RpfFileKind.TextureDictionary => ViewerKind.Texture,
        RpfFileKind.Xml or RpfFileKind.Meta or RpfFileKind.Text => ViewerKind.Text,
        _ => ViewerKind.Hex,
    };

    private static string Ext(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name.Substring(dot).ToLowerInvariant();
    }
}
