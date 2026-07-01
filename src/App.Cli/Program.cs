using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using App.Core;
using CodeWalker.GameFiles;

// rpfcli — headless file access to a GTA V install, INCLUDING inside .rpf archives.
// Built so a coding agent (or any script) can view and edit game files directly,
// no manual extraction needed. Virtual paths are GTA-root-relative and may cross
// archive boundaries, e.g.  update/update.rpf/common/data/timecycle/w_clear.xml
//
//   rpfcli ls   [vpath]                      list a folder / archive directory
//   rpfcli find <text> [--ext] [--limit N]   search all entries by name
//   rpfcli info <vpath>                      entry details
//   rpfcli cat  <vpath> [-o out] [--dds dir] read as text (binary meta/resource -> CodeWalker XML)
//   rpfcli get  <vpath> <outfile>            extract raw bytes (valid standalone file)
//   rpfcli put  <vpath> <infile> [--dds dir] write into archive/disk (xml input vs binary
//                                            target is converted back automatically)
//   --gta <folder>   GTA install (default: EPICRPF_GTA env or the Epic path)
//
// Exit codes: 0 ok, 1 usage, 2 not found, 3 operation failed.

Console.OutputEncoding = Encoding.UTF8;

string gta = Environment.GetEnvironmentVariable("EPICRPF_GTA") ?? @"C:\Program Files\Epic Games\GTAV";
var rest = new List<string>();
string? outFile = null, ddsDir = null;
string? password = null, adminKeyPath = null, modeStr = null;
bool extSearch = false, reveal = false, assumeYes = false, noBackup = false;
int limit = 200;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--gta": gta = args[++i]; break;
        case "-o": case "--out": outFile = args[++i]; break;
        case "--dds": ddsDir = args[++i]; break;
        case "--ext": extSearch = true; break;
        case "--limit": limit = int.Parse(args[++i]); break;
        case "-p": case "--password": password = args[++i]; break;
        case "--key": adminKeyPath = args[++i]; break;
        case "--mode": modeStr = args[++i]; break;
        case "--reveal": reveal = true; break;
        case "-y": case "--yes": assumeYes = true; break;
        case "--no-backup": noBackup = true; break;
        default: rest.Add(args[i]); break;
    }
}
if (rest.Count == 0) return Usage();
string cmd = rest[0].ToLowerInvariant();

// `epic` subcommands: create/inspect don't need a mount; install does.
if (cmd == "epic")
{
    string sub = (rest.ElementAtOrDefault(1) ?? "").ToLowerInvariant();
    if (sub == "create") return EpicCreate(rest.ElementAtOrDefault(2), rest.ElementAtOrDefault(3));
    if (sub == "inspect") return EpicInspect(rest.ElementAtOrDefault(2));
    if (sub == "install") { /* falls through to mount below */ }
    else return Usage();
}

// Lock-system commands operate on a single .rpf on disk and must NOT mount the GTA folder
// (a locked archive is encrypted/tampered and would break the mount). Handle them first.
switch (cmd)
{
    case "admin-keygen": return AdminKeygen(rest.ElementAtOrDefault(1));
    case "lock": return LockCmd(rest.ElementAtOrDefault(1));
    case "unlock": return UnlockCmd(rest.ElementAtOrDefault(1));
    case "lockinfo": return LockInfoCmd(rest.ElementAtOrDefault(1));
    case "selftest": return SelfTest();
    case "tolerant": return TolerantCmd(rest.ElementAtOrDefault(1));
}

if (!Directory.Exists(gta)) { Console.Error.WriteLine($"GTA folder not found: {gta}"); return 2; }
var ws = RpfWorkspace.Mount(gta);

