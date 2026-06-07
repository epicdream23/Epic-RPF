using System;
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
        _bridge = await WebHost.AttachAsync(this, Web, BuildAutoloadInject());
    }

    // Optional auto-load (deep-link / smoke verification), injected before navigation.
    private static string? BuildAutoloadInject()
    {
        if (Environment.GetEnvironmentVariable("EPICRPF_AUTOLOAD") != "1") return null;
        string folder = Environment.GetEnvironmentVariable("EPICRPF_FOLDER")
                        ?? @"C:\Program Files\Epic Games\GTAV";
        bool gen9 = Environment.GetEnvironmentVariable("EPICRPF_GEN9") == "1";
        string open = Environment.GetEnvironmentVariable("EPICRPF_OPEN") ?? "model";
        var auto = new { folder, gen9, open };
        return "window.__AUTOLOAD=" + JsonSerializer.Serialize(auto) + ";";
    }
}
