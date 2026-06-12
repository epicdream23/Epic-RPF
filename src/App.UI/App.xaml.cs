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
        string? file = e.Args.FirstOrDefault(File.Exists);

        // Already-running instance? Hand it the file (it opens a tab) and exit.
        if (file != null && SingleInstance.TrySendToExisting(file)) { Shutdown(); return; }

        // We're the primary instance: serve the pipe so later launches forward here,
        // remember any file to open, and register file associations (best-effort).
        PendingFile = file;
        ViewerMode = file != null;
        SingleInstance.StartServer(p => Dispatcher.Invoke(() => (MainWindow as MainWindow)?.OpenExternalFile(p)));
        FileAssoc.EnsureRegistered();

        base.OnStartup(e);   // creates MainWindow via StartupUri
    }
}
