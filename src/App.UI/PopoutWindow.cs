using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace App.UI;

/// <summary>
/// A frameless window that hosts a single tab torn off the main window. It shares
/// the (static) mounted workspace via <see cref="Bridge"/> and pulls the tab's
/// rendered state from the bridge using the popout id.
/// </summary>
public sealed class PopoutWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly int _popoutId;
    private Bridge? _bridge;

    public PopoutWindow(int popoutId, string title)
    {
        _popoutId = popoutId;
        Title = title;
        Width = 960; Height = 680;
        MinWidth = 420; MinHeight = 300;
        Background = WebHost.BgBrush;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Grid { Children = { _web } };

        WebHost.ApplyChrome(this);
        WebHost.SetAppIcon(this);

        SourceInitialized += (_, __) => PositionAtCursor();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string inject = "window.__POPOUT=" +
            JsonSerializer.Serialize(new { id = _popoutId, title = Title }) + ";";
        _bridge = await WebHost.AttachAsync(this, _web, inject);
    }

    // Drop the new window near the cursor (so it lands where the tab was released,
    // including a second monitor). Cursor is in physical px; convert to DIPs.
    private void PositionAtCursor()
    {
        try
        {
            if (!GetCursorPos(out var p)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = p.X / dpi.DpiScaleX - 80;
            Top = p.Y / dpi.DpiScaleY - 14;
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
}
