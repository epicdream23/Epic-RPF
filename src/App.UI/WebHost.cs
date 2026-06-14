using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static CoreWebView2Environment? _env;

    public static async Task<Bridge> AttachAsync(Window window, WebView2 web, string? injectJs)
    {
        // Keep the WebView2 user-data folder in LocalAppData. By default it is created
        // NEXT TO THE EXE ("EpicRpf.exe.WebView2"), which fails the moment the app is
        // installed to a read-only location (Program Files) — the browser process can't
        // start and the window stays blank. One shared environment per process.
        _env ??= await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicRpf", "WebView2"));
        await web.EnsureCoreWebView2Async(_env);
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
            openPopout: (id, title) => window.Dispatcher.Invoke(() => OpenPopout(id, title)),
            startDrag: paths => StartFileDrag(web, paths),
            pickOpenPath: filter => PickOpenPath(window, filter));

        core.WebMessageReceived += (_, args) =>
        {
            try
            {
                string json = args.WebMessageAsJson;
                // The native file drag-out must run on THIS (UI) thread, synchronously,
                // while the mouse button is still down — so handle it inline rather than
                // through the bridge's gated background queue.
                if (json.Contains("\"dragOut\"") && TryGetDragNodes(json, out int[] ids))
                {
                    bridge.HandleDrag(ids);
                    return;
                }
                // postMessageWithAdditionalObjects: dropped File objects arrive here with
                // their REAL disk paths — imports use the path (no giant base64 strings).
                string[]? dropped = null;
                try
                {
                    if (args.AdditionalObjects is { Count: > 0 } extra)
                        dropped = extra.OfType<CoreWebView2File>()
                                       .Select(f => f.Path)
                                       .Where(p => !string.IsNullOrEmpty(p))
                                       .ToArray();
                }
                catch { }
                bridge.HandleMessage(json, dropped);
            }
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

    // Parse {cmd:"dragOut", nodes:[...]} -> node ids.
    private static bool TryGetDragNodes(string json, out int[] ids)
    {
        ids = Array.Empty<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var c) || c.GetString() != "dragOut") return false;
            if (!root.TryGetProperty("nodes", out var ns) || ns.ValueKind != JsonValueKind.Array) return false;
            var list = new List<int>();
            foreach (var n in ns.EnumerateArray()) if (n.TryGetInt32(out int v)) list.Add(v);
            ids = list.ToArray();
            return ids.Length > 0;
        }
        catch { return false; }
    }

    // Run the OLE drag loop with a real FileDrop data object so the files can be
    // dropped onto Explorer / the desktop / any app. DoDragDrop is modal: it tracks
    // the mouse (the button is still down from the HTML gesture) until the drop, then
    // returns. Must be on the UI thread.
    private static void StartFileDrag(System.Windows.DependencyObject source, string[] paths)
    {
        try
        {
            if (paths == null || paths.Length == 0) return;
            var files = new StringCollection();
            files.AddRange(paths);
            var data = new DataObject();
            data.SetFileDropList(files);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch { /* drag is best-effort; a failed gesture just does nothing */ }
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

    private static string? PickOpenPath(Window w, string filter) => w.Dispatcher.Invoke(() =>
    {
        var dlg = new OpenFileDialog { Title = "Open file", Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter };
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
