using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace App.Core;

// A small, READ-ONLY Scaleform GFx (.gfx) parser. GFx is an extended SWF: the same
// container + tag stream as Flash, with a few Scaleform-specific tags (1000-1009,
// mostly external image/font references). This walks the tag list so the UI can
// show the file's shapes, sprites, fonts, images, text, referenced textures, etc.
// Tag codes follow the SWF spec; the GFx extensions match JPEXS (ffdec).

public sealed class GfxTag
{
    public int Code;
    public string Name = "";
    public string Category = "Other";
    public int? Id;
    public string Detail = "";
}

public sealed class GfxImageRef { public int Id; public string File = ""; public int Width; public int Height; }
public sealed class GfxSymbol { public int Id; public string Name = ""; }

public sealed class GfxInfo
{
    public bool Ok;
    public string? Error;
    public string Signature = "";
    public string Compression = "none";
    public int Version;
    public long FileLength;
    public double Width, Height;
    public double FrameRate;
    public int FrameCount;
    public int TagCount;
    public List<GfxTag> Tags = new();
    public List<GfxImageRef> Images = new();   // external images (the referenced .dds/textures)
    public List<GfxSymbol> Symbols = new();     // named symbols (ExportAssets / SymbolClass)
    public Dictionary<string, int> Counts = new();
}

