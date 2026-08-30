using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using CsSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace GlamSource.Services.ModelExport;

/// <summary>Live bone pose for one character, read straight out of game memory — whatever the
/// character is actually doing right now (idle, sitting, weapon drawn, mid-emote, ...). Must be
/// built on the framework thread.</summary>
public sealed class SkeletonPose
{
    // bone name -> current model-space matrix, and its bind-pose (reference pose) inverse.
    public Dictionary<string, Matrix4x4> CurrentModel { get; } = new();
    public Dictionary<string, Matrix4x4> BindInverse { get; } = new();

    /// <summary>Bone name -> parent bone name (null = skeleton root), read straight from the live
    /// Havok skeleton's ParentIndices — the real skeleton hierarchy the game itself uses. Lets
    /// RacialDeform walk "no direct PBD entry for this bone? use the nearest ancestor's" exactly
    /// like TexTools' PDB.BuildNewTransfromMatrices, without needing our own .sklb parser.</summary>
    public Dictionary<string, string?> ParentOf { get; } = new();

    /// <summary>Per-partial capture log — which partials existed, how many bones each had, whether
    /// they got skipped. For chasing "bone X isn't in the live skeleton at all" reports.</summary>
    public List<string> DebugLog { get; } = new();

    /// <summary>Final skin matrix for one bone: moves a bind-pose vertex into the current pose.
    /// Identity (rigid, no deform) for any bone we couldn't find — degrades gracefully instead of
    /// throwing on VFX-only/attach bones that aren't in the mdl's skeleton.</summary>
    public Matrix4x4 SkinMatrix(string boneName)
    {
        var resolved = Resolve(boneName);
        if (resolved != null && BindInverse.TryGetValue(resolved, out var bi) && CurrentModel.TryGetValue(resolved, out var cm))
            return bi * cm;
        return Matrix4x4.Identity;
    }

    public bool HasBone(string boneName) => Resolve(boneName) != null;

    /// <summary>Equipment "extra bone" (j_ex_*) mesh tables sometimes name a bone with an extra "v"
    /// before its final a/b/l/r suffix (e.g. mdl wants "j_ex_met_va", the live skeleton's actual
    /// partial for that item's extra bones has it as "j_ex_met_a") — verified against a real trace:
    /// the partial skeleton genuinely exists with the right bone count, just this one-letter-off
    /// name. Try the exact name first; only fall back to the de-"v"'d variant if that's what the
    /// live skeleton actually has.</summary>
    private string? Resolve(string boneName)
    {
        if (BindInverse.ContainsKey(boneName) && CurrentModel.ContainsKey(boneName)) return boneName;
        if (boneName.Length > 1 && boneName[^1] is 'a' or 'b' && boneName[^2] == 'v')
        {
            var stripped = boneName[..^2] + boneName[^1];
            if (BindInverse.ContainsKey(stripped) && CurrentModel.ContainsKey(stripped)) return stripped;
        }
        return null;
    }
}

// ponytail: reads the character's ALREADY-COMPUTED live skeleton pose (game recomputes this every
// frame for rendering) instead of parsing .pap/Havok animation files from disk — same approach
// Anamnesis/Brio/Ktisis use for their posing tools (verified against their HavokPosing.cs/
// SkeletonService.cs). Gives the exact current in-game pose, whatever it is; "2 poses" becomes
// "however many poses — recapture whenever", which is strictly better than 2 fixed named ones.
public static unsafe class SkeletonPoseService
{
    /// <summary>Framework thread only. Null if the object has no live skeleton (e.g. gone from
    /// zone, or not a character).</summary>
    public static SkeletonPose? Capture(nint gameObjectAddress)
    {
        if (gameObjectAddress == 0) return null;
        var obj = (GameObject*)gameObjectAddress;
        var drawObject = obj->DrawObject;
        if (drawObject == null) return null;
        var chara = (CharacterBase*)drawObject;
        var skeleton = chara->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0) return null;

