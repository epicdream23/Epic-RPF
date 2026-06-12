using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace App.Core;

/// <summary>
/// The <c>.epic</c> extension package: an AES-encrypted container holding a
/// <see cref="EpicManifest"/> plus the payload files it installs. Encryption is
/// obfuscation — it keeps packages from being trivially opened/edited by generic
/// archive tools (a key is derived from a baked passphrase + per-file salt), not a
/// security boundary. On disk:
///   magic "EPICPKG\x01" | salt[16] | iv[16] | AES-CBC( inner-zip )
/// The inner zip has <c>manifest.json</c> and <c>payload/&lt;name&gt;</c> entries.
///
/// Both packing and opening STREAM through the AES + zip layers, and payload entries
/// are STORED (not deflated) — game resources like .rpf/.ytd are already compressed, so
/// re-deflating them just burned minutes of CPU for ~no size win. This lets a multi-GB
/// payload (e.g. a ~2 GB update.rpf) pack/extract in seconds with bounded memory, instead
/// of buffering several GB of arrays (which also hit the &lt;2 GB single-array limit).
/// </summary>
public sealed class EpicPackage : IDisposable
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EPICPKG\x01");
    private const int HeaderLen = 8 + 16 + 16;   // magic + salt + iv
    // Obfuscation passphrase (NOT a security secret — the app must be able to read every .epic).
    private const string Pass = "EpicRpf::extension::v1::4fd1c5";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public EpicManifest Manifest { get; private init; } = new();

    // Open archive over a seekable backing stream (an in-memory buffer for Open(byte[]) or a
    // temp file for OpenFile). Payloads are read lazily so we never hold them all at once.
    private ZipArchive? _zip;

    private ZipArchiveEntry? Entry(string? name)
        => name == null || _zip == null ? null
         : _zip.GetEntry(name.StartsWith("payload/") ? name : "payload/" + name);

    /// <summary>Uncompressed size of a payload entry, without extracting it.</summary>
    public long PayloadLength(string? name) => Entry(name)?.Length ?? 0;

    /// <summary>Payload bytes for an entry named in an op's <c>source</c>, or null.</summary>
    public byte[]? Payload(string? name)
    {
        var e = Entry(name);
        if (e == null) return null;
        using var s = e.Open();
        using var ms = e.Length is > 0 and < int.MaxValue ? new MemoryStream((int)e.Length) : new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Open a payload entry as a stream (for streaming a large file to its target).</summary>
    public Stream? OpenPayload(string? name) => Entry(name)?.Open();

    public void Dispose() { _zip?.Dispose(); _zip = null; }

    // ---------------------------------------------------------------- create

    /// <summary>
    /// Stream an encrypted .epic to <paramref name="dest"/>: AES-CBC over a zip of the
    /// manifest + payload files. Payload entries are streamed from disk and STORED, so
    /// even multi-GB sources pack quickly with bounded memory. <paramref name="dest"/> is
    /// left open for the caller.
    /// </summary>
    public static void PackToStream(EpicManifest manifest, IReadOnlyList<(string name, string path)> payload, Stream dest)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = DeriveKey(salt);
        aes.GenerateIV();

        dest.Write(Magic, 0, Magic.Length);
        dest.Write(salt, 0, salt.Length);
        dest.Write(aes.IV, 0, aes.IV.Length);

        using var enc = aes.CreateEncryptor();
        using (var cs = new CryptoStream(dest, enc, CryptoStreamMode.Write, leaveOpen: true))
        using (var zip = new ZipArchive(cs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var me = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var s = me.Open())
            {
                byte[] mb = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOpts);
                s.Write(mb, 0, mb.Length);
            }
            foreach (var (name, path) in payload)
            {
                string n = name.StartsWith("payload/") ? name : "payload/" + name;
                var e = zip.CreateEntry(n, CompressionLevel.NoCompression);   // already-compressed game data
                using var es = e.Open();
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                fs.CopyTo(es, 1 << 20);
            }
            // zip disposed -> central directory; then cs disposed -> final AES block to dest.
        }
    }

    /// <summary>In-memory pack from byte payloads (CLI / small callers). Payload is STORED.</summary>
    public static byte[] Pack(EpicManifest manifest, IDictionary<string, byte[]> payload)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = DeriveKey(salt);
        aes.GenerateIV();

        using var outMs = new MemoryStream();
        outMs.Write(Magic, 0, Magic.Length);
        outMs.Write(salt, 0, salt.Length);
        outMs.Write(aes.IV, 0, aes.IV.Length);
        using var enc = aes.CreateEncryptor();
        using (var cs = new CryptoStream(outMs, enc, CryptoStreamMode.Write, leaveOpen: true))
        using (var zip = new ZipArchive(cs, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOpts), CompressionLevel.Optimal);
            foreach (var kv in payload)
            {
                string n = kv.Key.StartsWith("payload/") ? kv.Key : "payload/" + kv.Key;
                WriteEntry(zip, n, kv.Value, CompressionLevel.NoCompression);
            }
        }
        return outMs.ToArray();
    }

    // ---------------------------------------------------------------- open

    /// <summary>Open + decrypt a .epic from a byte buffer (drag-drop bytes / CLI).</summary>
    public static EpicPackage Open(byte[] bytes)
    {
        if (bytes.Length < HeaderLen || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not an Epic RPF extension (.epic) file.");
        byte[] salt = bytes[8..24], iv = bytes[24..40];

        using var aes = Aes.Create();
        aes.Key = DeriveKey(salt);
        var plain = new MemoryStream();
        try
        {
            using var dec = aes.CreateDecryptor(aes.Key, iv);
            using var src = new MemoryStream(bytes, HeaderLen, bytes.Length - HeaderLen, writable: false);
            using var cs = new CryptoStream(src, dec, CryptoStreamMode.Read);
            cs.CopyTo(plain);
        }
        catch { plain.Dispose(); throw new InvalidDataException("Could not decrypt the .epic file (corrupt or not an Epic extension)."); }
        plain.Position = 0;
        return FromZip(plain);
    }

    /// <summary>
    /// Stream-open a .epic from a file path: decrypts to a temporary (auto-deleted) file so a
    /// huge package never needs a multi-GB in-memory buffer. Dispose the returned package to
    /// release the archive and delete the temp file.
    /// </summary>
    public static EpicPackage OpenFile(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var hdr = new byte[HeaderLen];
        f.ReadExactly(hdr, 0, hdr.Length);
        if (!hdr.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not an Epic RPF extension (.epic) file.");
        byte[] salt = hdr[8..24], iv = hdr[24..40];

        using var aes = Aes.Create();
        aes.Key = DeriveKey(salt);

        string temp = Path.Combine(Path.GetTempPath(), "EpicRpf_open_" + Guid.NewGuid().ToString("N") + ".tmp");
        var tmp = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
        try
        {
            using (var dec = aes.CreateDecryptor(aes.Key, iv))
            using (var cs = new CryptoStream(f, dec, CryptoStreamMode.Read, leaveOpen: true))
                cs.CopyTo(tmp, 1 << 20);
            tmp.Position = 0;
            return FromZip(tmp);   // package takes ownership of tmp (closed + deleted on Dispose)
        }
        catch { tmp.Dispose(); throw new InvalidDataException("Could not decrypt the .epic file (corrupt or not an Epic extension)."); }
    }

    /// <summary>Read just the manifest of a .epic (for previews).</summary>
    public static EpicManifest PeekManifest(byte[] bytes) { using var p = Open(bytes); return p.Manifest; }

    /// <summary>Read just the manifest of a .epic from a file (streamed).</summary>
    public static EpicManifest PeekManifestFile(string path) { using var p = OpenFile(path); return p.Manifest; }

    private static EpicPackage FromZip(Stream seekablePlainZip)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(seekablePlainZip, ZipArchiveMode.Read, leaveOpen: false); }
        catch { seekablePlainZip.Dispose(); throw new InvalidDataException("Could not read the .epic contents (corrupt file)."); }

        var me = zip.GetEntry("manifest.json");
        if (me == null) { zip.Dispose(); throw new InvalidDataException("Extension has no manifest."); }

        EpicManifest? manifest;
        using (var s = me.Open())
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            manifest = JsonSerializer.Deserialize<EpicManifest>(ms.ToArray(), JsonOpts);
        }
        if (manifest == null) { zip.Dispose(); throw new InvalidDataException("Extension has no manifest."); }
        return new EpicPackage { Manifest = manifest, _zip = zip };
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    private static byte[] DeriveKey(byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(Pass, salt, 100_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }
}
