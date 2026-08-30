using System;
using System.IO;
using Lumina;

namespace GlamSource.Services.ModelExport;

/// <summary>
/// Reads skin/hair swatch color straight from the game's own character-creation color table
/// (chara/xls/charamake/human.cmp), indexed by the live Customize array's SkinColor/HairColor
/// bytes. This is the authoritative source the character creator itself uses to paint its color
/// swatches — unlike our three failed shader-buffer reads and Glamourer's own IPC (which both kept
/// returning the exact same implausible near-white/near-black values), this is a plain binary file
/// read with no memory reverse-engineering involved.
/// Layout and offset math ported from Ktisis (GameData/Chara/CharaCmpReader.cs), confirmed against
/// real game files: verified plausible warm skin tones for Midlander/Viera male+female before
/// shipping (see scratch colorprobe — never shipped on a single-sample guess again after the
/// colorset-ramp lesson).
/// </summary>
public static class CmpColorReader
{
    private const string CmpPath = "chara/xls/charamake/human.cmp";
    private const int BlockLength = 256;
    private const int DataLength = 192;
    private const int ExtendedDataLength = 208;

    private static uint[]? _raw;
    private static bool _loadFailed;

    /// <summary>Skin/hair color for the given tribe (1-16, CustomizeIndex.Tribe), gender
    /// (0=male/1=female, CustomizeIndex.Gender) and swatch indices (CustomizeIndex.SkinColor /
    /// HairColor). Returns null if human.cmp couldn't be read or the indices are out of range.</summary>
    public static CustomizeColors? Read(GameData gameData, byte tribe, byte gender, byte skinIdx, byte hairIdx)
    {
        if (!EnsureLoaded(gameData)) return null;
        if (tribe < 1 || tribe > 16) return null;

        var tribeGenderIdx = (uint)((tribe - 1) * 2 + (gender == 1 ? 1 : 0));
        // hrothgar (13 Helions, 14 Lost) don't have extended hair colors
        var isHairExtended = tribe is not (13 or 14);

        // ponytail: skin lives in BLOCK 0 of the tribe/gender area, not block 3 as Ktisis' reader
        // suggested — verified against ground truth: Glamourer displays #A99B98 for this user's
        // skin swatch (Veena male idx 127), and a full scan of human.cmp finds that exact color
        // ONLY at block 0 idx 127 of the matching tribe/gender blocks (block 3 gives a warm tan
        // that is NOT what the game shows). Hair stays at block 4: its value there matches both
        // the classic white-to-black hair swatch ramp and the user's visually dark hair.
        var skin = ReadEntry(4608 + tribeGenderIdx * 1280 + 0 * (uint)BlockLength, DataLength, skinIdx);
        var hair = ReadEntry(4608 + tribeGenderIdx * 1280 + 4 * (uint)BlockLength, isHairExtended ? ExtendedDataLength : DataLength, hairIdx);
        if (skin == null || hair == null) return null;
        return new CustomizeColors(skin, hair);
    }

    // ponytail: highlight colors are tribe-independent, stored in the "common" block right after
    // the eye-color block (word offset 1280, length 192, rounded up to the next 256-word boundary
    // = 1536). Verified against real data: word 1536.. gives a plausible gray ramp then warm
    // blonde tones, matching CustomizeIndex.HairColor2's swatch picker.
    private const uint HighlightBlockStart = 1536;

    /// <summary>Highlight color for CustomizeIndex.HairColor2's swatch index. Only meaningful when
    /// CustomizeIndex.HasHighlights has its 0x80 bit set (negative = enabled, per Dalamud's enum
    /// doc comment).</summary>
    public static float[]? ReadHighlightColor(GameData gameData, byte highlightIdx)
    {
        if (!EnsureLoaded(gameData)) return null;
        return ReadEntry(HighlightBlockStart, ExtendedDataLength, highlightIdx);
    }

    private static float[]? ReadEntry(uint blockStart, int length, byte index)
    {
        if (index >= length) return null;
        var raw = _raw!;
        var wordIndex = blockStart + index;
        if (wordIndex >= raw.Length) return null;
        var c = raw[wordIndex];
        return new[] { (c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f };
    }

    private static bool EnsureLoaded(GameData gameData)
    {
        if (_raw != null) return true;
        if (_loadFailed) return false;
        try
        {
            var file = gameData.GetFile(CmpPath);
            if (file == null) { _loadFailed = true; return false; }
            using var br = new BinaryReader(new MemoryStream(file.Data));
            var words = new uint[file.Data.Length / 4];
            for (var i = 0; i < words.Length; i++) words[i] = br.ReadUInt32();
            _raw = words;
            return true;
        }
        catch { _loadFailed = true; return false; }
    }
}
