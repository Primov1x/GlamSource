// Purpose-built, minimal re-implementation of the relevant slice of Penumbra.GameData's
// Files/MtrlFile.cs + Files/MaterialStructs/{ColorTable,LegacyColorTable,TableFlags}.cs
// (https://github.com/xivdev/Penumbra, AGPL-3.0 — same license as GlamSource).
//
// We only need one number per material: the average diffuse color across its color-table rows,
// as a fallback tint for meshes with no diffuse texture. Vendoring the full MtrlFile class
// (read+write, dye application, shader packages, ~1700 lines across 9 files) would pull in a
// sibling "Luna" project we don't have locally. This reads just far enough into the raw .mtrl
// bytes to reach the color table and average it — same field layout, verified against the real
// structs, but none of the unrelated machinery.
//
// Lumina's own MtrlFile.ColorSetInfo is a fixed 512-byte (16 rows x 16 halfs) buffer — the
// pre-Dawntrail legacy format. Current-patch gear commonly uses the Dawntrail format (32 rows x
// 32 halfs / 64 bytes), which Lumina silently truncates/misreads. This handles both.
using System;

namespace Penumbra.GameData.Files;

public static class MaterialColorTable
{
    private const int LegacyRows = 16;
    private const int LegacyRowSize = 32; // 4 vec4 x 2 bytes = matches Lumina's own legacy struct
    private const int DawntrailRows = 32;
    private const int DawntrailRowSize = 64; // 8 vec4 x 2 bytes

    /// <summary>Average diffuse color (RGB, 0-1) across the material's color table rows, or null
    /// if the material has no table / couldn't be read.</summary>
    public static float[]? AverageDiffuse(ReadOnlySpan<byte> data)
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

            float r = 0, g = 0, b = 0;
            var n = 0;
            for (var row = 0; row < rows; row++)
            {
                var o = row * rowSize;
                var cr = (float)BitConverter.ToHalf(table.Slice(o + 0, 2));
                var cg = (float)BitConverter.ToHalf(table.Slice(o + 2, 2));
                var cb = (float)BitConverter.ToHalf(table.Slice(o + 4, 2));
                if (float.IsNaN(cr) || float.IsNaN(cg) || float.IsNaN(cb)) continue;
                if (cr + cg + cb < 0.02f) continue; // empty/black row
                r += Math.Clamp(cr, 0f, 1f); g += Math.Clamp(cg, 0f, 1f); b += Math.Clamp(cb, 0f, 1f);
                n++;
            }
            return n > 0 ? new[] { r / n, g / n, b / n } : null;
        }
        catch
        {
            return null; // malformed/unexpected layout — caller falls back to a flat tint
        }
    }
}
