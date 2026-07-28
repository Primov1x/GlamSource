using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using GlamSource.Core;

namespace GlamSource.Windows.Helpers;

public class MainWindowHelpers
{
    private const float LabelWidth = 120f;
    private readonly GameDataService _gameDataService;

    public MainWindowHelpers(GameDataService gameDataService)
    {
        _gameDataService = gameDataService;
    }

    public void RenderJobInfo()
    {
        if (Plugin.PlayerState == null)
        {
            ImGui.Text("Our local player is currently not logged in.");
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

    public void RenderLocationInfo()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        var locationName = _gameDataService.GetLocationName(territoryId);

        if (!string.IsNullOrEmpty(locationName))
        {
            ImGui.Text("Current location:");
            ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);
            ImGui.Text(locationName);
        }
        else
        {
            ImGui.Text("Invalid territory.");
        }
    }
}
