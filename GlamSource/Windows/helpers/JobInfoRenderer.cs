using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace GlamSource.Windows.Helpers;

public class JobInfoRenderer
{
    private const float LabelWidth = 120f;

    public static void Render()
    {
        if (Plugin.PlayerState == null)
        {
            ImGui.Text("Player state not available.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Current job:");
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);

        var playerState = Plugin.PlayerState;
        ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
        ImGui.SameLine();
        ImGui.Text($" [Level {playerState.Level}]");
    }
}