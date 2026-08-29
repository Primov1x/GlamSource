using System;

namespace GlamSource.Services.ModelExport;

// ponytail: game normal maps are BC5 (2-channel) — same format as the id textures we already
// bake (see MaterialColorTable header comment for the verified Lumina decode-order quirk: the two
// real BC5 channels land in decoded bytes 1 (G) and 2 (B), byte 0 is always 0, not the R/G the
// community docs describe). X/Y = those two channels remapped -1..1; Z is reconstructed
// (sqrt(1-x²-y²), standard tangent-space normal map convention) since BC5 only stores X/Y.
// Without this the exporter had no normal map at all — flat MeshStandardMaterial shading, which
// is most of why the model looked "flat"/detail-less (missing knee/muscle definition etc.) even
// though the diffuse color and geometry were already correct.
public static class NormalMapDecoder
{
    public static byte[] DecodeFromBc5(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var n = width * height;
        var outPixels = new byte[n * 4];
        for (var p = 0; p < n; p++)
        {
            var o = p * 4;
            var x = rgba[o + 2] / 255f * 2f - 1f;
            var y = rgba[o + 1] / 255f * 2f - 1f;
            var z = MathF.Sqrt(Math.Max(0f, 1f - x * x - y * y));
            outPixels[o + 0] = (byte)Math.Clamp((x * 0.5f + 0.5f) * 255f, 0f, 255f);
            outPixels[o + 1] = (byte)Math.Clamp((y * 0.5f + 0.5f) * 255f, 0f, 255f);
            outPixels[o + 2] = (byte)Math.Clamp((z * 0.5f + 0.5f) * 255f, 0f, 255f);
            outPixels[o + 3] = 255;
        }
        return outPixels;
    }
}
