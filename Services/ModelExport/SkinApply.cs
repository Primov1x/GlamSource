using System.Collections.Generic;
using System.Numerics;
using Penumbra.GameData.Files.ModelStructs;

namespace GlamSource.Services.ModelExport;

// ponytail: CPU skinning, baked once into final vertex positions — no glTF <skin>/joints needed,
// the viewer stays a plain static mesh. Normals use the 3x3 rotation+scale part only (no proper
// inverse-transpose for non-uniform scale) — bones in this rig are near-uniform scale, visually
// fine; upgrade if a stretched-limb normal artifact ever shows up.
public static class SkinApply
{
    public static void Apply(DecodedMesh mesh, string[] mdlBones, BoneTableStruct[] boneTables, SkeletonPose pose)
    {
        if (!mesh.HasSkinning || mesh.BoneTableIndex >= boneTables.Length) return;
        var table = boneTables[mesh.BoneTableIndex].BoneIndex;
        var n = mesh.Positions.Length / 3;
        var skinCache = new Dictionary<byte, Matrix4x4>();

        Matrix4x4 SkinFor(byte localIdx)
        {
            if (skinCache.TryGetValue(localIdx, out var cached)) return cached;
            var m = localIdx < table.Length && table[localIdx] < mdlBones.Length
                ? pose.SkinMatrix(mdlBones[table[localIdx]])
                : Matrix4x4.Identity;
            skinCache[localIdx] = m;
            return m;
        }

        var hasNormals = mesh.Normals.Length == n * 3;
        for (var v = 0; v < n; v++)
        {
            var bi = v * 4;
            var pos = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            var skinnedPos = Vector3.Zero;
            var normal = hasNormals ? new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]) : Vector3.Zero;
            var skinnedNormal = Vector3.Zero;
            var wsum = 0f;
            for (var k = 0; k < 4; k++)
            {
                var w = mesh.BlendWeights[bi + k];
                if (w <= 0f) continue;
                var m = SkinFor(mesh.BlendIndices[bi + k]);
                skinnedPos += Vector3.Transform(pos, m) * w;
                if (hasNormals) skinnedNormal += Vector3.TransformNormal(normal, m) * w;
                wsum += w;
            }
            if (wsum <= 0.0001f) continue; // no influence found — leave bind-pose vertex as-is
            skinnedPos /= wsum;
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
    }
}
