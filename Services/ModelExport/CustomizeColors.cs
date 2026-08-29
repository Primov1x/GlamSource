using System;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Shader;

namespace GlamSource.Services.ModelExport;

/// <summary>Real live skin/hair color read from the character's shader constant buffer — replaces
/// the flat approximated tints for anyone whose customize colors actually differ from the average
/// we were guessing. Framework thread only.</summary>
public readonly record struct CustomizeColors(float[] Skin, float[] Hair);

public static unsafe class CustomizeColorsService
{
    /// <summary>Null if the object isn't a Human (some NPCs/monsters) or the buffer isn't ready yet.</summary>
    public static CustomizeColors? Capture(nint gameObjectAddress)
    {
        if (gameObjectAddress == 0) return null;
        var obj = (GameObject*)gameObjectAddress;
        var drawObject = obj->DrawObject;
        if (drawObject == null) return null;
        var human = (Human*)drawObject;
        var cbuf = human->CustomizeParameterCBuffer;
        if (cbuf == null) return null;

        // ponytail: ConstantBufferPointer<T>.TryGetBuffer() has an inverted null check upstream in
        // FFXIVClientStructs (would null-deref instead of returning empty) — call the underlying
        // ConstantBuffer method directly with our own correct null check instead.
        var span = cbuf->TryGetBuffer<CustomizeParameter>();
        if (span.Length == 0) return null;
        var p = span[0];

        // ponytail: SkinColor/MainColor are stored as "squared RGB" (a common shader trick — the
        // GPU squares the display color once and stores that, skipping a pow() per pixel at
        // render time) — sqrt to get back the actual 0-1 display color.
        float[] Sqrt3(System.Numerics.Vector3 v) => new[] { MathF.Sqrt(MathF.Max(0, v.X)), MathF.Sqrt(MathF.Max(0, v.Y)), MathF.Sqrt(MathF.Max(0, v.Z)) };
        var skin = new[] { MathF.Sqrt(MathF.Max(0, p.SkinColor.X)), MathF.Sqrt(MathF.Max(0, p.SkinColor.Y)), MathF.Sqrt(MathF.Max(0, p.SkinColor.Z)) };
        var hair = Sqrt3(p.MainColor);

        // ponytail: reported result was flat white skin/hair (ears, untextured face overlays) —
        // a near-(1,1,1) or near-(0,0,0) read means the cbuffer wasn't actually populated yet (or
        // we read it at the wrong moment/offset), not a real customize color. No real character
        // has literally pure white or pure black skin/hair; reject and let the caller fall back
        // to the flat approximation instead of trusting garbage.
        bool IsDegenerate(float[] c) => (c[0] > 0.97f && c[1] > 0.97f && c[2] > 0.97f) || (c[0] < 0.02f && c[1] < 0.02f && c[2] < 0.02f);
        if (IsDegenerate(skin) || IsDegenerate(hair)) return null;

        return new CustomizeColors(skin, hair);
    }
}
