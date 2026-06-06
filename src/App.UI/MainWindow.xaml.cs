using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace App.UI;

public partial class MainWindow : Window
{
    private Bridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;

        // Serve the front-end from a virtual https origin so ES modules / fetch work.
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(
            "app.epicrpf", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        _bridge = new Bridge(
            post: json => Dispatcher.Invoke(() => Web.CoreWebView2.PostWebMessageAsJson(json)),
            pickFolder: PickFolderOnUi,
            pickSavePath: PickSavePathOnUi);

        core.WebMessageReceived += (_, args) =>
        {
            try { _bridge!.HandleMessage(args.WebMessageAsJson); }
            catch { /* a malformed message must never take the app down */ }
        };

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        // Optional auto-load (deep-link / smoke verification). Injected before navigation.
        if (Environment.GetEnvironmentVariable("EPICRPF_AUTOLOAD") == "1")
        {
            string folder = Environment.GetEnvironmentVariable("EPICRPF_FOLDER")
                            ?? @"C:\Program Files\Epic Games\GTAV";
            bool gen9 = Environment.GetEnvironmentVariable("EPICRPF_GEN9") == "1";
            string open = Environment.GetEnvironmentVariable("EPICRPF_OPEN") ?? "model"; // "model" | "meta"
            var auto = new { folder, gen9, open };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__AUTOLOAD=" + System.Text.Json.JsonSerializer.Serialize(auto) + ";");
        }

        Web.Source = new Uri("https://app.epicrpf/index.html");
    }

    private string? PickFolderOnUi() => Dispatcher.Invoke(() =>
    {
        var dlg = new OpenFolderDialog { Title = "Select GTA V install folder" };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    });

    private string? PickSavePathOnUi(string suggestedName) => Dispatcher.Invoke(() =>
    {
        var dlg = new SaveFileDialog { FileName = suggestedName, Title = "Extract file" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    });
}
