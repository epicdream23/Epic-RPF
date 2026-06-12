using System;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace App.UI;

public partial class MainWindow : Window
{
    private Bridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        WebHost.ApplyChrome(this);   // frameless; the HTML toolbar is the title bar
        WebHost.SetAppIcon(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _bridge = await WebHost.AttachAsync(this, Web, BuildInject());
    }

    /// <summary>Open a file forwarded from a second launch (file-association double-click)
    /// as a tab, and bring this window to the front. Runs on the UI thread.</summary>
    public void OpenExternalFile(string path)
    {
        try
        {
            if (Web?.CoreWebView2 == null) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate(); Topmost = true; Topmost = false;   // pop to foreground
            _ = Web.CoreWebView2.ExecuteScriptAsync("window.__openExternalFile && window.__openExternalFile(" + JsonSerializer.Serialize(path) + ")");
        }
        catch { }
    }

    // Pre-navigation injection: optional auto-load (deep-link / smoke) and, when the app
    // was launched to view a single file, the path the front-end opens in viewer mode.
    private static string? BuildInject()
    {
        var sb = new StringBuilder();
        if (Environment.GetEnvironmentVariable("EPICRPF_AUTOLOAD") == "1")
        {
            string folder = Environment.GetEnvironmentVariable("EPICRPF_FOLDER") ?? @"C:\Program Files\Epic Games\GTAV";
            bool gen9 = Environment.GetEnvironmentVariable("EPICRPF_GEN9") == "1";
            string open = Environment.GetEnvironmentVariable("EPICRPF_OPEN") ?? "model";
            sb.Append("window.__AUTOLOAD=" + JsonSerializer.Serialize(new { folder, gen9, open }) + ";");
        }
        if (EpicApp.ViewerMode && EpicApp.PendingFile != null)
            sb.Append("window.__VIEWFILE=" + JsonSerializer.Serialize(EpicApp.PendingFile) + ";");
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