return cmd switch
{
    "ls" => Ls(rest.ElementAtOrDefault(1) ?? ""),
    "find" => Find(rest.ElementAtOrDefault(1) ?? ""),
    "info" => Info(rest.ElementAtOrDefault(1) ?? ""),
    "cat" => Cat(rest.ElementAtOrDefault(1) ?? ""),
    "get" => Get(rest.ElementAtOrDefault(1) ?? "", rest.ElementAtOrDefault(2)),
    "put" => Put(rest.ElementAtOrDefault(1) ?? "", rest.ElementAtOrDefault(2)),
    "epic" => EpicInstall(rest.ElementAtOrDefault(2)),
    "ybntest" => YbnTest(rest.ElementAtOrDefault(1)),
    "fixall" => FixAll(),
    _ => Usage(),
};

// Brute-force "archive fix" the WHOLE install: rebuild (defragment) every mounted .rpf — and EVERY
// archive nested inside it, innermost first — so GTA V loads them after editing. Each root file is
// backed up first and auto-rolled-back if the rebuild doesn't re-open cleanly, so a bad rebuild is
// never left on disk (pass --no-backup to skip that, faster but unsafe). Rewrites files in place;
// slow. `--yes` skips the prompt. Run elevated if the install is under Program Files, and with GTA
// closed (an open archive fails with a sharing error).
int FixAll()
{
    var roots = ws.AllRpfs.Where(a => a.Parent == null && a.AllEntries != null).ToList();
    int totalArchives = ws.AllRpfs.Count(a => a.AllEntries != null);
    Console.Error.WriteLine($"Archive-fix EVERY archive under:\n  {gta}");
    Console.Error.WriteLine($"  {roots.Count} root .rpf  ({totalArchives} archive(s) total incl. nested), innermost first.");
    Console.Error.WriteLine(noBackup
        ? "  --no-backup: rewrites in place with NO rollback safety. Back up the install yourself first!"
        : "  Each root is backed up + auto-rolled-back if the rebuild fails. Rewrites in place; slow.");
    if (!assumeYes)
    {
        Console.Error.Write("Type YES to continue: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal))
        { Console.Error.WriteLine("aborted."); return 1; }
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    int okRoots = 0, fixedArchives = 0; var failures = new List<string>();
    for (int i = 0; i < roots.Count; i++)
    {
        var root = roots[i];
        var ordered = ArchiveFixer.Subtree(root);       // root + every nested archive, deepest first
        Console.Error.WriteLine($"[{i + 1}/{roots.Count}] {root.Path ?? root.Name}  ({ordered.Count} archive(s))");
        int lastPct = -1;
        var res = ArchiveFixer.FixRoot(root, ordered, backup: !noBackup, (name, p) =>
        {
            int pct = (int)(p * 100);
            if (pct != lastPct) { lastPct = pct; Console.Error.Write($"\r   {pct,3}%  {name}".PadRight(72)[..72]); }
        });
        if (res.Ok) { okRoots++; fixedArchives += res.Fixed; Console.Error.WriteLine($"\r   done ({res.Fixed} archive(s))".PadRight(72)); }
        else { failures.Add($"{res.Root}: {res.Message}"); Console.Error.WriteLine($"\r   FAILED: {res.Message}".PadRight(72)); }
    }
    sw.Stop();
    Console.WriteLine($"-- fixall: {okRoots}/{roots.Count} root archive(s) ok ({fixedArchives} archive(s) rebuilt), {failures.Count} failed, in {sw.Elapsed:hh\\:mm\\:ss}");
    foreach (var f in failures.Take(80)) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 3;
}

// Validate the .ybn collision -> 3D mesh extractor on a real file.
int YbnTest(string? vpath)
{
    RpfFileEntry? fe = string.IsNullOrEmpty(vpath) ? null : ws.Manager.GetEntry(Norm(vpath)) as RpfFileEntry;
    if (fe == null)
        foreach (var rpf in ws.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries) if (e is RpfFileEntry f && f.NameLower.EndsWith(".ybn", StringComparison.Ordinal)) { fe = f; break; }
            if (fe != null) break;
        }
    if (fe == null) { Console.Error.WriteLine("no .ybn found"); return 2; }
    var ybn = ws.Manager.GetFile<YbnFile>(fe);
    if (ybn?.Bounds == null) { Console.Error.WriteLine("no bounds in " + fe.Path); return 3; }
    var subs = App.Geometry.BoundsMesh.Build(ybn.Bounds);
    long tris = 0; foreach (var s in subs) tris += s.VertexCount / 3;
    Console.WriteLine(fe.Path);
    Console.WriteLine($"  root bound : {ybn.Bounds.Type}");
    Console.WriteLine($"  submeshes  : {subs.Count}");
    Console.WriteLine($"  triangles  : {tris:N0}");
    return subs.Count > 0 ? 0 : 3;
}

// --- .epic extension packaging / install ---
int EpicCreate(string? manifestPath, string? outPath)
{
    if (manifestPath == null || outPath == null || !File.Exists(manifestPath)) { Console.Error.WriteLine("usage: rpfcli epic create <manifest.json> <out.epic>"); return 1; }
    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var manifest = System.Text.Json.JsonSerializer.Deserialize<EpicManifest>(File.ReadAllText(manifestPath), opts);
    if (manifest == null) { Console.Error.WriteLine("bad manifest json"); return 1; }
    string baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    var payload = new Dictionary<string, byte[]>();
    foreach (var op in manifest.Operations.Where(o => o.Op == "replaceFile" && !string.IsNullOrEmpty(o.Source)))
    {
        string srcPath = Path.IsPathRooted(op.Source!) ? op.Source! : Path.Combine(baseDir, op.Source!);
        if (!File.Exists(srcPath)) { Console.Error.WriteLine($"payload source not found: {op.Source}"); return 2; }
        string name = Path.GetFileName(srcPath);
        if (payload.ContainsKey(name)) name = Guid.NewGuid().ToString("N")[..8] + "_" + name;  // de-dup
        payload["payload/" + name] = File.ReadAllBytes(srcPath);
        op.Source = name;   // store the in-package name
    }
    File.WriteAllBytes(outPath, EpicPackage.Pack(manifest, payload));
    Console.WriteLine($"built {outPath}  ({new FileInfo(outPath).Length:N0} b, {manifest.Operations.Count} ops, {payload.Count} payload file(s))");
    return 0;
}

int EpicInspect(string? pkgPath)
{
    if (pkgPath == null || !File.Exists(pkgPath)) { Console.Error.WriteLine("usage: rpfcli epic inspect <pkg.epic>"); return 1; }
    EpicPackage pkg;
    try { pkg = EpicPackage.Open(File.ReadAllBytes(pkgPath)); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    var m = pkg.Manifest;
    Console.WriteLine($"{m.Name} v{m.Version}  by {m.Author}");
    if (!string.IsNullOrEmpty(m.Description)) Console.WriteLine($"  {m.Description}");
    Console.WriteLine($"  target: {m.Target}   operations: {m.Operations.Count}");
    foreach (var op in m.Operations)
        Console.WriteLine("   - " + op.Op switch
        {
            "replaceFile" => $"replaceFile {op.Target}",
            "deleteFile" => $"deleteFile {op.Target}",
            "xml" => $"xml {op.Action} {op.Target} [{op.Xpath}]",
            "text" => $"text {op.Action} {op.Target}",
            _ => $"{op.Op} {op.Target}",
        });
    return 0;
}

int EpicInstall(string? pkgPath)
{
    if (pkgPath == null || !File.Exists(pkgPath)) { Console.Error.WriteLine("usage: rpfcli epic install <pkg.epic> [--gta ...]"); return 1; }
    EpicPackage pkg;
    try { pkg = EpicPackage.Open(File.ReadAllBytes(pkgPath)); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    Console.WriteLine($"Installing: {pkg.Manifest.Name} v{pkg.Manifest.Version}");
    foreach (var l in EpicInstaller.Plan(pkg, ws.Manager, gta)) Console.WriteLine("   plan: " + l);
    string backup = Path.Combine(gta, "EpicRpf_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    var results = EpicInstaller.Apply(pkg, ws, backup);
    int ok = results.Count(r => r.Ok), fail = results.Count - ok;
    foreach (var r in results) Console.WriteLine($"   [{(r.Ok ? "OK" : "FAIL")}] {r.Op} {r.Target} — {r.Message}");
    Console.WriteLine($"-- {ok} ok / {fail} failed. backups: {backup}");
    return fail == 0 ? 0 : 3;
}

// ---- archive lock system (admin tool) ----

int AdminKeygen(string? outPath)
{
    outPath ??= "EpicRpf-Admin.epickey";
    var (text, pub) = AdminKey.Generate();
    File.WriteAllText(outPath, text, new UTF8Encoding(false));
    Console.WriteLine($"Wrote admin PRIVATE key -> {Path.GetFullPath(outPath)}");
    Console.WriteLine("KEEP THIS FILE SECRET — it opens any locked file with no password.");
    Console.WriteLine();
    Console.WriteLine("Bake this PUBLIC key into src/App.Core/AppSecret.cs (AdminPublicKeyB64):");
    Console.WriteLine(pub);
    return 0;
}

// Resolve a lock target: a real path, or a GTA-root-relative vpath.
string? ResolveLockPath(string? arg)
{
    if (string.IsNullOrEmpty(arg)) return null;
    if (File.Exists(arg)) return Path.GetFullPath(arg);
    string p = Path.Combine(gta, Norm(arg));
    return File.Exists(p) ? p : null;
}

int LockCmd(string? arg)
{
    string? path = ResolveLockPath(arg);
    if (path == null)
    {
        if (string.IsNullOrEmpty(arg)) Console.Error.WriteLine("usage: rpfcli lock <file.rpf> [--password P]   (Full encryption; decrypted at runtime by the injected hook)");
        else Console.Error.WriteLine($"file not found: {arg}\n  (looked in current dir: {Directory.GetCurrentDirectory()}\n   and GTA folder: {gta})");
        return 2;
    }
    if (modeStr != null && !modeStr.Equals("full", StringComparison.OrdinalIgnoreCase))
    { Console.Error.WriteLine("only --mode full is supported (the light mode was removed)."); return 1; }
    try { RpfLock.Lock(path, LockMode.Full, password, s => Console.Error.WriteLine(s)); Console.WriteLine($"locked (Full) {path}"); return 0; }
    catch (Exception ex) { Console.Error.WriteLine("lock failed: " + ex.Message); return 3; }
}

int UnlockCmd(string? arg)
{
    string? path = ResolveLockPath(arg);
    if (path == null)
    {
        if (string.IsNullOrEmpty(arg)) Console.Error.WriteLine("usage: rpfcli unlock <file.rpf> [--password P | --key admin.epickey]");
        else Console.Error.WriteLine($"file not found: {arg}\n  (looked in current dir: {Directory.GetCurrentDirectory()}\n   and GTA folder: {gta})");
        return 2;
    }
    AdminKey? admin = null;
    try
    {
        if (adminKeyPath != null) admin = AdminKey.LoadPrivate(adminKeyPath);
        RpfLock.Unlock(path, password, admin, s => Console.Error.WriteLine(s));
        Console.WriteLine($"unlocked {path}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("unlock failed: " + ex.Message); return 3; }
    finally { admin?.Dispose(); }
}

int LockInfoCmd(string? arg)
{
    string? path = ResolveLockPath(arg);
    if (path == null)
    {
        if (string.IsNullOrEmpty(arg)) Console.Error.WriteLine("usage: rpfcli lockinfo <file.rpf> [--reveal]");
        else Console.Error.WriteLine($"file not found: {arg}\n  (looked in current dir: {Directory.GetCurrentDirectory()}\n   and GTA folder: {gta})");
        return 2;
    }
    var info = RpfLock.ReadInfo(path);
    if (!info.IsLocked) { Console.WriteLine("not locked"); return 0; }
    Console.WriteLine($"locked:       {info.Mode}");
    Console.WriteLine($"password:     {(info.PasswordProtected ? "yes" : "no")}");
    Console.WriteLine($"admin escrow: {(info.HasAdminEscrow ? "yes" : "no")}");
    Console.WriteLine($"original:     {info.OriginalName} ({info.OriginalSize:N0} b)");
    if (reveal && info.PasswordProtected)
    {
        try { Console.WriteLine($"embedded password: {RpfLock.RevealPassword(path)}"); }
        catch (Exception ex) { Console.Error.WriteLine("reveal failed: " + ex.Message); }
    }
    return 0;
}

int SelfTest()
{
    string dir = Path.Combine(Path.GetTempPath(), "epicrpf_selftest_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    int failures = 0;
    void Check(string name, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures++; }
    bool TryUnlock(string p, string pw) { try { RpfLock.Unlock(p, pw); return true; } catch { return false; } }
    try
    {
        // A pseudo-RPF: valid 16-byte RPF7 header + random body.
        var data = RandomNumberGenerator.GetBytes(300_000);
        BitConverter.GetBytes(0x52504637u).CopyTo(data, 0);   // RPF7
        BitConverter.GetBytes(10u).CopyTo(data, 4);            // entrycount
        BitConverter.GetBytes(64u).CopyTo(data, 8);            // nameslength
        BitConverter.GetBytes(0x4E45504Fu).CopyTo(data, 12);   // OPEN
        byte[] original = (byte[])data.Clone();

        string f1 = Path.Combine(dir, "full.rpf"); File.WriteAllBytes(f1, original);
        RpfLock.Lock(f1, LockMode.Full, null);
        Check("full: header encrypted", !File.ReadAllBytes(f1).AsSpan(0, 16).SequenceEqual(original.AsSpan(0, 16)));
        Check("full: detected as locked", RpfLock.IsLocked(f1));
        RpfLock.Unlock(f1, null);
        Check("full: round-trip identical", File.ReadAllBytes(f1).AsSpan().SequenceEqual(original));

        string f2 = Path.Combine(dir, "full_pw.rpf"); File.WriteAllBytes(f2, original);
        RpfLock.Lock(f2, LockMode.Full, "hunter2");
        Check("full+pw: wrong password refused", !TryUnlock(f2, "nope"));
        Check("full+pw: reveal embedded password", RpfLock.RevealPassword(f2) == "hunter2");
        Check("full+pw: correct password opens", TryUnlock(f2, "hunter2"));
        Check("full+pw: round-trip identical", File.ReadAllBytes(f2).AsSpan().SequenceEqual(original));

        if (adminKeyPath != null && File.Exists(adminKeyPath))
        {
            string f4 = Path.Combine(dir, "admin.rpf"); File.WriteAllBytes(f4, original);
            RpfLock.Lock(f4, LockMode.Full, "secret");
            using var ak = AdminKey.LoadPrivate(adminKeyPath);
            RpfLock.Unlock(f4, null, ak);
            Check("admin: opens without password via key file", File.ReadAllBytes(f4).AsSpan().SequenceEqual(original));
        }
        else Console.WriteLine("  [skip] admin test — pass --key admin.epickey after baking the public key");
    }
    catch (Exception ex) { Console.Error.WriteLine("selftest exception: " + ex); failures++; }
    finally { try { Directory.Delete(dir, true); } catch { } }
    Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
    return failures == 0 ? 0 : 3;
}

// Recover a "protected"/modified .rpf that GTA reads but tools refuse (e.g. a bogus encryption
// flag) and list what's inside — proves the tolerant loader works on a real file.
int TolerantCmd(string? arg)
{
    if (string.IsNullOrEmpty(arg) || !File.Exists(arg)) { Console.Error.WriteLine("usage: rpfcli tolerant <file.rpf>"); return 2; }
    try { KeyLoader.EnsureLoaded(gta, false); } catch { }   // enables AES/NG recovery too (OPEN needs no keys)
    var rpf = TolerantRpf.TryOpen(Path.GetFullPath(arg), Path.GetFileName(arg));
    if (rpf == null) { Console.WriteLine("could not recover (not RPF7, genuinely corrupt, or an encrypted TOC needing keys)"); return 3; }
    Console.WriteLine($"RECOVERED as {rpf.Encryption} — {rpf.AllEntries.Count} entries, {rpf.GrandTotalFileCount} files, {rpf.GrandTotalRpfCount} archive(s)");
    int n = 0;
    foreach (var e in rpf.AllEntries)
        if (e is RpfFileEntry fe) { Console.WriteLine("  " + fe.Path + (fe is RpfResourceFileEntry ? "  [resource]" : "")); if (++n >= 40) { Console.WriteLine("  …"); break; } }
    return 0;
}

int Usage()
{
    Console.Error.WriteLine("usage: rpfcli <ls|find|info|cat|get|put> [args]  (see source header)");
    Console.Error.WriteLine("  fixall [-y] [--no-backup]   archive-fix EVERY .rpf under --gta + nested, innermost first (slow; backs up + auto-rolls-back each root)");
    Console.Error.WriteLine("  lock system (Full encryption only): admin-keygen [out.epickey] | lock <f.rpf> [--password P]");
    Console.Error.WriteLine("               unlock <f.rpf> [--password P | --key admin.epickey] | lockinfo <f.rpf> [--reveal] | selftest [--key admin.epickey] | tolerant <f.rpf>");
    return 1;
}

string Norm(string vpath) => vpath.Replace('/', '\\').Trim('\\');

// Resolve a vpath to an archive entry (file or dir), or null.
RpfEntry? Entry(string vpath) => ws.Manager.GetEntry(Norm(vpath));

// A vpath that is a real on-disk path under the GTA root (loose file/folder).
string Disk(string vpath) => Path.Combine(gta, Norm(vpath));

int Ls(string vpath)
{
    string norm = Norm(vpath);
    var rows = new List<(string kind, string name, long size)>();

    RpfDirectoryEntry? dir = null;
    var e = norm.Length == 0 ? null : Entry(norm);
    if (e is RpfDirectoryEntry d) dir = d;
    else if (e is RpfFileEntry fe && fe.NameLower.EndsWith(".rpf")) dir = fe.File?.FindChildArchive(fe)?.Root;
    else if (e is RpfFileEntry) { Console.Error.WriteLine("that's a file — use cat/info"); return 1; }

    if (dir != null)
    {
        foreach (var sd in dir.Directories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(("dir", sd.Name, -1));
        foreach (var f in dir.Files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add((f.NameLower.EndsWith(".rpf") ? "rpf" : "file", f.Name, SafeSize(f)));
    }
    else
    {
        // disk folder (GTA root, loose folders) — base .rpf files are still browsable
        string dp = norm.Length == 0 ? gta : Disk(norm);
        if (Directory.Exists(dp))
        {
            foreach (var sd in Directory.GetDirectories(dp).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                rows.Add(("dir", Path.GetFileName(sd), -1));
            foreach (var f in Directory.GetFiles(dp).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                rows.Add((f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) ? "rpf" : "file", Path.GetFileName(f), new FileInfo(f).Length));
        }
        else if (norm.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && File.Exists(dp))
        {
            // a base archive on disk -> list its root
            var rpf = ws.AllRpfs.FirstOrDefault(r => r.Parent == null && string.Equals(r.GetPhysicalFilePath(), dp, StringComparison.OrdinalIgnoreCase));
            if (rpf?.Root != null) return LsDir(rpf.Root);
            Console.Error.WriteLine("archive not mounted"); return 2;
        }
        else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }
    }

    foreach (var (kind, name, size) in rows)
        Console.WriteLine($"{kind,-5} {(size >= 0 ? size.ToString() : ""),12}  {name}");
    Console.WriteLine($"-- {rows.Count} item(s)");
    return 0;

    int LsDir(RpfDirectoryEntry root)
    {
        foreach (var sd in root.Directories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"dir   {"",12}  {sd.Name}");
        foreach (var f in root.Files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{(f.NameLower.EndsWith(".rpf") ? "rpf" : "file"),-5} {SafeSize(f),12}  {f.Name}");
        return 0;
    }
}

int Find(string text)
{
    if (text.Length == 0) return Usage();
    string q = text.ToLowerInvariant();
    string extNeedle = "." + q.TrimStart('.');
    int total = 0;
    foreach (var rpf in ws.AllRpfs)
    {
        if (rpf.AllEntries == null) continue;
        foreach (var en in rpf.AllEntries)
        {
            if (en is not RpfFileEntry fe) continue;
            bool match = extSearch ? fe.NameLower.EndsWith(extNeedle, StringComparison.Ordinal) : fe.NameLower.Contains(q);
            if (!match) continue;
            total++;
            if (total <= limit) Console.WriteLine($"{SafeSize(fe),12}  {fe.Path}");
        }
    }
    Console.WriteLine($"-- {total} match(es){(total > limit ? $", showing first {limit}" : "")}");
    return 0;
}

int Info(string vpath)
{
    var e = Entry(vpath);
    if (e is RpfFileEntry fe)
    {
        Console.WriteLine($"path:  {fe.Path}");
        Console.WriteLine($"size:  {SafeSize(fe)}");
        Console.WriteLine($"kind:  {(fe is RpfResourceFileEntry ? "resource" : "binary")}  archive: {fe.File?.Path}");
        return 0;
    }
    if (e is RpfDirectoryEntry de) { Console.WriteLine($"dir: {de.Path} ({de.Directories.Count} dirs, {de.Files.Count} files)"); return 0; }
    string dp = Disk(vpath);
    if (File.Exists(dp)) { Console.WriteLine($"loose file: {dp} ({new FileInfo(dp).Length} b)"); return 0; }
    if (Directory.Exists(dp)) { Console.WriteLine($"disk dir: {dp}"); return 0; }
    Console.Error.WriteLine($"not found: {vpath}"); return 2;
}

int Cat(string vpath)
{
    byte[] bytes;
    var e = Entry(vpath);
    if (e is RpfFileEntry fe) bytes = RpfWorkspace.Extract(fe);
    else if (File.Exists(Disk(vpath))) bytes = File.ReadAllBytes(Disk(vpath));
    else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }

    string? text = null;
    if (LooksText(bytes)) text = Encoding.UTF8.GetString(bytes);
    else if (e is RpfFileEntry fent)
    {
        // binary meta / resource -> CodeWalker XML (embedded textures go to --dds dir)
        try { text = MetaXml.GetXml(fent, bytes, out _, ddsDir ?? ""); } catch { }
        if (string.IsNullOrEmpty(text)) { Console.Error.WriteLine("binary file with no XML conversion — use `get`"); return 3; }
    }
    else { Console.Error.WriteLine("binary loose file — use `get`"); return 3; }

    if (outFile != null) { File.WriteAllText(outFile, text, new UTF8Encoding(false)); Console.WriteLine($"wrote {text!.Length:N0} chars -> {outFile}"); }
    else Console.Write(text);
    return 0;
}

int Get(string vpath, string? dest)
{
    if (dest == null) return Usage();
    var e = Entry(vpath);
    byte[] bytes;
    if (e is RpfFileEntry fe) bytes = RpfWorkspace.ExtractForSave(fe);          // valid standalone file
    else if (File.Exists(Disk(vpath))) bytes = File.ReadAllBytes(Disk(vpath));
    else { Console.Error.WriteLine($"not found: {vpath}"); return 2; }
    File.WriteAllBytes(dest, bytes);
    Console.WriteLine($"extracted {bytes.Length:N0} b -> {dest}");
    return 0;
}

int Put(string vpath, string? src)
{
    if (src == null || !File.Exists(src)) { Console.Error.WriteLine("input file required"); return 1; }
    string norm = Norm(vpath);
    byte[] input = File.ReadAllBytes(src);

    var e = Entry(norm);
    if (e is RpfFileEntry fe)
    {
        byte[] data = input;
        // XML input against a binary target -> convert back to the binary format.
        // The format is derived from the target itself (GetXml names it, e.g.
        // carcols.ymt -> "carcols.ymt.pso.xml"), so callers never guess PSO vs RSC.
        string inputHead = Encoding.UTF8.GetString(input, 0, Math.Min(input.Length, 64)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        bool inputIsXml = src.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || inputHead.StartsWith('<');
        byte[] current = RpfWorkspace.Extract(fe);
        if (inputIsXml && !LooksText(current))
        {
            string xmlName;
            try { _ = MetaXml.GetXml(fe, current, out xmlName); }
            catch (Exception ex) { Console.Error.WriteLine("target has no XML mapping: " + ex.Message); return 3; }
            var conv = MetaXmlConvert.Convert(Encoding.UTF8.GetString(input), xmlName, ddsDir ?? "", out string? cerr);
            if (conv == null || conv.Length == 0) { Console.Error.WriteLine(cerr ?? "XML conversion produced no data (resources with embedded textures need --dds)"); return 3; }
            data = conv;
        }
        try
        {
            NgEncrypt.EnsureFor(fe.File, s => Console.Error.WriteLine(s));   // NG archives need encrypt tables
            RpfSafeWrite.CreateFile(fe.Parent, fe.Name, data, true);
            Console.WriteLine($"wrote {data.Length:N0} b -> {fe.Path}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("write failed: " + ex.Message); return 3; }
    }

    // new file inside an archive dir, or loose on disk
    int cut = norm.LastIndexOf('\\');
    string parent = cut < 0 ? "" : norm[..cut], leaf = cut < 0 ? norm : norm[(cut + 1)..];
    var pe = parent.Length > 0 ? Entry(parent) : null;
    RpfDirectoryEntry? dir = pe as RpfDirectoryEntry
        ?? (pe is RpfFileEntry prf && prf.NameLower.EndsWith(".rpf") ? prf.File?.FindChildArchive(prf)?.Root : null)
        // a BASE archive on disk isn't an entry in anything — resolve it from the mounted set
        ?? ws.AllRpfs.FirstOrDefault(r => r.Parent == null && string.Equals(r.Path, parent, StringComparison.OrdinalIgnoreCase))?.Root;
    if (dir != null)
    {
        try
        {
            NgEncrypt.EnsureFor(dir.File, s => Console.Error.WriteLine(s));
            RpfSafeWrite.CreateFile(dir, leaf, input, true);
            Console.WriteLine($"created {input.Length:N0} b -> {dir.Path}\\{leaf}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("write failed: " + ex.Message); return 3; }
    }
    string dp = Disk(norm);
    if (Directory.Exists(Path.GetDirectoryName(dp)!)) { File.WriteAllBytes(dp, input); Console.WriteLine($"wrote loose {input.Length:N0} b -> {dp}"); return 0; }
    Console.Error.WriteLine($"no such target or parent: {vpath}");
    return 2;
}

static long SafeSize(RpfFileEntry fe) { try { return fe.GetFileSize(); } catch { return fe.FileSize; } }

static bool LooksText(byte[] b)
{
    int n = Math.Min(b.Length, 2048), bad = 0;
    for (int i = 0; i < n; i++) { byte c = b[i]; if (c == 0) return false; if (c < 9 || (c > 13 && c < 32)) bad++; }
    return bad < n / 20 + 1;
}
