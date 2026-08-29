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
// by the FFXIV modding community (xivmodding.com's Material Colorsets (Dawntrail) page) and
// reproduced in PassiveModding/MeddleTools' Blender addon (node_setup/node_mappings.py:
// PackedColorTableRampLookup, getOddEvenRows) for the ramp-A/ramp-B split itself:
//   - 32 rows split into two 16-row ramps: even indices -> "ramp A", odd indices -> "ramp B".
//   - id-texture Green blends the two sampled colors: green=1 -> ramp A, green=0 -> ramp B.
// The exact byte->row mapping (RowPosition) is NOT what either of those sources describes
// directly — it was found by baking a real material and comparing side by side against an
// in-game screenshot (see RowPosition's own doc comment): the byte is inverted (high byte =
// near-black rows, not high-index rows) and split-range, not a plain byte/255*16.
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
    public static float[][]? ReadRows(ReadOnlySpan<byte> data)
    {
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
    /// Output is the same resolution, RGBA8, alpha forced opaque. Returns null if the material has
    /// no (Dawntrail-shaped) color table.</summary>
    public static byte[]? BakeDiffuse(ReadOnlySpan<byte> mtrlData, ReadOnlySpan<byte> idTexRgba, int idWidth, int idHeight)
    {
        var rows = ReadRows(mtrlData);
        if (rows == null || rows.Length != DawntrailRows) return null; // ramp split only defined for the 32-row format

        var rampA = new float[16][]; // even rows
        var rampB = new float[16][]; // odd rows
        for (var i = 0; i < 16; i++) { rampA[i] = rows[i * 2]; rampB[i] = rows[i * 2 + 1]; }

        var outPixels = new byte[idWidth * idHeight * 4];
        for (var p = 0; p < idWidth * idHeight; p++)
        {
            var o = p * 4;
            // id textures are BC5 (2-channel) — Lumina's decoder packs the two channels into the
            // Green and Blue bytes of the output RGBA (Red is always 0), not Red/Green as the
            // shader docs describe for the raw channel layout. Blue/Green gives distinct, plausible
            // per-material colors where Red/Green never did (see file history for how that was
            // verified). Green is the ramp-A/ramp-B blend directly (green=1 -> A, green=0 -> B, per
            // xivmodding.com's Material Colorsets (Dawntrail) page).
            var redByte = idTexRgba[o + 2];
            var green = idTexRgba[o + 1] / 255f;
            var pos = RowPosition(redByte);
            var colorA = SampleRamp(rampA, pos);
            var colorB = SampleRamp(rampB, pos);
            outPixels[o + 0] = (byte)(Math.Clamp(colorB[0] + (colorA[0] - colorB[0]) * green, 0f, 1f) * 255);
            outPixels[o + 1] = (byte)(Math.Clamp(colorB[1] + (colorA[1] - colorB[1]) * green, 0f, 1f) * 255);
            outPixels[o + 2] = (byte)(Math.Clamp(colorB[2] + (colorA[2] - colorB[2]) * green, 0f, 1f) * 255);
            outPixels[o + 3] = 255;
        }
        return outPixels;
    }

    /// <summary>Byte -> continuous ramp-pair position (0..15, 16 pairs). Two things the naive
    /// byte/255*16 mapping got wrong, both verified against a real baked material compared side by
    /// side with an in-game screenshot (a red/yellow bake where the real garment is black/orange):
    /// (1) the byte value is INVERTED — high byte = LOW pair index (near-black rows), not high;
    /// (2) xivmodding.com's Dawntrail colorset page documents the byte range as split, not
    /// continuous: 0x00-0x77 selects pairs 1-8, 0x88-0xFF selects pairs 9-16, with a dead zone
    /// between (0x78-0x87) that isn't meant to land on any pair cleanly.</summary>
    private static float RowPosition(byte redByte)
    {
        var b = (byte)(255 - redByte);
        if (b <= 0x77) return b / 119f * 7f;
        if (b >= 0x88) return 8f + (b - 136) / (255f - 136f) * 7f;
        return 7.5f;
    }

    /// <summary>Interpolate a 16-stop ramp at a continuous pair position (0..15).</summary>
    private static float[] SampleRamp(float[][] ramp, float pos)
    {
        var i0 = Math.Clamp((int)MathF.Floor(pos), 0, 15);
        var i1 = Math.Clamp(i0 + 1, 0, 15);
        var frac = Math.Clamp(pos - i0, 0f, 1f);
        var a = ramp[i0];
        var b = ramp[i1];
        return new[] { a[0] + (b[0] - a[0]) * frac, a[1] + (b[1] - a[1]) * frac, a[2] + (b[2] - a[2]) * frac };
    }
}
