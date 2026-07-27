using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using GlamSource.Core;

namespace GlamSource;

public class GameDataService : IGlamourService
{
    private readonly IDataManager _dataManager;
    private readonly GlamourService _coreService;

    public GameDataService(IDataManager dataManager)
    {
        _dataManager = dataManager;
        _coreService = new GlamourService();
    }

    public string? GetLocationName(uint territoryId)
    {
        if (_coreService.GetLocationName(territoryId) is { } cached)
            return cached;

        var sheet = _dataManager.GetExcelSheet<TerritoryType>();
        if (sheet?.TryGetRow(territoryId, out var territory) == true)
        {
            var name = territory.PlaceName.Value.Name.ToString();
            _coreService.RegisterLocationName(territoryId, name);
            return name;
        }

        return null;
    }

    public string? GetItemSource(long itemId)
    {
        return _coreService.GetItemSource(itemId);
    }

    public string? GetMountSource(uint mountId)
    {
        return _coreService.GetMountSource(mountId);
    }

    public void RegisterItemSource(long itemId, long sourceItemId, string source)
    {
        _coreService.RegisterItemSource(itemId, sourceItemId, source);
    }

    public void RegisterMountSource(uint mountId, uint sourceMountId, string source)
    {
        _coreService.RegisterMountSource(mountId, sourceMountId, source);
    }

    public void RegisterLocationName(uint territoryId, string name)
    {
        _coreService.RegisterLocationName(territoryId, name);
    }
}
