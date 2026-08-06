using System;
using System.Collections.Generic;
using System.Linq;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamSource.Core;

public sealed class LuminaItemSourceService : IItemSourceService
{
    private readonly GameData _gameData;
    private readonly Dictionary<uint, IReadOnlyList<ItemSource>> _cache = new();
    private readonly Recipe[] _recipes;
    private readonly SubrowExcelSheet<GilShopItem>? _gilShopItems;
    private readonly SpecialShop[] _specialShops;
    private readonly Quest[] _quests;

    // ponytail: cached Fate/Mob/HouseVendor lookups — built once at construction, read-only after
    private readonly Dictionary<uint, List<uint>> _itemToFateMap = new();
    private readonly Dictionary<uint, List<uint>> _itemToMobMap = new();
    private readonly Dictionary<uint, List<uint>> _shopToNpcIds = new();

    // ponytail: cached Coffer/DungeonBossChest/FieldOpCoffer lookups
    private readonly Dictionary<uint, List<uint>> _itemToCofferMap = new();
    private readonly Dictionary<uint, List<uint>> _itemToDungeonChestMap = new();
    private readonly Dictionary<uint, List<(FieldOpType Type, FieldOpCofferType CofferType)>> _itemToFieldOpCofferMap = new();
    private readonly Dictionary<uint, List<uint>> _itemToDungeonDropMap = new();

