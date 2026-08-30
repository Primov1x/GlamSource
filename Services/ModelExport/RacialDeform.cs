using System;
using System.Collections.Generic;
using System.Numerics;
using Penumbra.GameData.Files.ModelStructs;

namespace GlamSource.Services.ModelExport;

// ponytail: most equipment only ships a Hyur (c0101) model; the real game deforms it onto other
// races' skeletons at load time using chara/xls/bonedeformer/human.pbd (see PdbReader). Without
// this, Hyur-sized gear sits at Hyur bind-pose positions on a race with different proportions —
// confirmed root cause of "Lücke zwischen Ober- und Unterarm" (Viera has no full-arm body model to
// fill the gap the mismatch creates). Static per-bone correction data, independent of any live
// pose — applies the same in the default bind-pose view as in a live-posed one.
public static class RacialDeform
{
    public static void Apply(DecodedMesh mesh, string[] mdlBones, BoneTableStruct[] boneTables, IReadOnlyDictionary<string, Matrix4x4> deforms)
    {
        if (!mesh.HasSkinning || mesh.BoneTableIndex >= boneTables.Length) return;
        var table = boneTables[mesh.BoneTableIndex].BoneIndex;
        var n = mesh.Positions.Length / 3;
        var cache = new Dictionary<byte, Matrix4x4>();

        Matrix4x4 DeformFor(byte localIdx)
        {
            if (cache.TryGetValue(localIdx, out var cached)) return cached;
            var boneName = localIdx < table.Length && table[localIdx] < mdlBones.Length ? mdlBones[table[localIdx]] : null;
            // ponytail: a bone with no direct PBD entry for this race falls back to Identity (no
            // correction) rather than a guessed inherited transform — see PdbReader's doc comment,
            // the real inheritance walk needs a .sklb skeleton parser we don't have.
            var m = boneName != null && deforms.TryGetValue(boneName, out var dm) ? dm : Matrix4x4.Identity;
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
}
