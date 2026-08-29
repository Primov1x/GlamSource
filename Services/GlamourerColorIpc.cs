using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace GlamSource.Services;

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

    /// <summary>Skin/hair diffuse color for the given object index, or null if unavailable/failed.</summary>
    public GlamSource.Services.ModelExport.CustomizeColors? GetColors(int objectIndex)
    {
        if (_getState == null) return null;
        try
        {
            var (ec, state) = _getState.Invoke(objectIndex);
            if (ec != GlamourerApiEc.Success || state == null) return null;

            var parameters = state["Parameters"];
            if (parameters == null) return null;

            var skin = ReadRgb(parameters["SkinDiffuse"]);
            var hair = ReadRgb(parameters["HairDiffuse"]);
            if (skin == null || hair == null) return null;

            return new GlamSource.Services.ModelExport.CustomizeColors(skin, hair);
        }
        catch { return null; }
    }

    private static float[]? ReadRgb(JToken? token)
    {
        if (token == null) return null;
        var r = token["Red"]?.Value<float>();
        var g = token["Green"]?.Value<float>();
        var b = token["Blue"]?.Value<float>();
        if (r == null || g == null || b == null) return null;
        return new[] { r.Value, g.Value, b.Value };
    }
}
