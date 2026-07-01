using System;
using System.IO;
using System.Runtime.InteropServices;

namespace App.Core;

/// <summary>
/// Lightweight diagnostics sink for the launch-protection paths (auto-inject / IFEO). Writes
/// timestamped lines to a log file always, and — once <see cref="OpenConsole"/> is called — to a
/// real console window too (handy from the WinExe UI, which normally has none). Best-effort: never
/// throws, so logging can be sprinkled freely on hot paths.
/// </summary>
public static class Diag
{
    private static readonly object _lock = new();
    private static StreamWriter? _file;
    private static bool _console;

    /// <summary>Path of the on-disk log (set on first write).</summary>
    public static string LogPath { get; private set; } = "";

    /// <summary>Allocate a console window for this process (no-op if it already has one) and route
    /// <see cref="Console"/> output to it. Used so the user can watch live during debugging.</summary>
    public static void OpenConsole(string? title = null)
    {
        lock (_lock)
        {
            if (_console) { if (title != null) TrySetTitle(title); return; }
            try
            {
                if (AllocConsole())
                {
                    var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(stdout);
                }
                // AllocConsole returns false if we already had a console (e.g. rpfcli) — that's fine.
                _console = true;
                TrySetTitle(title ?? "Epic RPF — diagnostics");
                Console.WriteLine("=== Epic RPF diagnostics — " + DateTime.Now + " ===");
                if (LogPath.Length > 0) Console.WriteLine("log file: " + LogPath);
            }
            catch { }
        }
    }

    /// <summary>Write one diagnostic line (timestamped) to the log file, console, and debugger.</summary>
    public static void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
        lock (_lock)
        {
            try { EnsureFile(); _file!.WriteLine(line); } catch { }
            if (_console) { try { Console.WriteLine(line); } catch { } }
            try { System.Diagnostics.Debug.WriteLine(line); } catch { }
        }
    }

    private static void EnsureFile()
    {
        if (_file != null) return;
        string dir = Path.Combine(Path.GetTempPath(), "EpicRpf");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "launch-protection.log");
        _file = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        _file.WriteLine();
        _file.WriteLine("==================== session " + DateTime.Now + " ====================");
    }

    private static void TrySetTitle(string title) { try { Console.Title = title; } catch { } }

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
}
