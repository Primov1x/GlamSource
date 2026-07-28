using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamSource;

public class GameDataService
{
    private readonly IDataManager _dataManager;

    public GameDataService(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public string? GetLocationName(uint territoryId)
    {
        var sheet = _dataManager.GetExcelSheet<TerritoryType>();
        if (sheet?.TryGetRow(territoryId, out var territory) == true)
        {
            return territory.PlaceName.Value.Name.ToString();
        }

        return null;
    }
}
