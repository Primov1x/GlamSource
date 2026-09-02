using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GlamSource.Core;
using Lumina;
using Lumina.Data;
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
    IReadOnlyList<ItemSourceDetail> Sources,
    string? SetName = null,
    IReadOnlyList<SetMember>? SetMembers = null);

public record SetMember(uint ItemId, string Name, uint IconId);

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
    string? ShopUrl = null,
    uint? SourceItemId = null);

public interface IItemDetailService
{
    ItemDetail? GetDetail(uint itemId);
    GameData GameData { get; }
    uint? ResolveMountItemId(uint mountId);
    string? GetEnglishName(uint itemId);
}

public sealed class ItemDetailService : IItemDetailService
{
    private readonly GameData _gameData;
    private readonly Dictionary<uint, ItemDetail?> _cache = new();

    private readonly Dictionary<uint, string> _npcNameCache = new();
    private readonly Dictionary<uint, List<NpcLocationInfo>> _shopNpcLookup = new();
    private readonly Dictionary<uint, List<Recipe>> _recipeByResult = new();
    private Dictionary<string, uint>? _itemIdByName; // lazy, built once on first fallback lookup
    private Dictionary<uint, List<(uint id, string name, uint iconId)>>? _itemsBySeriesId; // lazy

    private record NpcLocationInfo(
        string NpcName, string ZoneName,
        float MapX, float MapY,
        uint TerritoryTypeId, uint MapId);
    private record GatheringInfo(
        int GatheringLevel, int GatheringType,
        string ZoneName, float MapX, float MapY,
        uint TerritoryTypeId, uint MapId);
    private readonly Dictionary<uint, string> _itemNameCache = new();

    // ponytail: ItemId â†’ List<ContentFinderCondition RowId> from LuminaSupplemental DungeonDrop
    private readonly Dictionary<uint, List<uint>> _itemToDutyMap = new();

    // ponytail: ItemId â†’ List<FateId> from LuminaSupplemental FateItem
    private readonly Dictionary<uint, List<uint>> _itemToFateMap = new();

    // ponytail: ItemId â†’ List<BNpcNameId> from LuminaSupplemental MobDrop
    private readonly Dictionary<uint, List<uint>> _itemToMobMap = new();

    // ponytail: ItemId â†’ List<ENpcResidentId> from LuminaSupplemental HouseVendor (hv.ParentId = ItemId)
    private readonly Dictionary<uint, List<uint>> _shopToNpcIds = new();

    // ponytail: CostItemId â†’ (bossName, cfcName, cfcRowId) from "Totem Gear (X)" shop name
    private readonly Dictionary<uint, (string bossName, string? cfcName, uint? cfcRowId, uint? questId)> _totemCostToBoss = new();

    // ponytail: ItemId â†’ List<ItemSupplement> (Loot/Desynth/Reduction sources)
    private Dictionary<uint, List<ItemSupplement>> _itemSupplementCache = new();

    // ponytail: ItemId â†’ List<CofferItemId> from ItemSupplement source=Coffer
    private readonly Dictionary<uint, List<uint>> _itemToCofferMap = new();

    // reverse of the above: CofferItemId â†’ List<ItemId> it unlocks. Coffer-sourced glamours (e.g.
    // "Street Attire Coffer") have no Item.ItemSeries — the existing set/setMembers logic only
    // checks ItemSeries, so these showed no set info at all ("Street Jacket (36831)... welche set
    // teile es noch gibt" fehlt). Same "which other items belong together" question, different
    // source: everything the same coffer unlocks IS the set.
    private Dictionary<uint, List<uint>>? _cofferToItemsMap;

    // ponytail: ItemId â†’ List<(FieldOpType, FieldOpCofferType)> from FieldOpCoffer
    private readonly Dictionary<uint, List<(FieldOpType Type, FieldOpCofferType CofferType)>> _itemToFieldOpCofferMap = new();

    // ponytail: ItemId â†’ List<Achievement RowId> from SpecialShop.ItemStruct.AchievementUnlock
    private readonly Dictionary<uint, List<uint>> _itemToAchievementMap = new();

    // ponytail: ItemId -> all nodes (level, type, zone, map position) from GatheringPointBase
    private readonly Dictionary<uint, List<GatheringInfo>> _itemToGatheringCache = new();

    private record FishingInfo(int GatheringLevel, string ZoneName, float MapX, float MapY, uint TerritoryTypeId, uint MapId);
    // ponytail: ItemId -> fishing spots (level, zone, map position) from FishingSpot.Item[] — a
    // separate sheet/mechanism from Botanist/Miner GatheringPointBase, previously not covered at
    // all (15/135 = 11% of a random no-source audit sample turned out to just be fish).
    private readonly Dictionary<uint, List<FishingInfo>> _itemToFishingCache = new();

    // ponytail: PvP items from SpecialShop (tome currencies), PvPSeries tier rewards
    private readonly Dictionary<uint, uint> _pvpItemToSeason = new();
    private readonly HashSet<uint> _pvpVendorItems = new();
    private uint _currentPvpSeasonId;

    // ponytail: ItemId -> Mogstation shop URL, from static scrape of Gamer Escape's Mog Station category (LuminaSupplemental/MogstationItems.csv)
    private readonly Dictionary<uint, string> _mogstationItems = new();

    // ponytail: TripleTriadCard RowId -> NPC win locations. Lumina's own TripleTriadCard/
    // TripleTriadResident sheets don't expose reward-NPC linkage (verified: TripleTriadCard's
    // relevant fields are just "Unknown0..5", TripleTriadResident only has an "Order" column) — the
    // client apparently doesn't ship this as a clean table. Scraped from FFTriadBuddy (MIT, static
    // game-data snapshot, same "our own CSV scrape" precedent as MogstationItems.csv), cross-verified
    // against the wiki for Rhitahtyn sas Arvina Card -> Indolent Imperial @ Mor Dhona (11.9, 17.4)
    // matching npc id=25 exactly. Covers 236 of ~340 cards; the rest fall through to the generic
    // fallback same as before, no regression.
    private record TriadCardNpc(string NpcName, string ZoneName, float MapX, float MapY);
    private readonly Dictionary<uint, List<TriadCardNpc>> _triadCardNpcs = new();

