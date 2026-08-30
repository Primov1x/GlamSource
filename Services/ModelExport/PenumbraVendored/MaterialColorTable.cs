// Purpose-built, minimal re-implementation of the relevant slice of Penumbra.GameData's
// Files/MtrlFile.cs + Files/MaterialStructs/{ColorTable,LegacyColorTable,TableFlags}.cs
// (https://github.com/xivdev/Penumbra, AGPL-3.0 — same license as GlamSource).
//
// We read the material's color table (diffuse RGB per row) directly from raw .mtrl bytes.
// Vendoring the full MtrlFile class (read+write, dye application, shader packages, ~1700 lines
// across 9 files) would pull in a sibling "Luna" project we don't have locally. This reads just
// far enough to reach the color table — same field layout, verified against the real structs and
// against real game files, but none of the unrelated machinery.
//
// Lumina's own MtrlFile.ColorSetInfo is a fixed 512-byte (16 rows x 16 halfs) buffer — the
// pre-Dawntrail legacy format. Current-patch gear commonly uses the Dawntrail format (32 rows x
// 32 halfs / 64 bytes), which Lumina silently truncates/misreads. This handles both.
//
// The A/B-ramp + id-texture sampling in BakeDiffuse mirrors the shader logic reverse-engineered
// by the FFXIV modding community and reproduced in PassiveModding/MeddleTools' Blender addon
// (node_setup/node_mappings.py: PackedColorTableRampLookup, getOddEvenRows) — verified against
// that source, not guessed:
//   - 32 rows split into two 16-row ramps: even indices -> "ramp A", odd indices -> "ramp B".
//   - Each ramp's 16 stops sit at position i/16 (i = 0..15); id-texture Red (0-1) samples both
//     ramps with linear interpolation between neighboring stops.
//   - id-texture Green blends the two sampled colors: green=1 -> ramp A, green=0 -> ramp B.
//
// A "split-range + inverted byte" variant was tried and shipped briefly (0.0.0.77) based on one
// jacket material compared against a screenshot, but baking a SECOND real material (plain black
// boots with a small colored emblem) proved it wrong: the inverted version baked to solid,
// degenerate white (every pixel landing on the same near-white row), while this plain formula
// bakes a correct, detailed black boot with a distinct emblem patch. Reverted — the jacket's
// stray red/yellow patch was very likely real (unseen-from-camera) atlas data, not a formula bug.
// Lesson: verify against more than one material before trusting a "this looks more plausible"
// comparison on a single item.
using System;

namespace Penumbra.GameData.Files;

public static class MaterialColorTable
{
    private const int LegacyRows = 16;
    private const int LegacyRowSize = 32; // 4 vec4 x 2 bytes = matches Lumina's own legacy struct
    private const int DawntrailRows = 32;
    private const int DawntrailRowSize = 64; // 8 vec4 x 2 bytes

    /// <summary>Locate and read the raw diffuse RGB (0-1, per row) of a material's color table.
    /// Returns null if the material has no table / couldn't be read.</summary>
    public static float[][]? ReadRows(ReadOnlySpan<byte> data) => ReadRowsAndDyeFlags(data, out _);

    /// <summary>Like <see cref="ReadRows"/>, but also returns each row's Roughness (half at byte
    /// offset +32) and Metalness (half at byte offset +36) — verified against Penumbra.GameData's
    /// own ColorTableRow struct layout (Files/MaterialStructs/ColorTableRow.cs: Roughness = this[16],
    /// Metalness = this[18], each a 2-byte half, row stride 64 bytes for the Dawntrail format). Only
    /// meaningful for the 32-row Dawntrail format; null for legacy/unreadable materials.</summary>
    public static (float[][] Diffuse, float[] Roughness, float[] Metalness)? ReadRowsWithPbr(ReadOnlySpan<byte> data)
    {
        var diffuse = ReadRowsAndDyeFlags(data, out _);
        if (diffuse == null || diffuse.Length != DawntrailRows) return null;
        try
        {
            var pos = FindTableOffset(data, out var rowSize);
            if (pos < 0 || rowSize != DawntrailRowSize) return null;
            var roughness = new float[DawntrailRows];
            var metalness = new float[DawntrailRows];
            for (var row = 0; row < DawntrailRows; row++)
            {
                var o = pos + row * rowSize;
                var r = (float)BitConverter.ToHalf(data.Slice(o + 32, 2));
                var m = (float)BitConverter.ToHalf(data.Slice(o + 36, 2));
                roughness[row] = float.IsNaN(r) ? 0.5f : Math.Clamp(r, 0f, 1f);
                metalness[row] = float.IsNaN(m) ? 0f : Math.Clamp(m, 0f, 1f);
            }
            return (diffuse, roughness, metalness);
        }
        catch { return null; }
    }

