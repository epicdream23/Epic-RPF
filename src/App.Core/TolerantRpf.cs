using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Opens RPF archives that GTA V still loads but CodeWalker/OpenIV refuse — typically
/// "protected" mods whose 16-byte header encryption flag has been set to a bogus value
/// (e.g. "CFXP"). CodeWalker defaults an unknown flag to NG-decrypt and chokes on the
/// plaintext table-of-contents; GTA treats an unknown flag as no-encryption and reads it
/// fine. We brute-force the real TOC encryption by feeding CodeWalker's own scanner a header
/// where the flag is overridden, and keep whichever yields a valid entry table. Nested child
/// archives that are protected the same way are recovered too (iteratively). The file on disk
/// is never modified — the override is applied only to the bytes the scanner reads.
/// </summary>
public static class TolerantRpf
{
    private const uint RPF7 = 0x52504637;
    private const uint OPEN = 0x4E45504F;
    // Plaintext first (the common anti-tool trick), then real encryptions for flag-corrupted-
    // but-genuinely-encrypted TOCs (those need GTA5Keys loaded, else they just fail and skip).
    private static readonly uint[] EncCandidates = { OPEN, 0x00000000, 0x0FFFFFF9 /*AES*/, 0x0FEFFFFF /*NG*/ };

    // CodeWalker's private ScanStructure(BinaryReader, status, error) — lets us scan from our
    // own (header-overriding) stream instead of the public overload that re-opens the file.
    private static readonly MethodInfo? ScanFromReader = typeof(RpfFile).GetMethod(
        "ScanStructure", BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(BinaryReader), typeof(Action<string>), typeof(Action<string>) }, null);

    private static readonly Action<string> NoOp = _ => { };

    /// <summary>Cheap check that the file starts with the RPF7 magic.</summary>
    public static bool LooksLikeRpf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && BitConverter.ToUInt32(b, 0) == RPF7;
        }
        catch { return false; }
    }

    /// <summary>Recover a broken/modified .rpf (and its similarly-broken nested archives) into a
    /// browsable <see cref="RpfFile"/>, or null. The caller splices it into the manager. Never
    /// modifies the file on disk.</summary>
    public static RpfFile? TryOpen(string fullPath, string relPath)
    {
        if (ScanFromReader == null || !LooksLikeRpf(fullPath)) return null;

        var patches = new List<(long off, byte[] bytes)>();

        // 1) Get the ROOT to parse: as-is first (a legit root with protected children), else by
        //    overriding the root's encryption flag (offset 12) with each candidate.
        RpfFile? rpf = ScanWith(fullPath, relPath, patches);
        if (rpf == null)
        {
            foreach (uint enc in EncCandidates)
            {
                var p = new List<(long, byte[])> { (12, BitConverter.GetBytes(enc)) };
                rpf = ScanWith(fullPath, relPath, p);
                if (rpf != null) { patches = p; break; }
            }
        }
        if (rpf == null) return null;

        // 2) Recover nested child archives that failed to scan (assume the same plaintext-flag
        //    trick → override each failed child's flag to OPEN) and re-scan until stable.
        byte[] openBytes = BitConverter.GetBytes(OPEN);
        for (int iter = 0; iter < 8; iter++)
        {
            var failed = FailedChildStarts(rpf).Where(o => patches.All(p => p.off != o + 12)).ToList();
            if (failed.Count == 0) break;
            foreach (long start in failed) patches.Add((start + 12, openBytes));
            var rescanned = ScanWith(fullPath, relPath, patches);
            if (rescanned == null) break;   // keep the best we had
            rpf = rescanned;
        }
        return rpf;
    }

    private static RpfFile? ScanWith(string fullPath, string relPath, List<(long off, byte[] bytes)> patches)
    {
        var rpf = new RpfFile(fullPath, relPath);
        try
        {
            using var ps = new HeaderPatchStream(fullPath, patches);
            using var br = new BinaryReader(ps);
            ScanFromReader!.Invoke(rpf, new object[] { br, NoOp, NoOp });
            return IsValid(rpf) ? rpf : null;
        }
        catch { return null; }
    }

    // Absolute file offsets of nested .rpf entries that didn't resolve to a scanned child archive.
    private static List<long> FailedChildStarts(RpfFile root)
    {
        var res = new List<long>();
        var stack = new Stack<RpfFile>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var arc = stack.Pop();
            if (arc.AllEntries != null)
            {
                foreach (var e in arc.AllEntries)
                {
                    if (e is RpfBinaryFileEntry be && be.NameLower != null && be.NameLower.EndsWith(".rpf"))
                    {
                        RpfFile? child = null;
                        try { child = arc.FindChildArchive(be); } catch { }
                        if (child == null) res.Add(arc.StartPos + (long)be.FileOffset * 512);
                    }
                }
            }
            if (arc.Children != null) foreach (var c in arc.Children) stack.Push(c);
        }
        return res;
    }

    private static bool IsValid(RpfFile rpf)
    {
        var all = rpf.AllEntries;
        if (all == null || all.Count == 0 || rpf.Root == null) return false;
        // Reject a wrong guess that didn't throw but produced garbage: require that most named
        // entries have plausible (printable, sane-length) names.
        int ok = 0, named = 0;
        foreach (var e in all)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            named++;
            if (e.Name.Length <= 256 && e.Name.All(c => c >= 0x20 && c < 0x7F)) ok++;
        }
        return named == 0 ? all.Count == 1 : ok >= (named + 1) / 2;   // empty archive = just the root
    }

    // Read-only stream over a file that overrides bytes at one or more absolute offsets.
    private sealed class HeaderPatchStream : Stream
    {
        private readonly FileStream _fs;
        private readonly List<(long off, byte[] bytes)> _patches;

        public HeaderPatchStream(string path, List<(long off, byte[] bytes)> patches)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            _patches = patches;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = _fs.Position;
            int n = _fs.Read(buffer, offset, count);
            foreach (var (off, bytes) in _patches)
            {
                long pEnd = off + bytes.Length;
                if (off >= start + n || pEnd <= start) continue;   // no overlap with this read
                for (int i = 0; i < n; i++)
                {
                    long abs = start + i;
                    if (abs >= off && abs < pEnd) buffer[offset + i] = bytes[abs - off];
                }
            }
            return n;
        }

        public override long Position { get => _fs.Position; set => _fs.Position = value; }
        public override long Seek(long o, SeekOrigin origin) => _fs.Seek(o, origin);
        public override long Length => _fs.Length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _fs.Dispose(); base.Dispose(disposing); }
    }
}
