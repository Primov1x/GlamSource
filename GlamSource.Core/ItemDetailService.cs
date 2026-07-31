using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GlamSource.Core;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamSource.Core;

public record ItemDetail(
    uint ItemId,
    string Name,
    ushort ItemLevel,
    bool IsMarketable,
    uint IconId,
    IReadOnlyList<ItemSourceDetail> Sources);

public record CostEntry(
    uint ItemId,
    string Name,
    uint Count,
    uint IconId);

public record ItemSourceDetail(
    ItemSourceType Type,
    string Description,
    string? NpcName,
    string? ZoneName,
    float? MapX, float? MapY,
    uint? TerritoryTypeId,
    uint? MapId,
    IReadOnlyList<CostEntry>? Costs,
    IReadOnlyList<CostEntry>? Materials,
    string? QuestName,
    uint? CfcRowId,
    string? CfcName,
    string? CfcType,
    string? BossName,
    uint? QuestForUnlock);

public interface IItemDetailService
{
    ItemDetail? GetDetail(uint itemId);
}

public sealed class ItemDetailService : IItemDetailService
{
    private readonly GameData _gameData;
    private readonly Dictionary<uint, ItemDetail?> _cache = new();

    private readonly Dictionary<uint, string> _npcNameCache = new();
    private readonly Dictionary<uint, List<NpcLocationInfo>> _shopNpcLookup = new();
    private readonly Dictionary<uint, List<Recipe>> _recipeByResult = new();

    private record NpcLocationInfo(
        string NpcName, string ZoneName,
        float MapX, float MapY,
        uint TerritoryTypeId, uint MapId);
    private readonly Dictionary<uint, string> _itemNameCache = new();

    // ponytail: ItemId → List<ContentFinderCondition RowId> from LuminaSupplemental DungeonDrop
    private readonly Dictionary<uint, List<uint>> _itemToDutyMap = new();

    // ponytail: CostItemId → (bossName, cfcName, cfcRowId) from "Totem Gear (X)" shop name
    private readonly Dictionary<uint, (string bossName, string? cfcName, uint? cfcRowId)> _totemCostToBoss = new();



