using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace App.Core;

// Turns a Scaleform GFx / SWF file into a render-ready "scene": vector shapes
// (fills + strokes as path commands), sprite/main timelines (display lists with
// matrices), and image references. The UI replays this on an HTML5 canvas. This is
// the visual counterpart to GfxParser's structural view. Read-only.

public sealed class GfxFillDto
{
    public string Type = "solid";        // solid | linear | radial | bitmap
    public string? Color;                // solid -> css rgba()
    public double[]? Matrix;             // gradient/bitmap -> [a,b,c,d,e,f] in px
    public List<object>? Stops;          // gradient -> [{pos, color}]
    public int Image = -1;               // bitmap -> image character id
    public bool Repeat;                  // bitmap tiling
    public List<double> Path = new();    // flat: 0,x,y(move) 1,x,y(line) 2,cx,cy,x,y(quad)  (px)
}
public sealed class GfxStrokeDto
{
    public double Width;                 // px
    public string? Color;                // css rgba()
    public List<double> Path = new();
}
public sealed class GfxShapeDto
{
    public double[] Bounds = new double[4];   // x,y,w,h (px)
    public List<GfxFillDto> Fills = new();
    public List<GfxStrokeDto> Strokes = new();
}
public sealed class GfxPlaceDto
{
    public int Depth;
    public int Char = -1;
    public double[] Matrix = { 1, 0, 0, 1, 0, 0 };
    public double Alpha = 1;
    // Colour transform (CXFORM): out = in * Mul + Add, per channel (RGBA). Scaleform
    // tints white "neutral" shapes (e.g. the green health / blue armour bars) via this,
    // so without it those bars render white. Null = identity (no tint).
    public double[]? CMul;   // [r,g,b,a] multipliers, 1 = identity
    public double[]? CAdd;   // [r,g,b,a] additive, 0..255 scale, 0 = identity
    public int ClipDepth;
    public string? Name;
}
public sealed class GfxSpriteDto
{
    public int FrameCount;
    public List<List<GfxPlaceDto>> Frames = new();   // each frame = display list snapshot (sorted by depth)
}
public sealed class GfxImageEntryDto
{
    public int Id;
    public string Kind = "";            // "external" | "lossless" | "jpeg"
    public string? File;                // external filename
    public int W, H;
    public byte[]? Bytes;               // embedded payload (jpeg/lossless) for the bridge to decode
    public int LosslessFormat;
}
public sealed class GfxSceneDto
{
    public bool Ok;
    public string? Error;
    public double Width, Height, FrameRate;
    public int FrameCount;
    public Dictionary<int, GfxShapeDto> Shapes = new();
    public Dictionary<int, GfxSpriteDto> Sprites = new();
    public GfxSpriteDto Main = new();
    public List<GfxImageEntryDto> Images = new();
    public List<GfxSymbol> Symbols = new();
}