    public LuminaItemSourceService(GameData gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));

        _recipes = _gameData.GetExcelSheet<Recipe>()?.ToArray() ?? Array.Empty<Recipe>();
        _gilShopItems = _gameData.GetSubrowExcelSheet<GilShopItem>();
        _specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        _quests = _gameData.GetExcelSheet<Quest>()?.ToArray() ?? Array.Empty<Quest>();

        BuildFateDropCache();
        BuildMobDropCache();
        BuildHouseVendorCache();
        BuildItemSupplementCofferCache();
        BuildDungeonBossChestCache();
        BuildDungeonDropCache();
        BuildFieldOpCofferCache();
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

        // 5. Fate drops
        if (!sources.Any(s => s.Type == ItemSourceType.Fate) && _itemToFateMap.TryGetValue(itemId, out var fateIds))
        {
            var fateSheet = _gameData.GetExcelSheet<Fate>();
            var fateName = string.Join(", ", fateIds.Select(id => fateSheet?.GetRow(id).Name.ToString() ?? $"{id}"));
            sources.Add(new ItemSource(ItemSourceType.Fate, $"Fate Drop: {fateName}"));
        }

        // 6. Mob drops
        if (!sources.Any(s => s.Type == ItemSourceType.Mob) && _itemToMobMap.TryGetValue(itemId, out var mobIds))
        {
            var npcSheet = _gameData.GetExcelSheet<BNpcName>();
            var mobName = string.Join(", ", mobIds.Select(id => npcSheet?.GetRow(id).Singular.ToString() ?? $"{id}"));
            sources.Add(new ItemSource(ItemSourceType.Mob, $"Mob Drop: {mobName}"));
        }

        // 7. House Vendor
        if (!sources.Any(s => s.Type == ItemSourceType.Vendor) && _shopToNpcIds.TryGetValue(itemId, out var hvNpcs) && hvNpcs.Count > 0)
        {
            var npcSheet = _gameData.GetExcelSheet<ENpcResident>();
            var npcName = npcSheet?.GetRow(hvNpcs[0]).Singular.ToString() ?? $"{hvNpcs[0]}";
            sources.Add(new ItemSource(ItemSourceType.Vendor, $"House Vendor: {npcName}"));
        }

        // 8. ItemSupplement Coffer (coffer → contents, source=Coffer=7)
        if (!sources.Any(s => s.Type == ItemSourceType.Coffer) && _itemToCofferMap.TryGetValue(itemId, out var cofferIds))
        {
            var cofferSheet = _gameData.GetExcelSheet<Item>();
            var cofferName = string.Join(", ", cofferIds.Select(id => cofferSheet?.GetRow(id).Name.ExtractText() ?? $"{id}"));
            sources.Add(new ItemSource(ItemSourceType.Coffer, $"Coffer: {cofferName}"));
        }

        // 9. DungeonBossChest — boss chest drops
        if (!sources.Any(s => s.Type == ItemSourceType.Coffer) && _itemToDungeonChestMap.TryGetValue(itemId, out var chestIds))
        {
            var dutySheet = _gameData.GetExcelSheet<ContentFinderCondition>();
            var dutyName = string.Join(", ", chestIds.Select(id => dutySheet?.GetRow(id).Name.ExtractText() ?? $"{id}"));
            sources.Add(new ItemSource(ItemSourceType.Coffer, $"Boss Chest: {dutyName}"));
        }

        // 10. DungeonDrop — duty drops
        if (!sources.Any(s => s.Type == ItemSourceType.Coffer) && _itemToDungeonDropMap.TryGetValue(itemId, out var dropDutyIds))
        {
            var dutySheet = _gameData.GetExcelSheet<ContentFinderCondition>();
            var dutyName = string.Join(", ", dropDutyIds.Select(id => dutySheet?.GetRow(id).Name.ExtractText() ?? $"{id}"));
            sources.Add(new ItemSource(ItemSourceType.Coffer, $"Dungeon Drop: {dutyName}"));
        }

        // 11. FieldOpCoffer — Pagos/Pyros/Hydatos/Occult chests
        if (!sources.Any(s => s.Type == ItemSourceType.Coffer) && _itemToFieldOpCofferMap.TryGetValue(itemId, out var fieldOpEntries))
        {
            var desc = string.Join("; ", fieldOpEntries.Select(e => $"{e.Type} {e.CofferType}"));
            sources.Add(new ItemSource(ItemSourceType.Coffer, $"Field Op Coffer: {desc}"));
        }

        var result = sources.AsReadOnly();
        _cache[itemId] = result;
        return result;
    }

    private void BuildFateDropCache()
    {
        var fateItems = CsvLoader.LoadResource<FateItem>(
            CsvLoader.FateItemResourceName, includesHeaders: true, out _, out _, gameData: null);

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
            CsvLoader.MobDropResourceName, includesHeaders: true, out _, out _, gameData: null);

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
            CsvLoader.HouseVendorResourceName, includesHeaders: true, out _, out _, gameData: null);

        foreach (var hv in houseVendors)
        {
            if (hv.ENpcResidentId == 0 || hv.ParentId == 0)
                continue;
            if (!_shopToNpcIds.ContainsKey(hv.ParentId))
                _shopToNpcIds[hv.ParentId] = new();
            if (!_shopToNpcIds[hv.ParentId].Contains(hv.ENpcResidentId))
                _shopToNpcIds[hv.ParentId].Add(hv.ENpcResidentId);
        }
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

    private void BuildDungeonBossChestCache()
    {
        var chests = CsvLoader.LoadResource<DungeonBossChest>(
            CsvLoader.DungeonBossChestResourceName, includesHeaders: true, out _, out _, gameData: null);

        foreach (var chest in chests)
        {
            if (chest.ItemId == 0 || chest.ContentFinderConditionId == 0)
                continue;
            if (!_itemToDungeonChestMap.ContainsKey(chest.ItemId))
                _itemToDungeonChestMap[chest.ItemId] = new();
            if (!_itemToDungeonChestMap[chest.ItemId].Contains(chest.ContentFinderConditionId))
                _itemToDungeonChestMap[chest.ItemId].Add(chest.ContentFinderConditionId);
        }
    }

    private void BuildDungeonDropCache()
    {
        var drops = CsvLoader.LoadResource<DungeonDrop>(
            CsvLoader.DungeonDropItemResourceName, includesHeaders: true, out _, out _, gameData: null);

        foreach (var drop in drops)
        {
            if (drop.ItemId == 0 || drop.ContentFinderConditionId == 0)
                continue;
            if (!_itemToDungeonDropMap.ContainsKey(drop.ItemId))
                _itemToDungeonDropMap[drop.ItemId] = new();
            if (!_itemToDungeonDropMap[drop.ItemId].Contains(drop.ContentFinderConditionId))
                _itemToDungeonDropMap[drop.ItemId].Add(drop.ContentFinderConditionId);
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
}