    /// <summary>Re-locates the color table's byte offset within the material (same header parse as
    /// <see cref="ReadRowsAndDyeFlags"/>) without re-decoding the diffuse rows — used by
    /// <see cref="ReadRowsWithPbr"/> to reach the Roughness/Metalness scalars past byte 4.</summary>
    private static int FindTableOffset(ReadOnlySpan<byte> data, out int rowSize)
    {
        rowSize = 0;
        try
        {
            var pos = 0;
            pos += 4; pos += 2;
            var dataSetSize = BitConverter.ToUInt16(data.Slice(pos, 2)); pos += 2;
            var stringTableSize = BitConverter.ToUInt16(data.Slice(pos, 2)); pos += 2;
            pos += 2;
            var textureCount = data[pos++];
            var uvSetCount = data[pos++];
            var colorSetCount = data[pos++];
            var additionalDataSize = data[pos++];
            pos += (textureCount + uvSetCount + colorSetCount) * 4;
            pos += stringTableSize;
            var additionalData = data.Slice(pos, additionalDataSize);
            pos += additionalDataSize;
            var flags = additionalData.Length switch
            {
                0 => 0u,
                1 => additionalData[0],
                2 => (uint)(additionalData[0] | (additionalData[1] << 8)),
                _ => (uint)(additionalData[0] | (additionalData[1] << 8) | (additionalData[2] << 16) | (additionalData[3] << 24)),
            };
            if ((flags & 0x4u) == 0) return -1;
            var dimensionLogs = unchecked((byte)(flags >> 4));
            var (rows, size) = dimensionLogs switch
            {
                0x53 => (DawntrailRows, DawntrailRowSize),
                0 or 0x42 => (LegacyRows, LegacyRowSize),
                _ => (0, 0),
            };
            if (rows == 0) return -1;
            var tableSize = rows * size;
            if (tableSize > dataSetSize || pos + tableSize > data.Length) return -1;
            rowSize = size;
            return pos;
        }
        catch { return -1; }
    }

    /// <summary>Like <see cref="ReadRows"/>, but also returns which rows have their diffuse color
    /// actually affected by the player's chosen dye (Penumbra's advanced-dye editor shows this as
    /// a per-row "D" toggle — most materials leave most rows NOT dyeable, e.g. accent colors that
    /// must stay put regardless of dye). The dye-flag table (ColorDyeTableRow, 4 bytes/row) sits
    /// immediately after the color table within the same dataSetSize block — verified: a real
    /// material's dataSetSize (2176) was exactly DawntrailRows*DawntrailRowSize (2048) +
    /// DawntrailRows*4 (128). null dyeable means the flags couldn't be read (legacy 16-row format,
    /// or malformed) — caller should treat every row as dyeable in that case (old flat-tint behavior).</summary>
    public static float[][]? ReadRowsAndDyeFlags(ReadOnlySpan<byte> data, out bool[]? dyeable)
    {
        dyeable = null;
        try
        {
            var pos = 0;
            pos += 4; // version
            pos += 2; // file size (unused)
            var dataSetSize = BitConverter.ToUInt16(data.Slice(pos, 2)); pos += 2;
            var stringTableSize = BitConverter.ToUInt16(data.Slice(pos, 2)); pos += 2;
            pos += 2; // shader package name offset
            var textureCount = data[pos++];
            var uvSetCount = data[pos++];
            var colorSetCount = data[pos++];
            var additionalDataSize = data[pos++];

            pos += (textureCount + uvSetCount + colorSetCount) * 4; // offset(u16) + flags/index(u16) per entry
            pos += stringTableSize;
            var additionalData = data.Slice(pos, additionalDataSize);
            pos += additionalDataSize;

            var flags = additionalData.Length switch
            {
                0 => 0u,
                1 => additionalData[0],
                2 => (uint)(additionalData[0] | (additionalData[1] << 8)),
                _ => (uint)(additionalData[0] | (additionalData[1] << 8) | (additionalData[2] << 16) | (additionalData[3] << 24)),
            };
            var hasTable = (flags & 0x4u) != 0;
            if (!hasTable) return null;
            var dimensionLogs = unchecked((byte)(flags >> 4));

            var (rows, rowSize) = dimensionLogs switch
            {
                0x53 => (DawntrailRows, DawntrailRowSize),
                0 or 0x42 => (LegacyRows, LegacyRowSize),
                _ => (0, 0), // opaque/unknown table shape — not worth guessing
            };
            if (rows == 0) return null;

            var tableSize = rows * rowSize;
            if (tableSize > dataSetSize || pos + tableSize > data.Length) return null;
            var table = data.Slice(pos, tableSize);

            var result = new float[rows][];
            for (var row = 0; row < rows; row++)
            {
                var o = row * rowSize;
                var cr = (float)BitConverter.ToHalf(table.Slice(o + 0, 2));
                var cg = (float)BitConverter.ToHalf(table.Slice(o + 2, 2));
                var cb = (float)BitConverter.ToHalf(table.Slice(o + 4, 2));
                if (float.IsNaN(cr)) cr = 0; if (float.IsNaN(cg)) cg = 0; if (float.IsNaN(cb)) cb = 0;
                result[row] = new[] { Math.Clamp(cr, 0f, 1f), Math.Clamp(cg, 0f, 1f), Math.Clamp(cb, 0f, 1f) };
            }

            // dye table: rows * 4 bytes, right after the color table, only defined for Dawntrail.
            if (dimensionLogs == 0x53)
            {
                var dyeTableSize = rows * 4;
                if (pos + tableSize + dyeTableSize <= data.Length && tableSize + dyeTableSize <= dataSetSize)
                {
                    var dyeTable = data.Slice(pos + tableSize, dyeTableSize);
                    var dyeFlags = new bool[rows];
                    for (var row = 0; row < rows; row++)
                    {
                        var rowData = BitConverter.ToUInt32(dyeTable.Slice(row * 4, 4));
                        dyeFlags[row] = (rowData & 0x0001u) != 0; // ColorDyeTableRow.DiffuseColor, bit 0
                    }
                    dyeable = dyeFlags;
                }
            }
            return result;
        }
        catch
        {
            return null; // malformed/unexpected layout — caller falls back to a flat tint
        }
    }

