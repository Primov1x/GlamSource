using System;
using System.Collections.Generic;
using System.Linq;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamSource.Core;

public sealed class LuminaItemSourceService : IItemSourceService
{
    private readonly GameData _gameData;
    private readonly Dictionary<uint, IReadOnlyList<ItemSource>> _cache = new();
    private readonly Recipe[] _recipes;
    private readonly SubrowExcelSheet<GilShopItem>? _gilShopItems;
    private readonly SpecialShop[] _specialShops;
    private readonly Quest[] _quests;

    public LuminaItemSourceService(GameData gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));

        _recipes = _gameData.GetExcelSheet<Recipe>()?.ToArray() ?? Array.Empty<Recipe>();
        _gilShopItems = _gameData.GetSubrowExcelSheet<GilShopItem>();
        _specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        _quests = _gameData.GetExcelSheet<Quest>()?.ToArray() ?? Array.Empty<Quest>();
    }

    public IReadOnlyList<ItemSource> GetSources(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        var sources = new List<ItemSource>();

        // 1. Recipe — Crafted
        foreach (var recipe in _recipes)
        {
            if (recipe.ItemResult.RowId == itemId)
            {
                sources.Add(new ItemSource(ItemSourceType.Crafted, "Crafted"));
                break;
            }
        }

        // 2. GilShop — Vendor (via subrow sheet)
        if (!sources.Any(s => s.Type == ItemSourceType.Vendor) && _gilShopItems != null)
        {
            foreach (var collection in _gilShopItems)
            {
                foreach (var shopItem in collection)
                {
                    if (shopItem.Item.RowId == itemId)
                    {
                        sources.Add(new ItemSource(ItemSourceType.Vendor, "Vendor"));
                        break;
                    }
                }
                if (sources.Any(s => s.Type == ItemSourceType.Vendor))
                    break;
            }
        }

        // 3. SpecialShop — Vendor (Tomestones etc.)
        if (!sources.Any(s => s.Type == ItemSourceType.Vendor))
        {
            foreach (var shop in _specialShops)
            {
                var shopName = shop.Name.ToString();
                foreach (var itemStruct in shop.Item)
                {
                    foreach (var receiveItem in itemStruct.ReceiveItems)
                    {
                        if (receiveItem.Item.RowId == itemId)
                        {
                            sources.Add(new ItemSource(ItemSourceType.Vendor, $"Shop: {shopName}"));
                            break;
                        }
                    }
                    if (sources.Any(s => s.Type == ItemSourceType.Vendor))
                        break;
                }
                if (sources.Any(s => s.Type == ItemSourceType.Vendor))
                    break;
            }
        }

        // 4. Quest — Quest Reward
        if (!sources.Any(s => s.Type == ItemSourceType.Quest))
        {
            foreach (var quest in _quests)
            {
                foreach (var reward in quest.Reward)
                {
                    if (reward.RowId == itemId)
                    {
                        sources.Add(new ItemSource(ItemSourceType.Quest, "Quest"));
                        break;
                    }
                }
                if (sources.Any(s => s.Type == ItemSourceType.Quest))
                    break;
            }
        }

        var result = sources.AsReadOnly();
        _cache[itemId] = result;
        return result;
    }
}
