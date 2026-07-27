namespace GlamSource.Core;

public class GlamourService : IGlamourService
{
    private readonly Dictionary<long, (long itemId, string source)> _itemSources = new();
    private readonly Dictionary<uint, (uint mountId, string source)> _mountSources = new();
    private readonly Dictionary<uint, string> _locationNames = new();

    public void RegisterItemSource(long itemId, long sourceItemId, string source)
    {
        _itemSources[itemId] = (sourceItemId, source);
    }

    public void RegisterMountSource(uint mountId, uint sourceMountId, string source)
    {
        _mountSources[mountId] = (sourceMountId, source);
    }

    public void RegisterLocationName(uint territoryId, string name)
    {
        _locationNames[territoryId] = name;
    }

    public string? GetLocationName(uint territoryId)
    {
        return _locationNames.TryGetValue(territoryId, out var name) ? name : null;
    }

    public string? GetItemSource(long itemId)
    {
        if (_itemSources.TryGetValue(itemId, out var entry))
            return entry.source;
        return null;
    }

    public string? GetMountSource(uint mountId)
    {
        if (_mountSources.TryGetValue(mountId, out var entry))
            return entry.source;
        return null;
    }

    public bool HasItemSource(long itemId) => _itemSources.ContainsKey(itemId);
    public bool HasMountSource(uint mountId) => _mountSources.ContainsKey(mountId);
}
