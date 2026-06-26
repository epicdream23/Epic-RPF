using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace App.UI;

/// <summary>
/// Rounds the ACTUAL OS window (not just CSS), driven by the Settings "Round edges" choice.
/// The WebView2 lives in a child HWND, so CSS border-radius can't clip the window — we set a
/// rounded-rectangle window region on the top-level HWND, which clips the whole window
/// (chrome + the child WebView) to the desired radius. The region is re-applied on resize and
/// removed while maximized (a maximized window should fill the screen with square corners).
/// </summary>
internal static class WindowRounding
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    // Desired CORNER radius in logical (DIP) pixels, per window.
    private static readonly Dictionary<Window, double> Radii = new();
    private static readonly HashSet<Window> Hooked = new();

    /// <summary>Set the corner radius (logical px; 0 = square) and keep it applied across resizes.</summary>
    public static void Set(Window w, double logicalRadius)
    {
        if (w == null) return;
        Radii[w] = logicalRadius;
        if (Hooked.Add(w))
        {
            w.SizeChanged += (_, __) => Reapply(w);
            w.StateChanged += (_, __) => Reapply(w);
            w.Closed += (_, __) => { Radii.Remove(w); Hooked.Remove(w); };
        }
        Reapply(w);
    }

    private static void Reapply(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!Radii.TryGetValue(w, out double rad)) return;

            // Maximized or square → no region (full rectangular window).
            if (w.WindowState == WindowState.Maximized || rad <= 0)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            if (!GetWindowRect(hwnd, out var rc)) return;
            int wpx = rc.right - rc.left, hpx = rc.bottom - rc.top;
            if (wpx <= 0 || hpx <= 0) return;

            double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
            int d = (int)Math.Round(rad * scale) * 2;   // ellipse diameter
            var rgn = CreateRoundRectRgn(0, 0, wpx + 1, hpx + 1, d, d);
            SetWindowRgn(hwnd, rgn, true);   // window owns the region now (don't delete)
        }
        catch { /* rounding is cosmetic; never throw */ }
    }
}
