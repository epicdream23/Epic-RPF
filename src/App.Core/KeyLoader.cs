using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Loads the GTA5 resource-decryption keys once per process. We always use the
/// Gen8/Legacy key set (gen9:false) — that is what the Epic/Steam "Legacy" game
/// and alt:V run on, and the only asset generation this viewer targets in v1.
/// </summary>
public static class KeyLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static bool _gen9;

    /// <summary>Whether keys have been loaded in this process.</summary>
    public static bool IsLoaded => _loaded;

    /// <summary>Which key generation is currently loaded (true=Gen9/Enhanced).</summary>
    public static bool Gen9 => _gen9;

    /// <summary>The folder the keys were loaded from, or null.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>
    /// Loads the key tables for the requested generation from a GTA V install.
    /// The key tables are process-global, so we only (re)load when the generation
    /// actually changes — switching generation mid-session reloads the keys.
    /// </summary>
    public static void EnsureLoaded(string gtaFolder, bool gen9)
    {
        if (_loaded && _gen9 == gen9) return;
        lock (Gate)
        {
            if (_loaded && _gen9 == gen9) return;
            GTA5Keys.LoadFromPath(gtaFolder, gen9);
            LoadedFrom = gtaFolder;
            _gen9 = gen9;
            _loaded = true;
        }
    }
}
