using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace GlamSource.Services;

/// <summary>Skin/hair color from Glamourer's state, each null unless Glamourer's own "Apply" flag
/// for that parameter is set — meaning the player actually overrode it via Advanced Customization,
/// not just Glamourer's placeholder for "leave as-is". Reading these unconditionally the first time
/// (ignoring Apply) is why the IPC path produced the same implausible near-white/near-black values
/// as our own shader-buffer read: Apply=false rows carry a meaningless placeholder, not the live
/// color — the swatch-index-based human.cmp read (<see cref="ModelExport.CmpColorReader"/>) is the
/// correct source for those.</summary>
public readonly record struct GlamourerAppliedColors(float[]? Skin, float[]? Hair);

/// <summary>
/// Reads skin/hair diffuse color straight from Glamourer's own state (IPC), instead of GlamSource's
/// own memory read of the CustomizeParameter shader buffer (which produced implausible near-white/
/// near-black values three times running — see ModelExport/CustomizeColors.cs).
/// JSON shape confirmed against Glamourer's own serializer (Designs/DesignBase.cs, SerializeParameters):
/// state.Parameters.SkinDiffuse.{Red,Green,Blue} / state.Parameters.HairDiffuse.{Red,Green,Blue},
/// already linear 0..1 floats — no sqrt/square math needed on our end.
/// </summary>
public class GlamourerColorIpc
{
    private readonly GetState? _getState;

    public GlamourerColorIpc(IDalamudPluginInterface pi)
    {
        try { _getState = new GetState(pi); }
        catch { _getState = null; }
    }

    /// <summary>True if Glamourer exposes its IPC (i.e. installed &amp; loaded).</summary>
    public bool IsAvailable
    {
        get
        {
            if (_getState == null) return false;
            try { return _getState.Valid; }
            catch { return false; }
        }
    }

    /// <summary>Skin/hair diffuse color for the given object index — each field null unless
    /// Glamourer's own "Apply" flag says the player actually overrode it. Null on IPC failure.</summary>
    public GlamourerAppliedColors? GetColors(int objectIndex)
    {
        if (_getState == null) return null;
        try
        {
            var (ec, state) = _getState.Invoke(objectIndex);
            if (ec != GlamourerApiEc.Success || state == null) return null;

            var parameters = state["Parameters"];
            if (parameters == null) return null;

            var skin = ReadRgbIfApplied(parameters["SkinDiffuse"]);
            var hair = ReadRgbIfApplied(parameters["HairDiffuse"]);
            return new GlamourerAppliedColors(skin, hair);
        }
        catch { return null; }
    }

    private static float[]? ReadRgbIfApplied(JToken? token)
    {
        if (token == null) return null;
        if (token["Apply"]?.Value<bool>() != true) return null;
        var r = token["Red"]?.Value<float>();
        var g = token["Green"]?.Value<float>();
        var b = token["Blue"]?.Value<float>();
        if (r == null || g == null || b == null) return null;
        return new[] { r.Value, g.Value, b.Value };
    }

    /// <summary>Raw Customize array bytes (Clan/Gender/SkinColor/HairColor) straight from
    /// Glamourer's own tracked state, not our own memory read of IPlayerCharacter.Customize.
    /// Needed because the two diverged for a real character: Glamourer actively manages/reapplies
    /// appearance for characters it has a design/state on, and its own tracked byte can differ from
    /// what Dalamud's live read reports at any given moment — Glamourer's own color picker (see
    /// Gui/Customization/CustomizationDrawer.Color.cs: "{_customize[index].Value}") shows exactly
    /// this value, so it's what the player actually sees. Null on failure/unavailable.</summary>
    public (byte Clan, byte Gender, byte SkinColor, byte HairColor)? GetCustomizeBytes(int objectIndex)
    {
        if (_getState == null) return null;
        try
        {
            var (ec, state) = _getState.Invoke(objectIndex);
            if (ec != GlamourerApiEc.Success || state == null) return null;

            var customize = state["Customize"];
            if (customize == null) return null;

            byte? Value(string key) => customize[key]?["Value"]?.Value<byte>();
            var clan = Value("Clan");
            var gender = Value("Gender");
            var skin = Value("SkinColor");
            var hair = Value("HairColor");
            if (clan == null || gender == null || skin == null || hair == null) return null;
            return (clan.Value, gender.Value, skin.Value, hair.Value);
        }
        catch { return null; }
    }
}
