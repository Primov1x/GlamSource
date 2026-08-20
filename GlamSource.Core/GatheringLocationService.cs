using Lumina;
using Lumina.Excel.Sheets;

namespace GlamSource.Core;

/// <summary>
/// One gather point (Mining/Botany node) where an item can be collected.
/// </summary>
public record GatheringLocation(
    uint TerritoryId,
    string? TerritoryName,
    string GatheringTypeName,
    int GatheringLevel,
    float MapX,
    float MapY);

public interface IGatheringLocationService
{
    /// <summary>
    /// Resolves any ItemId to its gathering (Mining/Botany) locations.
    /// Empty list = item has no gathering node (e.g. only Vendor/Craft/Duty/Market sources).
    /// Works for arbitrary ItemIds, not just a pre-filtered gatherable subset.
    /// </summary>
    IReadOnlyList<GatheringLocation> GetLocations(uint itemId);

    /// <summary>
    /// Returns the aetheryte RowId that lands the player in <paramref name="territoryId"/>,
    /// or null if no aetheryte is registered for that zone.
    /// </summary>
    uint? GetAetheryteFor(uint territoryId);
}

/// <summary>
/// Minimal ItemId -&gt; GatherPoint lookup for Mining/Botany nodes.
/// ponytail: Fishing spots use a structurally different sheet (FishingSpot/SpearfishingItem),
/// out of scope for this phase — add a parallel lookup there if fishing is needed later.
/// </summary>
public sealed class GatheringLocationService : IGatheringLocationService
{
    private readonly Dictionary<uint, List<GatheringLocation>> _byItemId = new();
    private readonly Dictionary<uint, uint> _aetheryteByTerritoryId = new();

    public GatheringLocationService(GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        // ponytail: one aetheryte per territory (pick the first IsAetheryte hit).
        // Enough for "get me into that zone", not for optimal walk distance.
        foreach (var aetheryte in gameData.GetExcelSheet<Aetheryte>() ?? Enumerable.Empty<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte)
                continue;
            var territoryId = aetheryte.Territory.RowId;
            if (territoryId == 0)
                continue;
            _aetheryteByTerritoryId.TryAdd(territoryId, aetheryte.RowId);
        }

        var gatheringItems = gameData.GetExcelSheet<GatheringItem>();
        var gatheringTypes = gameData.GetExcelSheet<GatheringType>();
        var territories = gameData.GetExcelSheet<TerritoryType>();
        var coords = gameData.GetExcelSheet<ExportedGatheringPoint>();
        if (gatheringItems == null)
            return;

        // GatheringItem.RowId ("gathering id") -> real game ItemId
        var gatheringIdToItemId = new Dictionary<uint, uint>();
        foreach (var gi in gatheringItems)
        {
            if (gi.Item.RowId != 0)
                gatheringIdToItemId[gi.RowId] = gi.Item.RowId;
        }

        // GatheringPointBase.RowId -> GatheringPoint rows placing it in the world
        var pointsByBaseId = new Dictionary<uint, List<GatheringPoint>>();
        foreach (var point in gameData.GetExcelSheet<GatheringPoint>() ?? Enumerable.Empty<GatheringPoint>())
        {
            var baseId = point.GatheringPointBase.RowId;
            if (!pointsByBaseId.TryGetValue(baseId, out var list))
                pointsByBaseId[baseId] = list = new List<GatheringPoint>();
            list.Add(point);
        }

        foreach (var baseNode in gameData.GetExcelSheet<GatheringPointBase>() ?? Enumerable.Empty<GatheringPointBase>())
        {
            var typeName = gatheringTypes?.GetRowOrDefault(baseNode.GatheringType.RowId)?.Name.ToString() ?? "Unknown";
            var coordRow = coords?.GetRowOrDefault(baseNode.RowId);

            if (!pointsByBaseId.TryGetValue(baseNode.RowId, out var points))
                continue;

            foreach (var itemLink in baseNode.Item)
            {
                if (!gatheringIdToItemId.TryGetValue(itemLink.RowId, out var itemId))
                    continue;

                foreach (var point in points)
                {
                    var territoryId = point.TerritoryType.RowId;
                    var territoryName = territories?.GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ToString();

                    var location = new GatheringLocation(
                        TerritoryId: territoryId,
                        TerritoryName: territoryName,
                        GatheringTypeName: typeName,
                        GatheringLevel: baseNode.GatheringLevel,
                        MapX: coordRow?.X ?? 0f,
                        MapY: coordRow?.Y ?? 0f);

                    if (!_byItemId.TryGetValue(itemId, out var list))
                        _byItemId[itemId] = list = new List<GatheringLocation>();
                    list.Add(location);
                }
            }
        }
    }

    public IReadOnlyList<GatheringLocation> GetLocations(uint itemId)
        => _byItemId.TryGetValue(itemId, out var list) ? list : Array.Empty<GatheringLocation>();

    public uint? GetAetheryteFor(uint territoryId)
        => _aetheryteByTerritoryId.TryGetValue(territoryId, out var id) ? id : null;
}