public static class GfxParser
{
    public static GfxInfo Parse(byte[] data)
    {
        var info = new GfxInfo();
        try
        {
            if (data.Length < 8) { info.Error = "file too small"; return info; }
            char c0 = (char)data[0], c1 = (char)data[1], c2 = (char)data[2];
            info.Signature = $"{c0}{c1}{c2}";
            info.Version = data[3];
            info.FileLength = BitConverter.ToUInt32(data, 4);

            bool isGfx = c1 == 'F' && c2 == 'X';   // GFX / CFX (Scaleform)
            bool isSwf = c1 == 'W' && c2 == 'S';   // FWS / CWS / ZWS (Flash)
            if (!isGfx && !isSwf) { info.Error = "not an SWF/GFX file"; return info; }

            byte[] body;   // everything after the 8-byte header, decompressed
            if (c0 == 'C')
            {
                info.Compression = "zlib";
                using var ms = new MemoryStream(data, 8, data.Length - 8);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                body = outMs.ToArray();
            }
            else if (c0 == 'Z') { info.Compression = "lzma"; info.Error = "LZMA-compressed GFX isn't supported"; return info; }
            else { info.Compression = "none"; body = data[8..]; }

            var rd = new Reader(body);
            rd.AlignByte();
            int nbits = (int)rd.ReadUB(5);
            long xmin = rd.ReadSB(nbits), xmax = rd.ReadSB(nbits), ymin = rd.ReadSB(nbits), ymax = rd.ReadSB(nbits);
            rd.AlignByte();
            info.Width = (xmax - xmin) / 20.0;
            info.Height = (ymax - ymin) / 20.0;
            info.FrameRate = rd.ReadUI16() / 256.0;
            info.FrameCount = rd.ReadUI16();

            while (rd.Remaining >= 2)
            {
                int rh = rd.ReadUI16();
                int code = rh >> 6;
                long len = rh & 0x3F;
                if (len == 0x3F) len = rd.ReadUI32();
                long end = Math.Min(rd.Pos + len, rd.Length);

                var tag = new GfxTag { Code = code, Name = TagName(code), Category = TagCategory(code) };
                try { Detail(rd, code, end, tag, info); } catch { }
                rd.Pos = end;                          // robust: always resume at the tag boundary
                info.Tags.Add(tag);
                Bump(info.Counts, tag.Category);
                if (code == 0) break;                  // End tag
            }
            info.TagCount = info.Tags.Count;
            info.Ok = true;
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    private static void Detail(Reader rd, int code, long end, GfxTag tag, GfxInfo info)
    {
        switch (code)
        {
            case 2: case 22: case 32: case 83:                 // DefineShape 1-4
                tag.Id = rd.ReadUI16();
                tag.Detail = ReadRectSize(rd);
                break;
            case 46: case 84:                                  // DefineMorphShape 1-2
                tag.Id = rd.ReadUI16();
                break;
            case 39:                                           // DefineSprite
            {
                tag.Id = rd.ReadUI16();
                int frames = rd.ReadUI16();
                int nested = 0;
                while (rd.Pos < end && rd.Remaining >= 2)
                {
                    int rh = rd.ReadUI16(); int c = rh >> 6; long l = rh & 0x3F;
                    if (l == 0x3F) l = rd.ReadUI32();
                    rd.Pos = Math.Min(rd.Pos + l, end);
                    nested++;
                    if (c == 0) break;
                }
                tag.Detail = $"{frames} frame{(frames == 1 ? "" : "s")}, {nested} tags";
                break;
            }
            case 20: case 36:                                  // DefineBitsLossless 1-2
            {
                tag.Id = rd.ReadUI16();
                int fmt = rd.ReadUI8(); int w = rd.ReadUI16(); int h = rd.ReadUI16();
                tag.Detail = $"{w}×{h} (lossless fmt {fmt})";
                break;
            }
            case 6: case 21: case 35: case 90:                 // DefineBits / JPEG 2-4
                tag.Id = rd.ReadUI16(); tag.Detail = "JPEG";
                break;
            case 10: case 48: case 75: case 1005:              // fonts (+ GFX compacted font)
            case 88: case 91: case 73:                         // font name / glyph names / align zones
                tag.Id = rd.ReadUI16();
                break;
            case 11: case 33:                                  // DefineText 1-2
                tag.Id = rd.ReadUI16(); tag.Detail = ReadRectSize(rd);
                break;
            case 37:                                           // DefineEditText
                tag.Id = rd.ReadUI16();
                break;
            case 7: case 34:                                   // DefineButton 1-2
                tag.Id = rd.ReadUI16();
                break;
            case 14:                                           // DefineSound
                tag.Id = rd.ReadUI16();
                break;
            case 43:                                           // FrameLabel
                tag.Detail = rd.ReadString();
                break;
            case 56: case 76:                                  // ExportAssets / SymbolClass
            {
                int count = rd.ReadUI16();
                for (int i = 0; i < count && rd.Pos < end; i++)
                {
                    int tid = rd.ReadUI16();
                    string nm = rd.ReadString();
                    info.Symbols.Add(new GfxSymbol { Id = tid, Name = nm });
                }
                tag.Detail = $"{count} symbol{(count == 1 ? "" : "s")}";
                break;
            }
            case 1000:                                         // GFX ExporterInfo
                tag.Detail = $"Scaleform export v0x{rd.ReadUI16():X}";
                break;
            case 1001:                                         // GFX DefineExternalImage
            {
                int id = rd.ReadUI16(); tag.Id = id;
                rd.ReadUI16();                                 // bitmap format
                int w = rd.ReadUI16(), h = rd.ReadUI16();
                string export = rd.ReadNetString();
                string file = rd.Pos < end ? rd.ReadNetString() : export;
                info.Images.Add(new GfxImageRef { Id = id, File = file, Width = w, Height = h });
                tag.Detail = $"{w}×{h} → {file}";
                break;
            }
            case 1009:                                         // GFX DefineExternalImage2
            {
                int id = rd.ReadUI16(); tag.Id = id;
                rd.ReadUI16();                                 // idType
                rd.ReadUI16();                                 // bitmap format
                int w = rd.ReadUI16(), h = rd.ReadUI16();
                rd.ReadNetString();                            // export name
                string file = rd.ReadNetString();
                info.Images.Add(new GfxImageRef { Id = id, File = file, Width = w, Height = h });
                tag.Detail = $"{w}×{h} → {file}";
                break;
            }
            case 1008:                                         // GFX DefineSubImage
                tag.Id = rd.ReadUI16();
                break;
        }
    }

    private static string ReadRectSize(Reader rd)
    {
        rd.AlignByte();
        int nb = (int)rd.ReadUB(5);
        long x0 = rd.ReadSB(nb), x1 = rd.ReadSB(nb), y0 = rd.ReadSB(nb), y1 = rd.ReadSB(nb);
        return $"{(x1 - x0) / 20.0:0.#}×{(y1 - y0) / 20.0:0.#}";
    }

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;

    private static string TagCategory(int code) => code switch
    {
        2 or 22 or 32 or 83 or 46 or 84 => "Shape",
        39 => "Sprite",
        10 or 48 or 75 or 73 or 88 or 91 or 1005 or 1002 => "Font",
        6 or 20 or 21 or 35 or 36 or 90 or 1001 or 1003 or 1004 or 1008 or 1009 => "Image",
        11 or 33 or 37 => "Text",
        7 or 34 => "Button",
        14 or 15 or 18 or 19 or 1006 or 1007 => "Sound",
        0 or 1 or 4 or 5 or 9 or 24 or 26 or 28 or 43 or 69 or 70 or 86 => "Control",
        56 or 76 or 1000 => "Meta",
        _ => "Other",
    };

    private static string TagName(int code) => code switch
    {
        0 => "End", 1 => "ShowFrame", 2 => "DefineShape", 4 => "PlaceObject",
        5 => "RemoveObject", 6 => "DefineBits", 7 => "DefineButton", 8 => "JPEGTables",
        9 => "SetBackgroundColor", 10 => "DefineFont", 11 => "DefineText", 12 => "DoAction",
        13 => "DefineFontInfo", 14 => "DefineSound", 15 => "StartSound", 18 => "SoundStreamHead",
        19 => "SoundStreamBlock", 20 => "DefineBitsLossless", 21 => "DefineBitsJPEG2",
        22 => "DefineShape2", 24 => "Protect", 26 => "PlaceObject2", 28 => "RemoveObject2",
        32 => "DefineShape3", 33 => "DefineText2", 34 => "DefineButton2", 35 => "DefineBitsJPEG3",
        36 => "DefineBitsLossless2", 37 => "DefineEditText", 39 => "DefineSprite",
        43 => "FrameLabel", 45 => "SoundStreamHead2", 46 => "DefineMorphShape",
        48 => "DefineFont2", 56 => "ExportAssets", 59 => "DoInitAction", 69 => "FileAttributes",
        70 => "PlaceObject3", 73 => "DefineFontAlignZones", 75 => "DefineFont3",
        76 => "SymbolClass", 83 => "DefineShape4", 84 => "DefineMorphShape2",
        86 => "DefineSceneAndFrameLabelData", 88 => "DefineFontName", 90 => "DefineBitsJPEG4",
        91 => "DefineFont4",
        1000 => "GFx ExporterInfo", 1001 => "GFx DefineExternalImage", 1002 => "GFx FontTextureInfo",
        1003 => "GFx DefineExternalGradient", 1004 => "GFx DefineGradientMap",
        1005 => "GFx DefineCompactedFont", 1006 => "GFx DefineExternalSound",
        1007 => "GFx DefineExternalStreamSound", 1008 => "GFx DefineSubImage",
        1009 => "GFx DefineExternalImage2",
        _ => $"Tag {code}",
    };

    // Bit/byte reader over the SWF body. Tags are byte-aligned; RECT/bit fields use MSB-first bits.
    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _bytePos;
        private int _bitPos;
        public Reader(byte[] d) { _d = d; }
        public long Length => _d.Length;
        public long Pos { get => _bytePos; set { _bytePos = (int)value; _bitPos = 0; } }
        public long Remaining => _d.Length - _bytePos;
        public void AlignByte() { if (_bitPos != 0) { _bitPos = 0; _bytePos++; } }
        public int ReadUI8() => _d[_bytePos++];
        public int ReadUI16() { int v = _d[_bytePos] | (_d[_bytePos + 1] << 8); _bytePos += 2; return v; }
        public long ReadUI32() { long v = (uint)(_d[_bytePos] | (_d[_bytePos + 1] << 8) | (_d[_bytePos + 2] << 16) | (_d[_bytePos + 3] << 24)); _bytePos += 4; return v; }
        public long ReadUB(int n) { long v = 0; for (int i = 0; i < n; i++) v = (v << 1) | (uint)ReadBit(); return v; }
        public long ReadSB(int n) { if (n == 0) return 0; long v = ReadUB(n); long sign = 1L << (n - 1); return (v & sign) != 0 ? v - (1L << n) : v; }
        private int ReadBit() { int b = _d[_bytePos]; int bit = (b >> (7 - _bitPos)) & 1; if (++_bitPos == 8) { _bitPos = 0; _bytePos++; } return bit; }
        public string ReadString() { var sb = new StringBuilder(); while (_bytePos < _d.Length) { byte b = _d[_bytePos++]; if (b == 0) break; sb.Append((char)b); } return sb.ToString(); }
        public string ReadNetString() { if (_bytePos >= _d.Length) return ""; int len = _d[_bytePos++]; if (_bytePos + len > _d.Length) len = _d.Length - _bytePos; var s = Encoding.UTF8.GetString(_d, _bytePos, len); _bytePos += len; return s; }
    }
}
