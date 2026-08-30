using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace GlamSource.Services.ModelExport;

/// <summary>Real live skin/hair color read from the character's shader constant buffer — replaces
/// the flat approximated tints for anyone whose customize colors actually differ from the average
/// we were guessing. Framework thread only.</summary>
public readonly record struct CustomizeColors(float[] Skin, float[] Hair, float[]? Highlight = null, float[]? Eye = null, string? DebugSource = null);

// ponytail: previous attempt went through FFXIVClientStructs' Human.CustomizeParameterCBuffer +
// ConstantBuffer.TryGetSourcePointer() (a "(Flags & 0x4003) == 0 ? ptr : null" gate) and kept
// returning implausible near-white/near-black values. Brio ships a WORKING skin/hair color editor
// reading the exact same underlying data (verified: same 0xBF0 field offset on Human, same 0x20
// SkinColor / MainColor("HairColor") struct offsets, same sqrt-for-display "Root()" convention —
// see Brio/Game/Actor/Interop/BrioHuman.cs + UI/Controls/Editors/AppearanceEditorCommon.cs) via a
// plain double pointer chase with NO flags gate at all. Mirrored here instead of guessing further.
public static unsafe class CustomizeColorsService
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct ShaderParams
    {
        [System.Runtime.InteropServices.FieldOffset(0x00)] public Vector3 SkinColor;
        [System.Runtime.InteropServices.FieldOffset(0x20)] public Vector3 HairColor;
    }

    /// <summary>Null if the object isn't a Human (some NPCs/monsters) or the pointer chain isn't ready yet.</summary>
    public static CustomizeColors? Capture(nint gameObjectAddress)
    {
        if (gameObjectAddress == 0) return null;
        var obj = (GameObject*)gameObjectAddress;
        var drawObject = obj->DrawObject;
        if (drawObject == null) return null;
        var human = (Human*)drawObject;

        // Human+0xBF0 -> ShaderManager* -> (+0x28) -> ShaderParams* -> SkinColor/HairColor.
        var shaderManager = *(nint*)((byte*)human + 0xBF0);
        if (shaderManager == 0) return null;
        var paramsPtr = *(ShaderParams**)((byte*)shaderManager + 0x28);
        if (paramsPtr == null) return null;
        var p = *paramsPtr;

        // SkinColor/HairColor are stored as "squared RGB" (GPU trick: square once, skip a pow()
        // per pixel at render time) — sqrt to get the actual 0-1 display color, matching Brio's
        // own Root()/Square() round-trip for its color picker.
        float[] Sqrt3(Vector3 v) => new[] { MathF.Sqrt(MathF.Max(0, v.X)), MathF.Sqrt(MathF.Max(0, v.Y)), MathF.Sqrt(MathF.Max(0, v.Z)) };
        var skin = Sqrt3(p.SkinColor);
        var hair = Sqrt3(p.HairColor);

        // Still guard against an obviously-unpopulated read (e.g. pointer chain valid but this
        // particular frame hasn't written real values yet) — pure white/black isn't a real
        // customize color, fall back to the flat approximation instead of trusting it.
        bool IsDegenerate(float[] c) => (c[0] > 0.99f && c[1] > 0.99f && c[2] > 0.99f) || (c[0] < 0.01f && c[1] < 0.01f && c[2] < 0.01f);
        if (IsDegenerate(skin) || IsDegenerate(hair)) return null;

        return new CustomizeColors(skin, hair);
    }
}