    public ItemDetailService(GameData gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));

        BuildCaches();
        BuildDutyDropCache();
    }

    public ItemDetail? GetDetail(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
        {
            _cache[itemId] = null;
            return null;
        }
        var name = item.Name.ToString()!;
        var itemLevel = item.LevelEquip;
        var isMarketable = item.ItemSearchCategory.RowId > 0;
        var iconId = item.Icon;

        var sources = BuildSources(itemId, item);
        var detail = new ItemDetail(itemId, name, itemLevel, isMarketable, iconId, sources);

        _cache[itemId] = detail;
        return detail;
    }

    private IReadOnlyList<ItemSourceDetail> BuildSources(uint itemId, Item item)
    {
        var results = new List<ItemSourceDetail>();

        // 1. Crafted — from Recipe sheet (grouped by material set)
        if (_recipeByResult.TryGetValue(itemId, out var recipes))
        {
            var materialGroups = recipes.GroupBy(r => GetMaterialKey(r));
            foreach (var group in materialGroups)
            {
                var jobNames = group.Select(r => GetClassJobAbbreviation(r.CraftType.RowId)).Distinct();
                var jobList = string.Join(", ", jobNames);
                var levelTableRowId = group.First().RecipeLevelTable.RowId;
                var desc = $"Crafted Lv.{levelTableRowId} ({jobList})";

                var ingredientArray = group.First().Ingredient.Cast<dynamic>().ToArray();
                var amountArray = group.First().AmountIngredient.Cast<dynamic>().ToArray();

                var materials = new List<CostEntry>();
                for (int i = 0; i < ingredientArray.Length; i++)
                {
                    var ing = ingredientArray[i];
                    var ingredientId = (uint)ing.RowId;
                    if (ingredientId == 0)
                        continue;

                    uint amount = 1;
                    if (i < amountArray.Length)
                    {
                        amount = (uint)amountArray[i];
                    }

                    if (amount > 0)
                    {
                        materials.Add(new CostEntry(ingredientId, GetItemName(ingredientId) ?? "", amount, GetItemIconId(ingredientId)));
                    }
                }

                results.Add(new ItemSourceDetail(
                    ItemSourceType.Crafted,
                    desc,
                    null, null, null, null, null, null,
                    null,
                    materials,
                    null, null, null, null, null, null));
            }
        }

        // 2. GilShop — Vendor
        var gilShopSources = FindGilShopSources(itemId);
        results.AddRange(gilShopSources);

        // 3. SpecialShop — Vendor (tomestones etc.)
        var specialShopSources = FindSpecialShopSources(itemId);
        results.AddRange(specialShopSources);

        // 4. Duty Drop from LuminaSupplemental DungeonDrop
        if (results.Count == 0 && _itemToDutyMap.TryGetValue(itemId, out var dutyCfcIds))
        {
            var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();
            foreach (var cfcId in dutyCfcIds)
            {
                if (cfcSheet != null && cfcSheet.TryGetRow(cfcId, out var cfc))
                {
                    var cfcName = cfc.Name.ToString();
                    var cfcType = cfc.TrialRoulette ? "Trial" : "Dungeon";
                    results.Add(new ItemSourceDetail(
                        ItemSourceType.Dungeon,
                        $"Duty Drop: {cfcName}",
                        null, null, null, null, null, null, null, null,
                        null, cfc.RowId, cfcName, cfcType, null, null));
                }
            }
        }

        // 4b. CostItem → Totem boss name from "Totem Gear (X)" shop name
        if (results.Count == 0 && _totemCostToBoss.TryGetValue(itemId, out var totemInfo))
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Trial,
                $"Trial Drop: {totemInfo.bossName} (Extreme)",
                null, null, null, null, null, null, null, null,
                null, totemInfo.cfcRowId, totemInfo.cfcName, "Trial",
                totemInfo.bossName, null));
        }

        // 5. Quest — Quest Reward
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
                            var questName = quest.Name.ToString() ?? "Unknown Quest";
                            results.Add(new ItemSourceDetail(
                                ItemSourceType.Quest,
                                $"Quest Reward: {questName}",
                                null, null, null, null, null, null,
                                null, null,
                                questName, null, null, null, null, null));
                            break;
                        }
                    }
                    if (results.Any(s => s.Type == ItemSourceType.Quest))
                        break;
                }
            }
        }

        // 6. Generic fallback — nothing found
        if (results.Count == 0)
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                "No vendor/crafting source found. May drop from duties, raids, or other content.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        }

        return results;
    }

    private IEnumerable<ItemSourceDetail> FindGilShopSources(uint itemId)
    {
        var gilShopItems = _gameData.GetSubrowExcelSheet<GilShopItem>();
        if (gilShopItems == null)
            return Enumerable.Empty<ItemSourceDetail>();

        uint price = 100;
        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet != null)
        {
            foreach (var it in itemSheet)
            {
                if (it.RowId == itemId)
                {
                    price = it.PriceMid;
                    break;
                }
            }
        }

        var costs = new List<CostEntry>
        {
            new CostEntry(0, "Gil", price, 800)
        };

        var allSources = new List<ItemSourceDetail>();
        var seenShopIds = new HashSet<uint>();

        foreach (var collection in gilShopItems)
        {
            foreach (var shopItem in collection)
            {
                if (shopItem.Item.RowId != itemId)
                    continue;

                var shopId = collection.RowId;
                if (!seenShopIds.Add(shopId))
                    continue;

                var npcInfos = _shopNpcLookup.GetValueOrDefault(shopId);

                if (npcInfos != null)
                {
                    foreach (var npc in npcInfos)
                    {
                        var desc = $"Vendor: {npc.NpcName}";
                        allSources.Add(new ItemSourceDetail(
                            ItemSourceType.Vendor,
                            desc,
                            npc.NpcName, npc.ZoneName,
                            npc.MapX, npc.MapY,
                            npc.TerritoryTypeId, npc.MapId,
                            costs, null, null, null, null, null, null, null));
                    }
                }
                else
                {
                    allSources.Add(new ItemSourceDetail(
                        ItemSourceType.Vendor,
                        "Vendor: Merchant",
                        null, null, null, null, null, null,
                        costs, null, null, null, null, null, null, null));
                }
            }
        }

        return allSources;
    }

    private static readonly Dictionary<uint, uint> GilCurrencyMap = new()
    {
        [1] = 28,       // Gil
        [2] = 33913,    // Allagan Tomestone of Mnemonics
        [3] = 33912,    // Allagan Tomestone of Warriorhood
        [4] = 33914,    // Allagan Tomestone of Hyleo
        [5] = 33915,    // Allagan Tomestone of Hesperos
        [6] = 41784,    // Allagan Tomestone of Aesthetics
        [7] = 41785,    // Allagan Tomestone of Pallady
    };

    private uint ResolveSpecialShopCostItem(uint costRowId, uint useCurrencyType)
    {
        if (costRowId == 0 || costRowId >= 8)
            return costRowId;

        return useCurrencyType switch
        {
            16 => GilCurrencyMap.TryGetValue(costRowId, out var mapped) ? mapped : costRowId,
            8 => 1,
            4 => ResolveTomestoneCostItem(costRowId),
            _ => costRowId,
        };
    }

    private uint ResolveTomestoneCostItem(uint costRowId)
    {
        var tomestonesSheet = _gameData.GetExcelSheet<TomestonesItem>();
        if (tomestonesSheet == null)
            return costRowId;

        foreach (var t in tomestonesSheet)
        {
            if (t.Tomestones.RowId == costRowId && t.Item.RowId != 0)
                return t.Item.RowId;
        }

        return costRowId;
    }

    private IEnumerable<ItemSourceDetail> FindSpecialShopSources(uint itemId)
    {
        var specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        var allSources = new List<ItemSourceDetail>();
        var seenShopIds = new HashSet<uint>();

        foreach (var shop in specialShops)
        {
            var shopId = shop.RowId;
            if (!seenShopIds.Add(shopId))
                continue;

            var shopName = shop.Name.ToString();
            if (string.IsNullOrEmpty(shopName))
                continue;

            foreach (var itemStruct in shop.Item)
            {
                foreach (var receiveItem in itemStruct.ReceiveItems)
                {
                    if (receiveItem.Item.RowId == itemId)
                    {
                        var currencyItemIds = new List<CostEntry>();

                        foreach (var cost in itemStruct.ItemCosts)
                        {
                            if (cost.ItemCost.RowId == 0)
                                continue;

                            var resolvedId = ResolveSpecialShopCostItem(cost.ItemCost.RowId, shop.UseCurrencyType);
                            var resolvedName = GetItemName(resolvedId) ?? "Unknown";

                            currencyItemIds.Add(new CostEntry(
                                resolvedId,
                                resolvedName,
                                (uint)cost.CurrencyCost,
                                GetItemIconId(resolvedId)));
                        }

                        if (currencyItemIds.Count == 0)
                        {
                            var currencyName = GetItemName(receiveItem.Item.RowId) ?? "Unknown Currency";
                            var amount = receiveItem.ReceiveCount;
                            currencyItemIds.Add(new CostEntry(receiveItem.Item.RowId, currencyName, amount, GetItemIconId(receiveItem.Item.RowId)));
                        }

                        var questName = GetQuestName(shop.Quest.RowId);

                        allSources.Add(new ItemSourceDetail(
                            ItemSourceType.Vendor,
                            $"Shop: {shopName}",
                            null, null, null, null, null, null,
                            currencyItemIds, null,
                            questName, null, null, null, null, null));
                        break;
                    }
                }
            }
        }

        return allSources;
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
                    if (!_recipeByResult.ContainsKey(recipe.ItemResult.RowId))
                        _recipeByResult[recipe.ItemResult.RowId] = new();
                    _recipeByResult[recipe.ItemResult.RowId].Add(recipe);
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

        // Shop → NPC/Zone reverse map
        BuildShopNpcCache();
    }

    private void BuildShopNpcCache()
    {
        var enpcBaseSheet = _gameData.GetExcelSheet<ENpcBase>();
        var levelSheet = _gameData.GetExcelSheet<Level>();
        var mapSheet = _gameData.GetExcelSheet<Map>();

        if (enpcBaseSheet == null || levelSheet == null || mapSheet == null)
            return;

        // Level lookup: ENpcBase.RowId → (MapId, Map, X, Z)
        // Note: FFXIV uses X/Z for map coordinates (Z = Y on 2D map)
        var npcLevelLookup = new Dictionary<uint, (uint mapId, Map map, float x, float z)>();
        foreach (var level in levelSheet)
        {
            if (level.Type != 8 || level.Object.RowId == 0)
                continue;

            var mapId = level.Map.RowId;
            if (mapId != 0)
            {
                npcLevelLookup[level.Object.RowId] = (mapId, level.Map.Value, level.X, level.Z);
            }
        }
        // ENpcBase → Shop RowIds
        foreach (var npcBase in enpcBaseSheet)
        {
            var npcId = npcBase.RowId;
            if (!_npcNameCache.TryGetValue(npcId, out var npcName) || string.IsNullOrEmpty(npcName))
                continue;

            if (!npcLevelLookup.TryGetValue(npcId, out var levelInfo))
                continue;

            var zoneName = levelInfo.map.PlaceName.ValueNullable?.Name.ToString() ?? "";
            var territoryTypeId = levelInfo.map.TerritoryType.RowId;
            var mapId = levelInfo.mapId;
            var map = levelInfo.map;
            var mapX = ToMapCoordinate(levelInfo.x, map.SizeFactor, map.OffsetX);
            var mapY = ToMapCoordinate(levelInfo.z, map.SizeFactor, map.OffsetY);

            foreach (var dataRef in npcBase.ENpcData)
            {
                if (dataRef.RowId == 0)
                    continue;

                var shopId = dataRef.RowId;
                if (!_shopNpcLookup.ContainsKey(shopId))
                    _shopNpcLookup[shopId] = new();
                _shopNpcLookup[shopId].Add(new NpcLocationInfo(
                    npcName, zoneName, mapX, mapY, territoryTypeId, mapId));
            }
        }
    }

    private void BuildDutyDropCache()
    {
        var dungeonDrops = CsvLoader.LoadResource<DungeonDrop>(
            CsvLoader.DungeonDropItemResourceName,
            includesHeaders: true,
            out _,
            out _,
            gameData: null);

        foreach (var drop in dungeonDrops)
        {
            if (drop.ItemId == 0 || drop.ContentFinderConditionId == 0)
                continue;

            if (!_itemToDutyMap.ContainsKey(drop.ItemId))
                _itemToDutyMap[drop.ItemId] = new();

            if (!_itemToDutyMap[drop.ItemId].Contains(drop.ContentFinderConditionId))
                _itemToDutyMap[drop.ItemId].Add(drop.ContentFinderConditionId);
        }

        var bossDrops = CsvLoader.LoadResource<DungeonBossDrop>(
            CsvLoader.DungeonBossDropResourceName,
            includesHeaders: true,
            out _,
            out _,
            gameData: null);

        foreach (var drop in bossDrops)
        {
            if (drop.ItemId == 0 || drop.ContentFinderConditionId == 0)
                continue;

            if (!_itemToDutyMap.ContainsKey(drop.ItemId))
                _itemToDutyMap[drop.ItemId] = new();

            if (!_itemToDutyMap[drop.ItemId].Contains(drop.ContentFinderConditionId))
                _itemToDutyMap[drop.ItemId].Add(drop.ContentFinderConditionId);
        }

        BuildTotemLookupCache();
    }

    private static readonly Regex _totemBossRegex = new(@"\((.+)\)");

    private void BuildTotemLookupCache()
    {
        var specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();

        foreach (var shop in specialShops)
        {
            var shopName = shop.Name.ToString();
            if (string.IsNullOrEmpty(shopName))
                continue;

            foreach (var itemStruct in shop.Item)
            {
                foreach (var cost in itemStruct.ItemCosts)
                {
                    if (cost.ItemCost.RowId <= 19)
                        continue;

                    if (shopName.Contains("Totem Gear"))
                    {
                        var match = _totemBossRegex.Match(shopName);
                        if (match.Success)
                        {
                            var bossName = match.Groups[1].Value;
                            var cfcMatch = MatchCfcForBoss(bossName, cfcSheet);
                            _totemCostToBoss[cost.ItemCost.RowId] = (bossName, cfcMatch?.Name.ToString() ?? null, cfcMatch?.RowId);
                        }
                    }
                }
            }
        }
    }

    private ContentFinderCondition? MatchCfcForBoss(string bossName, ExcelSheet<ContentFinderCondition>? cfcSheet)
    {
        if (cfcSheet == null)
            return null;

        foreach (var cfc in cfcSheet)
        {
            if (cfc.RowId == 0)
                continue;
            var cfcName = cfc.Name.ToString();
            if (string.IsNullOrEmpty(cfcName))
                continue;

            if (cfcName.Contains(bossName) && (cfcName.Contains("Extreme") || cfcName.Contains("Minstrel")))
                return cfc;
        }
        return null;
    }

    private static float ToMapCoordinate(float raw, ushort sizeFactor, short offset)
    {
        var scale = sizeFactor / 100.0f;
        return (raw / 1000.0f * scale) + (41.0f / scale) / 2.0f + 1.0f;
    }

    private string? GetItemName(uint itemId)
    {
        if (itemId == 0)
            return null;
        return _itemNameCache.TryGetValue(itemId, out var name) ? name : null;
    }

    private uint GetItemIconId(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
            return 0;

        return item.Icon;
    }

    private string? GetQuestName(uint questId)
    {
        if (questId == 0)
            return null;

        var questSheet = _gameData.GetExcelSheet<Quest>();
        if (questSheet == null || !questSheet.TryGetRow(questId, out var quest))
            return null;

        return quest.Name.ToString();
    }

    // ponytail: single lookup, Lumina TryGetRow instead of foreach
    private (string? name, string? type)? GetCfcInfo(uint cfcId)
    {
        if (cfcId == 0)
            return null;

        var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null)
            return null;

        if (cfcSheet.TryGetRow(cfcId, out var cfc))
            return (cfc.Name.ToString(), cfc.TrialRoulette ? "Trial" : "Dungeon");

        return null;
    }

    private string GetClassJobAbbreviation(uint craftTypeRowId)
    {
        var classJobId = craftTypeRowId + 8;
        var classJobSheet = _gameData.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            var cj = classJobSheet.GetRow(classJobId);
            var abbr = cj.Abbreviation.ToString();
            if (!string.IsNullOrEmpty(abbr))
                return abbr;
        }
        return "???";
    }

    private string GetMaterialKey(Recipe recipe)
    {
        var ingredientArray = recipe.Ingredient.Cast<dynamic>().ToArray();
        var amountArray = recipe.AmountIngredient.Cast<dynamic>().ToArray();

        var parts = new List<(uint id, uint amount)>();
        for (int i = 0; i < ingredientArray.Length; i++)
        {
            var ing = ingredientArray[i];
            var ingredientId = (uint)ing.RowId;
            if (ingredientId == 0)
                continue;

            uint amount = 1;
            if (i < amountArray.Length)
            {
                amount = (uint)amountArray[i];
            }

            if (amount > 0)
            {
                parts.Add((ingredientId, amount));
            }
        }

        parts.Sort((a, b) => a.id.CompareTo(b.id));
        return string.Join(",", parts.Select(p => $"{p.id}:{p.amount}"));
    }

}

