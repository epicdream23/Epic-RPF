using System;
using System.IO;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Makes writes into NG-encrypted archives possible. <c>GTA5Keys.LoadFromPath</c>
/// only loads the DECRYPT material; the NG ENCRYPT tables/LUTs are normally computed
/// during CodeWalker's full from-exe key generation and are otherwise null — so
/// <c>RpfFile.CreateFile</c> into an NG archive throws "Unable to encrypt - tables
/// not loaded." This computes them from the loaded decrypt tables (same math as
/// CodeWalker: Gauss-solve rounds 0, 1, 16; lookup-tables for rounds 2–15) and
/// caches the result in LocalAppData, so the cost is paid once per machine.
/// </summary>
public static class NgEncrypt
{
    private static readonly object Gate = new();

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicRpf");

    /// <summary>True when NG encryption is ready (tables + LUTs present).</summary>
    public static bool Ready => GTA5Keys.PC_NG_ENCRYPT_TABLES != null && GTA5Keys.PC_NG_ENCRYPT_LUTs != null;

    /// <summary>
    /// Ensure the NG encrypt tables are available: load from cache, else compute
    /// from the decrypt tables (requires keys loaded, i.e. after a mount) and cache.
    /// No-op when already present. <paramref name="status"/> gets progress lines —
    /// the first-ever compute takes a while.
    /// </summary>
    public static void Ensure(Action<string>? status = null)
    {
        if (Ready) return;
        lock (Gate)
        {
            if (Ready) return;
            if (GTA5Keys.PC_NG_DECRYPT_TABLES == null)
                throw new InvalidOperationException("NG decrypt tables not loaded — mount a GTA install first.");

            string tablesPath = Path.Combine(CacheDir, "gtav_ng_encrypt_tables.dat");
            string lutsPath = Path.Combine(CacheDir, "gtav_ng_encrypt_luts.dat");
            try
            {
                if (File.Exists(tablesPath) && File.Exists(lutsPath))
                {
                    status?.Invoke("Loading cached NG encrypt tables…");
                    GTA5Keys.PC_NG_ENCRYPT_TABLES = CryptoIO.ReadNgTables(tablesPath);
                    GTA5Keys.PC_NG_ENCRYPT_LUTs = CryptoIO.ReadNgLuts(lutsPath);
                    if (Ready) return;
                }
            }
            catch { /* bad cache -> recompute below */ }

            var dec = GTA5Keys.PC_NG_DECRYPT_TABLES;
            var tables = new uint[17][][];
            for (int i = 0; i < 17; i++)
            {
                tables[i] = new uint[16][];
                for (int j = 0; j < 16; j++) tables[i][j] = new uint[256];
            }
            var luts = new GTA5NGLUT[17][];
            for (int i = 0; i < 17; i++)
            {
                luts[i] = new GTA5NGLUT[16];
                for (int j = 0; j < 16; j++) luts[i][j] = new GTA5NGLUT();
            }

            status?.Invoke("Computing NG encrypt tables (one-time, cached afterwards)…");
            tables[0] = RandomGauss.Solve(dec[0]);
            tables[1] = RandomGauss.Solve(dec[1]);
            for (int i = 2; i <= 15; i++)
            {
                status?.Invoke($"  round {i - 1}/14…");
                luts[i] = LookUpTableGenerator.BuildLUTs2(dec[i]);
            }
            tables[16] = RandomGauss.Solve(dec[16]);

            GTA5Keys.PC_NG_ENCRYPT_TABLES = tables;
            GTA5Keys.PC_NG_ENCRYPT_LUTs = luts;

            try
            {
                Directory.CreateDirectory(CacheDir);
                CryptoIO.WriteNgTables(tablesPath, tables);
                CryptoIO.WriteLuts(lutsPath, luts);
                status?.Invoke("Cached NG encrypt tables.");
            }
            catch { /* cache is an optimisation only */ }
        }
    }

    /// <summary>Ensure encryption material for writing into this archive, if needed.</summary>
    public static void EnsureFor(RpfFile? archive, Action<string>? status = null)
    {
        if (archive != null && archive.Encryption == RpfEncryption.NG) Ensure(status);
    }
}
