using System;
using System.IO;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Pre-write safety checks for RPF archive edits, to make a bad write fail loudly
/// instead of silently corrupting the archive.
///
/// Two real failure modes this guards against (both seen in the wild):
///  1. OFFSET CAP. RAGE stores each entry's position as a 512-byte BLOCK index in
///     only 23 bits (resource files) / 24 bits (binary files) — see
///     RpfResourceFileEntry (&amp;0x7FFFFF) / RpfBinaryFileEntry (&amp;0xFFFFFF). That caps
///     where a file can physically sit at ~4.29 GB / ~8.59 GB. Writing/relocating a
///     file past the cap truncates its offset, so the file table points into garbage
///     and the WHOLE archive is corrupt (restoring one file can't fix it).
///  2. MALFORMED RESOURCE. A .ypt/.ytd built from a bad import (e.g. an sRGB/foreign
///     DDS) can carry a texture with pixel format 0 or undecodable data. That both
///     crashes the game on load AND, because the resource is oversized/inconsistent,
///     can trip CreateFile's relocation math and corrupt neighbouring data.
///
/// <see cref="Check"/> returns an error string (the write must be refused) or null.
/// </summary>
public static class RpfWriteGuard
{
    const long ResourceOffsetCapBytes = 0x7FFFFFL * 512L;   // ~4.29 GB (23-bit block offset)
    const long BinaryOffsetCapBytes   = 0xFFFFFFL * 512L;   // ~8.59 GB (24-bit block offset)
    const long Margin = 64L * 1024 * 1024;                  // leave headroom for relocations

    /// <summary>Null if writing <paramref name="data"/> as <paramref name="name"/> into
    /// <paramref name="archive"/> is safe; otherwise a human-readable reason to refuse.</summary>
    public static string? Check(RpfFile? archive, string? name, byte[]? data)
    {
        if (archive == null || data == null || data.Length == 0) return null;
        string lower = (name ?? "").ToLowerInvariant();
        bool isResource = data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x37435352u; // 'RSC7'

        // 1) block-offset cap on the ROOT physical archive.
        try
        {
            RpfFile root = archive;
            while (root.Parent != null) root = root.Parent;
            string p = root.GetPhysicalFilePath();
            long physical = (!string.IsNullOrEmpty(p) && File.Exists(p)) ? new FileInfo(p).Length : 0;
            long cap = isResource ? ResourceOffsetCapBytes : BinaryOffsetCapBytes;
            if (physical > 0 && physical + data.Length + Margin > cap)
                return $"refusing write — archive \"{Path.GetFileName(p)}\" is {physical / (1024 * 1024)} MB; adding {Math.Max(1, data.Length / (1024 * 1024))} MB risks the RPF "
                     + $"{(isResource ? "~4.3 GB resource" : "~8.6 GB")} offset limit, past which the entry table truncates and the whole archive corrupts. "
                     + "Put new/large content in a separate DLC .rpf instead of growing this one.";
        }
        catch { /* size check is best-effort; never block on its own failure */ }

        // 2) resource integrity for textured resources.
        if (isResource && (lower.EndsWith(".ypt") || lower.EndsWith(".ytd")))
            return ValidateResourceTextures(data, lower);

        return null;
    }

    static string? ValidateResourceTextures(byte[] data, string lower)
    {
        TextureDictionary? dict;
        try
        {
            dict = lower.EndsWith(".ytd")
                ? RpfFile.GetResourceFile<YtdFile>(data)?.TextureDict
                : RpfFile.GetResourceFile<YptFile>(data)?.PtfxList?.TextureDictionary;
        }
        catch (Exception ex)
        {
            return "refusing write — the built resource is malformed and would corrupt the file "
                 + $"(it could not be re-parsed): {ex.Message}";
        }

        var texes = dict?.Textures?.data_items;
        if (texes == null) return null;
        foreach (var t in texes)
        {
            if (t == null) continue;
            // Format 0 = a pixel format RAGE can't create (e.g. an sRGB DX10 DDS that
            // DDSIO mapped to 0). It serializes fine but crashes the game the instant the
            // texture loads — the exact case the app's TextureFromDds already rejects.
            // (We deliberately DON'T decode pixels here: DDSIO.GetPixels returns null for
            // formats it doesn't decode like BC7, which would false-positive valid textures.)
            if ((uint)t.Format == 0u)
                return $"refusing write — texture \"{t.Name}\" has an unsupported pixel format (0); "
                     + "it would crash the game on load. Re-import it as DXT5 / BC7 / DXT1 (non-sRGB).";
        }
        return null;
    }
}
