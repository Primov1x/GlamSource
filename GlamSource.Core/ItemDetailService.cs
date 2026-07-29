using System;
using System.Collections.Generic;
using System.Linq;
using GlamSource.Core;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamSource.Core;

public record ItemDetail(
    uint ItemId,
    string Name,
    ushort ItemLevel,
    IReadOnlyList<ItemSourceDetail> Sources);

public record ItemSourceDetail(
    ItemSourceType Type,
    string Description,
    string? NpcName,
    string? ZoneName,
    float? MapX, float? MapY,
    uint? TerritoryTypeId,
    uint? MapId,
    IReadOnlyList<(uint itemId, string name, uint count)>? Costs,
    IReadOnlyList<(uint itemId, string name, uint count)>? Materials);

public interface IItemDetailService
{
    ItemDetail? GetDetail(uint itemId);
}

public sealed class ItemDetailService : IItemDetailService, IDisposable
{
    private readonly GameData _gameData;
    private readonly IItemSourceService _sourceService;
    private readonly Dictionary<uint, ItemDetail?> _cache = new();

    // Pre-resolved NPC name cache: npcId → npcName
    private readonly Dictionary<uint, string> _npcNameCache = new();

    // Recipe result → Recipe lookup
    private readonly Dictionary<uint, Recipe> _recipeByResult = new();

    // Item name cache
    private readonly Dictionary<uint, string> _itemNameCache = new();

    public ItemDetailService(GameData gameData, IItemSourceService sourceService)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _sourceService = sourceService ?? throw new ArgumentNullException(nameof(sourceService));

        BuildCaches();
    }

    public ItemDetail? GetDetail(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            _cache[itemId] = null;
            return null;
        }

        Item? foundItem = null;
        foreach (var i in itemSheet)
        {
            if (i.RowId == itemId)
            {
                foundItem = i;
                break;
            }
        }

        if (foundItem == null)
        {
            _cache[itemId] = null;
            return null;
        }

        var item = foundItem.Value;
        var name = item.Name.ToString()!;
        var itemLevel = item.LevelEquip;

        var sources = BuildSources(itemId, item);
        var detail = new ItemDetail(itemId, name, itemLevel, sources);

        _cache[itemId] = detail;
        return detail;
    }

    private IReadOnlyList<ItemSourceDetail> BuildSources(uint itemId, Item item)
    {
        var results = new List<ItemSourceDetail>();

        // 1. Crafted — from Recipe sheet
        if (_recipeByResult.TryGetValue(itemId, out var recipe))
        {
            var materials = recipe.Ingredient
                .Cast<dynamic>()
                .Where(r => r.RowId != 0)
                .Select(r => ((uint)r.RowId, GetItemName((uint)r.RowId) ?? "", (uint)1))
                .ToList();

            var jobName = recipe.CraftType.Value.Name.ToString();
            var requiredLevel = recipe.RequiredCraftsmanship;
            var desc = $"Crafted: {jobName} Lv.{requiredLevel}";

            results.Add(new ItemSourceDetail(
                ItemSourceType.Crafted,
                desc,
                null, null, null, null, null, null,
                null,
                materials));
        }

        // 2. GilShop — Vendor
        var gilShopSources = FindGilShopSources(itemId);
        results.AddRange(gilShopSources);

        // 3. SpecialShop — Vendor (tomestones etc.)
        var specialShopSources = FindSpecialShopSources(itemId);
        results.AddRange(specialShopSources);

        // 4. Quest — Quest Reward
        if (!results.Any(s => s.Type == ItemSourceType.Quest))
        {
            var questSheet = _gameData.GetExcelSheet<Quest>();
            if (questSheet != null)
            {
                foreach (var quest in questSheet)
                {
                    foreach (var reward in quest.Reward)
                    {
                        if (reward.RowId == itemId)
                        {
                            results.Add(new ItemSourceDetail(
                                ItemSourceType.Quest,
                                "Quest Reward",
                                null, null, null, null, null, null,
                                null, null));
                            break;
                        }
                    }
                    if (results.Any(s => s.Type == ItemSourceType.Quest))
                        break;
                }
            }
        }

        return results;
    }

    private IEnumerable<ItemSourceDetail> FindGilShopSources(uint itemId)
    {
        var gilShopItems = _gameData.GetSubrowExcelSheet<GilShopItem>();
        if (gilShopItems == null)
            return Enumerable.Empty<ItemSourceDetail>();

        foreach (var collection in gilShopItems)
        {
            foreach (var shopItem in collection)
            {
                if (shopItem.Item.RowId != itemId)
                    continue;

                var costs = new List<(uint, string, uint)>
                {
                    (0, "Gil", 100)
                };

                return new[] { new ItemSourceDetail(
                    ItemSourceType.Vendor,
                    "Vendor: Merchant",
                    null, null, null, null, null, null,
                    costs, null) };
            }
        }

        return Enumerable.Empty<ItemSourceDetail>();
    }

    private IEnumerable<ItemSourceDetail> FindSpecialShopSources(uint itemId)
    {
        var specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();

        foreach (var shop in specialShops)
        {
            var shopName = shop.Name.ToString();
            if (string.IsNullOrEmpty(shopName))
                continue;

            foreach (var itemStruct in shop.Item)
            {
                foreach (var receiveItem in itemStruct.ReceiveItems)
                {
                    if (receiveItem.Item.RowId == itemId)
                    {
                        var currencyName = GetItemName(receiveItem.Item.RowId) ?? "Unknown Currency";
                        var amount = receiveItem.ReceiveCount;
                        var costs = new List<(uint, string, uint)>
                        {
                            (receiveItem.Item.RowId, currencyName, amount)
                        };

                        return new[] { new ItemSourceDetail(
                            ItemSourceType.Vendor,
                            $"Shop: {shopName} ({amount} {currencyName})",
                            null, null, null, null, null, null,
                            costs, null) };
                    }
                }
            }
        }

        return Enumerable.Empty<ItemSourceDetail>();
    }

    private void BuildCaches()
    {
        // Item name cache
        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet != null)
        {
            foreach (var item in itemSheet)
            {
                var name = item.Name.ToString();
                if (!string.IsNullOrEmpty(name))
                    _itemNameCache[item.RowId] = name;
            }
        }

        // Recipe → result cache
        var recipeSheet = _gameData.GetExcelSheet<Recipe>();
        if (recipeSheet != null)
        {
            foreach (var recipe in recipeSheet)
            {
                if (recipe.ItemResult.RowId != 0)
                {
                    _recipeByResult[recipe.ItemResult.RowId] = recipe;
                }
            }
        }

        // NPC name cache (ENpcResident → singular name)
        var enpcResidentSheet = _gameData.GetExcelSheet<ENpcResident>();
        if (enpcResidentSheet != null)
        {
            foreach (var npc in enpcResidentSheet)
            {
                var name = npc.Singular.ToString();
                if (!string.IsNullOrEmpty(name))
                    _npcNameCache[npc.RowId] = name;
            }
        }
    }

    private string? GetItemName(uint itemId)
    {
        if (itemId == 0)
            return null;
        return _itemNameCache.TryGetValue(itemId, out var name) ? name : null;
    }

    public void Dispose()
    {
        // No unmanaged resources, but interface contract
    }
}
