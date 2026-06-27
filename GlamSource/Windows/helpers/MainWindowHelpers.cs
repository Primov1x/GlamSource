using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Lumina.Excel;
using System.Numerics;

namespace GlamSource.Windows.Helpers;

public static class MainWindowHelpers
{
    private const float LabelWidth = 120f;

    public static void RenderJobInfo(Plugin plugin)
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

    public static void RenderLocationInfo(Plugin plugin)
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text("Current location:");
            ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);
            ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
        }
        else
        {
            ImGui.Text("Invalid territory.");
        }
    }
}