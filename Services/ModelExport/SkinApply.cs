using System;
using System.Collections.Generic;
using System.Numerics;
using Penumbra.GameData.Files.ModelStructs;

namespace GlamSource.Services.ModelExport;

/// <summary>Diagnostics from one Apply() call — how many bone lookups actually hit the live pose,
/// and how many vertices got rejected as garbage (see Apply's sanity guard).</summary>
public readonly record struct SkinStats(int Vertices, int SkinnedVertices, int BoneRefsMatched, int BoneRefsTotal, int RejectedVertices);

// ponytail: CPU skinning, baked once into final vertex positions — no glTF <skin>/joints needed,
// the viewer stays a plain static mesh. Normals use the 3x3 rotation+scale part only (no proper
// inverse-transpose for non-uniform scale) — bones in this rig are near-uniform scale, visually
// fine; upgrade if a stretched-limb normal artifact ever shows up.
public static class SkinApply
{
    public static SkinStats Apply(DecodedMesh mesh, string[] mdlBones, BoneTableStruct[] boneTables, SkeletonPose pose)
    {
        if (!mesh.HasSkinning || mesh.BoneTableIndex >= boneTables.Length) return default;
        var table = boneTables[mesh.BoneTableIndex].BoneIndex;
        var n = mesh.Positions.Length / 3;
        var skinCache = new Dictionary<byte, (Matrix4x4 M, bool Matched)>();
        var refsMatched = 0;
        var refsTotal = 0;

        (Matrix4x4 M, bool Matched) SkinFor(byte localIdx)
        {
            if (skinCache.TryGetValue(localIdx, out var cached)) return cached;
            var matched = localIdx < table.Length && table[localIdx] < mdlBones.Length && pose.HasBone(mdlBones[table[localIdx]]);
            var m = matched ? pose.SkinMatrix(mdlBones[table[localIdx]]) : Matrix4x4.Identity;
            var result = (m, matched);
            skinCache[localIdx] = result;
            return result;
        }

        var hasNormals = mesh.Normals.Length == n * 3;
        var skinnedCount = 0;
        var rejected = 0;
        for (var v = 0; v < n; v++)
        {
            var bi = v * 8;
            var pos = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            var skinnedPos = Vector3.Zero;
            var normal = hasNormals ? new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]) : Vector3.Zero;
            var skinnedNormal = Vector3.Zero;
            var wsum = 0f;
            for (var k = 0; k < 8; k++)
            {
                var w = mesh.BlendWeights[bi + k];
                if (w <= 0f) continue;
                var (m, matched) = SkinFor(mesh.BlendIndices[bi + k]);
                refsTotal++;
                if (matched) refsMatched++;
                skinnedPos += Vector3.Transform(pos, m) * w;
                if (hasNormals) skinnedNormal += Vector3.TransformNormal(normal, m) * w;
                wsum += w;
            }
            if (wsum <= 0.0001f) continue; // no influence found — leave bind-pose vertex as-is
            skinnedPos /= wsum;

            // sanity guard: a bad bind-pose/bone match can fling a vertex meters away (or NaN it) —
            // character meshes are ~2m tall, so a shift beyond that is bad data, not a real pose.
            // Fall back to the original bind-pose vertex instead of shipping exploded/invisible geometry.
            if (!IsFinite(skinnedPos) || Vector3.DistanceSquared(skinnedPos, pos) > 25f) { rejected++; continue; }

            skinnedCount++;
            mesh.Positions[v * 3] = skinnedPos.X;
            mesh.Positions[v * 3 + 1] = skinnedPos.Y;
            mesh.Positions[v * 3 + 2] = skinnedPos.Z;
            if (hasNormals)
            {
                var normalized = skinnedNormal.LengthSquared() > 0.0001f ? Vector3.Normalize(skinnedNormal) : normal;
                mesh.Normals[v * 3] = normalized.X;
                mesh.Normals[v * 3 + 1] = normalized.Y;
                mesh.Normals[v * 3 + 2] = normalized.Z;
            }
        }
        return new SkinStats(n, skinnedCount, refsMatched, refsTotal, rejected);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
