using System.Numerics;
using GlamSource.Core;
using Xunit;

namespace GlamSource.Core.Tests;

public class HexColorTests
{
    [Theory]
    [InlineData("#FF0000", 1f, 0f, 0f)]
    [InlineData("#00FF00", 0f, 1f, 0f)]
    [InlineData("#0000FF", 0f, 0f, 1f)]
    [InlineData("#FFFFFF", 1f, 1f, 1f)]
    [InlineData("#000000", 0f, 0f, 0f)]
    [InlineData("#808080", 0.5019608f, 0.5019608f, 0.5019608f)]
    [InlineData("#ff0000", 1f, 0f, 0f)]
    public void Parse_ValidHex_ReturnsCorrectVector4(string hex, float r, float g, float b)
    {
        var result = HexColor.Parse(hex);
        Assert.Equal(r, result.X, 5);
        Assert.Equal(g, result.Y, 5);
        Assert.Equal(b, result.Z, 5);
        Assert.Equal(1f, result.W, 5);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("")]
    public void Parse_InvalidHex_ThrowsFormatException(string? hex)
    {
        Assert.Throws<FormatException>(() => HexColor.Parse(hex));
    }

    [Fact]
    public void ToString_Red_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(1f, 0f, 0f, 1f));
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void ToString_Green_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(0f, 1f, 0f, 1f));
        Assert.Equal("#00FF00", result);
    }

    [Fact]
    public void ToString_Blue_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(0f, 0f, 1f, 1f));
        Assert.Equal("#0000FF", result);
    }

    [Fact]
    public void ToString_White_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(1f, 1f, 1f, 1f));
        Assert.Equal("#FFFFFF", result);
    }

    [Fact]
    public void ToString_Black_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(0f, 0f, 0f, 1f));
        Assert.Equal("#000000", result);
    }

    [Fact]
    public void ToString_ClampHigh_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(1.5f, 0f, 0f, 1f));
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void ToString_ClampLow_ReturnsCorrectHex()
    {
        var result = HexColor.ToString(new Vector4(-0.5f, 2f, 0f, 1f));
        Assert.Equal("#00FF00", result);
    }

    [Fact]
    public void Roundtrip_ParseAndToString_MatchesOriginal()
    {
        var original = "#A52A2B";
        var parsed = HexColor.Parse(original);
        var roundtripped = HexColor.ToString(parsed);
        Assert.Equal(original.ToUpperInvariant(), roundtripped);
    }

    [Fact]
    public void Roundtrip_FromVector4ToParse_MatchesOriginal()
    {
        var original = new Vector4(1f / 3f, 2f / 3f, 0.5f, 1f);
        var hex = HexColor.ToString(original);
        var parsed = HexColor.Parse(hex);
        var roundtripped = HexColor.ToString(parsed);
        Assert.Equal(hex, roundtripped);
    }
}