    /// <summary>Average diffuse color (RGB, 0-1) across the material's color table rows, or null
    /// if the material has no table / couldn't be read. Rough — blends every material region
    /// (leather, accents, metal, ...) into one flat color; prefer <see cref="BakeDiffuse"/> when
    /// an id texture is available.</summary>
    public static float[]? AverageDiffuse(ReadOnlySpan<byte> data)
    {
        var rows = ReadRows(data);
        if (rows == null) return null;
        float r = 0, g = 0, b = 0;
        var n = 0;
        foreach (var row in rows)
        {
            if (row[0] + row[1] + row[2] < 0.02f) continue; // empty/black row
            r += row[0]; g += row[1]; b += row[2];
            n++;
        }
        // same squared-storage gamma trick as BakeDiffuse — sqrt after averaging, not per-row before
        // (averaging in squared space then sqrt-ing once matches how the ramp bake does it: lerp in
        // squared space, sqrt at the end).
        return n > 0 ? new[] { MathF.Sqrt(r / n), MathF.Sqrt(g / n), MathF.Sqrt(b / n) } : null;
    }

    /// <summary>Bake a real per-pixel diffuse texture from the material's color table using its id
    /// texture (Red picks the nearest row-pair 0-15, Green blends within that pair) — ported
    /// directly from Penumbra's own glTF exporter, see the method body for the citation.
    /// idTexRgba must be already-decoded RGBA8 bytes, idWidth*idHeight*4 long.
    /// When <paramref name="stainColor"/> is given, it's blended in ONLY where the underlying
    /// color-table row is actually flagged dyeable (Penumbra's advanced-dye editor shows this per
    /// row as a "D" toggle — most materials mark only some rows dyeable, e.g. accent/emblem colors
    /// stay put regardless of the player's chosen dye). This was the real reason a "dyed black"
    /// jacket kept showing its original red/orange design: we were multiplying the WHOLE texture
    /// by the stain instead of only the rows the game actually recolors.
    /// Output is the same resolution, RGBA8, alpha forced opaque. Returns null if the material has
    /// no (Dawntrail-shaped) color table.</summary>
    public static byte[]? BakeDiffuse(ReadOnlySpan<byte> mtrlData, ReadOnlySpan<byte> idTexRgba, int idWidth, int idHeight, float[]? stainColor = null)
    {
        var rows = ReadRowsAndDyeFlags(mtrlData, out var dyeable);
        if (rows == null || rows.Length != DawntrailRows) return null; // ramp split only defined for the 32-row format

        var outPixels = new byte[idWidth * idHeight * 4];
        for (var p = 0; p < idWidth * idHeight; p++)
        {
            var o = p * 4;
            // id textures are BC5 (2-channel) — Lumina's decoder packs the two channels into the
            // Green and Blue bytes of the output RGBA (Red is always 0), not Red/Green as the
            // shader docs describe for the raw channel layout. Verified against real files: with
            // Red/Green every material outside a lucky one baked to flat white; Blue/Green gives
            // distinct, plausible colors for every material tested.
            var red = idTexRgba[o + 2] / 255f;
            var green = idTexRgba[o + 1] / 255f;
            // ponytail: previous formula treated Red as a continuous position across a 16-stop ramp
            // (smoothly blending EVERY neighboring row pair into each other) — wrong. Ground truth:
            // Penumbra's own glTF exporter (Import/Models/Export/MaterialExporter.cs,
            // ProcessCharacterIndexOperation.Invoke) rounds Red to the NEAREST row-pair index
            // (0-15, no cross-pair blending at all) and only blends WITHIN that one pair using Green.
            // That's why ours came out as smeared, wrong-hued blotches instead of the game's sharp
            // per-region colors. Also applies PseudoSqrtRgb (sqrt) to the result — the color table
            // stores diffuse "squared" for a GPU gamma trick, same convention already used for
            // skin/hair in CustomizeColorsService.
            var pairIndex = Math.Clamp((int)MathF.Round(red * 15f), 0, 15);
            var prevIdx = pairIndex * 2;
            var nextIdx = Math.Min(pairIndex * 2 + 1, DawntrailRows - 1);
            var rowBlend = 1f - green;
            var prevRow = rows[prevIdx];
            var nextRow = rows[nextIdx];
            var r = MathF.Sqrt(Math.Clamp(prevRow[0] + (nextRow[0] - prevRow[0]) * rowBlend, 0f, 1f));
            var g = MathF.Sqrt(Math.Clamp(prevRow[1] + (nextRow[1] - prevRow[1]) * rowBlend, 0f, 1f));
            var b = MathF.Sqrt(Math.Clamp(prevRow[2] + (nextRow[2] - prevRow[2]) * rowBlend, 0f, 1f));

            if (stainColor is { Length: 3 })
            {
                // dye blend happens in display space (post-sqrt) — stainColor is a plain 0-255 UI
                // color from the Stain sheet, not the shader's squared gamma-trick space.
                var dPrev = dyeable != null && dyeable[prevIdx] ? 1f : 0f;
                var dNext = dyeable != null && dyeable[nextIdx] ? 1f : 0f;
                var dyeWeight = Math.Clamp(dPrev + (dNext - dPrev) * rowBlend, 0f, 1f);
                r += (stainColor[0] - r) * dyeWeight;
                g += (stainColor[1] - g) * dyeWeight;
                b += (stainColor[2] - b) * dyeWeight;
            }

            outPixels[o + 0] = (byte)(r * 255);
            outPixels[o + 1] = (byte)(g * 255);
            outPixels[o + 2] = (byte)(b * 255);
            outPixels[o + 3] = 255;
        }
        return outPixels;
    }

