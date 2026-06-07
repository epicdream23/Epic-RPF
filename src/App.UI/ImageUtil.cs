using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace App.UI;

/// <summary>RGBA8 → PNG (via WPF imaging, so no extra image dependency).</summary>
public static class ImageUtil
{
    public static byte[] PngFromRgba(byte[] rgba, int w, int h)
    {
        if (w <= 0 || h <= 0 || rgba.Length < (long)w * h * 4) return Array.Empty<byte>();
        // WPF wants BGRA; swap R/B from our RGBA.
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4] = rgba[i * 4 + 2];
            bgra[i * 4 + 1] = rgba[i * 4 + 1];
            bgra[i * 4 + 2] = rgba[i * 4];
            bgra[i * 4 + 3] = rgba[i * 4 + 3];
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static string DataUrlPng(byte[] png)
        => png.Length == 0 ? "" : "data:image/png;base64," + Convert.ToBase64String(png);
}
