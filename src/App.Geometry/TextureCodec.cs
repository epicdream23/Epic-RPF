using System;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace App.Geometry;

/// <summary>
/// Texture decode/encode. Decoding turns a RAGE <see cref="Texture"/> or a raw
/// .dds into RGBA8; encoding turns RGBA8 into a .dds of a chosen BCn format (for
/// the OpenIV-style PNG→DDS converter). CodeWalker's DDSIO handles the common
/// formats fast; BCnEncoder.Net covers BC7 decode and all encoding.
/// </summary>
public static class TextureCodec
{
    public static readonly string[] Formats =
        { "DXT1", "DXT3", "DXT5", "BC7", "BC4", "BC5", "RGBA" };

    /// <summary>RAGE texture → RGBA8 (row-major, 4 bytes/pixel). Null on failure.</summary>
    public static byte[]? DecodeTexture(Texture tex, out int w, out int h)
    {
        w = tex.Width; h = tex.Height;
        try
        {
            // CodeWalker's DDSIO returns pixels in BGRA order; flip to RGBA so it
            // matches the BCnEncoder path (and what callers expect). Without this,
            // red and blue are swapped (a blue texture renders as yellow/red).
            var px = DDSIO.GetPixels(tex, 0);
            if (px != null && px.Length >= w * h * 4) { SwapRB(px); return px; }
        }
        catch { }
        try
        {
            return DecodeDds(DDSIO.GetDDSFile(tex), out w, out h);
        }
        catch { return null; }
    }

    /// <summary>In-place BGRA↔RGBA channel swap (red and blue).</summary>
    private static void SwapRB(byte[] px)
    {
        for (int i = 0; i + 2 < px.Length; i += 4)
            (px[i], px[i + 2]) = (px[i + 2], px[i]);
    }

    /// <summary>Raw .dds bytes → RGBA8. Null on failure.</summary>
    public static byte[]? DecodeDds(byte[] dds, out int w, out int h)
    {
        w = 0; h = 0;
        try
        {
            using var ms = new MemoryStream(dds);
            var file = DdsFile.Load(ms);
            var dec = new BcDecoder();
            var pixels = dec.Decode(file);
            w = (int)file.header.dwWidth;
            h = (int)file.header.dwHeight;
            return ToBytes(pixels);
        }
        catch { return null; }
    }

    /// <summary>RGBA8 → .dds bytes in the requested format (with mipmaps).</summary>
    public static byte[] EncodeDds(byte[] rgba, int w, int h, string format, bool mips = true)
    {
        var enc = new BcEncoder();
        enc.OutputOptions.Format = MapFormat(format);
        enc.OutputOptions.Quality = CompressionQuality.Balanced;
        enc.OutputOptions.GenerateMipMaps = mips;
        enc.OutputOptions.FileFormat = OutputFileFormat.Dds;
        var dds = enc.EncodeToDds(rgba.AsSpan(), w, h, PixelFormat.Rgba32);
        using var ms = new MemoryStream();
        dds.Write(ms);
        return ms.ToArray();
    }

    private static CompressionFormat MapFormat(string f) => f.ToUpperInvariant() switch
    {
        "DXT1" or "BC1" => CompressionFormat.Bc1,
        "DXT3" or "BC2" => CompressionFormat.Bc2,
        "DXT5" or "BC3" => CompressionFormat.Bc3,
        "BC4" => CompressionFormat.Bc4,
        "BC5" => CompressionFormat.Bc5,
        "BC7" => CompressionFormat.Bc7,
        "RGBA" or "UNCOMPRESSED" => CompressionFormat.Rgba,
        _ => CompressionFormat.Bc3,
    };

    private static byte[] ToBytes(ColorRgba32[] px)
    {
        var b = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            b[i * 4] = px[i].r; b[i * 4 + 1] = px[i].g; b[i * 4 + 2] = px[i].b; b[i * 4 + 3] = px[i].a;
        }
        return b;
    }
}
