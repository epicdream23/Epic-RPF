using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace App.UI;

public partial class EpicApp : Application
{
    /// <summary>A file passed on the command line (file association double-click), if any.</summary>
    public static string? PendingFile;
    /// <summary>True when this process launched to view a single file (no instance was running).</summary>
    public static bool ViewerMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless association refresh: the installer runs "EpicRpf.exe --register" right after
        // install so every file-type icon (incl. the new .rpf icon) is applied immediately,
        // without opening a window. EnsureRegistered also pokes the shell to refresh icons.
        if (e.Args.Any(a => string.Equals(a, "--register", StringComparison.OrdinalIgnoreCase)))
        {
            FileAssoc.EnsureRegistered();
            Shutdown();
            return;
        }

        string? file = e.Args.FirstOrDefault(File.Exists);
        bool isRpf = file != null && file.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);

        // Already-running instance? Hand it the file (it opens a tab) and exit.
        if (file != null && SingleInstance.TrySendToExisting(file)) { Shutdown(); return; }

        // We're the primary instance: serve the pipe so later launches forward here,
        // remember any file to open, and register file associations (best-effort).
        // A .rpf opens the FULL app (mount + browse) rather than the chromeless single-file
        // viewer — an archive is browsed, not shown as one giant hex dump.
        PendingFile = file;
        ViewerMode = file != null && !isRpf;
        SingleInstance.StartServer(p => Dispatcher.Invoke(() => (MainWindow as MainWindow)?.OpenExternalFile(p)));
        FileAssoc.EnsureRegistered();

        base.OnStartup(e);   // creates MainWindow via StartupUri
    }
}
