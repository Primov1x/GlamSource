using System.Numerics;

namespace GlamSource.Core;

public static class HexColor
{
    /// <summary>
    /// Parses a <c>#RRGGBB</c> string into a <see cref="Vector4"/> with R, G, B in [0,1] and A=1.
    /// </summary>
    /// <exception cref="FormatException">The input is not a valid 6-digit hex color.</exception>
    public static Vector4 Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new FormatException("Hex color string must not be null or empty.");

        hex = hex.Trim();

        if (hex.StartsWith("#"))
            hex = hex[1..];

        if (hex.Length != 6 || !hex.IsHexDigit())
            throw new FormatException($"Invalid hex color format: '{hex}'. Expected #RRGGBB.");

        return new Vector4(
            byte.Parse(hex[0..2], System.Globalization.NumberStyles.HexNumber) / 255f,
            byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber) / 255f,
            byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber) / 255f,
            1f);
    }

    /// <summary>
    /// Converts a <see cref="Vector4"/> (R,G,B in [0,1], A ignored) back to a <c>#RRGGBB</c> string.
    /// </summary>
    public static string ToString(Vector4 color)
    {
        byte r = ClampByte(color.X);
        byte g = ClampByte(color.Y);
        byte b = ClampByte(color.Z);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static byte ClampByte(float value) =>
        (byte)Math.Clamp(value * 255f, 0f, 255f);

    private static bool IsHexDigit(this string s) =>
        s.All(c => "0123456789ABCDEFabcdef".IndexOf(c) >= 0);
}
