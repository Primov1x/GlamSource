using System;
using System.Collections.Generic;
using Lumina.Data.Parsing;
using Penumbra.GameData.Files;

namespace GlamSource.Services.ModelExport;

/// <summary>One LOD-0 mesh decoded to plain arrays, ready for glTF.</summary>
public sealed class DecodedMesh
{
    public float[] Positions = [];   // xyz per vertex
    public float[] Normals = [];     // xyz per vertex (may be empty)
    public float[] Uvs = [];         // xy per vertex (may be empty)
    public ushort[] Indices = [];
    public int MaterialIndex;
}

// ponytail: LOD0 only, positions/normals/uv0 only — no skinning (verts are stored in bind pose),
// no color/tangent. Enough for a static glTF viewer; add channels when something needs them.
public static class MdlGeometry
{
    // Lumina.Models.Models.Vertex+VertexType values (verified via reflection against Lumina 7.6).
    private const byte TSingle3 = 2;
    private const byte TSingle4 = 3;
    private const byte TUInt = 5;
    private const byte TByteFloat4 = 8;
    private const byte THalf2 = 13;
    private const byte THalf4 = 14;
    // Dawntrail additions (xivModdingFramework naming); only hit for blend data we don't read.
    private const byte TUShort2 = 16;
    private const byte TUShort4 = 17;

    private const byte UsagePosition = 0;
    private const byte UsageNormal = 3;
    private const byte UsageUv = 4;

    public static List<DecodedMesh> DecodeLod0(MdlFile mdl)
    {
        var result = new List<DecodedMesh>();
        if (mdl.LodCount == 0) return result;
        var lod = mdl.Lods[0];
        var data = mdl.RemainingData;

        for (var mi = lod.MeshIndex; mi < lod.MeshIndex + lod.MeshCount; mi++)
        {
            var mesh = mdl.Meshes[mi];
            var decl = mdl.VertexDeclarations[mi];
            var n = mesh.VertexCount;
            var outMesh = new DecodedMesh
            {
                Positions = new float[n * 3],
                MaterialIndex = mesh.MaterialIndex,
            };

            foreach (var el in decl.VertexElements)
            {
                if (el.Usage != UsagePosition && el.Usage != UsageNormal && !(el.Usage == UsageUv && el.UsageIndex == 0))
                    continue;
                var stride = mesh.VertexBufferStride(el.Stream);
                var baseOffset = lod.VertexDataOffset + mesh.VertexBufferOffset(el.Stream) + el.Offset;

                float[] target;
                int comps;
                switch (el.Usage)
                {
                    case UsagePosition: target = outMesh.Positions; comps = 3; break;
                    case UsageNormal: outMesh.Normals = new float[n * 3]; target = outMesh.Normals; comps = 3; break;
                    default: outMesh.Uvs = new float[n * 2]; target = outMesh.Uvs; comps = 2; break;
                }

                for (var v = 0; v < n; v++)
                {
                    var o = (int)(baseOffset + (uint)(v * stride));
                    ReadElement(data, o, el.Type, target.AsSpan(v * comps, comps));
                }
            }

            // ponytail: only unconditional submeshes (AttributeIndexMask == 0) — the others are
            // optional variants (race-specific parts, hide-flags, alt decorations) gated on
            // attributes we have no way to evaluate; showing them all overlapped/stretched wrong
            // detail across the mesh (verified: a small 'emblem' submesh + conditional jacket
            // panels rendering as a big wrong-colored patch on an otherwise-correct outfit).
            var indices = new List<ushort>();
            var idxBaseAll = (int)lod.IndexDataOffset;
            for (var si = mesh.SubMeshIndex; si < mesh.SubMeshIndex + mesh.SubMeshCount; si++)
            {
                var sub = mdl.SubMeshes[si];
                if (sub.AttributeIndexMask != 0) continue;
                for (var i = 0; i < sub.IndexCount; i++)
                    indices.Add(BitConverter.ToUInt16(data, idxBaseAll + (int)(sub.IndexOffset + i) * 2));
            }
            outMesh.Indices = indices.ToArray();

            result.Add(outMesh);
        }

        return result;
    }

    /// <summary>Decode one vertex element into up to dst.Length floats (extra source components dropped).</summary>
    private static void ReadElement(byte[] d, int o, byte type, Span<float> dst)
    {
        switch (type)
        {
            case TSingle3:
            case TSingle4:
                for (var c = 0; c < dst.Length; c++) dst[c] = BitConverter.ToSingle(d, o + c * 4);
                break;
            case THalf2:
            case THalf4:
                for (var c = 0; c < dst.Length; c++) dst[c] = (float)BitConverter.ToHalf(d, o + c * 2);
                break;
            case TByteFloat4:
                // normalized bytes; for normals the -1..1 remap is the convention that renders correctly
                for (var c = 0; c < dst.Length; c++) dst[c] = d[o + c] / 255f * 2f - 1f;
                break;
            case TUInt:
            case TUShort2:
            case TUShort4:
                // blend indices/weights formats — never routed here (we only read pos/normal/uv),
                // but keep a defined behavior instead of throwing on odd files
                dst.Clear();
                break;
            default:
                dst.Clear();
                break;
        }
    }
}
