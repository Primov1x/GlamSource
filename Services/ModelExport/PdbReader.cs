using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lumina;

namespace GlamSource.Services.ModelExport;

/// <summary>Reads chara/xls/bonedeformer/human.pbd — the game's static, per-race bone-deform table.
/// Most equipment ships only a Hyur (c0101) model; the real game deforms that mesh onto other
/// races' skeletons at load time via this file. Without it, Hyur-sized gear sits at Hyur bind-pose
/// positions on a race with different proportions (Viera has no full arm/body model to fill the
/// gap) — "Lücke zwischen Ober- und Unterarm". Purely static game data, no live memory read, so
/// this works even in the default bind-pose (no-live-feed) view.
/// Format verified against TexTools' real, shipped reader (xivModdingFramework/Models/FileTypes/
/// PDB.cs) — not guessed. Ported: race-set headers + per-bone deform matrices. NOT ported: the
/// tree-walk that lets a bone with no DIRECT deform entry inherit its nearest ancestor's — that
/// needs a full .sklb skeleton parser we don't have. Direct lookup only; a bone missing from the
/// target race's set falls back to Identity (no correction) instead of a wrong inherited guess.
/// Known gap from this simplification: exotic/rare bones without their own PBD entry won't deform,
/// only unusual body shapes would show it — core limb bones (arms, legs, torso) all have direct
/// entries and are unaffected.</summary>
public static class PdbReader
{
    private const string PdbPath = "chara/xls/bonedeformer/human.pbd";

    private static Dictionary<ushort, Dictionary<string, Matrix4x4>>? _sets;
    private static bool _loadFailed;

    /// <summary>Direct per-bone deform matrices for one race (no inherited fallback — see class
    /// doc). Null if the race has no entry or the file couldn't be read.</summary>
    public static IReadOnlyDictionary<string, Matrix4x4>? GetDeforms(GameData gameData, ushort raceId)
    {
        if (!EnsureLoaded(gameData)) return null;
        return _sets!.TryGetValue(raceId, out var d) ? d : null;
    }

    private static bool EnsureLoaded(GameData gameData)
    {
        if (_sets != null) return true;
        if (_loadFailed) return false;
        try
        {
            var file = gameData.GetFile(PdbPath);
            if (file == null) { _loadFailed = true; return false; }
            using var br = new BinaryReader(new MemoryStream(file.Data));

            var numSets = br.ReadInt32();
            var raceIds = new ushort[numSets];
            var dataOffsets = new uint[numSets];
            for (var i = 0; i < numSets; i++)
            {
                raceIds[i] = br.ReadUInt16();
                br.ReadUInt16(); // tree index — unused, no inheritance walk
                dataOffsets[i] = br.ReadUInt32();
                br.ReadSingle(); // overall scale — unused
            }
            // tree entries block (4 x uint16 per set) follows the headers, skipped entirely —
            // only needed for the inheritance walk this reader doesn't implement.
            br.BaseStream.Seek(numSets * 8, SeekOrigin.Current);

            var result = new Dictionary<ushort, Dictionary<string, Matrix4x4>>();
            for (var i = 0; i < numSets; i++)
            {
                if (raceIds[i] == ushort.MaxValue || dataOffsets[i] == 0) continue;
                br.BaseStream.Seek(dataOffsets[i], SeekOrigin.Begin);
                var start = br.BaseStream.Position;

                var numBones = br.ReadInt32();
                var boneOffsets = new uint[numBones];
                for (var b = 0; b < numBones; b++) boneOffsets[b] = br.ReadUInt16() + (uint)start;

                var afterOffsets = br.BaseStream.Position;
                var bones = new string[numBones];
                for (var b = 0; b < numBones; b++)
                {
                    br.BaseStream.Seek(boneOffsets[b], SeekOrigin.Begin);
                    bones[b] = ReadNullTerminatedString(br);
                }
                br.BaseStream.Seek(afterOffsets, SeekOrigin.Begin);
                while (br.BaseStream.Position % 4 != 0) br.ReadByte();

                var deforms = new Dictionary<string, Matrix4x4>();
                for (var b = 0; b < numBones; b++)
                {
                    var m = new float[16];
                    for (var f = 0; f < 12; f++) m[f] = br.ReadSingle();
                    m[15] = 1f;
                    // row-major 4x4, last row (0,0,0,1) omitted in the file — matches
                    // System.Numerics.Matrix4x4's own M11..M44 field order directly.
                    deforms[bones[b]] = new Matrix4x4(
                        m[0], m[1], m[2], m[3],
                        m[4], m[5], m[6], m[7],
                        m[8], m[9], m[10], m[11],
                        m[12], m[13], m[14], m[15]);
                }
                result[raceIds[i]] = deforms;
            }

            _sets = result;
            return true;
        }
        catch { _loadFailed = true; return false; }
    }

    private static string ReadNullTerminatedString(BinaryReader br)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = br.ReadByte()) != 0) bytes.Add(b);
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
