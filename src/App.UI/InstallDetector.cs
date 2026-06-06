using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace App.UI;

/// <summary>A discovered GTA V install.</summary>
public sealed class GtaInstall
{
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";   // Epic / Steam / Rockstar
    public bool Gen9 { get; init; }              // true = Enhanced, false = Legacy
    public string Edition => Gen9 ? "Enhanced" : "Legacy";
    public string Label => $"{Source} · {Edition}";
}

/// <summary>
/// Finds GTA V installs from Epic, Steam and Rockstar via well-known paths,
/// launcher manifests and the registry. A folder qualifies if it has the game
/// exe plus a base archive; Legacy vs Enhanced is inferred from the exe name.
/// </summary>
public static class InstallDetector
{
    public static List<GtaInstall> Detect()
    {
        var found = new List<GtaInstall>();

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!Directory.Exists(full)) return;
            if (!IsGtaFolder(full, out bool gen9)) return;
            if (found.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(new GtaInstall { Path = full, Source = source, Gen9 = gen9 });
        }

        // Epic
        Add(@"C:\Program Files\Epic Games\GTAV", "Epic");
        foreach (var p in EpicManifestLocations()) Add(p, "Epic");

        // Steam
        foreach (var lib in SteamLibraries())
        {
            Add(Path.Combine(lib, "steamapps", "common", "Grand Theft Auto V"), "Steam");
            Add(Path.Combine(lib, "steamapps", "common", "Grand Theft Auto V Legacy"), "Steam");
            Add(Path.Combine(lib, "steamapps", "common", "GTAV Enhanced"), "Steam");
        }

        // Rockstar
        Add(ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", "InstallFolder"), "Rockstar");
        Add(ReadReg(Registry.LocalMachine, @"SOFTWARE\Rockstar Games\Grand Theft Auto V", "InstallFolder"), "Rockstar");
        Add(@"C:\Program Files\Rockstar Games\Grand Theft Auto V", "Rockstar");

        return found;
    }

    private static bool IsGtaFolder(string dir, out bool gen9)
    {
        gen9 = false;
        bool legacy = File.Exists(Path.Combine(dir, "GTA5.exe"));
        bool enhanced = File.Exists(Path.Combine(dir, "GTA5_Enhanced.exe"));
        bool hasRpf = File.Exists(Path.Combine(dir, "common.rpf")) || File.Exists(Path.Combine(dir, "x64a.rpf"));
        if (enhanced && !legacy) gen9 = true;
        return (legacy || enhanced) && hasRpf;
    }

    private static IEnumerable<string> EpicManifestLocations()
    {
        string dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in SafeFiles(dir, "*.item"))
        {
            string? loc = null, name = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                if (root.TryGetProperty("InstallLocation", out var l)) loc = l.GetString();
                if (root.TryGetProperty("DisplayName", out var n)) name = n.GetString();
            }
            catch { }
            if (loc != null && (name?.Contains("Grand Theft Auto", StringComparison.OrdinalIgnoreCase) ?? false))
                yield return loc;
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (steam == null) yield break;
        yield return steam;

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static string? ReadReg(RegistryKey hive, string subkey, string name)
    {
        try { using var k = hive.OpenSubKey(subkey); return k?.GetValue(name) as string; }
        catch { return null; }
    }
}