    // ponytail: ItemId -> minion/mount sources, from FFXIV Collect's public API (non-commercial use,
    // attribution appreciated — see BuildCollectSourceCache). No Lumina sheet exposes minion/mount
    // unlock sources at all (checked: Item's ItemAction for a sample minion has no GameContentLinks
    // either) — these come from every mechanic FFXIV has (FATE gold completion, Hunting Log,
    // achievement, Gold Saucer currency, promo, seasonal event...), so a community-maintained
    // aggregation is the only realistic source. Covers 930 items (583 minions + 353 mounts).
    private record CollectSource(string Kind, string SourceType, string SourceText);
    private readonly Dictionary<uint, List<CollectSource>> _collectSources = new();

    // ponytail: MountId (the same id Character.Mount.MountId reads natively) -> unlock ItemId, from
    // the same FFXIV Collect mounts dataset as _collectSources — its "id" field IS the Mount sheet
    // RowId (same convention already verified for Triple Triad card ids matching TripleTriadCard
    // RowIds). Lets "who's mount is this" resolve straight into the existing item-detail pipeline.
    private readonly Dictionary<uint, uint> _mountToItemId = new();

    // Name-only fallback for NPCs with no location data
    private readonly Dictionary<uint, string> _shopNpcNameOnly = new();

    // "kriegen wir das immer aktuell?" — live Gamer Escape lookup, replaces the old one-time
    // MogstationItems.csv scrape for freshness; the CSV stays as a fallback (older items the wiki
    // itself may have since delisted/renamed, or a transient fetch failure this session).
    private readonly MogStationLiveService _mogstationLive = new(new HttpClient());

