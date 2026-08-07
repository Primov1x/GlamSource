using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GlamSource.Core;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
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
    uint? QuestForUnlock,
    IReadOnlyList<uint>? CfcRowIds,
    uint? SourceItemId = null);

public interface IItemDetailService
{
    ItemDetail? GetDetail(uint itemId);
    GameData GameData { get; }
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

    // ponytail: ItemId → List<FateId> from LuminaSupplemental FateItem
    private readonly Dictionary<uint, List<uint>> _itemToFateMap = new();

    // ponytail: ItemId → List<BNpcNameId> from LuminaSupplemental MobDrop
    private readonly Dictionary<uint, List<uint>> _itemToMobMap = new();

    // ponytail: ItemId → List<ENpcResidentId> from LuminaSupplemental HouseVendor (hv.ParentId = ItemId)
    private readonly Dictionary<uint, List<uint>> _shopToNpcIds = new();

    // ponytail: CostItemId → (bossName, cfcName, cfcRowId) from "Totem Gear (X)" shop name
    private readonly Dictionary<uint, (string bossName, string? cfcName, uint? cfcRowId, uint? questId)> _totemCostToBoss = new();

    // ponytail: ItemId → List<ItemSupplement> (Loot/Desynth/Reduction sources)
    private Dictionary<uint, List<ItemSupplement>> _itemSupplementCache = new();

    // ponytail: ItemId → List<CofferItemId> from ItemSupplement source=Coffer
    private readonly Dictionary<uint, List<uint>> _itemToCofferMap = new();

    // ponytail: ItemId → List<(FieldOpType, FieldOpCofferType)> from FieldOpCoffer
    private readonly Dictionary<uint, List<(FieldOpType Type, FieldOpCofferType CofferType)>> _itemToFieldOpCofferMap = new();

    // ponytail: ItemId → List<Achievement RowId> from SpecialShop.ItemStruct.AchievementUnlock
    private readonly Dictionary<uint, List<uint>> _itemToAchievementMap = new();

    // Name-only fallback for NPCs with no location data
    private readonly Dictionary<uint, string> _shopNpcNameOnly = new();

