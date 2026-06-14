using System;
using System.Collections.Generic;
using System.Linq;
using CodeWalker.GameFiles;
using SharpDX;

namespace App.Geometry;

/// <summary>
/// Translates CodeWalker drawables into renderer-ready <see cref="MeshData"/>.
/// Robustness is the whole point: every geometry is decoded behind a try/catch,
/// the vertex layout is read dynamically from the declaration (never assumed),
/// buffers are validated before use, and missing channels degrade rather than
/// crash. A single bad geometry is logged and skipped; the rest still render.
/// </summary>
public static class GeometryDecoder
{
    /// <summary>Decode a whole drawable (all present LODs) into a model.</summary>
    public static ModelData Decode(DrawableBase drawable, string name)
    {
        var model = new ModelData { Name = name };
        if (drawable == null) return model;

        try
        {
            model.BoundsMin = ToVec(drawable.BoundingBoxMin);
            model.BoundsMax = ToVec(drawable.BoundingBoxMax);

            var shaders = drawable.ShaderGroup?.Shaders?.data_items;
            if (shaders != null)
                foreach (var s in shaders)
                    model.Materials.Add(MakeMaterial(s));

            // bones whose skinned vertices form toggleable parts (vehicle extras)
            bool[]? extraBones = null;
            var bones = drawable.Skeleton?.Bones?.Items;
            if (bones != null)
            {
                extraBones = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    extraBones[i] = bones[i]?.Name?.StartsWith("extra_", StringComparison.OrdinalIgnoreCase) == true;
                if (!extraBones.Any(x => x)) extraBones = null;
            }

            var dm = drawable.DrawableModels;
            AddLod(model, "High", dm?.High, extraBones);
            AddLod(model, "Med", dm?.Med, extraBones);
            AddLod(model, "Low", dm?.Low, extraBones);
            AddLod(model, "VLow", dm?.VLow, extraBones);
        }
        catch (Exception ex)
        {
            model.SkippedReasons.Add("drawable: " + ex.Message);
        }

        return model;
    }

    private static void AddLod(ModelData model, string level, DrawableModel[]? models, bool[]? extraBones = null)
    {
        if (models == null || models.Length == 0) return;
        var lod = new LodData { Level = level };

        foreach (var m in models)
        {
            var geoms = m?.Geometries;
            if (geoms == null) continue;
            foreach (var g in geoms)
            {
                model.GeometryCount++;
                var r = DecodeGeometry(g);
                if (r.Mesh != null)
                {
                    r.Mesh.BoneIndex = m!.BoneIndex;
                    r.Mesh.HasSkin = m.HasSkin != 0;
                    // A skinned geometry can MIX body and extra_* vertices (geometries
                    // split by material, not by part) — carve the extra regions out
                    // into their own meshes so they can be toggled individually.
                    if (r.Mesh.HasSkin && extraBones != null && r.Mesh.BlendIndices != null)
                        foreach (var piece in SplitByExtraBones(r.Mesh, extraBones)) { lod.Meshes.Add(piece); }
                    else lod.Meshes.Add(r.Mesh);
                    model.RenderedCount++;
                }
                else
                {
                    model.SkippedReasons.Add($"{level}: {r.SkipReason}");
                }
            }
        }

        if (lod.Meshes.Count > 0) model.Lods.Add(lod);
    }

    // Split a skinned mesh into [rest] + [one mesh per extra_* bone]: a triangle whose
    // three vertices are all dominated by the same extra bone moves into that bone's
    // mesh (DominantBone set so the part is identified and toggleable downstream).
    private static IEnumerable<MeshData> SplitByExtraBones(MeshData mesh, bool[] extraBones)
    {
        int count = mesh.VertexCount;
        var bw = mesh.BlendWeights; var bi = mesh.BlendIndices!;
        var vdom = new int[count];
        bool any = false;
        for (int v = 0; v < count; v++)
        {
            int idx = bi[v * 4];
            if (bw != null)
            {
                float best = bw[v * 4];
                for (int k = 1; k < 4; k++)
                    if (bw[v * 4 + k] > best) { best = bw[v * 4 + k]; idx = bi[v * 4 + k]; }
            }
            vdom[v] = idx;
            if (idx < extraBones.Length && extraBones[idx]) any = true;
        }
        if (!any) { yield return mesh; yield break; }

        // assign each triangle to an extra bone (all 3 verts dominated by it) or -1
        var idxs = mesh.Indices;
        var triOwner = new int[idxs.Length / 3];
        var owners = new HashSet<int>();
        for (int t = 0; t < triOwner.Length; t++)
        {
            int a = vdom[idxs[t * 3]], b = vdom[idxs[t * 3 + 1]], c = vdom[idxs[t * 3 + 2]];
            triOwner[t] = (a == b && b == c && a < extraBones.Length && extraBones[a]) ? a : -1;
            if (triOwner[t] >= 0) owners.Add(triOwner[t]);
        }
        if (owners.Count == 0) { yield return mesh; yield break; }

        foreach (int owner in owners.Append(-1))
        {
            var sub = ExtractSubMesh(mesh, idxs, triOwner, owner);
            if (sub != null) { sub.DominantBone = owner; yield return sub; }
        }
    }

