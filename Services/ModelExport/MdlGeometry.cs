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

    // ponytail: skinning inputs, 4 influences per vertex — enough for all classic (non-Dawntrail-
    // extended 8-influence) meshes, which is everything we've tested. Empty when the mesh has no
    // blend vertex elements (unskinned parts render rigid/bind-pose, same as before this existed).
    public byte[] BlendIndices = [];  // 4 per vertex, local index into BoneTableIndex's table
    public float[] BlendWeights = []; // 4 per vertex
    public ushort BoneTableIndex;
    public bool HasSkinning;

    // diagnostics: how many of this mesh's submeshes got dropped by the AttributeIndexMask filter,
    // and which attribute names they were gated on — helps tell "correctly hid a conditional
    // variant" from "wrongly hid always-visible geometry for this race/body".
    public int SubmeshesTotal;
    public int SubmeshesKept;
    public List<string> DroppedAttributes = [];
}

// ponytail: LOD0 only, positions/normals/uv0/blend weights — no color/tangent. Enough for a static
// or skinned glTF viewer; add channels when something needs them.
public static class MdlGeometry
{
    // Lumina.Models.Models.Vertex+VertexType values (verified via reflection against Lumina 7.6).
    private const byte TSingle3 = 2;
    private const byte TSingle4 = 3;
    private const byte TUInt = 5;
    private const byte TByteFloat4 = 8;
    private const byte THalf2 = 13;
    private const byte THalf4 = 14;
    // Dawntrail additions (xivModdingFramework naming); only hit for the extended 8-influence blend
    // format we don't support yet — those meshes fall back to unskinned (HasSkinning stays false).
    private const byte TUShort2 = 16;
    private const byte TUShort4 = 17;

    private const byte UsagePosition = 0;
    private const byte UsageBlendWeights = 1;
    private const byte UsageBlendIndices = 2;
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
                BoneTableIndex = mesh.BoneTableIndex,
            };

            byte[]? blendIdx = null;
            float[]? blendWt = null;

            foreach (var el in decl.VertexElements)
            {
                var isPos = el.Usage == UsagePosition;
                var isNormal = el.Usage == UsageNormal;
                var isUv0 = el.Usage == UsageUv && el.UsageIndex == 0;
                var isBlendIdx = el.Usage == UsageBlendIndices;
                var isBlendWt = el.Usage == UsageBlendWeights;
                if (!isPos && !isNormal && !isUv0 && !isBlendIdx && !isBlendWt) continue;

                var stride = mesh.VertexBufferStride(el.Stream);
                var baseOffset = lod.VertexDataOffset + mesh.VertexBufferOffset(el.Stream) + el.Offset;

                if (isBlendIdx)
                {
                    // raw bytes, never normalized — these are table indices, not a color/normal
                    blendIdx = new byte[n * 4];
                    for (var v = 0; v < n; v++)
                    {
                        var o = (int)(baseOffset + (uint)(v * stride));
                        for (var c = 0; c < 4; c++) blendIdx[v * 4 + c] = data[o + c];
                    }
                    continue;
                }
                if (isBlendWt)
                {
                    // classic format only: 4 unsigned-normalized bytes (ByteFloat4) or 4 halfs.
                    // Dawntrail's extended 8-influence format (UShort2/4 pairs) isn't handled —
                    // those meshes just fall back to unskinned below.
                    if (el.Type != TByteFloat4 && el.Type != THalf4) continue;
                    blendWt = new float[n * 4];
                    for (var v = 0; v < n; v++)
                    {
                        var o = (int)(baseOffset + (uint)(v * stride));
                        for (var c = 0; c < 4; c++)
                            blendWt[v * 4 + c] = el.Type == TByteFloat4 ? data[o + c] / 255f : (float)BitConverter.ToHalf(data, o + c * 2);
                    }
                    continue;
                }

                float[] target;
                int comps;
                if (isPos) { target = outMesh.Positions; comps = 3; }
                else if (isNormal) { outMesh.Normals = new float[n * 3]; target = outMesh.Normals; comps = 3; }
                else { outMesh.Uvs = new float[n * 2]; target = outMesh.Uvs; comps = 2; }

                for (var v = 0; v < n; v++)
                {
                    var o = (int)(baseOffset + (uint)(v * stride));
                    ReadElement(data, o, el.Type, target.AsSpan(v * comps, comps));
                }
            }

            if (blendIdx != null && blendWt != null)
            {
                outMesh.BlendIndices = blendIdx;
                outMesh.BlendWeights = blendWt;
                outMesh.HasSkinning = true;
            }

            // ponytail: AttributeIndexMask != 0 was previously treated as "optional decoration,
            // hide by default" — WRONG. Verified against real data (debug trace across many
            // meshes): atr_ude/atr_arm/atr_hiz/atr_sne/atr_leg/atr_nek etc. are actual body-part
            // submeshes (arm/knee/shin/neck) that other equipped items can hide via their own IMC
            // hide-flags — we don't evaluate those, so the correct default is VISIBLE, not hidden.
            // Dropping them removed whole limbs. atr_lod is the one genuine exception: an explicit
            // lower-detail LOD variant that should never render alongside the main geometry.
            var indices = new List<ushort>();
            var idxBaseAll = (int)lod.IndexDataOffset;
            for (var si = mesh.SubMeshIndex; si < mesh.SubMeshIndex + mesh.SubMeshCount; si++)
            {
                var sub = mdl.SubMeshes[si];
                outMesh.SubmeshesTotal++;
                var isLod = false;
                for (var bit = 0; bit < 32 && bit < mdl.Attributes.Length; bit++)
                    if ((sub.AttributeIndexMask & (1u << bit)) != 0 && mdl.Attributes[bit] == "atr_lod")
                        isLod = true;
                if (isLod)
                {
                    outMesh.DroppedAttributes.Add("atr_lod");
                    continue;
                }
                outMesh.SubmeshesKept++;
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
                // blend indices/weights formats — never routed here (handled separately above),
                // but keep a defined behavior instead of throwing on odd files
                dst.Clear();
                break;
            default:
                dst.Clear();
                break;
        }
    }
}