public static class GfxSceneParser
{
    public static GfxSceneDto Parse(byte[] data)
    {
        var scene = new GfxSceneDto();
        try
        {
            if (data.Length < 8) { scene.Error = "file too small"; return scene; }
            char c0 = (char)data[0], c1 = (char)data[1], c2 = (char)data[2];
            bool isGfx = c1 == 'F' && c2 == 'X', isSwf = c1 == 'W' && c2 == 'S';
            if (!isGfx && !isSwf) { scene.Error = "not an SWF/GFX file"; return scene; }
            int version = data[3];

            byte[] body;
            if (c0 == 'C')
            {
                using var ms = new MemoryStream(data, 8, data.Length - 8);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs); body = outMs.ToArray();
            }
            else if (c0 == 'Z') { scene.Error = "LZMA-compressed GFX isn't supported"; return scene; }
            else body = data[8..];

            var rd = new R(body);
            rd.AlignByte();
            int nb = (int)rd.UB(5);
            long xmin = rd.SB(nb), xmax = rd.SB(nb), ymin = rd.SB(nb), ymax = rd.SB(nb);
            rd.AlignByte();
            scene.Width = (xmax - xmin) / 20.0;
            scene.Height = (ymax - ymin) / 20.0;
            scene.FrameRate = rd.U16() / 256.0;
            scene.FrameCount = rd.U16();

            BuildTimeline(rd, body.Length, scene, scene.Main, version);
            scene.Ok = true;
        }
        catch (Exception ex) { scene.Error = ex.Message; }
        return scene;
    }

    // Walk a tag stream (top-level or inside a DefineSprite), filling the dictionary
    // and the timeline's frames.
    private static void BuildTimeline(R rd, long end, GfxSceneDto scene, GfxSpriteDto timeline, int version)
    {
        var display = new Dictionary<int, GfxPlaceDto>();
        while (rd.Pos < end && rd.Remaining >= 2)
        {
            int rh = rd.U16();
            int code = rh >> 6;
            long len = rh & 0x3F;
            if (len == 0x3F) len = rd.U32();
            long tagEnd = Math.Min(rd.Pos + len, rd.Length);

            try
            {
                switch (code)
                {
                    case 2: case 22: case 32: case 83:
                        ParseShape(rd, scene, code); break;
                    case 39:
                    {
                        int id = rd.U16(); int fc = rd.U16();
                        var sprite = new GfxSpriteDto();
                        BuildTimeline(rd, tagEnd, scene, sprite, version);
                        scene.Sprites[id] = sprite;
                        break;
                    }
                    case 20: case 36:
                    {
                        int id = rd.U16(); int fmt = rd.U8(); int w = rd.U16(); int h = rd.U16();
                        var b = rd.Bytes((int)(tagEnd - rd.Pos));
                        scene.Images.Add(new GfxImageEntryDto { Id = id, Kind = "lossless", W = w, H = h, Bytes = b, LosslessFormat = fmt });
                        break;
                    }
                    case 6: case 21: case 35: case 90:
                    {
                        int id = rd.U16();
                        var b = rd.Bytes((int)(tagEnd - rd.Pos));
                        scene.Images.Add(new GfxImageEntryDto { Id = id, Kind = "jpeg", Bytes = b });
                        break;
                    }
                    case 1001:
                    {
                        int id = rd.U16(); rd.U16(); int w = rd.U16(), h = rd.U16();
                        string export = rd.NetStr();
                        string file = rd.Pos < tagEnd ? rd.NetStr() : export;
                        scene.Images.Add(new GfxImageEntryDto { Id = id, Kind = "external", File = file, W = w, H = h });
                        break;
                    }
                    case 1009:
                    {
                        int id = rd.U16(); rd.U16(); rd.U16(); int w = rd.U16(), h = rd.U16();
                        rd.NetStr(); string file = rd.NetStr();
                        scene.Images.Add(new GfxImageEntryDto { Id = id, Kind = "external", File = file, W = w, H = h });
                        break;
                    }
                    case 26: ParsePlace(rd, tagEnd, display, place3: false); break;
                    case 70: ParsePlace(rd, tagEnd, display, place3: true); break;
                    case 4:  ParsePlace1(rd, tagEnd, display); break;
                    case 5:  { int d = rd.U16(); display.Remove(d); break; }      // RemoveObject (id+depth, but depth is 2nd)
                    case 28: { int d = rd.U16(); display.Remove(d); break; }      // RemoveObject2
                    case 1:  timeline.Frames.Add(Snapshot(display)); break;       // ShowFrame
                    case 56: case 76: CollectNames(rd, tagEnd, scene); break;
                    case 0: rd.Pos = tagEnd; goto done;
                }
            }
            catch { /* skip a malformed tag, keep going */ }
            rd.Pos = tagEnd;
        }
    done:
        if (timeline.Frames.Count == 0) timeline.Frames.Add(Snapshot(display));   // a single static frame
        timeline.FrameCount = timeline.Frames.Count;
    }

    private static List<GfxPlaceDto> Snapshot(Dictionary<int, GfxPlaceDto> display)
    {
        var list = new List<GfxPlaceDto>(display.Count);
        foreach (var p in display.Values)
            list.Add(new GfxPlaceDto { Depth = p.Depth, Char = p.Char, Matrix = (double[])p.Matrix.Clone(), Alpha = p.Alpha, CMul = p.CMul, CAdd = p.CAdd, ClipDepth = p.ClipDepth, Name = p.Name });
        list.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        return list;
    }

    private static void CollectNames(R rd, long end, GfxSceneDto scene)
    {
        int count = rd.U16();
        for (int i = 0; i < count && rd.Pos < end; i++)
        {
            int id = rd.U16();
            string nm = rd.Str();
            scene.Symbols.Add(new GfxSymbol { Id = id, Name = nm });
        }
    }

    private static void ParsePlace1(R rd, long end, Dictionary<int, GfxPlaceDto> display)
    {
        int ch = rd.U16(); int depth = rd.U16();
        var p = new GfxPlaceDto { Depth = depth, Char = ch, Matrix = rd.Matrix() };
        if (rd.Pos < end) rd.CxformInto(p, false);
        display[depth] = p;
    }

    private static void ParsePlace(R rd, long end, Dictionary<int, GfxPlaceDto> display, bool place3)
    {
        int flags = rd.U8();
        bool hasMove = (flags & 1) != 0, hasChar = (flags & 2) != 0, hasMatrix = (flags & 4) != 0,
             hasCx = (flags & 8) != 0, hasRatio = (flags & 16) != 0, hasName = (flags & 32) != 0,
             hasClip = (flags & 64) != 0;
        int flags2 = place3 ? rd.U8() : 0;
        bool hasClassName = (flags2 & 0x08) != 0, hasImage = (flags2 & 0x10) != 0;
        int depth = rd.U16();
        if (hasClassName || (hasImage && hasChar)) rd.Str();
        var p = (hasMove && display.TryGetValue(depth, out var prev))
            ? new GfxPlaceDto { Depth = depth, Char = prev.Char, Matrix = (double[])prev.Matrix.Clone(), Alpha = prev.Alpha, CMul = prev.CMul, CAdd = prev.CAdd, ClipDepth = prev.ClipDepth, Name = prev.Name }
            : new GfxPlaceDto { Depth = depth };
        if (hasChar) p.Char = rd.U16();
        if (hasMatrix) p.Matrix = rd.Matrix();
        if (hasCx) rd.CxformInto(p, true);
        if (hasRatio) rd.U16();
        if (hasName) p.Name = rd.Str();
        if (hasClip) p.ClipDepth = rd.U16();
        display[depth] = p;   // remaining (filters/blend/clip actions) skipped via tagEnd
    }

    // ---- shape parsing --------------------------------------------------------
    private struct Edge { public long X0, Y0, CX, CY, X1, Y1; public bool Curve; }

    private static void ParseShape(R rd, GfxSceneDto scene, int code)
    {
        int id = rd.U16();
        bool alpha = code == 32 || code == 83;          // shape3/4 use RGBA
        bool shape4 = code == 83;
        var shape = new GfxShapeDto();
        rd.AlignByte();
        var b = rd.Rect();                              // SWF RECT = [Xmin, Xmax, Ymin, Ymax] (twips)
        shape.Bounds = new[] { b[0] / 20.0, b[2] / 20.0, (b[1] - b[0]) / 20.0, (b[3] - b[2]) / 20.0 };  // -> x, y, w, h
        if (shape4) { rd.Rect(); rd.U8(); }            // edge bounds + flags

        var fills = ReadFillStyles(rd, alpha);
        var lines = ReadLineStyles(rd, alpha, shape4);
        int nFill = (int)rd.UB(4), nLine = (int)rd.UB(4);

        // accumulators per current style arrays
        var fillEdges = new Dictionary<int, List<Edge>>();
        var lineEdges = new Dictionary<int, List<Edge>>();
        long x = 0, y = 0; int fs0 = 0, fs1 = 0, ls = 0;

        void AddFill(int idx, Edge e) { if (idx <= 0) return; if (!fillEdges.TryGetValue(idx, out var l)) fillEdges[idx] = l = new(); l.Add(e); }
        void AddLine(int idx, Edge e) { if (idx <= 0) return; if (!lineEdges.TryGetValue(idx, out var l)) lineEdges[idx] = l = new(); l.Add(e); }
        void Flush()
        {
            foreach (var kv in fillEdges)
            {
                if (kv.Key > fills.Count) continue;
                var f = fills[kv.Key - 1];
                f.Path = Connect(kv.Value);
                if (f.Path.Count > 0) shape.Fills.Add(f.Clone());
            }
            foreach (var kv in lineEdges)
            {
                if (kv.Key > lines.Count) continue;
                var s = lines[kv.Key - 1];
                var st = new GfxStrokeDto { Width = s.Width, Color = s.Color, Path = ConnectOpen(kv.Value) };
                if (st.Path.Count > 0) shape.Strokes.Add(st);
            }
            fillEdges.Clear(); lineEdges.Clear();
        }

        while (true)
        {
            int type = (int)rd.UB(1);
            if (type == 0)
            {
                int flags = (int)rd.UB(5);
                if (flags == 0) break;                  // end of shape
                bool newStyles = (flags & 16) != 0, sLine = (flags & 8) != 0, sf1 = (flags & 4) != 0, sf0 = (flags & 2) != 0, move = (flags & 1) != 0;
                if (move) { int mb = (int)rd.UB(5); x = rd.SB2(mb); y = rd.SB2(mb); }
                if (sf0) fs0 = (int)rd.UB(nFill);
                if (sf1) fs1 = (int)rd.UB(nFill);
                if (sLine) ls = (int)rd.UB(nLine);
                if (newStyles)
                {
                    Flush();
                    fills = ReadFillStyles(rd, alpha);
                    lines = ReadLineStyles(rd, alpha, shape4);
                    nFill = (int)rd.UB(4); nLine = (int)rd.UB(4);
                    fs0 = fs1 = ls = 0;
                }
            }
            else
            {
                bool straight = rd.UB(1) != 0;
                int numBits = (int)rd.UB(4) + 2;
                long nx, ny, cx = 0, cy = 0; bool curve = false;
                if (straight)
                {
                    if (rd.UB(1) != 0) { nx = x + rd.SB2(numBits); ny = y + rd.SB2(numBits); }      // general
                    else if (rd.UB(1) != 0) { nx = x; ny = y + rd.SB2(numBits); }                   // vertical
                    else { nx = x + rd.SB2(numBits); ny = y; }                                       // horizontal
                }
                else
                {
                    cx = x + rd.SB2(numBits); cy = y + rd.SB2(numBits);
                    nx = cx + rd.SB2(numBits); ny = cy + rd.SB2(numBits);
                    curve = true;
                }
                var e = new Edge { X0 = x, Y0 = y, CX = cx, CY = cy, X1 = nx, Y1 = ny, Curve = curve };
                AddFill(fs1, e);
                AddFill(fs0, new Edge { X0 = nx, Y0 = ny, CX = cx, CY = cy, X1 = x, Y1 = y, Curve = curve });   // fill0 = reverse
                AddLine(ls, e);
                x = nx; y = ny;
            }
        }
        Flush();
        scene.Shapes[id] = shape;
    }

    // Chain edges into closed subpaths (exact integer-twip endpoint matching), output px path cmds.
    private static List<double> Connect(List<Edge> edges)
    {
        var path = new List<double>();
        var byStart = new Dictionary<long, List<int>>();
        long Key(long px, long py) => (px & 0xFFFFFFFFL) << 32 | (py & 0xFFFFFFFFL);
        for (int i = 0; i < edges.Count; i++)
        {
            long k = Key(edges[i].X0, edges[i].Y0);
            if (!byStart.TryGetValue(k, out var l)) byStart[k] = l = new();
            l.Add(i);
        }
        var used = new bool[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            if (used[i]) continue;
            var e = edges[i]; used[i] = true;
            long sx = e.X0, sy = e.Y0;
            path.Add(0); path.Add(sx / 20.0); path.Add(sy / 20.0);
            Emit(path, e);
            long cx = e.X1, cy = e.Y1;
            for (int guard = 0; guard < edges.Count; guard++)
            {
                int next = -1;
                if (byStart.TryGetValue(Key(cx, cy), out var cand))
                    foreach (var ci in cand) if (!used[ci]) { next = ci; break; }
                if (next < 0) break;
                used[next] = true; var ne = edges[next]; Emit(path, ne); cx = ne.X1; cy = ne.Y1;
                if (cx == sx && cy == sy) break;
            }
        }
        return path;
    }

    // Strokes: keep direction, just chain visually (open paths ok).
    private static List<double> ConnectOpen(List<Edge> edges)
    {
        var path = new List<double>();
        long cx = long.MinValue, cy = long.MinValue;
        foreach (var e in edges)
        {
            if (e.X0 != cx || e.Y0 != cy) { path.Add(0); path.Add(e.X0 / 20.0); path.Add(e.Y0 / 20.0); }
            Emit(path, e); cx = e.X1; cy = e.Y1;
        }
        return path;
    }

    private static void Emit(List<double> path, Edge e)
    {
        if (e.Curve) { path.Add(2); path.Add(e.CX / 20.0); path.Add(e.CY / 20.0); path.Add(e.X1 / 20.0); path.Add(e.Y1 / 20.0); }
        else { path.Add(1); path.Add(e.X1 / 20.0); path.Add(e.Y1 / 20.0); }
    }

    private static List<GfxFillDto> ReadFillStyles(R rd, bool alpha)
    {
        var list = new List<GfxFillDto>();
        int count = rd.U8(); if (count == 0xFF) count = rd.U16();
        for (int i = 0; i < count; i++)
        {
            int type = rd.U8();
            var f = new GfxFillDto();
            if (type == 0x00) { f.Type = "solid"; f.Color = rd.ColorCss(alpha); }
            else if (type is 0x10 or 0x12 or 0x13)
            {
                f.Type = type == 0x10 ? "linear" : "radial";
                f.Matrix = rd.MatrixGrad();
                rd.AlignByte();
                rd.UB(2); rd.UB(2);                       // spread, interpolation
                int n = (int)rd.UB(4);
                f.Stops = new List<object>();
                for (int g = 0; g < n; g++) { int pos = rd.U8(); string col = rd.ColorCss(alpha); f.Stops.Add(new { pos = pos / 255.0, color = col }); }
                if (type == 0x13) rd.U16();               // focal point
            }
            else if (type is 0x40 or 0x41 or 0x42 or 0x43)
            {
                f.Type = "bitmap";
                f.Image = rd.U16();
                f.Matrix = rd.MatrixGrad();
                f.Repeat = type is 0x40 or 0x42;
            }
            else { f.Type = "solid"; f.Color = "rgba(128,128,128,1)"; }
            list.Add(f);
        }
        return list;
    }

    private static List<GfxStrokeDto> ReadLineStyles(R rd, bool alpha, bool shape4)
    {
        var list = new List<GfxStrokeDto>();
        int count = rd.U8(); if (count == 0xFF) count = rd.U16();
        for (int i = 0; i < count; i++)
        {
            var s = new GfxStrokeDto();
            s.Width = rd.U16() / 20.0;
            if (shape4)
            {
                int fl = rd.U16();
                int join = (fl >> 4) & 3; bool hasFill = (fl & 8) != 0;
                if (join == 2) rd.U16();                  // miter limit
                if (hasFill) { int t = rd.U8(); if (t == 0) s.Color = rd.ColorCss(true); else { rd.U16(); rd.MatrixGrad(); s.Color = "rgba(160,160,160,1)"; } }
                else s.Color = rd.ColorCss(true);
            }
            else s.Color = rd.ColorCss(alpha);
            list.Add(s);
        }
        return list;
    }

    // bit/byte reader (MSB-first bits; LE multibyte)
    private sealed class R
    {
        private readonly byte[] _d; private int _p, _b;
        public R(byte[] d) { _d = d; }
        public long Length => _d.Length;
        public long Pos { get => _p; set { _p = (int)value; _b = 0; } }
        public long Remaining => _d.Length - _p;
        public void AlignByte() { if (_b != 0) { _b = 0; _p++; } }
        public int U8() => _d[_p++];
        public int U16() { int v = _d[_p] | (_d[_p + 1] << 8); _p += 2; return v; }
        public long U32() { long v = (uint)(_d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16) | (_d[_p + 3] << 24)); _p += 4; return v; }
        public byte[] Bytes(int n) { if (n < 0) n = 0; if (_p + n > _d.Length) n = _d.Length - _p; var r = new byte[n]; Array.Copy(_d, _p, r, 0, n); _p += n; return r; }
        public long UB(int n) { long v = 0; for (int i = 0; i < n; i++) { int bit = (_d[_p] >> (7 - _b)) & 1; if (++_b == 8) { _b = 0; _p++; } v = (v << 1) | (uint)bit; } return v; }
        public long SB2(int n) { if (n == 0) return 0; long v = UB(n); long sign = 1L << (n - 1); return (v & sign) != 0 ? v - (1L << n) : v; }
        public long SB(int n) => SB2(n);
        public long[] Rect() { AlignByte(); int nb = (int)UB(5); long a = SB2(nb), b = SB2(nb), c = SB2(nb), d = SB2(nb); AlignByte(); return new[] { a, b, c, d }; }
        public string Str() { var sb = new System.Text.StringBuilder(); while (_p < _d.Length) { byte ch = _d[_p++]; if (ch == 0) break; sb.Append((char)ch); } return sb.ToString(); }
        public string NetStr() { if (_p >= _d.Length) return ""; int len = _d[_p++]; if (_p + len > _d.Length) len = _d.Length - _p; var s = System.Text.Encoding.UTF8.GetString(_d, _p, len); _p += len; return s; }
        public string ColorCss(bool alpha) { int r = U8(), g = U8(), b = U8(), a = alpha ? U8() : 255; return $"rgba({r},{g},{b},{(a / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})"; }

        // MATRIX -> [a,b,c,d,e(px),f(px)]
        public double[] Matrix()
        {
            AlignByte();
            double a = 1, b = 0, c = 0, d = 1;
            if (UB(1) != 0) { int n = (int)UB(5); a = SB2(n) / 65536.0; d = SB2(n) / 65536.0; }
            if (UB(1) != 0) { int n = (int)UB(5); b = SB2(n) / 65536.0; c = SB2(n) / 65536.0; }
            int nt = (int)UB(5); double e = SB2(nt) / 20.0, f = SB2(nt) / 20.0;
            AlignByte();
            return new[] { a, b, c, d, e, f };
        }
        // gradient/bitmap matrix: same but scale also divided by 20 so it maps image px -> shape px
        public double[] MatrixGrad()
        {
            AlignByte();
            double a = 1, b = 0, c = 0, d = 1;
            if (UB(1) != 0) { int n = (int)UB(5); a = SB2(n) / 65536.0; d = SB2(n) / 65536.0; }
            if (UB(1) != 0) { int n = (int)UB(5); b = SB2(n) / 65536.0; c = SB2(n) / 65536.0; }
            int nt = (int)UB(5); double e = SB2(nt) / 20.0, f = SB2(nt) / 20.0;
            AlignByte();
            return new[] { a / 20.0, b / 20.0, c / 20.0, d / 20.0, e, f };
        }
        // CXFORM -> capture the full colour transform onto the placement (mult + add per
        // channel). Alpha is also surfaced as p.Alpha for the renderer's globalAlpha path.
        public void CxformInto(GfxPlaceDto p, bool withAlpha)
        {
            AlignByte();
            bool hasAdd = UB(1) != 0, hasMult = UB(1) != 0;
            int n = (int)UB(4);
            double rm = 1, gm = 1, bm = 1, am = 1, ra = 0, ga = 0, ba = 0, aa = 0;
            if (hasMult) { rm = SB2(n) / 256.0; gm = SB2(n) / 256.0; bm = SB2(n) / 256.0; if (withAlpha) am = SB2(n) / 256.0; }
            if (hasAdd) { ra = SB2(n); ga = SB2(n); ba = SB2(n); if (withAlpha) aa = SB2(n); }
            AlignByte();
            p.CMul = new[] { rm, gm, bm, am };
            p.CAdd = new[] { ra, ga, ba, aa };
            p.Alpha = Math.Clamp(am, 0, 1);
        }
    }
}

internal static class GfxFillExt
{
    public static GfxFillDto Clone(this GfxFillDto f) => new()
    {
        Type = f.Type, Color = f.Color, Matrix = f.Matrix, Stops = f.Stops, Image = f.Image, Repeat = f.Repeat, Path = f.Path,
    };
}
