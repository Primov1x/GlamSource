using System;
using System.Collections.Generic;
using System.Numerics;
using Penumbra.GameData.Files.ModelStructs;

namespace GlamSource.Services.ModelExport;

// most equipment only ships a Hyur (c0101) model; the real game deforms it onto other races'
// skeletons at load time using chara/xls/bonedeformer/human.pbd (see PdbReader). Without this,
// Hyur-sized gear sits at Hyur bind-pose positions on a race with different proportions — confirmed
// root cause of "Lücke zwischen Ober- und Unterarm/Knie" (verified against real game files: Viera's
// own "nude" body/legs patches are ALSO plain Hyur c0101 models — even bare skin relies on this same
// runtime deform, there's no separate always-correct-shape body).
public static class RacialDeform
{
    /// <param name="parentOf">Bone name -> parent bone name (null = root), from the live Havok
    /// skeleton (SkeletonPoseService). Used to walk up to the nearest ancestor with a direct PBD
    /// entry — real algorithm ported from TexTools' xivModdingFramework (PDB.BuildNewTransfromMatrices,
    /// decompiled from the shipped xivModdingFramework.dll, not guessed). TexTools sources this
    /// hierarchy from a bundled per-race .sklb dump; we reuse the same ParentIndices data
    /// SkeletonPoseService already reads for bind-pose composition instead of writing a .sklb parser.
    /// Null (no live pose captured) means no hierarchy available — falls back to direct-lookup-only,
    /// same degraded behavior as before this was implemented.</param>
    public static void Apply(DecodedMesh mesh, string[] mdlBones, BoneTableStruct[] boneTables, IReadOnlyDictionary<string, Matrix4x4> deforms, IReadOnlyDictionary<string, string?>? parentOf = null)
    {
        if (!mesh.HasSkinning || mesh.BoneTableIndex >= boneTables.Length) return;
        var table = boneTables[mesh.BoneTableIndex].BoneIndex;
        var n = mesh.Positions.Length / 3;
        var cache = new Dictionary<byte, Matrix4x4>();

        Matrix4x4 DeformFor(byte localIdx)
        {
            if (cache.TryGetValue(localIdx, out var cached)) return cached;
            var boneName = localIdx < table.Length && table[localIdx] < mdlBones.Length ? mdlBones[table[localIdx]] : null;
            var m = boneName != null ? ResolveDeform(boneName, deforms, parentOf) : Matrix4x4.Identity;
            cache[localIdx] = m;
            return m;
        }

        var hasNormals = mesh.Normals.Length == n * 3;
        for (var v = 0; v < n; v++)
        {
            var bi = v * 8;
            var pos = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            var normal = hasNormals ? new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]) : Vector3.Zero;
            var deformedPos = Vector3.Zero;
            var deformedNormal = Vector3.Zero;
            var wsum = 0f;
            for (var k = 0; k < 8; k++)
            {
                var w = mesh.BlendWeights[bi + k];
                if (w <= 0f) continue;
                var m = DeformFor(mesh.BlendIndices[bi + k]);
                deformedPos += Vector3.Transform(pos, m) * w;
                if (hasNormals) deformedNormal += Vector3.TransformNormal(normal, m) * w;
                wsum += w;
            }
            if (wsum <= 0.0001f) continue;
            deformedPos /= wsum;
            if (!IsFinite(deformedPos)) continue; // malformed deform data — leave vertex as-is

            mesh.Positions[v * 3] = deformedPos.X;
            mesh.Positions[v * 3 + 1] = deformedPos.Y;
            mesh.Positions[v * 3 + 2] = deformedPos.Z;
            if (hasNormals)
            {
                var normalized = deformedNormal.LengthSquared() > 0.0001f ? Vector3.Normalize(deformedNormal) : normal;
                mesh.Normals[v * 3] = normalized.X;
                mesh.Normals[v * 3 + 1] = normalized.Y;
                mesh.Normals[v * 3 + 2] = normalized.Z;
            }
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Direct PBD entry if this bone has one; otherwise walk up the live skeleton to the
    /// nearest ancestor that does (TexTools' BuildNewTransfromMatrices); Identity if none of them do
    /// or no hierarchy is available. Capped walk depth as a guard against malformed/cyclic parent
    /// data — real skeletons are well under 64 bones deep.</summary>
    private static Matrix4x4 ResolveDeform(string boneName, IReadOnlyDictionary<string, Matrix4x4> deforms, IReadOnlyDictionary<string, string?>? parentOf)
    {
        string? current = boneName;
        for (var steps = 0; current != null && steps < 64; steps++)
        {
            if (deforms.TryGetValue(current, out var m)) return m;
            if (parentOf == null || !parentOf.TryGetValue(current, out var parent)) break;
            current = parent;
        }
        return Matrix4x4.Identity;
    }
}
