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
        return n > 0 ? new[] { r / n, g / n, b / n } : null;
    }

    /// <summary>Bake a real per-pixel diffuse texture from the material's color table using its id
    /// texture (Red = ramp position, Green = ramp-A/ramp-B blend) — see file header for the
    /// verified formula. idTexRgba must be already-decoded RGBA8 bytes, idWidth*idHeight*4 long.
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

        var rampA = new float[16][]; // even rows
        var rampB = new float[16][]; // odd rows
        var dyeA = new float[16];    // 1 if that ramp stop's row is dyeable, else 0
        var dyeB = new float[16];
        for (var i = 0; i < 16; i++)
        {
            rampA[i] = rows[i * 2]; rampB[i] = rows[i * 2 + 1];
            dyeA[i] = dyeable != null && dyeable[i * 2] ? 1f : 0f;
            dyeB[i] = dyeable != null && dyeable[i * 2 + 1] ? 1f : 0f;
        }

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
            var (i0, i1, frac) = RampPosition(red);
            var colorA = LerpRow(rampA[i0], rampA[i1], frac);
            var colorB = LerpRow(rampB[i0], rampB[i1], frac);
            var r = Math.Clamp(colorB[0] + (colorA[0] - colorB[0]) * green, 0f, 1f);
            var g = Math.Clamp(colorB[1] + (colorA[1] - colorB[1]) * green, 0f, 1f);
            var b = Math.Clamp(colorB[2] + (colorA[2] - colorB[2]) * green, 0f, 1f);

            if (stainColor is { Length: 3 })
            {
                // dye weight interpolated the same way as color — a pixel landing between a
                // dyeable and non-dyeable row gets a partial blend, not a hard edge.
                var dA = dyeA[i0] + (dyeA[i1] - dyeA[i0]) * frac;
                var dB = dyeB[i0] + (dyeB[i1] - dyeB[i0]) * frac;
                var dyeWeight = Math.Clamp(dB + (dA - dB) * green, 0f, 1f);
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

    /// <summary>Ramp position for a 16-stop ramp (stops at i/16) at t in [0,1] — the two
    /// neighboring stop indices and the interpolation fraction between them.</summary>
    private static (int I0, int I1, float Frac) RampPosition(float t)
    {
        var pos = Math.Clamp(t, 0f, 1f) * 16f; // stop i sits at t = i/16
        var i0 = Math.Clamp((int)MathF.Floor(pos), 0, 15);
        var i1 = Math.Clamp(i0 + 1, 0, 15);
        var frac = Math.Clamp(pos - i0, 0f, 1f);
        return (i0, i1, frac);
    }

    private static float[] LerpRow(float[] a, float[] b, float frac)
        => new[] { a[0] + (b[0] - a[0]) * frac, a[1] + (b[1] - a[1]) * frac, a[2] + (b[2] - a[2]) * frac };
}
