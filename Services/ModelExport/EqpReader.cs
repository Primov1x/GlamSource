using System;
using System.Collections.Generic;
using System.Numerics;
using Lumina;

namespace GlamSource.Services.ModelExport;

/// <summary>
/// Reads a gear set's EQP entry (chara/xls/equipmentparameter/equipmentparameter.eqp) — the flags
/// the game uses to hide/show other model parts when this item is equipped. Format ported from
/// Penumbra's ExpandedEqpGmpBase (Meta/Files/EqpGmpFile.cs): a u64 control bitmask of expanded
/// 160-entry blocks, then only the expanded blocks' u64 entries back to back; a collapsed block
/// means "all defaults". Bit layout from Penumbra.GameData's EqpEntry (Structs/EqpEntry.cs).
/// Verified against real data: e6084 (the user's cap) reads HeadHideScalp=true, ShowVieraHat=true
/// — matching its visible in-game behavior exactly.
/// </summary>
public static class EqpReader
{
    private const string EqpPath = "chara/xls/equipmentparameter/equipmentparameter.eqp";
    private const int BlockSize = 160;

    private static byte[]? _raw;
    private static bool _loadFailed;

    /// <summary>Hair-mesh attribute names this head gear hides (for MdlGeometry.DecodeLod0's
    /// hideAttributes), or empty when none/unknown. Both "atr_kam" (scalp/top hair) and "atr_bak"
    /// (back hair/ponytail) are gated by the SAME HeadHideScalp bit — verified against real data:
    /// e6084's EQP has HeadHideScalp=true and hair h0003's only non-base attributes are exactly
    /// atr_bak/atr_kam (see MdlGeometry.DecodeLod0's doc comment), i.e. one flag hides the whole
    /// scalp/back-hair group together, not two separate flags. Only atr_kam was wired before —
    /// hats clipped through any hairstyle whose ponytail/back-hair was its own atr_bak submesh.
    /// Full hide handled via <see cref="HidesHair"/> instead.</summary>
    public static IReadOnlyCollection<string> HiddenHairAttributes(GameData gameData, ushort headSetId)
    {
        var e = Entry(gameData, headSetId);
        if (e == null) return Array.Empty<string>();
        var result = new List<string>(2);
        if ((e.Value >> 40 & 0x02) != 0) { result.Add("atr_kam"); result.Add("atr_bak"); } // HeadHideScalp
        return result;
    }

    /// <summary>True when this head gear hides the hair entirely (HeadHideHair, e.g. full helmets).</summary>
    public static bool HidesHair(GameData gameData, ushort headSetId)
        => Entry(gameData, headSetId) is { } e && (e >> 40 & 0x04) != 0;

    private static ulong? Entry(GameData gameData, ushort setId)
    {
        if (!EnsureLoaded(gameData)) return null;
        var raw = _raw!;
        var control = BitConverter.ToUInt64(raw, 0);
        var blockIdx = setId / BlockSize;
        if (blockIdx >= 64 || ((control >> blockIdx) & 1) == 0) return null; // collapsed = defaults (no hide flags)
        var expandedBefore = BitOperations.PopCount(control & ((1ul << blockIdx) - 1));
        var wordIdx = BlockSize * expandedBefore + setId % BlockSize;
        if ((wordIdx + 1) * 8 > raw.Length) return null;
        return BitConverter.ToUInt64(raw, wordIdx * 8);
    }

    private static bool EnsureLoaded(GameData gameData)
    {
        if (_raw != null) return true;
        if (_loadFailed) return false;
        try
        {
            var file = gameData.GetFile(EqpPath);
            if (file == null) { _loadFailed = true; return false; }
            _raw = file.Data;
            return true;
        }
        catch { _loadFailed = true; return false; }
    }
}