    private static MeshData? ExtractSubMesh(MeshData src, uint[] idxs, int[] triOwner, int owner)
    {
        var newIdx = new List<uint>();
        var map = new Dictionary<uint, uint>();
        var order = new List<uint>();
        for (int t = 0; t < triOwner.Length; t++)
        {
            if (triOwner[t] != owner) continue;
            for (int k = 0; k < 3; k++)
            {
                uint ov = idxs[t * 3 + k];
                if (!map.TryGetValue(ov, out uint nv)) { nv = (uint)order.Count; map[ov] = nv; order.Add(ov); }
                newIdx.Add(nv);
            }
        }
        if (newIdx.Count == 0) return null;

        int n = order.Count;
        float[]? Slice(float[]? a, int stride)
        {
            if (a == null) return null;
            var o = new float[n * stride];
            for (int i = 0; i < n; i++)
                Array.Copy(a, order[i] * stride, o, i * stride, stride);
            return o;
        }
        byte[]? SliceB(byte[]? a, int stride)
        {
            if (a == null) return null;
            var o = new byte[n * stride];
            for (int i = 0; i < n; i++)
                Array.Copy(a, order[i] * stride, o, i * stride, stride);
            return o;
        }

        return new MeshData
        {
            VertexCount = n,
            Positions = Slice(src.Positions, 3)!,
            Normals = Slice(src.Normals, 3),
            TexCoords0 = Slice(src.TexCoords0, 2),
            Colors0 = Slice(src.Colors0, 4),
            BlendWeights = Slice(src.BlendWeights, 4),
            BlendIndices = SliceB(src.BlendIndices, 4),
            Indices = newIdx.ToArray(),
            MaterialIndex = src.MaterialIndex,
            BoundsMin = src.BoundsMin,
            BoundsMax = src.BoundsMax,
            BoneIndex = src.BoneIndex,
            HasSkin = src.HasSkin,
        };
    }