    public ItemDetailService(GameData gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));

        BuildCaches();
        BuildDutyDropCache();
        // ponytail: gathering cache reads GatheringPoint/TerritoryType/Map — column-hash mismatch
        // when DalaMock's Lumina.Excel lags behind live sqpack. Skip on drift, no source data lost.
        try { BuildGatheringCache(); }
        catch (Lumina.Excel.Exceptions.MismatchedColumnHashException) { }
        // ponytail: same DalaMock schema-drift caveat as gathering — verified locally via a raw
        // sheet dump before hitting this (FishingSpot.Item[] holds Item RowIds directly, X/Z ->
        // map coords via the same ToMapCoordinate convention already proven for gathering/quest
        // sources), but DalaMock's bundled Lumina.Excel doesn't match FishingSpot's live column
        // hash, so it can't run end-to-end here. Needs in-game verification once deployed.
        try { BuildFishingCache(); }
        catch (Lumina.Excel.Exceptions.MismatchedColumnHashException) { }
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
        // LevelItem is the item level (iLvl 120 for Ironworks Helm); LevelEquip is the required
        // character level (50) — the UI labels this "iLvl", so it must be the former.
        var itemLevel = (ushort)item.LevelItem.RowId;
        var isMarketable = item.ItemSearchCategory.RowId > 0;
        var iconId = item.Icon;
        // ponytail: Item.ItemSeries is the game's own Mog Station bundle grouping (verified: Abes
        // Jacket -> ItemSeries.Name "Abes Attire", matching the real store's set name exactly) — no
        // scraping needed, unlike the exact product page URL (the store itself has no search feature
        // and is fully client-rendered, so a precise per-set link isn't cheaply obtainable).
        var setName = item.ItemSeries.IsValid ? item.ItemSeries.Value.Name.ToString() : null;
        if (string.IsNullOrEmpty(setName)) setName = null;

        // ponytail: same ItemSeries field also lists every OTHER item sharing it — verified against
        // Garland Tools' own data for Abes Attire (series id 25 -> Jacket/Gloves/Halfslops/Boots,
        // exact match). Gives the "which 3-4 items make up this set" answer for free too.
        IReadOnlyList<SetMember>? setMembers = null;
        if (setName != null)
        {
            if (_itemsBySeriesId == null)
            {
                _itemsBySeriesId = new Dictionary<uint, List<(uint, string, uint)>>();
                foreach (var i in itemSheet)
                {
                    if (!i.ItemSeries.IsValid) continue;
                    var n = i.Name.ToString();
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!_itemsBySeriesId.TryGetValue(i.ItemSeries.RowId, out var list))
                    {
                        list = new List<(uint, string, uint)>();
                        _itemsBySeriesId[i.ItemSeries.RowId] = list;
                    }
                    list.Add((i.RowId, n, i.Icon));
                }
            }
            if (_itemsBySeriesId.TryGetValue(item.ItemSeries.RowId, out var members))
            {
                setMembers = members
                    .Where(m => m.id != itemId)
                    .Select(m => new SetMember(m.id, m.name, m.iconId))
                    .ToList();
            }
        }

        // Coffer-unlocked glamours (e.g. "Street Attire Coffer") have no ItemSeries at all — the
        // coffer's own contents ARE the set. Fallback only when ItemSeries found nothing.
        if (setName == null && _itemToCofferMap.TryGetValue(itemId, out var cofferIds) && cofferIds.Count > 0
            && _cofferToItemsMap != null)
        {
            var cofferId = cofferIds[0];
            var cofferName = GetItemName(cofferId);
            if (!string.IsNullOrEmpty(cofferName) && _cofferToItemsMap.TryGetValue(cofferId, out var siblingIds))
            {
                setName = cofferName;
                setMembers = siblingIds
                    .Where(id => id != itemId)
                    .Select(id =>
                    {
                        var row = itemSheet.GetRowOrDefault(id);
                        return row == null ? null : new SetMember(id, row.Value.Name.ToString(), row.Value.Icon);
                    })
                    .Where(m => m != null)
                    .Select(m => m!)
                    .ToList();
            }
        }

        var sources = BuildSources(itemId, item);
        var detail = new ItemDetail(itemId, name, itemLevel, isMarketable, iconId, sources, setName, setMembers);

        _cache[itemId] = detail;
        return detail;
    }

    private IReadOnlyList<ItemSourceDetail> BuildSources(uint itemId, Item item)
    {
        var results = new List<ItemSourceDetail>();

        // 1. Crafted â€” from Recipe sheet (grouped by material set)
        // ponytail: HQ items live at NQ RowId + 1_000_000; Recipe.ItemResult only ever points at the NQ id.
        var recipeLookupId = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
        if (_recipeByResult.TryGetValue(recipeLookupId, out var recipes))
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

        // 2. GilShop â€” Vendor
        var gilShopSources = FindGilShopSources(itemId);
        results.AddRange(gilShopSources);

        // 3. SpecialShop â€” Vendor (tomestones etc.)
        var specialShopSources = FindSpecialShopSources(itemId);
        results.AddRange(specialShopSources);

        // 4. Duty Drop from LuminaSupplemental (DungeonDrop + BossDrop + BossChest)
        if (!results.Any(s => s.Type == ItemSourceType.Dungeon || s.Type == ItemSourceType.Trial || s.Type == ItemSourceType.Raid) && _itemToDutyMap.TryGetValue(itemId, out var dutyCfcIds))
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

        // 4b. CostItem â†’ Totem boss name from "Totem Gear (X)" shop name
        if (!results.Any(s => s.Type == ItemSourceType.Dungeon || s.Type == ItemSourceType.Trial) && _totemCostToBoss.TryGetValue(itemId, out var totemInfo))
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

        // 4c. Exchange token â†’ Savage/Trial shop classification
        if (!results.Any(s => s.Type == ItemSourceType.Raid || s.Type == ItemSourceType.Trial) && _exchangeCostToShopCfcs.TryGetValue(itemId, out var exchangeInfo))
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

        // 5. Quest â€” Quest Reward
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
        if (!results.Any(s => s.Type == ItemSourceType.Fate) && _itemToFateMap.TryGetValue(itemId, out var fateIds))
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
        if (!results.Any(s => s.Type == ItemSourceType.Mob) && _itemToMobMap.TryGetValue(itemId, out var mobIds))
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

        // 5d. House Vendor â€” parent ID is the item, ENpcResidentId is the NPC
        if (!results.Any(s => s.Type == ItemSourceType.Vendor) && _shopToNpcIds.TryGetValue(itemId, out var hvNpcs) && hvNpcs.Count > 0)
        {
            var npcName = _npcNameCache.GetValueOrDefault(hvNpcs[0]);
            results.Add(new ItemSourceDetail(
                ItemSourceType.Vendor,
                $"House Vendor: {npcName ?? "Unknown"}",
                npcName, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 6. ItemSupplement (Loot, Gardening, PoD, etc. â€” NOT Desynth/Reduction/Coffer)
        // Coffer excluded here too: it falls into the switch's default branch below and builds
        // "Coffer: {name}" (ItemSourceType.Other) — the EXACT same text 6b builds properly typed
        // as ItemSourceType.Coffer from the same underlying data (_itemToCofferMap, built from
        // this same CSV filtered to Source==Coffer). Two source-type-different cards, identical
        // wording, for the same coffer — a real duplicate, not the trial/coffer MERGE case above
        // (that one is two genuinely different descriptions co-occurring; this was one description
        // rendered twice).
        if (_itemSupplementCache.TryGetValue(itemId, out var supplements))
        {
            var relevant = supplements
                .Where(s => s.ItemSupplementSource != ItemSupplementSource.Desynth
                         && s.ItemSupplementSource != ItemSupplementSource.Reduction
                         && s.ItemSupplementSource != ItemSupplementSource.Coffer)
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
                    null, null, null, null, null, null, null, null, null, SourceItemId: supp.SourceItemId));
            }
        }

        // 6b. ItemSupplement Coffer â€” coffer items and their contents
        if (!results.Any(s => s.Type == ItemSourceType.Coffer) && _itemToCofferMap.TryGetValue(itemId, out var cofferIds))
        {
            var cofferSheet = _gameData.GetExcelSheet<Item>();
            var cofferName = string.Join(", ", cofferIds.Select(id => cofferSheet?.GetRow(id).Name.ExtractText() ?? $"{id}"));
            results.Add(new ItemSourceDetail(
                ItemSourceType.Coffer, $"Coffer: {cofferName}",
                null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 6c. Achievement unlock â†’ Item (via SpecialShop.ItemStruct.AchievementUnlock)
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

        // 6d. FieldOpCoffer â€” Pagos/Pyros/Hydatos/Occult chests
        if (!results.Any(s => s.Type == ItemSourceType.Coffer) && _itemToFieldOpCofferMap.TryGetValue(itemId, out var fieldOpEntries))
        {
            var desc = string.Join("; ", fieldOpEntries.Select(e => $"{e.Type} {e.CofferType}"));
            results.Add(new ItemSourceDetail(
                ItemSourceType.Coffer, $"Field Op Coffer: {desc}",
                null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null));
        }

        // 7. PvP – SpecialShop (tome currencies), PvPSeries tier rewards
        if (!results.Any(s => s.Type == ItemSourceType.PvP))
        {
            if (_pvpVendorItems.Contains(itemId))
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.PvP, "PvP Vendor Reward",
                    null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null, null));
            }
            else if (_pvpItemToSeason.TryGetValue(itemId, out var seasonId))
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.PvP,
                    seasonId == _currentPvpSeasonId
                    ? $"PvP Season {seasonId} (currently available)"
                    : $"PvP Season {seasonId} (series ended)",
                    null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null, null));
            }
        }

        // 7b. Gathering sources
        if (!results.Any(s => s.Type == ItemSourceType.Gathering) && _itemToGatheringCache.TryGetValue(itemId, out var gatheringNodes))
        {
            foreach (var g in gatheringNodes)
            {
                var nodeTypeName = g.GatheringType switch
                {
                    0 => "Miner",
                    1 => "Miner",
                    2 => "Botanist",
                    3 => "Botanist",
                    _ => "Unknown"
                };
                var zoneSuffix = string.IsNullOrEmpty(g.ZoneName) ? "" : $" ({g.ZoneName})";
                var desc = $"{nodeTypeName} Lv.{g.GatheringLevel}{zoneSuffix}";
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Gathering,
                    desc,
                    null, null,
                    g.MapX, g.MapY,
                    g.TerritoryTypeId, g.MapId,
                    null, null, null, null, null, null, null, null, null));
            }
        }

        // 7c. Fishing sources
        if (!results.Any(s => s.Type == ItemSourceType.Gathering) && _itemToFishingCache.TryGetValue(itemId, out var fishingSpots))
        {
            foreach (var f in fishingSpots)
            {
                var zoneSuffix = string.IsNullOrEmpty(f.ZoneName) ? "" : $" ({f.ZoneName})";
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Gathering,
                    $"Fisher Lv.{f.GatheringLevel}{zoneSuffix}",
                    null, null,
                    f.MapX, f.MapY,
                    f.TerritoryTypeId, f.MapId,
                    null, null, null, null, null, null, null, null, null));
            }
        }

        // 7d. Triple Triad cards â€” gated on the item's own UI category first so this only ever
        // touches actual card items, then resolves the physical card -> TripleTriadCard RowId via
        // its ItemAction (Data[0], same "unlock item points at a definition row" pattern used by
        // minions/mounts), and looks that up in the scraped NPC table.
        if (item.ItemUICategory.IsValid && item.ItemUICategory.Value.Name.ToString() == "Triple Triad Card"
            && item.ItemAction.IsValid && item.ItemAction.Value.Data.Count > 0)
        {
            var cardRowId = item.ItemAction.Value.Data[0];
            if (_triadCardNpcs.TryGetValue(cardRowId, out var npcs))
            {
                foreach (var n in npcs)
                {
                    results.Add(new ItemSourceDetail(
                        ItemSourceType.TripleTriad,
                        "Won from an NPC in a Triple Triad match",
                        n.NpcName, n.ZoneName,
                        n.MapX, n.MapY,
                        null, null,
                        null, null, null, null, null, null, null, null, null));
                }
            }
        }

        // 8. Mogstation â€” always shown additively alongside any other detected sources. Live
        // lookup first (fresh, real per-item wiki link); static CSV as fallback (older items, or
        // this session's fetch hasn't completed/succeeded yet — see MogStationLiveService).
        var englishName = GetEnglishName(itemId) ?? item.Name.ToString();
        if (_mogstationLive.TryGetShopUrl(englishName, out var liveShopUrl))
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.MogStation,
                "Available for purchase on the Mog Station.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                ShopUrl: liveShopUrl));
        }
        else if (_mogstationItems.TryGetValue(itemId, out var shopUrl))
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.MogStation,
                "Available for purchase on the Mog Station.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                ShopUrl: shopUrl));
        }

        // 8b. Minion/mount sources (FFXIV Collect, non-commercial use, attribution appreciated) â€”
        // always shown additively, same reasoning as Mogstation above.
        if (_collectSources.TryGetValue(itemId, out var collectEntries))
        {
            foreach (var c in collectEntries)
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    $"{c.Kind}: {c.SourceType} - {c.SourceText} (via FFXIV Collect)",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            }
        }

        // 9. Removed equipment slot â€” the game itself files these under ItemUICategory
        // "Unobtainable". In the 500-item audit every hit here was a belt: Stormblood (4.0) removed
        // the belt slot entirely, so all belt items became permanently unobtainable/glamour-only.
        // Check the game's own category before any name-pattern guessing below.
        if (results.Count == 0 && item.ItemUICategory.IsValid && item.ItemUICategory.Value.Name.ToString() == "Unobtainable")
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                "Unobtainable — the game itself classifies this item as no longer acquirable (e.g. gear for a since-removed equipment slot, such as belts after Stormblood).",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        }

        // 9b. Retired dye â€” patch 7.5 consolidated most named dyes into a "Spectrum Dye" system
        // (Calamity Salvager exchanges old dyes for the new ones). Verified via the wiki, and the
        // signal holds up locally: every retired dye in the audit had an empty ItemSearchCategory
        // (no longer searchable/purchasable) despite still carrying the "Dye" UI category.
        if (results.Count == 0 && item.ItemUICategory.IsValid && item.ItemUICategory.Value.Name.ToString() == "Dye"
            && item.ItemSearchCategory.RowId == 0)
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                "Retired dye — patch 7.5 consolidated most named dyes into the Spectrum Dye system. Exchange it for a current dye at a Calamity Salvager.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        }

        // 9c. Materia / Gardening seeds â€” not "sourced" from a place at all, they come from a
        // player-driven system: materia converts out of 100%-spiritbonded gear, garden seeds come
        // from cross-breeding other seeds in a plot. No location to show, just say so.
        if (results.Count == 0 && item.ItemUICategory.IsValid)
        {
            var uiCat = item.ItemUICategory.Value.Name.ToString();
            if (uiCat == "Materia")
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    "Materia — converted from fully spiritbonded (100%) equipment, not purchased or dropped directly.",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            }
            else if (uiCat == "Gardening")
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    "Garden seed — obtained by cross-breeding compatible seeds in a garden plot, not purchased directly.",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            }
        }

        // 10. 1.0-legacy gear â€” "Dated X" / "Weathered X" items predate patch 1.19; only players who
        // transferred a character from 1.0 have them, permanently unobtainable since (verified via
        // Gamer Escape's Dated/Weathered item categories, and a dev forum post on the Weathered
        // Sledgehammer/Scythe). This alone was ~27% of a random 500-item source-coverage sample,
        // worth its own message instead of the generic "may be a rare drop" fallback.
        if (results.Count == 0)
        {
            var itemName = item.Name.ToString();
            if (itemName.StartsWith("Dated ", StringComparison.Ordinal) || itemName.StartsWith("Weathered ", StringComparison.Ordinal))
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    "Legacy 1.0 item — predates patch 1.19. Only players who transferred a character from the original FFXIV 1.0 have this; permanently unobtainable otherwise.",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            }
            // ponytail: "Aetherial X" gear (untradable, randomly-rolled bonus stat) drops from ARR
            // dungeon treasure chests or Battlecraft Leve rewards — an RNG loot pool, not any one
            // fixed dungeon/leve, so no single location to point at (verified via the wiki). Second
            // most common leftover name pattern after Dated/Weathered in the same audit sample.
            else if (itemName.StartsWith("Aetherial ", StringComparison.Ordinal))
            {
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    "Aetherial gear — dropped from ARR-era dungeon treasure chests or awarded from Battlecraft Leves. Random loot pool, not tied to one specific dungeon/leve.",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
            }
        }

        // 11. Retired-and-superseded â€” the common "no current source" case turns out to be: gear
        // whose vendor listing got replaced by an "Augmented X" upgrade of the same item (verified
        // against the wiki for Ironworks Armguards of Maiming: retired as of patch 5.3, only the
        // Augmented version is still purchasable). Detectable locally: an item named "Augmented "
        // + this item's name exists. Worth its own message instead of the generic fallback.
        if (results.Count == 0)
        {
            var augmentedId = FindItemIdByExactName("Augmented " + item.Name.ToString());
            if (augmentedId is { } augId)
            {
                var augName = GetItemName(augId) ?? "its augmented upgrade";
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Other,
                    $"Retired — replaced by {augName}. No longer obtainable itself; the augmented upgrade is still purchasable.",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                    SourceItemId: augId));
            }
        }

        // 11b. Merge a token-exchange Trial/Raid Drop entry with a coffer "Obtained from" entry for
        // the SAME item — "der coffer gibts aus der duty und sonst nirgends": live example was
        // "Trial Drop: Totem Gear (Enuo)" + "Obtained from: Weapon Coffer of Naught" showing as two
        // separate cards for what's really one acquisition path (kill the trial, get either the
        // exchange currency or the coffer). Our own data has no independent "which duty does this
        // coffer drop from" — the coffer's own detail literally shows "no known current source" for
        // the tested example — so this only merges when they co-occur on the same item, not a
        // verified cross-reference; safe because a coffer with a genuinely different source would
        // show its OWN card correctly if this item ever also has a second matching duty entry.
        var exchangeIdx = results.FindIndex(s => (s.Type == ItemSourceType.Trial || s.Type == ItemSourceType.Raid) && s.CfcRowIds != null);
        var cofferIdx = results.FindIndex(s => s.Type == ItemSourceType.Other && s.Description.StartsWith("Obtained from: ", StringComparison.Ordinal) && s.SourceItemId != null);
        if (exchangeIdx >= 0 && cofferIdx >= 0)
        {
            var exchange = results[exchangeIdx];
            var coffer = results[cofferIdx];
            var merged = exchange with { Description = $"{exchange.Description} — {coffer.Description}" };
            results.RemoveAt(Math.Max(exchangeIdx, cofferIdx));
            results.RemoveAt(Math.Min(exchangeIdx, cofferIdx));
            results.Add(merged);
        }

        // 12. Generic fallback â€” nothing found, and not a known legacy/retired/superseded item either.
        // Verified against live game data (not just our own sheets): items that land here
        // genuinely have no current recipe/vendor/duty-drop entry.
        if (results.Count == 0)
        {
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                "No known current source. Often old gear that's been rotated out of its vendor over patches — may still be a rare drop, achievement, or account-bound reward we don't track.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        }

        return results;
    }

    private uint? FindItemIdByExactName(string name)
    {
        if (_itemIdByName == null)
        {
            _itemIdByName = new Dictionary<string, uint>();
            foreach (var i in _gameData.GetExcelSheet<Item>()!)
            {
                var n = i.Name.ToString();
                if (!string.IsNullOrEmpty(n) && !_itemIdByName.ContainsKey(n))
                    _itemIdByName[n] = i.RowId;
            }
        }
        return _itemIdByName.TryGetValue(name, out var id) ? id : null;
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
                    // ponytail: name-only fallback before generic "Merchant"
                    var nameOnly = _shopNpcNameOnly.GetValueOrDefault(shopId);
                    allSources.Add(new ItemSourceDetail(
                        ItemSourceType.Vendor,
                        $"Vendor: {nameOnly ?? "Merchant"}",
                        nameOnly, null, null, null, null, null,
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

        // Recipe â†’ result cache
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

        // NPC name cache (ENpcResident â†’ singular name)
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

        // Shop â†’ NPC/Zone reverse map
        BuildShopNpcCache();
    }

    private void BuildShopNpcCache()
    {
        var enpcBaseSheet = _gameData.GetExcelSheet<ENpcBase>();
        var levelSheet = _gameData.GetExcelSheet<Level>();
        var mapSheet = _gameData.GetExcelSheet<Map>();

        if (enpcBaseSheet == null || levelSheet == null || mapSheet == null)
            return;

        // Stage 0: direct EventHandlerType.SpecialShop match on ENpcData (see ItemVendorLocation
        // reference plugin). Level/LGB stages below miss NPCs whose only shop link is an
        // EventHandler entry with no physical placement (e.g. quest/cutscene-triggered shops).
        // ponytail: additive only, never overwrites an entry the existing stages already found.
        var specialShopSheet = _gameData.GetExcelSheet<SpecialShop>();
        if (specialShopSheet != null)
        {
            const uint SpecialShopEventHandlerType = 0x001B;
            foreach (var npcBase in enpcBaseSheet)
            {
                var npcId = npcBase.RowId;
                if (!_npcNameCache.TryGetValue(npcId, out var npcName) || string.IsNullOrEmpty(npcName))
                    continue;

                foreach (var dataRef in npcBase.ENpcData)
                {
                    var data = dataRef.RowId;
                    if ((data >> 16) != SpecialShopEventHandlerType)
                        continue;

                    var shopId = data;
                    if (!specialShopSheet.HasRow(shopId))
                        continue;
                    if (_shopNpcLookup.ContainsKey(shopId) || _shopNpcNameOnly.ContainsKey(shopId))
                        continue;

                    _shopNpcNameOnly[shopId] = npcName;
                }
            }

            // Stage 0b: CustomTalk-gated SpecialShop (see ItemVendorLocation reference plugin).
            // Some NPCs (e.g. Calamity Salvager) link their shop through a CustomTalk dialogue
            // script instead of a direct ENpcData entry - the SpecialShop id sits behind
            // CustomTalk.SpecialLinks -> CustomTalkNestHandlers, or raw in the talk script args.
            // ponytail: additive only, never overwrites an entry an earlier stage already found.
            const uint CustomTalkEventHandlerType = 0x000B;
            var customTalkSheet = _gameData.GetExcelSheet<CustomTalk>();
            var customTalkNestHandlers = _gameData.GetSubrowExcelSheet<CustomTalkNestHandlers>();
            if (customTalkSheet != null && customTalkNestHandlers != null)
            {
                foreach (var npcBase in enpcBaseSheet)
                {
                    var npcId = npcBase.RowId;
                    if (!_npcNameCache.TryGetValue(npcId, out var npcName) || string.IsNullOrEmpty(npcName))
                        continue;

                    foreach (var dataRef in npcBase.ENpcData)
                    {
                        var data = dataRef.RowId;
                        if ((data >> 16) != CustomTalkEventHandlerType)
                            continue;

                        var customTalk = customTalkSheet.GetRowOrDefault(data);
                        if (!customTalk.HasValue)
                            continue;

                        void TryAddSpecialShop(uint candidateId)
                        {
                            if ((candidateId >> 16) != SpecialShopEventHandlerType)
                                return;
                            if (!specialShopSheet.HasRow(candidateId))
                                return;
                            if (_shopNpcLookup.ContainsKey(candidateId) || _shopNpcNameOnly.ContainsKey(candidateId))
                                return;

                            _shopNpcNameOnly[candidateId] = npcName;
                        }

                        var nestRowId = customTalk.Value.SpecialLinks.RowId;
                        if (nestRowId != 0)
                        {
                            for (ushort index = 0; index <= 30; index++)
                            {
                                var nestHandler = customTalkNestHandlers.GetSubrowOrDefault(nestRowId, index);
                                if (!nestHandler.HasValue)
                                    break;

                                TryAddSpecialShop(nestHandler.Value.NestHandler.RowId);
                            }
                        }

                        foreach (var script in customTalk.Value.Script)
                            TryAddSpecialShop((uint)script.ScriptArg);
                    }
                }
            }
        }

        // Level lookup: ENpcBase.RowId â†’ (MapId, Map, X, Z)
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

        // ENpcBase â†’ Shop RowIds
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

                    var supInfo = new NpcLocationInfo(
                        npcName, supZoneName, supMapX, supMapY, supTerritoryTypeId, supMapId);
                    foreach (var dataRef in npcBase.ENpcData)
                    {
                        if (dataRef.RowId == 0)
                            continue;
                        RegisterShopBinding(dataRef.RowId, supInfo, npcName, new HashSet<uint>());
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

            var levelInfoRecord = new NpcLocationInfo(
                npcName, zoneName, mapX, mapY, territoryTypeId, mapId);
            foreach (var dataRef in npcBase.ENpcData)
            {
                if (dataRef.RowId == 0)
                    continue;
                RegisterShopBinding(dataRef.RowId, levelInfoRecord, npcName, new HashSet<uint>());
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
                                var lgbInfo = new NpcLocationInfo(
                                    npcName, zoneName, mapX, mapY,
                                    territory.RowId, map.Value.RowId);
                                foreach (var dataRef in npcBase.ENpcData)
                                {
                                    if (dataRef.RowId == 0) continue;
                                    RegisterShopBinding(dataRef.RowId, lgbInfo, npcName, new HashSet<uint>());
                                }

                                missingNpcs.Remove(npcId);
                            }
                        }
                    }
                    catch
                    {
                        // LGB file missing or corrupt â€” skip
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
                if (dataRef.RowId > 0)
                    RegisterShopBinding(dataRef.RowId, null, npcName, new HashSet<uint>());
            }
        }
    }

    // ponytail: IVL-style menu unwrap (ItemVendorLocation ItemLookup.AddItem.cs) — ENpcData often
    // holds PreHandler (0x0036) / TopicSelect (0x0032) / InclusionShop (0x003A) wrapper ids, not the
    // GilShop/SpecialShop id our lookups expect. Recurse to the terminal shop ids and register those.
    // info == null → name-only registration (Stage 3 semantics).
    private void RegisterShopBinding(uint rowId, NpcLocationInfo? info, string npcName, HashSet<uint> visited)
    {
        if (rowId == 0 || !visited.Add(rowId)) return;

        try
        {
            switch (rowId >> 16)
            {
                case 0x0036: // PreHandler → Target
                    var pre = _gameData.GetExcelSheet<PreHandler>()?.GetRowOrDefault(rowId);
                    if (pre != null)
                        RegisterShopBinding(pre.Value.Target.RowId, info, npcName, visited);
                    return;
                case 0x0032: // TopicSelect → Shop[]
                    var topic = _gameData.GetExcelSheet<TopicSelect>()?.GetRowOrDefault(rowId);
                    if (topic != null)
                        foreach (var shopRef in topic.Value.Shop)
                            RegisterShopBinding(shopRef.RowId, info, npcName, visited);
                    return;
                case 0x003A: // InclusionShop → Category → InclusionShopSeries subrows → SpecialShop
                    var inc = _gameData.GetExcelSheet<InclusionShop>()?.GetRowOrDefault(rowId);
                    if (inc != null)
                    {
                        var seriesSheet = _gameData.GetSubrowExcelSheet<InclusionShopSeries>();
                        foreach (var category in inc.Value.Category)
                        {
                            if (category.RowId == 0) continue;
                            var seriesId = category.Value.InclusionShopSeries.RowId;
                            for (ushort i = 0; ; i++)
                            {
                                var series = seriesSheet?.GetSubrowOrDefault(seriesId, i);
                                if (series == null) break;
                                RegisterShopBinding(series.Value.SpecialShop.RowId, info, npcName, visited);
                            }
                        }
                    }
                    return;
            }
        }
        catch (Lumina.Excel.Exceptions.MismatchedColumnHashException)
        {
            return;
        }

        // Terminal id (GilShop/SpecialShop/GcShop/FcShop/CollectablesShop/…)
        if (info != null)
        {
            if (!_shopNpcLookup.ContainsKey(rowId))
                _shopNpcLookup[rowId] = new();
            // ponytail: the same NPC/shop binding can resolve here via more than one menu path
            // (e.g. a direct link AND a PreHandler/TopicSelect wrapper pointing at the same
            // terminal shop) — without this check the exact same vendor+location+cost row showed
            // up twice in the item detail UI ("unübersichtlich", confirmed via a real screenshot:
            // identical "Mesouaidonque, Sinus Ardorum (21.9,21.9)" card duplicated).
            if (!_shopNpcLookup[rowId].Contains(info))
                _shopNpcLookup[rowId].Add(info);
        }
        if (!string.IsNullOrEmpty(npcName) && !_shopNpcNameOnly.ContainsKey(rowId))
            _shopNpcNameOnly[rowId] = npcName;
    }

    // ponytail: all nodes per item (level + position vary by zone)
    // Performance: O(n+m) statt O(n×m) durch Index-Erstellung
    // Dedupliziere GatheringInfos: gleiche Zone+Level = eine Card, aber mit Node-Count
    private static List<GatheringInfo> DeduplicateGatheringInfos(List<GatheringInfo> infos)
    {
        if (infos.Count <= 1)
            return infos;

        var grouped = infos
            .GroupBy(g => new { g.ZoneName, g.GatheringLevel })
            .Select(g =>
            {
                var first = g.First();
                // Wenn nur ein Node, keine Anpassung nötig
                if (g.Count() == 1)
                    return first;

                // Mehrere Nodes → Zone anpassen um Count anzuzeigen
                var nodeCount = g.Count();
                var modifiedZone = nodeCount > 1 ? $"{first.ZoneName} ({nodeCount} nodes)" : first.ZoneName;
                return new GatheringInfo(
                    first.GatheringLevel,
                    first.GatheringType,
                    modifiedZone,
                    first.MapX, first.MapY,
                    first.TerritoryTypeId,
                    first.MapId);
            })
            .ToList();

        return grouped;
    }

    private void BuildGatheringCache()
    {
        var gatheringPointBaseSheet = _gameData.GetExcelSheet<GatheringPointBase>();
        if (gatheringPointBaseSheet == null)
            return;

        var gatheringPointSheet = _gameData.GetExcelSheet<GatheringPoint>();
        if (gatheringPointSheet == null)
            return;

        var exportedGatheringPointSheet = _gameData.GetExcelSheet<ExportedGatheringPoint>();
        var gatheringItemSheet = _gameData.GetExcelSheet<GatheringItem>();

        // Index: GatheringPointBase.RowId -> Liste von GatheringPoints (O(m) einmal)
        var pointsByBase = gatheringPointSheet
            .Where(gp => gp.TerritoryType.RowId > 0)  // Filterung: nur gültige TerritoryType
            .GroupBy(gp => gp.GatheringPointBase.RowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Iteriere nur über GatheringPointBase, Lookup ist O(1) (O(n) mal O(1))
        foreach (var point in gatheringPointBaseSheet)
        {
            if (!pointsByBase.TryGetValue(point.RowId, out var matchingPoints))
                continue;

            var coordRow = exportedGatheringPointSheet?.GetRowOrDefault(point.RowId);

            foreach (var gatheringPoint in matchingPoints)
            {
                var territoryType = gatheringPoint.TerritoryType.ValueNullable;
                if (territoryType == null)
                    continue;

                var tt = territoryType.Value;
                var mapNode = tt.Map.ValueNullable;
                if (mapNode == null)
                    continue;

                var map = mapNode.Value;
                var zoneName = tt.PlaceName.ValueNullable?.Name.ToString() ?? "";
                var territoryTypeId = tt.RowId;
                var mapId = map.RowId;
                var sizeFactor = map.SizeFactor;
                var offsetX = map.OffsetX;
                var offsetY = map.OffsetY;

                // Koordinaten von ExportedGatheringPoint
                float mapX = 0f, mapY = 0f;
                if (coordRow != null)
                {
                    mapX = ToMapCoordinate(coordRow.Value.X, sizeFactor, offsetX);
                    mapY = ToMapCoordinate(coordRow.Value.Y, sizeFactor, offsetY);
                }

                var level = (int)point.GatheringLevel;
                var type = (int)point.GatheringType.RowId;

                // point.Item enthält GatheringItem-RowIds, muss über GatheringItem->Item.RowId aufgelöst werden
                foreach (var itemRef in point.Item)
                {
                    if (itemRef.RowId == 0)
                        continue;

                    // GatheringItem-Row auflösen
                    var gatheringItemRow = gatheringItemSheet?.GetRowOrDefault(itemRef.RowId);
                    if (gatheringItemRow == null)
                        continue;

                    // Echte Item-ID aus gatheringItem.Item.RowId
                    var itemId = gatheringItemRow.Value.Item.RowId;
                    if (itemId == 0)
                        continue;

                    if (!_itemToGatheringCache.TryGetValue(itemId, out var list))
                    {
                        list = new List<GatheringInfo>();
                        _itemToGatheringCache[itemId] = list;
                    }

                    list.Add(new GatheringInfo(level, type, zoneName, mapX, mapY, territoryTypeId, mapId));
                }
            }
        }

        // Cleanup: keine Debug-Logs mehr

        // Dedupliziere alle Einträge
        foreach (var key in _itemToGatheringCache.Keys.ToList())
        {
            if (_itemToGatheringCache[key].Count > 1)
            {
                _itemToGatheringCache[key] = DeduplicateGatheringInfos(_itemToGatheringCache[key]);
            }
        }
    }

    // ponytail: FishingSpot.Item[] holds Item RowIds directly (no GatheringItem indirection like
    // Botanist/Miner nodes) — verified against a known fish (Silverfish, item 4978) locally.
    // BigFishOnReach/OnEnd/OnRefresh (spearfishing/mooch "!" chains) are skipped: separate
    // mechanic, out of scope for a first pass.
    private void BuildFishingCache()
    {
        var spotSheet = _gameData.GetExcelSheet<FishingSpot>();
        if (spotSheet == null)
            return;

        foreach (var spot in spotSheet)
        {
            var tt = spot.TerritoryType.ValueNullable;
            if (tt == null)
                continue;

            var mapNode = tt.Value.Map.ValueNullable;
            if (mapNode == null)
                continue;

            var map = mapNode.Value;
            var zoneName = spot.PlaceName.ValueNullable?.Name.ToString() ?? "";
            var mapX = ToMapCoordinate(spot.X, map.SizeFactor, map.OffsetX);
            var mapY = ToMapCoordinate(spot.Z, map.SizeFactor, map.OffsetY);

            foreach (var itemRef in spot.Item)
            {
                if (itemRef.RowId == 0)
                    continue;

                if (!_itemToFishingCache.TryGetValue(itemRef.RowId, out var list))
                {
                    list = new List<FishingInfo>();
                    _itemToFishingCache[itemRef.RowId] = list;
                }
                list.Add(new FishingInfo((int)spot.GatheringLevel, zoneName, mapX, mapY, tt.Value.RowId, map.RowId));
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
        BuildPvpItemCache();
        BuildMogstationCache();
        BuildTriadCardNpcCache();
        BuildCollectSourceCache();
        BuildMountItemMapCache();
    }

    // ponytail: MogstationItems.csv is our own static scrape, not a LuminaSupplemental package
    // resource, so it can't go through CsvLoader.LoadResource<T> â€” read it as our own embedded
    // resource instead.
    private void BuildMogstationCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.MogstationItems.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',', 3);
            if (parts.Length < 3)
                continue;
            if (!uint.TryParse(parts[0], out var itemId))
                continue;

            _mogstationItems[itemId] = parts[2];
        }
    }

    private void BuildTriadCardNpcCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.TripleTriadCardNpcs.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 5)
                continue;
            if (!uint.TryParse(parts[0], out var cardRowId))
                continue;
            if (!float.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var mapX))
                continue;
            if (!float.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, out var mapY))
                continue;

            if (!_triadCardNpcs.TryGetValue(cardRowId, out var list))
            {
                list = new List<TriadCardNpc>();
                _triadCardNpcs[cardRowId] = list;
            }
            list.Add(new TriadCardNpc(parts[1], parts[2], mapX, mapY));
        }
    }

    private void BuildCollectSourceCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.CollectSources.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',', 4);
            if (parts.Length < 4)
                continue;
            if (!uint.TryParse(parts[0], out var itemId))
                continue;

            if (!_collectSources.TryGetValue(itemId, out var list))
            {
                list = new List<CollectSource>();
                _collectSources[itemId] = list;
            }
            list.Add(new CollectSource(parts[1], parts[2], parts[3]));
        }
    }

    private void BuildMountItemMapCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.MountItemMap.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',', 3);
            if (parts.Length < 2)
                continue;
            if (!uint.TryParse(parts[0], out var mountId))
                continue;
            if (!uint.TryParse(parts[1], out var itemId))
                continue;
            _mountToItemId[mountId] = itemId;
        }
    }

    /// <summary>MountId (as read from Character.Mount.MountId natively) -> its unlock item, or null
    /// if this mount isn't in the scraped dataset.</summary>
    public uint? ResolveMountItemId(uint mountId) => _mountToItemId.TryGetValue(mountId, out var id) ? id : null;

    private ExcelSheet<Item>? _englishItemSheet;

    /// <summary>Item name in English regardless of the client's configured language. Needed for
    /// anything keyed off the item name against an English-only source — the item preview image
    /// wiki has no localized page titles, so a German/French/JP client name 404'd there ("Freiherrliche
    /// Jacke" -> no such page; live-confirmed via /api/debug/imageerror).</summary>
    public string? GetEnglishName(uint itemId)
    {
        _englishItemSheet ??= _gameData.GetExcelSheet<Item>(Language.English);
        var name = _englishItemSheet?.GetRowOrDefault(itemId)?.Name.ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void BuildPvpItemCache()
    {
        // 1. SpecialShop â€” items costing tomestones (Wolf Marks 25, Trophy Crystals 36656)
        var specialShops = _gameData.GetExcelSheet<SpecialShop>()?.ToArray() ?? Array.Empty<SpecialShop>();
        
        foreach (var shop in specialShops)
        {
            var shopName = shop.Name.ToString();
            if (string.IsNullOrEmpty(shopName)) continue;
            var costs = new[] { "Wolf Mark", "Trophy Crystal" };
            if (!costs.Any(c => shopName.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;

            foreach (var itemStruct in shop.Item)
            {
                foreach (var receiveItem in itemStruct.ReceiveItems)
                {
                    _pvpVendorItems.Add(receiveItem.Item.RowId);
                    
                }
            }
        }

        // 2. PvPSeries — tier rewards (Malmstone)
        var pvpSeriesSheet = _gameData.GetExcelSheet<PvPSeries>();
        if (pvpSeriesSheet != null)
        {
            foreach (var row in pvpSeriesSheet)
            {
                if (row.RowId > _currentPvpSeasonId)
                    _currentPvpSeasonId = row.RowId;

                foreach (var levelReward in row.LevelRewards)
                {
                    foreach (var itemRef in levelReward.LevelRewardItem)
                    {
                        if (itemRef.RowId > 0)
                            _pvpItemToSeason[itemRef.RowId] = row.RowId;
                    }
                }
            }
        }
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

        // ponytail: ENpcShop has ShopIdâ†’ENpcResidentId, but we lack Shopâ†’Item mapping here.
        // Keep HouseVendor only (ItemIdâ†’ENpcResidentId) for the vendor lookup path.
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

        _cofferToItemsMap = new Dictionary<uint, List<uint>>();
        foreach (var (itemId, cofferIds) in _itemToCofferMap)
            foreach (var cofferId in cofferIds)
            {
                if (!_cofferToItemsMap.TryGetValue(cofferId, out var list))
                    _cofferToItemsMap[cofferId] = list = new();
                list.Add(itemId);
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

    // ponytail: ItemId â†’ List<Achievement RowId> via SpecialShop.ItemStruct.AchievementUnlock
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

        // Stage 1: Hardcoded Bossâ†’CFC RowId map
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
