using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace GlamSource.Windows.Helpers;

public class JobInfoRenderer
{
    private const float LabelWidth = 120f;

    public static void Render(Plugin plugin)
    {
        if (plugin.ClientState.LocalPlayer == null)
        {
            ImGui.Text("Our local player is currently not logged in.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Current job:");
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);

        var playerState = plugin.ClientState.LocalPlayer;
        var jobIconId = 62100 + playerState.ClassJob.RowId;
        var iconTexture = plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrEmpty();
        ImGui.Image(iconTexture.Handle, new Vector2(28, 28) * ImGuiHelpers.GlobalScale);

        ImGui.SameLine();
        ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
        ImGui.SameLine();
        ImGui.Text($" [Level {playerState.Level}]");
    }
}