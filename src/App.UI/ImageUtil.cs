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

    /// <summary>
    /// Decode image bytes (PNG/JPG/BMP/WEBP/…) to RGBA8 (row-major, 4 bytes/pixel) with
    /// STRAIGHT (un-premultiplied) alpha. Returns null if it can't decode. We convert to
    /// Bgra32 (not Pbgra32), so transparent pixels keep their authored colour — unlike a
    /// browser &lt;canvas&gt; + getImageData, which zeroes the RGB of fully-transparent pixels
    /// and turns a transparent/empty PNG into solid black. Decoding server-side also avoids
    /// canvas colour-space/taint quirks.
    /// </summary>
    public static byte[]? RgbaFromImage(byte[] bytes, out int w, out int h)
    {
        w = 0; h = 0;
        try
        {
            using var ms = new MemoryStream(bytes);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count == 0) return null;
            var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            w = conv.PixelWidth; h = conv.PixelHeight;
            if (w <= 0 || h <= 0) { w = 0; h = 0; return null; }
            int stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            for (int i = 0; i + 2 < px.Length; i += 4)   // BGRA -> RGBA
                (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return px;
        }
        catch { w = 0; h = 0; return null; }
    }

    public static string DataUrlPng(byte[] png)
        => png.Length == 0 ? "" : "data:image/png;base64," + Convert.ToBase64String(png);
}
