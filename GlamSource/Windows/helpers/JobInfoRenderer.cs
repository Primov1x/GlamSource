using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace GlamSource.Windows.Helpers;

public class JobInfoRenderer
{
    private const float LabelWidth = 120f;

    public static void Render(Plugin plugin)
    {
        if (Plugin.ClientState.LocalPlayer == null)
        {
            ImGui.Text("Our local player is currently not logged in.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Current job:");
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);

        var playerState = Plugin.ClientState.LocalPlayer;
        ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
        ImGui.SameLine();
        ImGui.Text($" [Level {playerState.Level}]");
    }
}