    public ItemDetailService(GameData gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));

        BuildCaches();
        BuildDutyDropCache();
    }

    public GameData GameData => _gameData;

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
                var crafterLevel = group.First().RecipeLevelTable.Value.ClassJobLevel;
                var desc = $"Crafted Lv.{crafterLevel} ({jobList})";

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
                    null, null, null, null, null, null, null));
            }
        }

        // 2. GilShop — Vendor
        var gilShopSources = FindGilShopSources(itemId);
        results.AddRange(gilShopSources);

        // 3. SpecialShop — Vendor (tomestones etc.)
        var specialShopSources = FindSpecialShopSources(itemId);
        results.AddRange(specialShopSources);

        // 4. Duty Drop from LuminaSupplemental (DungeonDrop + BossDrop + BossChest)
        if (results.Count == 0 && _itemToDutyMap.TryGetValue(itemId, out var dutyCfcIds))
        {
            var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();
            var cfcNames = new List<(string name, string dutyType, ItemSourceType sourceType, uint rowId)>();
            foreach (var cfcId in dutyCfcIds)
            {
                if (cfcSheet != null && cfcSheet.TryGetRow(cfcId, out var cfc))
                {
                    var cfcName = cfc.Name.ToString();
                    var contentTypeId = cfc.ContentType.RowId;
                    var dutyType = GetDutyType(cfcId);
                    var sourceType = contentTypeId switch
                    {
                        4 => ItemSourceType.Trial,
                        5 => ItemSourceType.Raid,
                        28 => ItemSourceType.Raid,
                        _ => ItemSourceType.Dungeon
                    };
                    cfcNames.Add((cfcName, dutyType, sourceType, cfc.RowId));
                }
            }
            if (cfcNames.Count > 0)
            {
                var cfcRowIds = cfcNames.Select(c => c.rowId).ToList();
                foreach (var (name, dutyType, sourceType, rowId) in cfcNames)
                {
                    results.Add(new ItemSourceDetail(
                        sourceType,
                        $"{dutyType} Drop: {name}",
                        null, null, null, null, null, null, null, null,
                        null, rowId, name, dutyType, null, null, cfcRowIds));
                }
            }
        }

        // 4b. CostItem → Totem boss name from "Totem Gear (X)" shop name
        if (results.Count == 0 && _totemCostToBoss.TryGetValue(itemId, out var totemInfo))
        {
            var dutyType = totemInfo.cfcRowId.HasValue ? GetDutyType(totemInfo.cfcRowId.Value) : "Trial";
            var sourceType = dutyType == "Trial" ? ItemSourceType.Trial : ItemSourceType.Dungeon;
            results.Add(new ItemSourceDetail(
                sourceType,
                $"{dutyType} Drop: {totemInfo.bossName} (Extreme)",
                null, null, null, null, null, null, null, null,
                null, totemInfo.cfcRowId, totemInfo.cfcName, dutyType,
                totemInfo.bossName, totemInfo.questId, null));
        }

        // 4c. Exchange token → Savage/Trial shop classification
        if (results.Count == 0 && _exchangeCostToShopCfcs.TryGetValue(itemId, out var exchangeInfo))
        {
            var (exchangeType, shopName, cfcRowIds) = exchangeInfo;
            var displayType = exchangeType == "Savage" ? "Raid" : "Trial";
            var desc = exchangeType == "Savage"
                ? cfcRowIds.Count > 0 && cfcRowIds[0] > 0
                    ? $"{GetDutyType(cfcRowIds[0])} Drop: {shopName}"
                    : $"Raid Drop: {shopName}"
                : $"Trial Drop: {shopName}";
            var sourceType = exchangeType == "Savage" ? ItemSourceType.Raid : ItemSourceType.Trial;
            results.Add(new ItemSourceDetail(
                sourceType,
                desc,
                null, null, null, null, null, null, null, null,
                null, cfcRowIds.Count > 0 ? cfcRowIds[0] : null, null, displayType,
                null, null, cfcRowIds));
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

                            string? questNpcName = null;
                            string? questZoneName = null;
                            float? questMapX = null, questMapY = null;
                            uint? questTerritoryId = null, questMapId = null;

                            var issuerStart = quest.IssuerStart;
                            if (issuerStart.RowId > 0)
                            {
                                var resident = _gameData.GetExcelSheet<ENpcResident>()?.GetRow(issuerStart.RowId);
                                questNpcName = resident?.Singular.ToString();
                            }

                            var issuerLocation = quest.IssuerLocation;
                            if (issuerLocation.RowId > 0)
                            {
                                var level = issuerLocation.ValueNullable;
                                if (level != null)
                                {
                                    var map = level.Value.Map.ValueNullable;
                                    if (map != null)
                                    {
                                        questMapX = ToMapCoordinate(level.Value.X, map.Value.SizeFactor, map.Value.OffsetX);
                                        questMapY = ToMapCoordinate(level.Value.Z, map.Value.SizeFactor, map.Value.OffsetY);
                                        questZoneName = map.Value.PlaceName.ValueNullable?.Name.ToString();
                                        questTerritoryId = map.Value.TerritoryType.RowId;
                                        questMapId = map.Value.RowId;
                                    }
                                }
                            }

                            results.Add(new ItemSourceDetail(
                                ItemSourceType.Quest,
                                $"Quest Reward: {questName}",
                                questNpcName, questZoneName,
                                questMapX, questMapY,
                                questTerritoryId, questMapId,
                                null, null,
                                questName, null, null, null, null, quest.RowId, null));
                            break;
                        }
                    }
                    if (results.Any(s => s.Type == ItemSourceType.Quest))
                        break;
                }
            }
        }

        // 5b. Fate drops
        if (results.Count == 0 && _itemToFateMap.TryGetValue(itemId, out var fateIds))
        {
            var fateSheet = _gameData.GetExcelSheet<Fate>();
            foreach (var fateId in fateIds)
            {
                var fateRow = fateSheet?.GetRow(fateId);
                var fateName = fateRow?.Name.ToString();
                if (!string.IsNullOrEmpty(fateName))
                {
                    results.Add(new ItemSourceDetail(
                        ItemSourceType.Fate,
                        $"Fate Drop: {fateName}",
                        null, null, null, null, null, null,
                        null, null, null, null, null, null, null, null, null));
                }
            }
        }

        // 5c. Mob drops
        if (results.Count == 0 && _itemToMobMap.TryGetValue(itemId, out var mobIds))
        {
            var npcSheet = _gameData.GetExcelSheet<BNpcName>();
            foreach (var npcId in mobIds)
            {
                var npcRow = npcSheet?.GetRow(npcId);
                var npcName = npcRow?.Singular.ToString();
                if (!string.IsNullOrEmpty(npcName))
                {
                    results.Add(new ItemSourceDetail(
                        ItemSourceType.Mob,
                        $"Mob Drop: {npcName}",
                        null, null, null, null, null, null,
                        null, null, null, null, null, null, null, null, null));
                }
            }
        }

        // 5d. House Vendor — parent ID is the item, ENpcResidentId is the NPC
        if (results.Count == 0 && _shopToNpcIds.TryGetValue(itemId, out var hvNpcs) && hvNpcs.Count > 0)
        {
            var npcName = _npcNameCache.GetValueOrDefault(hvNpcs[0]);
            results.Add(new ItemSourceDetail(
                ItemSourceType.Vendor,
                $"House Vendor: {npcName ?? "Unknown"}",
                npcName, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 6. ItemSupplement (Loot, Gardening, PoD, etc. — NOT Desynth/Reduction)
        if (_itemSupplementCache.TryGetValue(itemId, out var supplements))
        {
            var relevant = supplements
                .Where(s => s.ItemSupplementSource != ItemSupplementSource.Desynth
                         && s.ItemSupplementSource != ItemSupplementSource.Reduction)
                .ToList();
            foreach (var supp in relevant)
            {
                var sourceItemName = GetItemName(supp.SourceItemId) ?? "Unknown";
                var desc = supp.ItemSupplementSource switch
                {
                    ItemSupplementSource.Loot => $"Obtained from: {sourceItemName}",
                    _ => $"{supp.ItemSupplementSource}: {sourceItemName}"
                };
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other, desc,
                    null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null, null, supp.SourceItemId));
            }
        }

        // 6b. ItemSupplement Coffer — coffer items and their contents
        if (!results.Any(s => s.Type == ItemSourceType.Coffer) && _itemToCofferMap.TryGetValue(itemId, out var cofferIds))
        {
            var cofferSheet = _gameData.GetExcelSheet<Item>();
            var cofferName = string.Join(", ", cofferIds.Select(id => cofferSheet?.GetRow(id).Name.ExtractText() ?? $"{id}"));
            results.Add(new ItemSourceDetail(
                ItemSourceType.Coffer, $"Coffer: {cofferName}",
                null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 6c. Achievement unlock → Item (via SpecialShop.ItemStruct.AchievementUnlock)
        if (!results.Any(s => s.Type == ItemSourceType.Achievement) && _itemToAchievementMap.TryGetValue(itemId, out var achievementIds))
        {
            var achSheet = _gameData.GetExcelSheet<Achievement>();
            var achNames = achievementIds.Select(aid => {
                    if (achSheet != null && achSheet.TryGetRow(aid, out var ach))
                        return ach.Name.ToString() ?? $"{aid}";
                    return $"{aid}";
                }).Distinct();
            results.Add(new ItemSourceDetail(
                ItemSourceType.Achievement, $"Achievement(s): {string.Join(", ", achNames)}",
                null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 6d. FieldOpCoffer — Pagos/Pyros/Hydatos/Occult chests
        if (!results.Any(s => s.Type == ItemSourceType.Coffer) && _itemToFieldOpCofferMap.TryGetValue(itemId, out var fieldOpEntries))
        {
            var desc = string.Join("; ", fieldOpEntries.Select(e => $"{e.Type} {e.CofferType}"));
            results.Add(new ItemSourceDetail(
                ItemSourceType.Coffer, $"Field Op Coffer: {desc}",
                null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 7. Generic fallback — nothing found
        if (results.Count == 0)
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                "No vendor/crafting source found. May drop from duties, raids, or other content.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
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
                            costs, null, null, null, null, null, null, null, null));
                    }
                }
                else
                {
                    allSources.Add(new ItemSourceDetail(
                        ItemSourceType.Vendor,
                        "Vendor: Merchant",
                        null, null, null, null, null, null,
                        costs, null, null, null, null, null, null, null, null));
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
                        var questForUnlock = shop.Quest.RowId > 0 ? (uint?)shop.Quest.RowId : null;

                        var exchangeType = ClassifyExchangeShop(shopName);
                        var displayDesc = exchangeType != null
                            ? $"{exchangeType} Exchange: {shopName}"
                            : $"Shop: {shopName}";

                        var npcInfos = _shopNpcLookup.GetValueOrDefault(shopId);
                        if (npcInfos == null && _shopNpcNameOnly.TryGetValue(shopId, out var nameOnly))
                        {
                            npcInfos = new List<NpcLocationInfo> { new(nameOnly, "", 0, 0, 0, 0) };
                        }

                        if (npcInfos != null && npcInfos.Count > 0)
                        {
                            foreach (var npc in npcInfos)
                            {
                                allSources.Add(new ItemSourceDetail(
                                    ItemSourceType.Vendor,
                                    displayDesc,
                                    npc.NpcName, npc.ZoneName,
                                    npc.MapX, npc.MapY,
                                    npc.TerritoryTypeId, npc.MapId,
                                    currencyItemIds, null,
                                    questName, null, null, null, null, questForUnlock, null));
                            }
                        }
                        else
                        {
                            allSources.Add(new ItemSourceDetail(
                                ItemSourceType.Vendor,
                                displayDesc,
                                null, null, null, null, null, null,
                                currencyItemIds, null,
                                questName, null, null, null, null, questForUnlock, null));
                        }
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

        // ENpcPlace fallback: supplemental NPC positions for NPCs without Level entry
        var enpcPlaces = CsvLoader.LoadResource<ENpcPlace>(
            CsvLoader.ENpcPlaceResourceName, true, out _, out _, _gameData);

        var supplementalNpcLocations = new Dictionary<uint, ENpcPlace>();
        foreach (var place in enpcPlaces)
        {
            if (place.ENpcResidentId > 0 && !supplementalNpcLocations.ContainsKey(place.ENpcResidentId))
                supplementalNpcLocations[place.ENpcResidentId] = place;
        }

        // Track NPCs that still have no location after Level + ENpcPlace
        var missingNpcs = new HashSet<uint>();
        foreach (var npcBase in enpcBaseSheet)
        {
            var npcId = npcBase.RowId;
            if (!_npcNameCache.ContainsKey(npcId)) continue;
            if (npcLevelLookup.ContainsKey(npcId)) continue;
            if (supplementalNpcLocations.ContainsKey(npcId)) continue;
            missingNpcs.Add(npcId);
        }

        // ENpcBase → Shop RowIds
        foreach (var npcBase in enpcBaseSheet)
        {
            var npcId = npcBase.RowId;
            if (!_npcNameCache.TryGetValue(npcId, out var npcName) || string.IsNullOrEmpty(npcName))
                continue;

            if (!npcLevelLookup.TryGetValue(npcId, out var levelInfo))
            {
                // Fallback: ENpcPlace supplemental data
                if (supplementalNpcLocations.TryGetValue(npcId, out var place))
                {
                    var mapRow = mapSheet?.GetRow(place.MapId);
                    var supZoneName = mapRow?.PlaceName.ValueNullable?.Name.ToString() ?? "";
                    var supTerritoryTypeId = place.TerritoryTypeId;
                    var supMapId = place.MapId;
                    // Position is already in map coordinates
                    var supMapX = place.Position.X;
                    var supMapY = place.Position.Y;

                    foreach (var dataRef in npcBase.ENpcData)
                    {
                        if (dataRef.RowId == 0)
                            continue;

                        var shopId = dataRef.RowId;
                        if (!_shopNpcLookup.ContainsKey(shopId))
                            _shopNpcLookup[shopId] = new();
                        _shopNpcLookup[shopId].Add(new NpcLocationInfo(
                            npcName, supZoneName, supMapX, supMapY, supTerritoryTypeId, supMapId));
                    }
                }
                continue;
            }

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

        // Stage 2: LGB fallback for NPCs still missing locations
        if (missingNpcs.Count > 0)
        {
            var territorySheet = _gameData.GetExcelSheet<TerritoryType>();
            if (territorySheet != null)
            {
                foreach (var territory in territorySheet)
                {
                    var bg = territory.Bg.ToString();
                    if (string.IsNullOrEmpty(bg)) continue;

                    try
                    {
                        var lgbIdx = bg.IndexOf("/level/");
                        if (lgbIdx < 0) continue;
                        var lgbFileName = "bg/" + bg[..(lgbIdx + 1)] + "level/planevent.lgb";
                        var lgbFile = _gameData.GetFile<LgbFile>(lgbFileName);
                        if (lgbFile == null) continue;

                        foreach (var layer in lgbFile.Layers)
                        {
                            foreach (var obj in layer.InstanceObjects)
                            {
                                if (obj.AssetType != LayerEntryType.EventNPC) continue;

                                var eventNpc = (LayerCommon.ENPCInstanceObject)obj.Object;
                                var npcId = eventNpc.ParentData.ParentData.BaseId;

                                if (!missingNpcs.Contains(npcId)) continue;

                                var pos = obj.Transform.Translation;
                                var map = territory.Map.ValueNullable;
                                if (map == null) continue;

                                var mapX = ToMapCoordinate(pos.X, map.Value.SizeFactor, map.Value.OffsetX);
                                var mapY = ToMapCoordinate(pos.Z, map.Value.SizeFactor, map.Value.OffsetY);
                                var zoneName = map.Value.PlaceName.ValueNullable?.Name.ToString() ?? "";

                                var npcName = _npcNameCache.GetValueOrDefault(npcId, "");
                                if (string.IsNullOrEmpty(npcName)) continue;

                                var npcBase = enpcBaseSheet.GetRow(npcId);
                                foreach (var dataRef in npcBase.ENpcData)
                                {
                                    if (dataRef.RowId == 0) continue;
                                    if (!_shopNpcLookup.ContainsKey(dataRef.RowId))
                                        _shopNpcLookup[dataRef.RowId] = new();
                                    _shopNpcLookup[dataRef.RowId].Add(new NpcLocationInfo(
                                        npcName, zoneName, mapX, mapY,
                                        territory.RowId, map.Value.RowId));
                                }

                                missingNpcs.Remove(npcId);
                            }
                        }
                    }
                    catch
                    {
                        // LGB file missing or corrupt — skip
                    }

                    if (missingNpcs.Count == 0) break;
                }
            }
        }

        // Stage 3: name-only fallback for NPCs with no location data at all
        foreach (var npcId in missingNpcs)
        {
            var npcName = _npcNameCache.GetValueOrDefault(npcId, "");
            if (string.IsNullOrEmpty(npcName)) continue;

            var npcBase = enpcBaseSheet.GetRow(npcId);
            foreach (var dataRef in npcBase.ENpcData)
            {
                if (dataRef.RowId > 0 && !_shopNpcNameOnly.ContainsKey(dataRef.RowId))
                    _shopNpcNameOnly[dataRef.RowId] = npcName;
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

        var bossChests = CsvLoader.LoadResource<DungeonBossChest>(
            CsvLoader.DungeonBossChestResourceName,
            includesHeaders: true,
            out _,
            out _,
            gameData: null);

        foreach (var drop in bossChests)
        {
            if (drop.ItemId == 0 || drop.ContentFinderConditionId == 0)
                continue;

            if (!_itemToDutyMap.ContainsKey(drop.ItemId))
                _itemToDutyMap[drop.ItemId] = new();

            if (!_itemToDutyMap[drop.ItemId].Contains(drop.ContentFinderConditionId))
                _itemToDutyMap[drop.ItemId].Add(drop.ContentFinderConditionId);
        }

        // Load ItemSupplement (Loot/Desynth/Reduction)
        var supplements = CsvLoader.LoadResource<ItemSupplement>(
            CsvLoader.ItemSupplementResourceName,
            includesHeaders: true,
            out _,
            out _,
            gameData: null);
        _itemSupplementCache = supplements
            .Where(s => s.ItemId != 0 && s.SourceItemId != 0)
            .GroupBy(s => s.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        BuildTotemLookupCache();
        BuildFateDropCache();
        BuildMobDropCache();
        BuildHouseVendorCache();
        BuildItemSupplementCofferCache();
        BuildFieldOpCofferCache();
        BuildAchievementCache();
    }

    private static readonly Regex _totemBossRegex = new(@"\((.+)\)");

    private static readonly Dictionary<string, uint> BossToCfcRowId = new()
    {
        // HW
        ["Ravana"] = 87,
        ["Bismarck"] = 89,
        ["Thordan"] = 91,
        ["Sephirot"] = 135,
        ["Nidhogg"] = 170,
        ["Sophia"] = 184,
        ["Zurvan"] = 224,
        // SB
        ["Susano"] = 244,
        ["Lakshmi"] = 264,
        ["Shinryu"] = 278,
        ["Byakko"] = 291,
        ["Tsukuyomi"] = 538,
        ["Suzaku"] = 597,
        ["Seiryu"] = 638,
        // ShB
        ["Titania"] = 658,
        ["Innocence"] = 667,
        ["Hades"] = 693,
        ["The Ruby Weapon"] = 718,
        ["Alexander"] = 725,
        ["Warrior of Light"] = 739,
        ["The Emerald Weapon"] = 763,
        ["The Diamond Weapon"] = 782,
        // EW
        ["Zodiark"] = 803,
        ["Hydaelyn"] = 791,
        ["The Endsinger"] = 846,
        ["Barbariccia"] = 871,
        ["Rubicante"] = 924,
        ["Golbez"] = 950,
        ["Zeromus"] = 965,
        // DT
        ["Valigarmanda"] = 833,
        ["Zoraal Ja"] = 996,
        ["Queen Eternal"] = 1017,
        ["Futures Rewritten"] = 1031,
        ["Cloud of Darkness"] = 1044,
        ["Zelenia"] = 1062,
        ["Necron"] = 1062,
        ["Doomtrain"] = 1077,
        ["Enuo"] = 1116,
    };

    private void BuildFateDropCache()
    {
        var fateItems = CsvLoader.LoadResource<FateItem>(
            CsvLoader.FateItemResourceName,
            includesHeaders: true,
            out _, out _,
            gameData: null);

        foreach (var fi in fateItems)
        {
            if (fi.ItemId == 0 || fi.FateId == 0)
                continue;
            if (!_itemToFateMap.ContainsKey(fi.ItemId))
                _itemToFateMap[fi.ItemId] = new();
            if (!_itemToFateMap[fi.ItemId].Contains(fi.FateId))
                _itemToFateMap[fi.ItemId].Add(fi.FateId);
        }
    }

    private void BuildMobDropCache()
    {
        var mobDrops = CsvLoader.LoadResource<MobDrop>(
            CsvLoader.MobDropResourceName,
            includesHeaders: true,
            out _, out _,
            gameData: null);

        foreach (var md in mobDrops)
        {
            if (md.ItemId == 0 || md.BNpcNameId == 0)
                continue;
            if (!_itemToMobMap.ContainsKey(md.ItemId))
                _itemToMobMap[md.ItemId] = new();
            if (!_itemToMobMap[md.ItemId].Contains(md.BNpcNameId))
                _itemToMobMap[md.ItemId].Add(md.BNpcNameId);
        }
    }

    private void BuildHouseVendorCache()
    {
        var houseVendors = CsvLoader.LoadResource<HouseVendor>(
            CsvLoader.HouseVendorResourceName,
            includesHeaders: true,
            out _, out _,
            gameData: null);

        foreach (var hv in houseVendors)
        {
            if (hv.ENpcResidentId == 0 || hv.ParentId == 0)
                continue;
            if (!_shopToNpcIds.ContainsKey(hv.ParentId))
                _shopToNpcIds[hv.ParentId] = new();
            if (!_shopToNpcIds[hv.ParentId].Contains(hv.ENpcResidentId))
                _shopToNpcIds[hv.ParentId].Add(hv.ENpcResidentId);
        }

        // ponytail: ENpcShop has ShopId→ENpcResidentId, but we lack Shop→Item mapping here.
        // Keep HouseVendor only (ItemId→ENpcResidentId) for the vendor lookup path.
    }

    private void BuildItemSupplementCofferCache()
    {
        var supplements = CsvLoader.LoadResource<ItemSupplement>(
            CsvLoader.ItemSupplementResourceName, includesHeaders: true, out _, out _, gameData: null);

        foreach (var sup in supplements)
        {
            if (sup.ItemId == 0 || sup.ItemSupplementSource != ItemSupplementSource.Coffer)
                continue;
            if (!_itemToCofferMap.ContainsKey(sup.ItemId))
                _itemToCofferMap[sup.ItemId] = new();
            if (!_itemToCofferMap[sup.ItemId].Contains(sup.SourceItemId))
                _itemToCofferMap[sup.ItemId].Add(sup.SourceItemId);
        }
    }

    private void BuildFieldOpCofferCache()
    {
        var coffers = CsvLoader.LoadResource<FieldOpCoffer>(
            CsvLoader.FieldOpCofferResourceName, includesHeaders: true, out _, out _, gameData: null);

        foreach (var coffer in coffers)
        {
            if (coffer.ItemId == 0)
                continue;
            if (!_itemToFieldOpCofferMap.ContainsKey(coffer.ItemId))
                _itemToFieldOpCofferMap[coffer.ItemId] = new();
            var entry = (coffer.Type, coffer.CofferType);
            if (!_itemToFieldOpCofferMap[coffer.ItemId].Contains(entry))
                _itemToFieldOpCofferMap[coffer.ItemId].Add(entry);
        }
    }

    // ponytail: ItemId → List<Achievement RowId> via SpecialShop.ItemStruct.AchievementUnlock
    private void BuildAchievementCache()
    {
        var specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        foreach (var shop in specialShops)
        {
            foreach (var itemStruct in shop.Item)
            {
                if (itemStruct.AchievementUnlock.RowId == 0)
                    continue;
                foreach (var receiveItem in itemStruct.ReceiveItems)
                {
                    var itemId = receiveItem.Item.RowId;
                    if (itemId == 0)
                        continue;
                    if (!_itemToAchievementMap.ContainsKey(itemId))
                        _itemToAchievementMap[itemId] = new();
                    if (!_itemToAchievementMap[itemId].Contains(itemStruct.AchievementUnlock.RowId))
                        _itemToAchievementMap[itemId].Add(itemStruct.AchievementUnlock.RowId);
                }
            }
        }
    }

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
                            _totemCostToBoss[cost.ItemCost.RowId] = (bossName, cfcMatch?.Name.ToString() ?? null, cfcMatch?.RowId, shop.Quest.RowId > 0 ? shop.Quest.RowId : null);
                        }
                    }

                    var exchangeType = ClassifyExchangeShop(shopName);
                    if (exchangeType != null)
                    {
                        foreach (var receiveItem in itemStruct.ReceiveItems)
                        {
                            if (receiveItem.Item.RowId > 0)
                            {
                                var matchedCfcs = FindCfcsForExchange(exchangeType, shopName, cfcSheet);
                                if (matchedCfcs.Count > 0)
                                {
                                    _exchangeCostToShopCfcs[receiveItem.Item.RowId] = (exchangeType, shopName, matchedCfcs);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private List<uint> FindCfcsForExchange(string exchangeType, string shopName, ExcelSheet<ContentFinderCondition>? cfcSheet)
    {
        var result = new List<uint>();
        if (cfcSheet == null)
            return result;

        if (exchangeType == "Savage")
        {
            var tierName = ExtractTierName(shopName);
            if (!string.IsNullOrEmpty(tierName))
            {
                foreach (var cfc in cfcSheet)
                {
                    if (cfc.RowId == 0) continue;
                    var cfcName = cfc.Name.ToString();
                    if (string.IsNullOrEmpty(cfcName)) continue;
                    if (!cfcName.Contains("Savage", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cfcName.Contains(tierName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(cfc.RowId);
                    }
                }
            }
        }
        else if (exchangeType == "Trial")
        {
            foreach (var cfc in cfcSheet)
            {
                if (cfc.RowId == 0) continue;
                if (cfc.TrialRoulette)
                {
                    result.Add(cfc.RowId);
                }
            }
        }

        return result;
    }

    private static readonly Dictionary<uint, (string exchangeType, string shopName, IReadOnlyList<uint> cfcRowIds)> _exchangeCostToShopCfcs = new();

    private string? ClassifyExchangeShop(string shopName)
    {
        if (shopName.StartsWith("Totem Gear") || shopName.StartsWith("Auspice Gear"))
            return "Trial";

        if (shopName.Contains("Unsung Relic") && shopName.Contains("Exchange"))
            return "Savage";
        if (shopName.Contains("Mythos Exchange"))
            return "Savage";

        if (Regex.IsMatch(shopName, @"Gear \(IL \d+-\d+\)"))
            return "Savage";

        return null;
    }

    private static string? ExtractTierName(string shopName)
    {
        var tierPatterns = new[] { "Asphodelos", "Abyssos", "Anabaseios", "Hephaistos", "Hypnos", "Orbonne", "Erebus", "Vakaura", "Mahakala", "Ktisis", "Oikema", "Kosmoe" };
        foreach (var tier in tierPatterns)
        {
            if (shopName.Contains(tier, StringComparison.OrdinalIgnoreCase))
                return tier;
        }
        return null;
    }

    private string GetDutyType(uint cfcRowId)
    {
        var cfc = _gameData.GetExcelSheet<ContentFinderCondition>()?.GetRow(cfcRowId);
        var contentTypeId = cfc?.ContentType.RowId ?? 0;
        return contentTypeId switch
        {
            2 => "Dungeon",
            4 => "Trial",
            5 => "Raid",
            28 => "Ultimate",
            _ => "Duty"
        };
    }

    private ContentFinderCondition? MatchCfcForBoss(string bossName, ExcelSheet<ContentFinderCondition>? cfcSheet)
    {
        if (cfcSheet == null)
            return null;

        // Stage 1: Hardcoded Boss→CFC RowId map
        if (BossToCfcRowId.TryGetValue(bossName, out var hardcodedCfcId))
        {
            if (cfcSheet.TryGetRow(hardcodedCfcId, out var cfc))
                return cfc;
        }

        var bossLower = bossName.ToLowerInvariant();

        // Stage 2: Direct name match in Trial/Minstrel name
        foreach (var cfc in cfcSheet)
        {
            if (cfc.RowId == 0) continue;
            var cfcName = cfc.Name.ToString();
            if (string.IsNullOrEmpty(cfcName)) continue;
            if (!cfc.TrialRoulette && !cfcName.Contains("Minstrel")) continue;
            var cfcLower = cfcName.ToLowerInvariant();
            if (cfcLower.Contains(bossLower))
                return cfc;
        }

        // Stage 3: Partial/short name match (e.g. "Enuo" -> "Enuo, the Omega Protocol")
        foreach (var cfc in cfcSheet)
        {
            if (cfc.RowId == 0) continue;
            var cfcName = cfc.Name.ToString();
            if (string.IsNullOrEmpty(cfcName)) continue;
            if (!cfc.TrialRoulette && !cfcName.Contains("Minstrel")) continue;
            var cfcLower = cfcName.ToLowerInvariant();
            var parts = bossLower.Split(new[] { ' ', ',', '&', '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length < 3) continue;
                if (cfcLower.Contains(part))
                    return cfc;
            }
        }

        return null;
    }

    private static float ToMapCoordinate(float val, ushort sizeFactor, short offset)
    {
        var c = sizeFactor / 100.0f;
        val = (val + offset) * c;
        return (41.0f / c * ((val + 1024.0f) / 2048.0f)) + 1;
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
            return (cfc.Name.ToString(), GetDutyType(cfcId));

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