        var result = new SkeletonPose();
        result.DebugLog.Add($"PartialSkeletonCount={skeleton->PartialSkeletonCount}");
        Matrix4x4[]? bindModel0 = null; // partial 0 (body/root)'s global bind pose — other partials attach to it
        string[]? names0 = null; // partial 0's bone names, for cross-partial parent links below
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = skeleton->PartialSkeletons[p];
            var pose = partial.GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) { result.DebugLog.Add($"partial {p}: pose or hkSkeleton null"); continue; }
            var hkSkel = pose->Skeleton;
            var boneCount = hkSkel->Bones.Length;
            if (boneCount == 0 || pose->ModelPose.Length < boneCount)
            {
                result.DebugLog.Add($"partial {p}: boneCount={boneCount} modelPoseLen={pose->ModelPose.Length} — skipped");
                continue;
            }
            result.DebugLog.Add($"partial {p}: boneCount={boneCount} connectedBoneIdx={partial.ConnectedBoneIndex} connectedParentIdx={partial.ConnectedParentBoneIndex} bones=[{string.Join(",", System.Linq.Enumerable.Range(0, boneCount).Select(i => hkSkel->Bones[i].Name.String))}]");

            // bind pose: hkSkel->ReferencePose is LOCAL space, walk ParentIndices to model space.
            // Parents always precede children in these arrays, so one forward pass suffices. This
            // is correct in isolation but only lands in the CHARACTER's shared model space for
            // partial 0 (body/root) — partial 1+ (fingers, face, hair, ...) have their own tiny
            // skeleton with its own local root, e.g. near the wrist for a hand partial.
            var bindModel = new Matrix4x4[boneCount];
            for (var i = 0; i < boneCount; i++)
            {
                var local = ToMatrix(hkSkel->ReferencePose[i]);
                var parent = hkSkel->ParentIndices[i];
                bindModel[i] = parent >= 0 && parent < i ? local * bindModel[parent] : local;
            }

            // re-base partial 1+ into partial 0's space via its attach point (ConnectedBoneIndex on
            // THIS partial = ConnectedParentBoneIndex on partial 0) — the live pose we read below is
            // already correctly composed by the engine either way, only the bind pose needs this to
            // match; skipping it is exactly what made attached geometry (fingers) explode/stretch.
            var boneNames = new string[boneCount];
            for (var i = 0; i < boneCount; i++) boneNames[i] = hkSkel->Bones[i].Name.String;

            if (p == 0)
            {
                bindModel0 = bindModel;
                names0 = boneNames;
            }
            else if (bindModel0 != null && partial.ConnectedBoneIndex >= 0 && partial.ConnectedBoneIndex < boneCount
                     && partial.ConnectedParentBoneIndex >= 0 && partial.ConnectedParentBoneIndex < bindModel0.Length
                     && Matrix4x4.Invert(bindModel[partial.ConnectedBoneIndex], out var attachLocalInv))
            {
                var offset = attachLocalInv * bindModel0[partial.ConnectedParentBoneIndex];
                for (var i = 0; i < boneCount; i++) bindModel[i] *= offset;
            }

            for (var i = 0; i < boneCount; i++)
            {
                var name = boneNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (!Matrix4x4.Invert(bindModel[i], out var bindInv)) continue;
                // first (body, index 0) partial wins on name collisions across partials
                result.BindInverse.TryAdd(name, bindInv);
                result.CurrentModel.TryAdd(name, ToMatrix(pose->ModelPose[i]));

                // parent bone name, for RacialDeform's ancestor walk: within-partial parent if this
                // bone has one; otherwise (this partial's local root) the OTHER partial it attaches
                // to, same ConnectedBoneIndex/ConnectedParentBoneIndex link used for bindModel above.
                var parentIdx = hkSkel->ParentIndices[i];
                string? parentName = parentIdx >= 0 && parentIdx < boneCount ? boneNames[parentIdx] : null;
                if (parentName == null && p != 0 && names0 != null
                    && partial.ConnectedParentBoneIndex >= 0 && partial.ConnectedParentBoneIndex < names0.Length)
                    parentName = names0[partial.ConnectedParentBoneIndex];
                result.ParentOf.TryAdd(name, parentName);
            }
        }
        return result.CurrentModel.Count > 0 ? result : null;
    }

    private static Matrix4x4 ToMatrix(FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf t)
    {
        var pos = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
        var rot = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        var scale = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
    }
}