    /// <summary>Bake a glTF-convention metallicRoughness texture (G=roughness, B=metalness, R/A
    /// unused/opaque) using the same nearest-row-pair id-texture lookup as <see cref="BakeDiffuse"/>
    /// (see that method for the Penumbra citation) — armor with metal trim/buckles has real per-row
    /// Metalness/Roughness values in the color table that were never read before (every material
    /// rendered as flat metallicFactor=0, "nirgends einbezogen"). Returns null if no Dawntrail-shaped
    /// color table.</summary>
    public static byte[]? BakeMetallicRoughness(ReadOnlySpan<byte> mtrlData, ReadOnlySpan<byte> idTexRgba, int idWidth, int idHeight)
    {
        var pbr = ReadRowsWithPbr(mtrlData);
        if (pbr == null) return null;
        var (_, roughness, metalness) = pbr.Value;

        var outPixels = new byte[idWidth * idHeight * 4];
        for (var p = 0; p < idWidth * idHeight; p++)
        {
            var o = p * 4;
            var red = idTexRgba[o + 2] / 255f;
            var green = idTexRgba[o + 1] / 255f;
            var pairIndex = Math.Clamp((int)MathF.Round(red * 15f), 0, 15);
            var prevIdx = pairIndex * 2;
            var nextIdx = Math.Min(pairIndex * 2 + 1, DawntrailRows - 1);
            var rowBlend = 1f - green;
            var rough = Math.Clamp(roughness[prevIdx] + (roughness[nextIdx] - roughness[prevIdx]) * rowBlend, 0f, 1f);
            var metal = Math.Clamp(metalness[prevIdx] + (metalness[nextIdx] - metalness[prevIdx]) * rowBlend, 0f, 1f);
            outPixels[o + 0] = 255;
            outPixels[o + 1] = (byte)(rough * 255);
            outPixels[o + 2] = (byte)(metal * 255);
            outPixels[o + 3] = 255;
        }
        return outPixels;
    }
}
