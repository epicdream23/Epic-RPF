using System;
using System.Collections.Generic;
using System.IO;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Thin lifecycle wrapper over CodeWalker.Core's <see cref="RpfManager"/>:
/// mounts a GTA V install (Gen8/Legacy), exposes the mounted archives, and
/// provides extraction and path lookups. No geometry or GPU concerns live here.
/// </summary>
public sealed class RpfWorkspace
{
    /// <summary>The GTA V install folder this workspace was mounted from.</summary>
    public string GtaFolder { get; }

    /// <summary>True if mounted as Gen9/Enhanced; false for Gen8/Legacy.</summary>
    public bool Gen9 { get; }

    /// <summary>The underlying CodeWalker manager (escape hatch for typed loads).</summary>
    public RpfManager Manager { get; }

    private RpfWorkspace(string folder, bool gen9, RpfManager man)
    {
        GtaFolder = folder;
        Gen9 = gen9;
        Manager = man;
    }

    /// <summary>
    /// Mounts every <c>.rpf</c> under <paramref name="gtaFolder"/>. Blocks until the
    /// archive scan completes. <paramref name="gen9"/> selects the resource
    /// generation: false = Legacy/Gen8 (Epic/Steam classic, alt:V), true =
    /// Enhanced/Gen9. <paramref name="buildIndex"/> controls whether Core builds
    /// its Jenkins string/hash index — off by default for fast mounting (the
    /// viewer resolves resources directly, not by hash lookup).
    /// </summary>
    public static RpfWorkspace Mount(
        string gtaFolder,
        bool gen9 = false,
        Action<string>? status = null,
        Action<string>? error = null,
        bool buildIndex = false)
    {
        if (string.IsNullOrWhiteSpace(gtaFolder))
            throw new ArgumentException("GTA folder is required.", nameof(gtaFolder));
        if (!Directory.Exists(gtaFolder))
            throw new DirectoryNotFoundException($"GTA folder not found: {gtaFolder}");

        KeyLoader.EnsureLoaded(gtaFolder, gen9);

        var man = new RpfManager();
        // Enhanced-build signature: Init(folder, gen9, status, error, rootOnly, buildIndex).
        // The 2nd arg is gen9 (NOT buildIndex as older CodeWalker builds had it).
        man.Init(gtaFolder, gen9, status ?? Nop, error ?? Nop, false, buildIndex);
        return new RpfWorkspace(gtaFolder, gen9, man);
    }

    /// <summary>All mounted archives (base archives and their nested children, flattened).</summary>
    public IReadOnlyList<RpfFile> AllRpfs => Manager.AllRpfs;

    /// <summary>Raw bytes for a file entry (binary or resource), via its owning archive.</summary>
    /// <remarks>
    /// For a <b>resource</b> (.ydr/.ydd/.yft/.ytd/.ypt) this returns the DECOMPRESSED
    /// payload with the RSC7 header stripped — only valid when paired with the entry's
    /// flags (as <c>GetFile</c> does). It is NOT a standalone file; use
    /// <see cref="ExtractForSave"/> when writing to disk or back into an archive.
    /// </remarks>
    public static byte[] Extract(RpfFileEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        return entry.File.ExtractFile(entry);
    }

    /// <summary>
    /// Bytes for a file entry as a valid standalone file, suitable for saving to disk or
    /// re-importing into an archive. Binary entries pass through; resource entries are
    /// re-wrapped with their RSC7 header (re-compressed), so the result is a real
    /// .ydr/.ytd/.ypt/etc. that other tools — and our own re-open/import — accept.
    /// Without this, an extracted resource is headerless garbage and an undo of a deleted
    /// resource is restored as a (corrupt) binary entry.
    /// </summary>
    public static byte[] ExtractForSave(RpfFileEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        byte[] data = entry.File.ExtractFile(entry);
        if (entry is RpfResourceFileEntry res && data != null)
            return ResourceBuilder.AddResourceHeader(res, ResourceBuilder.Compress(data));
        return data ?? Array.Empty<byte>();
    }

    /// <summary>Look up an entry by full archive path, or null if not found.</summary>
    public RpfEntry? GetEntry(string path) => Manager.GetEntry(path);

    /// <summary>Raw bytes for a file by full archive path, or null if not found.</summary>
    public byte[]? GetFileData(string path) => Manager.GetFileData(path);

    private static void Nop(string _) { }
}
