using System;
using System.Collections.Generic;

namespace App.Geometry;

/// <summary>Simple 3-component vector used in the renderer-agnostic DTOs.</summary>
public readonly struct Vec3
{
    public readonly float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static readonly Vec3 Zero = new(0, 0, 0);
}

/// <summary>
/// One decoded geometry, ready for any renderer. All channels are plain float
/// arrays (interleaving is the renderer's concern). Missing channels are null
/// and the renderer is expected to degrade gracefully (flat-shade, white, etc.).
/// </summary>
public sealed class MeshData
{
    public int VertexCount;
    public float[] Positions = Array.Empty<float>();   // 3 * VertexCount
    public float[]? Normals;                            // 3 * VertexCount, or null
    public float[]? TexCoords0;                         // 2 * VertexCount, or null
    public float[]? Colors0;                            // 4 * VertexCount (0..1), or null
    public float[]? BlendWeights;                       // 4 * VertexCount (0..1), or null
    public byte[]? BlendIndices;                        // 4 * VertexCount (skeleton bone indices), or null
    public uint[] Indices = Array.Empty<uint>();
    public int MaterialIndex;
    public Vec3 BoundsMin = Vec3.Zero;
    public Vec3 BoundsMax = Vec3.Zero;

    // Skeleton binding of the OWNING DrawableModel: rigid models hang off BoneIndex
    // (geometry is bone-local); skinned models (HasSkin) use BlendWeights/Indices.
    public int BoneIndex;
    public bool HasSkin;
    // For skinned meshes: the bone that owns (almost) every vertex, or -1 when mixed.
    // This is what identifies toggleable vehicle parts (extra_* regions).
    public int DominantBone = -1;
}

/// <summary>Result of decoding a single geometry: either a mesh, or a logged skip reason.</summary>
public readonly struct GeometryResult
{
    public readonly MeshData? Mesh;
    public readonly string? SkipReason;
    private GeometryResult(MeshData? m, string? r) { Mesh = m; SkipReason = r; }
    public static GeometryResult Ok(MeshData m) => new(m, null);
    public static GeometryResult Skip(string reason) => new(null, reason);
}

/// <summary>A single level of detail and its decoded meshes.</summary>
public sealed class LodData
{
    public string Level = "High";          // High / Med / Low / VLow
    public List<MeshData> Meshes = new();
}

/// <summary>A material entry resolved from a RAGE shader preset.</summary>
public sealed class MaterialData
{
    public uint ShaderNameHash;
    public string ShaderName = "default";  // resolved preset string if known, else the hash
    public string? DiffuseTextureName;     // name of the diffuse sampler texture, if any
}

/// <summary>A whole decoded drawable: LODs, materials, bounds, and skip diagnostics.</summary>
public sealed class ModelData
{
    public string Name = "";
    public List<LodData> Lods = new();
    public List<MaterialData> Materials = new();
    public Vec3 BoundsMin = Vec3.Zero;
    public Vec3 BoundsMax = Vec3.Zero;

    // Diagnostics for the robustness harness: total geometries seen, how many
    // rendered, and the reason each skipped geometry was dropped.
    public int GeometryCount;
    public int RenderedCount;
    public List<string> SkippedReasons = new();
}
