using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace App.UI;

/// <summary>
/// Shared WebView2 host setup so the main window and torn-off popout windows are
/// configured identically: virtual-host mapping, the JS bridge, settings (custom
/// title-bar drag regions, no browser zoom), and frameless window chrome.
/// </summary>
internal static class WebHost
{
    public static async Task<Bridge> AttachAsync(Window window, WebView2 web, string? injectJs)
    {
        await web.EnsureCoreWebView2Async();
        var core = web.CoreWebView2;

        web.AllowExternalDrop = true;   // accept files dragged in from Explorer (texture import)

        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(
            "app.epicrpf", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        var bridge = new Bridge(
            post: json => window.Dispatcher.Invoke(() => web.CoreWebView2.PostWebMessageAsJson(json)),
            pickFolder: () => PickFolder(window),
            pickSavePath: name => PickSavePath(window, name),
            windowAction: action => window.Dispatcher.Invoke(() => DoWindowAction(window, action)),
            openPopout: (id, title) => window.Dispatcher.Invoke(() => OpenPopout(id, title)));

        core.WebMessageReceived += (_, args) =>
        {
            try { bridge.HandleMessage(args.WebMessageAsJson); }
            catch { /* a malformed message must never take the app down */ }
        };

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;     // we draw our own
        core.Settings.IsZoomControlEnabled = false;              // wheel -> 3D zoom, not browser zoom
        try { core.Settings.IsNonClientRegionSupportEnabled = true; } catch { } // CSS app-region: drag

        if (!string.IsNullOrEmpty(injectJs))
            await core.AddScriptToExecuteOnDocumentCreatedAsync(injectJs);

        web.Source = new Uri("https://app.epicrpf/index.html");
        return bridge;
    }

    private static void DoWindowAction(Window w, string action)
    {
        switch (action)
        {
            case "min": w.WindowState = WindowState.Minimized; break;
            case "max": w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case "close": w.Close(); break;
            default:
                if (action.StartsWith("resize:", StringComparison.Ordinal)) StartResize(w, action.Substring(7));
                break;
        }
    }

    // The window is frameless (WindowChrome's resize border doesn't work because the
    // WebView2 child HWND covers it), so resizing is driven from the HTML edges:
    // JS detects an edge-drag and we kick off the native resize loop here.
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly Dictionary<string, int> HtCodes = new()
    {
        ["l"] = 10, ["r"] = 11, ["t"] = 12, ["tl"] = 13, ["tr"] = 14, ["b"] = 15, ["bl"] = 16, ["br"] = 17,
    };
    private static void StartResize(Window w, string edge)
    {
        if (w.WindowState == WindowState.Maximized) return;
        if (!HtCodes.TryGetValue(edge, out int ht)) return;
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void OpenPopout(int id, string title)
    {
        var win = new PopoutWindow(id, title);
        win.Show();
    }

    private static string? PickFolder(Window w) => w.Dispatcher.Invoke(() =>
    {
        var dlg = new OpenFolderDialog { Title = "Select GTA V install folder" };
        return dlg.ShowDialog(w) == true ? dlg.FolderName : null;
    });

    private static string? PickSavePath(Window w, string name) => w.Dispatcher.Invoke(() =>
    {
        var dlg = new SaveFileDialog { FileName = name, Title = "Save file" };
        return dlg.ShowDialog(w) == true ? dlg.FileName : null;
    });

    /// <summary>Frameless chrome: no OS title bar, but keep resize borders + Aero snap.</summary>
    public static void ApplyChrome(Window w)
    {
        w.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,                          // HTML title bar handles dragging (app-region)
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
    }

    public static void SetAppIcon(Window w)
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "app.png");
            if (File.Exists(p))
                w.Icon = BitmapFrame.Create(new Uri(p), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { }
    }

    public static Brush BgBrush => (Brush)new BrushConverter().ConvertFromString("#0d0f14")!;
}