    /// <summary>
    /// Decode one geometry. Never throws — failures return a skip reason so the
    /// caller can keep rendering the rest of the model.
    /// </summary>
    public static GeometryResult DecodeGeometry(DrawableGeometry g)
    {
        try
        {
            if (g == null) return GeometryResult.Skip("null geometry");

            var info = g.VertexBuffer?.Info;
            var vdata = g.VertexBuffer?.Data1 ?? g.VertexData;
            byte[]? bytes = vdata?.VertexBytes;
            if (info == null) return GeometryResult.Skip("no vertex declaration");
            if (bytes == null) return GeometryResult.Skip("no vertex bytes");

            int stride = info.Stride;
            if (stride <= 0) return GeometryResult.Skip($"non-positive stride {stride}");

            int count = vdata!.VertexCount;
            if (count <= 0) count = bytes.Length / stride;
            if (count <= 0) return GeometryResult.Skip("zero vertices");

            // Validation: declared vertices must fit in the buffer.
            if ((long)count * stride > bytes.Length)
                return GeometryResult.Skip($"buffer too small: {count}*{stride} > {bytes.Length}");

            var indices = g.IndexBuffer?.Indices;
            if (indices == null || indices.Length == 0)
                return GeometryResult.Skip("no indices");

            // Which channels does this geometry actually carry?
            uint flags = info.Flags;
            int posBit = (int)VertexSemantics.Position;
            if (((flags >> posBit) & 1) == 0)
                return GeometryResult.Skip("no position channel");

            var positions = new float[count * 3];
            float[]? normals = HasChannel(flags, VertexSemantics.Normal) ? new float[count * 3] : null;
            float[]? uv0 = HasChannel(flags, VertexSemantics.TexCoord0) ? new float[count * 2] : null;
            float[]? col0 = HasChannel(flags, VertexSemantics.Colour0) ? new float[count * 4] : null;
            float[]? bw = HasChannel(flags, VertexSemantics.BlendWeights) ? new float[count * 4] : null;
            byte[]? bi = HasChannel(flags, VertexSemantics.BlendIndices) ? new byte[count * 4] : null;

            // Precompute per-channel (offset, type) once, guarding against a
            // malformed declaration whose component would read past the stride.
            var chans = new (int bit, int offset, VertexComponentType type)[16];
            int chanCount = 0;
            for (int i = 0; i < 16; i++)
            {
                if (((flags >> i) & 1) == 0) continue;
                var ct = info.GetComponentType(i);
                int off = info.GetComponentOffset(i);
                int size = VertexComponentTypes.GetSizeInBytes(ct);
                if (off < 0 || off + size > stride) continue; // skip bogus component
                chans[chanCount++] = (i, off, ct);
            }

            Span<float> tmp = stackalloc float[4];
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);

            for (int v = 0; v < count; v++)
            {
                int vbase = v * stride;
                for (int c = 0; c < chanCount; c++)
                {
                    var (bit, off, type) = chans[c];
                    int n = ReadComponent(bytes, vbase + off, type, tmp);
                    if (n == 0) continue;

                    switch ((VertexSemantics)bit)
                    {
                        case VertexSemantics.Position:
                        {
                            float x = tmp[0], y = n > 1 ? tmp[1] : 0, z = n > 2 ? tmp[2] : 0;
                            positions[v * 3 + 0] = x;
                            positions[v * 3 + 1] = y;
                            positions[v * 3 + 2] = z;
                            if (x < bmin.X) bmin.X = x; if (x > bmax.X) bmax.X = x;
                            if (y < bmin.Y) bmin.Y = y; if (y > bmax.Y) bmax.Y = y;
                            if (z < bmin.Z) bmin.Z = z; if (z > bmax.Z) bmax.Z = z;
                            break;
                        }
                        case VertexSemantics.Normal when normals != null:
                            normals[v * 3 + 0] = tmp[0];
                            normals[v * 3 + 1] = n > 1 ? tmp[1] : 0;
                            normals[v * 3 + 2] = n > 2 ? tmp[2] : 0;
                            break;
                        case VertexSemantics.TexCoord0 when uv0 != null:
                            uv0[v * 2 + 0] = tmp[0];
                            uv0[v * 2 + 1] = n > 1 ? tmp[1] : 0;
                            break;
                        case VertexSemantics.Colour0 when col0 != null:
                            col0[v * 4 + 0] = tmp[0];
                            col0[v * 4 + 1] = n > 1 ? tmp[1] : 1;
                            col0[v * 4 + 2] = n > 2 ? tmp[2] : 1;
                            col0[v * 4 + 3] = n > 3 ? tmp[3] : 1;
                            break;
                        case VertexSemantics.BlendWeights when bw != null:
                            bw[v * 4 + 0] = tmp[0];
                            bw[v * 4 + 1] = n > 1 ? tmp[1] : 0;
                            bw[v * 4 + 2] = n > 2 ? tmp[2] : 0;
                            bw[v * 4 + 3] = n > 3 ? tmp[3] : 0;
                            break;
                        case VertexSemantics.BlendIndices when bi != null:
                        {
                            // stored as UByte4 (decoded to 0..1) — recover the raw bone indices
                            bi[v * 4 + 0] = (byte)Math.Clamp(tmp[0] * 255f + 0.5f, 0, 255);
                            bi[v * 4 + 1] = n > 1 ? (byte)Math.Clamp(tmp[1] * 255f + 0.5f, 0, 255) : (byte)0;
                            bi[v * 4 + 2] = n > 2 ? (byte)Math.Clamp(tmp[2] * 255f + 0.5f, 0, 255) : (byte)0;
                            bi[v * 4 + 3] = n > 3 ? (byte)Math.Clamp(tmp[3] * 255f + 0.5f, 0, 255) : (byte)0;
                            break;
                        }
                    }
                }
            }

            // Dominant bone: when one bone owns (weighted) nearly every vertex, this
            // geometry is a rigid REGION of a skinned model (doors, vehicle extras…).
            int dominant = -1;
            if (bi != null)
            {
                var counts = new Dictionary<int, int>();
                for (int v = 0; v < count; v++)
                {
                    int idx = bi[v * 4];   // primary influence
                    if (bw != null)
                    {
                        // pick the highest-weighted influence rather than slot 0
                        float best = bw[v * 4]; idx = bi[v * 4];
                        for (int k = 1; k < 4; k++)
                            if (bw[v * 4 + k] > best) { best = bw[v * 4 + k]; idx = bi[v * 4 + k]; }
                    }
                    counts[idx] = counts.TryGetValue(idx, out int c) ? c + 1 : 1;
                }
                foreach (var kv in counts)
                    if (kv.Value >= count * 9 / 10) { dominant = kv.Key; break; }
            }

            // Indices: validate bounds. RAGE uses 16-bit indices; widen to 32.
            var outIdx = new uint[indices.Length];
            int maxIndex = count - 1;
            for (int i = 0; i < indices.Length; i++)
            {
                ushort idx = indices[i];
                if (idx > maxIndex)
                    return GeometryResult.Skip($"index {idx} out of range (verts={count})");
                outIdx[i] = idx;
            }

            var mesh = new MeshData
            {
                VertexCount = count,
                Positions = positions,
                Normals = normals,
                TexCoords0 = uv0,
                Colors0 = col0,
                BlendWeights = bw,
                BlendIndices = bi,
                Indices = outIdx,
                MaterialIndex = g.ShaderID,
                BoundsMin = count > 0 ? new Vec3(bmin.X, bmin.Y, bmin.Z) : Vec3.Zero,
                BoundsMax = count > 0 ? new Vec3(bmax.X, bmax.Y, bmax.Z) : Vec3.Zero,
                DominantBone = dominant,
            };
            return GeometryResult.Ok(mesh);
        }
        catch (Exception ex)
        {
            return GeometryResult.Skip("exception: " + ex.Message);
        }
    }

    private static bool HasChannel(uint flags, VertexSemantics s) => ((flags >> (int)s) & 1) == 1;

    /// <summary>Decode a single vertex component into up to 4 floats; returns the count written.</summary>
    private static int ReadComponent(byte[] b, int off, VertexComponentType t, Span<float> dst)
    {
        switch (t)
        {
            case VertexComponentType.Float:
                dst[0] = BitConverter.ToSingle(b, off); return 1;
            case VertexComponentType.Float2:
                dst[0] = BitConverter.ToSingle(b, off);
                dst[1] = BitConverter.ToSingle(b, off + 4); return 2;
            case VertexComponentType.Float3:
                dst[0] = BitConverter.ToSingle(b, off);
                dst[1] = BitConverter.ToSingle(b, off + 4);
                dst[2] = BitConverter.ToSingle(b, off + 8); return 3;
            case VertexComponentType.Float4:
                dst[0] = BitConverter.ToSingle(b, off);
                dst[1] = BitConverter.ToSingle(b, off + 4);
                dst[2] = BitConverter.ToSingle(b, off + 8);
                dst[3] = BitConverter.ToSingle(b, off + 12); return 4;
            case VertexComponentType.Half2:
                dst[0] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off));
                dst[1] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off + 2)); return 2;
            case VertexComponentType.Half4:
                dst[0] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off));
                dst[1] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off + 2));
                dst[2] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off + 4));
                dst[3] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, off + 6)); return 4;
            case VertexComponentType.Colour:   // RGBA bytes, 0..255
            case VertexComponentType.UByte4:
                dst[0] = b[off] / 255f;
                dst[1] = b[off + 1] / 255f;
                dst[2] = b[off + 2] / 255f;
                dst[3] = b[off + 3] / 255f; return 4;
            case VertexComponentType.RGBA8SNorm: // signed bytes, -1..1
                dst[0] = Math.Max(-1f, (sbyte)b[off] / 127f);
                dst[1] = Math.Max(-1f, (sbyte)b[off + 1] / 127f);
                dst[2] = Math.Max(-1f, (sbyte)b[off + 2] / 127f);
                dst[3] = Math.Max(-1f, (sbyte)b[off + 3] / 127f); return 4;
            default:
                return 0; // Nothing / FloatUnk / unknown -> contribute nothing
        }
    }

    private static MaterialData MakeMaterial(ShaderFX? s)
    {
        var mat = new MaterialData();
        if (s == null) return mat;
        mat.ShaderNameHash = s.Name;
        string preset = s.Name.ToString();
        mat.ShaderName = string.IsNullOrWhiteSpace(preset) ? "default" : preset;
        mat.DiffuseTextureName = FindDiffuseName(s);
        return mat;
    }

    /// <summary>Best-effort lookup of the diffuse sampler's texture name.</summary>
    private static string? FindDiffuseName(ShaderFX s)
    {
        try
        {
            var ps = s.ParametersList?.Parameters;
            var hashes = s.ParametersList?.Hashes;
            if (ps == null || hashes == null) return null;
            for (int i = 0; i < ps.Length && i < hashes.Length; i++)
            {
                string pn = hashes[i].ToString().ToLowerInvariant();
                if (pn.Contains("diffuse") && ps[i].Data is TextureBase tb)
                    return tb.Name;
            }
            // Fallback: first texture parameter of any kind.
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].Data is TextureBase tb)
                    return tb.Name;
        }
        catch { }
        return null;
    }

    private static Vec3 ToVec(Vector3 v) => new(v.X, v.Y, v.Z);
}
