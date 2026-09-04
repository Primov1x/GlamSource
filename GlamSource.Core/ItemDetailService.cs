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
    IReadOnlyList<SetMember>? SetMembers = null,
    bool IsEquippable = false,
    IReadOnlyList<SetMember>? Contents = null, // this item IS a coffer/sack — what it can contain (clickable, small pools)
    IReadOnlyList<ContentsCategory>? ContentsSummary = null); // same idea but for huge pools (Accursed Hoard sacks) — grouped by category, expandable to the real item list

public record SetMember(uint ItemId, string Name, uint IconId);

/// One collapsed "Nx Category" row under ContentsSummary — Items holds the real list so the UI can expand it on click.
public record ContentsCategory(string Label, uint IconId, IReadOnlyList<SetMember> Items);

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

/// One duty (ContentFinderCondition) that has at least one known drop.
/// Bosses: fight names in order (Susano, Titan, ...) so the tab's search finds a duty by its boss.
/// Difficulty: "Normal" / "Extreme" / "Savage" / "Unreal" / "Alliance" — the Duty Finder's sub-folders.
public sealed record DutyInfo(uint CfcId, string Name, string Type, uint TerritoryTypeId, int DropCount, uint ImageId, int Level, int ItemLevel, string Expansion, uint TypeIconId, IReadOnlyList<string> Bosses, string Difficulty);
/// Kind: "Mount" (item of a MountItemMap entry) / "Minion" (ItemUICategory 81) / "" — the tab lifts those to the top.
/// Unlocked: null unless Kind is Mount/Minion — this record lives in GlamSource.Core (no clib access),
/// so it's always null here; the Services layer (which CAN call PlayerState/UIState) fills it in
/// after the fact via `with` before the data reaches a UI, see UnlockCheckService.
public sealed record DutyDrop(uint ItemId, string Name, uint IconId, int ItemLevel, string Kind = "", bool? Unlocked = null);
public sealed record DutyChest(int CofferNo, IReadOnlyList<DutyDrop> Items);
public sealed record DutyBoss(int FightNo, string Name, IReadOnlyList<DutyDrop> Drops, IReadOnlyList<DutyChest> Chests);
/// Duty Finder style detail: banner image (ContentFinderCondition.Image), per-boss drops and
/// chests (LuminaSupplemental DungeonBoss / DungeonBossDrop / DungeonBossChest), plus the
/// duty-wide DungeonDrop list.
/// An exchange shop that belongs to the duty: "Totem Gear (Zelenia)" for its totem, the savage
/// book exchange, ... Token = what the duty drops to pay with (null when unknown).
public sealed record DutyExchange(string Shop, DutyDrop? Token, IReadOnlyList<DutyDrop> Items);
public sealed record DutyDetail(uint CfcId, string Name, string Type, uint ImageId, int Level, int ItemLevel,
    IReadOnlyList<DutyBoss> Bosses, IReadOnlyList<DutyDrop> General, uint TerritoryTypeId, uint MapId,
    IReadOnlyList<DutyDrop> Featured, IReadOnlyList<DutyExchange> Exchanges);
/// A treasure coffer along the way (Garland Tools), with its map coordinates.
/// FightNo >= 0 = boss coffer of that fight (Garland, no coordinates), -1 = placed chest with X/Y.
public sealed record DutyCoffer(float X, float Y, IReadOnlyList<DutyDrop> Items, int FightNo = -1);

public interface IItemDetailService
{
    ItemDetail? GetDetail(uint itemId);
    /// Duty Drops tab: every duty with a known drop table, sorted by type then name.
    IReadOnlyList<DutyInfo> ListDutiesWithDrops();
    /// Duty Drops tab: one duty's banner, bosses, chests and drops (iLvl descending inside each list).
    DutyDetail? GetDutyDetail(uint cfcId);
    /// Duty Drops tab: treasure coffers along the way (Garland Tools, live), minus the boss coffers
    /// GetDutyDetail already lists. Empty without a Garland service or when Garland has nothing.
    Task<IReadOnlyList<DutyCoffer>> GetDutyCoffersAsync(uint cfcId);
    /// Duty Drops tab auto-detect: the ContentFinderCondition for the territory the player is in
    /// (prefers one we have drops for), null outside duties.
    uint? FindDutyByTerritory(uint territoryTypeId);
    GameData GameData { get; }
    uint? ResolveMountItemId(uint mountId);
    /// Item -> Mount/Companion(minion) sheet RowId, resolved natively from Item.ItemAction (Action
    /// RowId 1322 = Mount, 853 = Companion; Data[0] = the target RowId) — no external dataset needed.
    /// Null when the item isn't a mount/minion unlock item. Feeds UnlockCheckService (Services/,
    /// has clib access this project doesn't) to check PlayerState/UIState unlock status.
    uint? MountRowIdForItem(uint itemId);
    uint? CompanionRowIdForItem(uint itemId);
    string? GetEnglishName(uint itemId);
    /// Event item availability (FFXIV Collect "Event" sources + a live Lodestone news check).
    /// Null when the item isn't a known event item.
    Task<EventStatus?> GetEventStatusAsync(uint itemId);
    /// Wiki page to scrape the preview picture from: the MOUNT page for mount items ("Enbarr" has
    /// Enbarr_Image.png, "Enbarr Whistle" only an icon), otherwise the English item name.
    string? GetWikiPageName(uint itemId);
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

    // Deep Dungeon checkpoint-row suffix: most use "(Floors X-Y)" (Palace of the Dead, Heaven-on-
    // High, Eureka Orthos), but Pilgrim's Traverse — Eureka Orthos's own newer sibling — uses
    // "(Stones X-Y)" instead. Live report: "fix pilgrim's traverse (stones) auch" — the merge/dedup
    // logic below only matched "Floors", so its 10 checkpoint rows never got merged into one tile.
    // One shared pattern so every place that merges/strips this suffix stays in sync.
    private const string DeepDungeonCheckpointWord = "(?:Floors?|Stones)";
    private static readonly Regex DeepDungeonCheckpointSuffix = new($@"\s*\({DeepDungeonCheckpointWord} \d+-\d+\)\s*$");
    private static readonly Regex DeepDungeonCheckpointRange = new($@"\({DeepDungeonCheckpointWord} (\d+)-(\d+)\)\s*$");

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

    // "auch prüfen ob event gerade läuft, wiederkehrende gibt's ja": FFXIV Collect's SourceText for
    // Kind=Event carries "<event name> (<year>)" for seasonal events that recur every year, or a
    // bare name (no year) for one-time collabs/promos. Recurring items are never "gone for good";
    // one-time ones are, once the (very generous, months-long) window passes. The live part — is it
    // running RIGHT NOW — has no local answer (Lumina carries no calendar), so it's a best-effort
    // Lodestone news check with an honest "unknown" when that fails, never a guessed answer.
    private static readonly Regex EventYearRx = new(@"^(?<name>.+?)\s*\((?<year>\d{4})\)$");

    public async Task<EventStatus?> GetEventStatusAsync(uint itemId)
    {
        if (!_collectSources.TryGetValue(itemId, out var entries)) return null;
        var ev = entries.FirstOrDefault(e => e.SourceType == "Event");
        if (ev == null) return null;
        var m = EventYearRx.Match(ev.SourceText);
        var recurring = m.Success;
        var eventName = recurring ? m.Groups["name"].Value : ev.SourceText;
        bool? active = _lodestone == null ? null : await _lodestone.IsEventActiveAsync(eventName).ConfigureAwait(false);
        return new EventStatus(eventName, recurring, active);
    }

    // ponytail: MountId (the same id Character.Mount.MountId reads natively) -> unlock ItemId, from
    // the same FFXIV Collect mounts dataset as _collectSources — its "id" field IS the Mount sheet
    // RowId (same convention already verified for Triple Triad card ids matching TripleTriadCard
    // RowIds). Lets "who's mount is this" resolve straight into the existing item-detail pipeline.
    private readonly Dictionary<uint, uint> _mountToItemId = new();

    // Accursed Hoard sack (Palace of the Dead / Heaven-on-High / Eureka Orthos "-trimmed/-haloed/
    // -tinged Sack") -> the flat pool of items it can yield when opened. Not in any Lumina sheet —
    // this reward table is server-side, undocumented in client Excel data (checked ffxiv-datamining
    // directly). Hand-compiled from garlandtools.org's per-item `loot` array (Palace/Heaven-on-High,
    // direct) and consolegameswiki's item-level category lists resolved to real IDs via garlandtools'
    // search API (Eureka Orthos, whose garlandtools loot array is still incomplete). No drop-rate
    // weights exist anywhere — this is the possible pool, not odds.
    private readonly Dictionary<uint, List<uint>> _hoardSackContents = new();

    // "orthos und pilgrim's traverse leer" — LuminaSupplemental.Excel (already on latest 5.1.4,
    // nothing newer on NuGet) has no drop-table rows for either: Eureka Orthos launched too late
    // for that package's last update, Pilgrim's Traverse (patch 7.35) is brand new. Hand-compiled
    // instead: their own Accursed Hoard sack items per 10-floor CFC row, verified against
    // ffxiv.consolegameswiki.com AND cross-checked live against our own Item sheet (name match) —
    // same "which duty drops this item" shape as LuminaSupplemental's own DungeonDrop.csv, just a
    // separate small file since we can't add rows to their embedded package resource.
    private readonly List<(uint ItemId, uint CfcId)> _deepDungeonNewFloorDrops = new();

    // Name-only fallback for NPCs with no location data
    private readonly Dictionary<uint, string> _shopNpcNameOnly = new();

    // "kriegen wir das immer aktuell?" — live Gamer Escape lookup, replaces the old one-time
    // MogstationItems.csv scrape for freshness; the CSV stays as a fallback (older items the wiki
    // itself may have since delisted/renamed, or a transient fetch failure this session).
    private readonly MogStationLiveService _mogstationLive = new(new HttpClient());

    private readonly IGarlandInstanceService? _garland;

    private readonly ILodestoneEventService? _lodestone;

    public ItemDetailService(GameData gameData, IGarlandInstanceService? garland = null, ILodestoneEventService? lodestone = null)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _garland = garland;
        _lodestone = lodestone;

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

    // same concurrency story as LuminaItemSourceService.GetSources: web request threads + draw thread
    public ItemDetail? GetDetail(uint itemId)
    {
        lock (_cache)
            return GetDetailCore(itemId);
    }

    private ItemDetail? GetDetailCore(uint itemId)
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
        var isEquippable = item.EquipSlotCategory.RowId > 0; // mounts/minions/etc. get no "Apply to Self"
        // ponytail: Item.ItemSeries is the game's own Mog Station bundle grouping (verified: Abes
        // Jacket -> ItemSeries.Name "Abes Attire", matching the real store's set name exactly) — no
        // scraping needed, unlike the exact product page URL (the store itself has no search feature
        // and is fully client-rendered, so a precise per-set link isn't cheaply obtainable).
        // Equippable gear ONLY: minions apparently share ItemSeries rows across huge unrelated
        // batches (live: item 20531 "Road Sparrow" came back with a 54-item "Rest of the set"
        // including Odder Otter, Wind-up Susano, Capybara Pup... — reported live as clearly wrong,
        // confirmed via /api/item/20531). There's no glamour "set" concept for a standalone minion.
        var setName = isEquippable && item.ItemSeries.IsValid ? item.ItemSeries.Value.Name.ToString() : null;
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
        // Equippable-only for the same reason as the ItemSeries path above — verified live this
        // fallback ALSO misfires for minions (item 20531 "Road Sparrow": the ItemSeries gate above
        // alone wasn't enough, this coffer map matched instead and produced the exact same bogus
        // 54-item "set" via "Materiel Container 4.0", a minion-batch coffer, not a glamour one).
        if (isEquippable && setName == null && _itemToCofferMap.TryGetValue(itemId, out var cofferIds) && cofferIds.Count > 0
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

        IReadOnlyList<ItemSourceDetail> sources;
        try { sources = BuildSources(itemId, item); }
        catch (Exception e)
        {
            // one bad row id in any of the ~40 lookups below must not take the whole window down
            sources = new[] { Note(ItemSourceType.Other, $"Source detection failed for this item ({e.GetType().Name} at {e.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim()}). Please report the item ID.") };
        }
        IReadOnlyList<SetMember>? contents = null;
        IReadOnlyList<ContentsCategory>? contentsSummary = null;
        List<uint>? cofferIds2 = null;
        List<uint>? hoardIds = null;
        var cofferHit = _cofferToItemsMap != null && _cofferToItemsMap.TryGetValue(itemId, out cofferIds2) && cofferIds2.Count > 0;
        var hoardHit = !cofferHit && _hoardSackContents.TryGetValue(itemId, out hoardIds) && hoardIds.Count > 0;
        if (cofferHit)
        {
            contents = cofferIds2!
                .Select(id =>
                {
                    var row = itemSheet.GetRowOrDefault(id);
                    return row == null ? null : new SetMember(id, row.Value.Name.ToString(), row.Value.Icon);
                })
                .Where(m => m != null)
                .Select(m => m!)
                .ToList();
        }
        else if (hoardHit)
        {
            // Accursed Hoard sacks hold 37-119 possible items each — a flat clickable chip per item
            // is unreadable clutter, but a plain "13x Minion" count with no way to see WHICH 13 isn't
            // useful either. Group by ItemUICategory, keep the real item list per category (Items),
            // so the UI can render "13x Minion" collapsed and let the user expand it on click.
            var mountIds = MountItemIds;
            var byCategory = new Dictionary<string, List<SetMember>>();
            foreach (var id in hoardIds!)
            {
                var row = itemSheet.GetRowOrDefault(id);
                if (row == null) continue;
                var member = new SetMember(id, row.Value.Name.ToString(), row.Value.Icon);
                var cat = mountIds.Contains(id)
                    ? "Mount"
                    : row.Value.ItemUICategory.IsValid ? row.Value.ItemUICategory.Value.Name.ToString() : "Other";
                if (string.IsNullOrEmpty(cat)) cat = "Other";
                if (!byCategory.TryGetValue(cat, out var list))
                    byCategory[cat] = list = new();
                list.Add(member);
            }
            contentsSummary = byCategory
                .OrderByDescending(kv => kv.Key == "Mount") // mounts first — rare, worth surfacing
                .ThenByDescending(kv => kv.Value.Count)
                .Select(kv => new ContentsCategory(kv.Key, kv.Value[0].IconId, kv.Value))
                .ToList();
        }

        var detail = new ItemDetail(itemId, name, itemLevel, isMarketable, iconId, sources, setName, setMembers,
            IsEquippable: item.EquipSlotCategory.RowId > 0, // mounts/minions/etc. get no "Apply to Self"
            Contents: contents,
            ContentsSummary: contentsSummary);

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
                // Deep Dungeons split into one CFC row PER 10-floor set (Palace of the Dead alone is
                // 20 rows, "Floors 1-10" through "Floors 191-200") — a floor-gear piece droppable
                // across the whole dungeon used to get one near-identical card per row. Group by the
                // dungeon name with the floor suffix stripped; >1 row sharing a base name collapses
                // into a single card with the real overall range (matches GetDutyDetail's header —
                // "die deep dungeons checken" caught this one still saying generic "(all floors)").
                // Verified live: this screenshot showed 5+ Palace of the Dead cards for one item
                // ("lieblos").
                foreach (var group in cfcNames.GroupBy(c => DeepDungeonCheckpointSuffix.Replace(c.name, "")))
                {
                    var groupList = group.ToList();
                    var (name, dutyType, sourceType, rowId) = groupList[0];
                    string displayName;
                    if (groupList.Count > 1)
                    {
                        var floorNums = groupList.SelectMany(c => DeepDungeonCheckpointRange.Match(c.name) is { Success: true } m
                            ? new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value) } : Array.Empty<int>()).ToList();
                        displayName = floorNums.Count > 0 ? $"{group.Key} ({floorNums.Min()}-{floorNums.Max()})" : $"{group.Key} (all floors)";
                    }
                    else displayName = name;
                    results.Add(new ItemSourceDetail(
                        sourceType,
                        $"{dutyType} Drop: {displayName}",
                        null, null, null, null, null, null, null, null,
                        null, rowId, displayName, dutyType, null, null, cfcRowIds));
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
                                // GetRowOrDefault: the issuer can be an EObj id (2011341 for Brightlily Seeds) — GetRow threw
                                var resident = _gameData.GetExcelSheet<ENpcResident>()?.GetRowOrDefault(issuerStart.RowId);
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

        // 7e. Grand Company seal shop — GCScripShopItem is a subrow sheet whose parent row is a
        // GCScripShopCategory carrying the GC. Verified: 3588 Serpent Private's Bracers → category 12
        // (GC 2 Twin Adder), 1050 seals, rank 3. ~330 Storm/Serpent/Flame arms + gear + bardings
        // used to land on the generic fallback.
        if (!results.Any(s => s.Type == ItemSourceType.Vendor) && GcSealShop.TryGetValue(itemId, out var gcEntries))
        {
            foreach (var g in gcEntries)
            {
                var sealItemId = g.gc switch { 1 => 20u, 2 => 21u, 3 => 22u, _ => 0u }; // Storm / Serpent / Flame Seal
                var costs = sealItemId == 0 ? null
                    : new List<CostEntry> { new(sealItemId, GetItemName(sealItemId) ?? "Company Seals", g.seals, GetItemIconId(sealItemId)) };
                results.Add(new ItemSourceDetail(
                    ItemSourceType.Vendor,
                    $"Grand Company Quartermaster: {g.gcName} (rank {g.rank})",
                    "Quartermaster", null, null, null, null, null,
                    costs, null, null, null, null, null, null, null, null));
            }
        }

        var enName = GetEnglishName(itemId) ?? item.Name.ToString();

        // 7f. Outfit "Attire" items (ItemUICategory "Outfits") — MirageStoreSetItem, keyed by the
        // outfit's own item id, lists its pieces (verified: 45416 Hidefiend's Costume Attire →
        // 33063–33067, the five costume pieces). 1077 of these had no source at all.
        if (results.Count == 0 && OutfitPieces.TryGetValue(itemId, out var pieces) && pieces.Count > 0)
        {
            results.Add(Note(ItemSourceType.Other,
                "Outfit — a glamour set made up of the pieces below. The outfit itself isn't sold or dropped; each piece has its own source.",
                pieces.Select(x => new CostEntry(x.itemId, x.name, 1, x.icon)).ToList(),
                pieces[0].itemId));
        }

        // 7g. Free Company workshop — CompanyCraftSequence.ResultItem (verified: sequence 7 → 9654
        // Grade 2 Wheel of Productivity). Airship/submersible parts, FC wheels, workshop furniture.
        if (results.Count == 0 && WorkshopResults.Contains(itemId))
        {
            results.Add(Note(ItemSourceType.Other, "Company Workshop — crafted as a Free Company workshop project (Company Crafting Log), not by a single crafter."));
        }
        else if (results.Count == 0 && enName.StartsWith("Primed ", StringComparison.Ordinal)
                 && FindItemIdByExactName(enName["Primed ".Length..]) is { } baseWheel)
        {
            results.Add(Note(ItemSourceType.Other, "Company Workshop — primed from its base wheel in the Free Company workshop.", sourceItemId: baseWheel));
        }

        // 7h. Cosmic Exploration — WKSItemInfo lists every item that exists on the Sinus Ardorum
        // site (fish, tackle, cosmotools, materials). ~360 audit leftovers.
        if (results.Count == 0 && CosmicItems.Contains(itemId))
        {
            results.Add(Note(ItemSourceType.Other, "Cosmic Exploration (Sinus Ardorum) — obtained on the Cosmic Exploration site through its missions, gathering or fishing."));
        }

        // 7i. Spearfishing — SpearfishingItem carries item, level and territory directly
        // (FishingSpot only covers rod fishing).
        if (!results.Any(s => s.Type == ItemSourceType.Gathering) && Spearfishing.TryGetValue(itemId, out var spearSpots))
        {
            foreach (var sp in spearSpots)
            {
                var zoneSuffix = string.IsNullOrEmpty(sp.zone) ? "" : $" ({sp.zone})";
                results.Add(new ItemSourceDetail(ItemSourceType.Gathering, $"Spearfishing Lv.{sp.level}{zoneSuffix}",
                    null, null, null, null, sp.territory == 0 ? null : sp.territory, null,
                    null, null, null, null, null, null, null, null, null));
            }
        }

        // 7j. Island Sanctuary — MJIItemPouch (gathered island materials) plus the Isleworks /
        // Islekeep's / Island-prefixed produce, none of which ever leaves the island.
        if (results.Count == 0 && (IslandPouch.Contains(itemId)
            || Regex.IsMatch(enName, @"^(Isleworks |Islekeep's |Islander's |Island |Islewort|Isleberry|Islefish|Isleshroom)")))
        {
            results.Add(Note(ItemSourceType.Other, "Island Sanctuary — gathered, grown or crafted on your island (Isleworks); island items can't be taken off the island."));
        }

        // 7k. Hidden gathering yields — in GatheringItem but referenced by no GatheringPointBase
        // node (verified: 6688 Timeworn Leather Map, GatheringItem 10121, zero node references).
        if (!results.Any(s => s.Type == ItemSourceType.Gathering) && GatheringItemLevels.TryGetValue(itemId, out var hiddenLevel))
        {
            results.Add(Note(ItemSourceType.Gathering, $"Gathering Lv.{hiddenLevel} — random/hidden yield at Miner or Botanist nodes of that level (Timeworn maps and the like), not tied to one specific node."));
        }

        // 7l. Fish that are in the fishing log (FishParameter) but whose spot isn't in FishingSpot:
        // ocean fishing, the Diadem, event spots. Say what it is instead of shrugging.
        if (results.Count == 0 && FishLog.Contains(itemId))
        {
            results.Add(Note(ItemSourceType.Gathering, "Fish — listed in the fishing log, but its spot isn't in the game's FishingSpot table (ocean fishing, the Diadem, or an event/special spot)."));
        }

        // 7m. Trade-in-only items — the item appears as a COST in a SpecialShop entry, never as
        // something received. Verified: 9633 Antiquated Chaos Flanchard is the cost at "Artifact
        // Gear Repair (DRK)" → Chaos Flanchard; 21393 Ryumyaku Bracelet at "Ryumyaku Gear
        // Augmentation (DoM)" → Dai-ryumyaku; 38211 Irregular Tomestone at "Past Irregular
        // Tomestone Exchange". The shop tells the player what it's FOR, the patterns below say
        // where it came from.
        if (results.Count == 0)
        {
            if (enName.StartsWith("Antiquated ", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.Quest, "Artifact gear — awarded by that job's level-cap job quests, not sold anywhere."));
            else if (enName.StartsWith("Irregular Tomestone of ", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.Other, "Moogle Treasure Trove event currency — earned from the event's selected duties while it ran; retired afterward."));
            else if (Regex.IsMatch(enName, @"^(Manderville|Amazing Manderville|Majestic Manderville|Mandervillous) "))
                results.Add(Note(ItemSourceType.Quest, "Manderville relic weapon step — obtained by progressing the Endwalker relic quest line (Hildibrand / House Manderville), never sold."));
            else if (Regex.IsMatch(enName, @"^(Animated|Awoken|Hyperconductive|Sharpened) "))
                results.Add(Note(ItemSourceType.Quest, "Anima relic weapon step — obtained by progressing the Heavensward relic quest line (Ardashir, Azys Lla), never sold."));
            // the game's own item text: "Eureka gear." (Anemos/Pagos/Pyros/Hydatos weapons and armor)
            else if (((_englishItemSheet ??= _gameData.GetExcelSheet<Item>(Language.English))?.GetRowOrDefault(itemId)?.Description.ToString() ?? "").StartsWith("Eureka gear", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.Other, "Eureka (The Forbidden Land) — Eureka-only gear, exchanged with Gerolt / the Expedition Artisan inside Eureka; not obtainable outside it."));
        }

        // 7n. Deep Dungeon / Bozja-Zadnor / Occult Crescent trade-in currencies — the block above
        // only ever explains where these SPEND (via TradeInUses below), never where they're EARNED.
        // Verified live 2026-09-03 (audit): Aetherpool Grip/Fragment items across all 3 real Deep
        // Dungeons, Resistance Token, and Enlightenment Silver Piece all showed only the outbound
        // exchange with no acquisition note. Additive — falls through to TradeInUses right after,
        // doesn't set results.Count so the outbound trade-in note still appears alongside this.
        var tokenInboundNote =
            Regex.IsMatch(enName, @"^(Palace|Yggdrasil|Orthos) Aetherpool (Fragment|Grip|Core)$")
                ? "Deep Dungeon currency — earned as a reward for clearing that Deep Dungeon's floors (progression reward, not a drop or purchase)."
            : enName == "Resistance Token"
                ? "Bozja/Zadnor Resistance relic currency — earned from Critical Engagements and Duels in Bozja/Zadnor, and from Save the Queen relic quest steps."
            : enName == "Bozjan Cluster"
                ? "Bozja/Zadnor field currency — earned from Critical Engagements, Duels, and general activity in the Bozjan Southern Front/Zadnor."
            : enName == "Enlightenment Silver Piece"
                ? "Occult Crescent currency — earned from combat participation (Critical Engagements/duels) in the Occult Crescent (South Horn)."
            : null;
        if (results.Count == 0 && tokenInboundNote != null)
            results.Add(Note(ItemSourceType.Other, tokenInboundNote));
        if ((tokenInboundNote != null || results.Count == 0) && TradeInUses.TryGetValue(itemId, out var tradeIns))
        {
            // ponytail: used to be one near-identical card PER received item (Take(3), same shop,
            // "Open item" jumping to just one of them) — same "unübersichtlich" repeated-card problem
            // the vendor-location grouping above already solved once. Group by shop instead: one card
            // listing every piece it buys, no "Open item" button (ambiguous with >1 destination).
            foreach (var group in tradeIns.GroupBy(t => t.shopId).Take(3))
            {
                var shopName = group.First().shop;
                var groupList = group.DistinctBy(g => g.receiveId).ToList();
                uint? singleReceiveId = groupList.Count == 1 ? groupList[0].receiveId : null;

                // one row per receivable piece (icon + name + cost amount) instead of a giant
                // comma-joined sentence — reuses the same "Pieces:" list the outfit-coffer case (7f)
                // already renders in both UIs, verified live: 12 items in one description read as a
                // wall of text ("unschön").
                var tradeInPieces = groupList
                    .Select(g => new CostEntry(g.receiveId, g.receiveName, g.costAmount, GetItemIconId(g.receiveId)))
                    .ToList();

                // where to actually go trade it in — same NPC lookup FindSpecialShopSources uses.
                var npcInfos = _shopNpcLookup.GetValueOrDefault(group.Key);
                if (npcInfos == null && _shopNpcNameOnly.TryGetValue(group.Key, out var nameOnly))
                    npcInfos = new List<NpcLocationInfo> { new(nameOnly, "", 0, 0, 0, 0) };

                var desc = $"Trade-in only — handed over at \"{shopName}\"; the item itself isn't sold there.";
                if (npcInfos is { Count: > 0 })
                {
                    foreach (var npc in npcInfos)
                        results.Add(new ItemSourceDetail(ItemSourceType.Other, desc,
                            npc.NpcName, npc.ZoneName, npc.MapX, npc.MapY, npc.TerritoryTypeId, npc.MapId,
                            null, tradeInPieces, null, null, null, null, null, null, null, SourceItemId: singleReceiveId));
                }
                else
                {
                    results.Add(Note(ItemSourceType.Other, desc, materials: tradeInPieces, sourceItemId: singleReceiveId));
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

        // 11c. Diadem (Heavensward exploratory missions) — the only rarity-7 (random-stat green)
        // gear left after the Aetherial name check above: Mistfall/Deepmist/Mistbreak/Sunstreak/
        // Sunburst sets + Coven weapons, 293 items in the audit, all Diadem 3.1–3.55 loot.
        if (results.Count == 0 && item.Rarity == 7)
        {
            results.Add(Note(ItemSourceType.Other, "Diadem (Heavensward exploratory missions, patches 3.1–3.55) random-stat loot — the original Diadem was retired with the 5.1 rework, so this is no longer obtainable."));
        }

        if (results.Count == 0)
        {
            var uiCat = item.ItemUICategory.IsValid ? item.ItemUICategory.Value.Name.ToString() : "";
            // 11d. PvP season rank rewards (Feast / Crystalline Conflict): "Season Four Lone Wolf
            // Voucher B", "Season Sixteen Silver Framer's Kit", "Season One Final Conflict Chit"...
            // 357 items; the item text itself says "Documentation of noteworthy accomplishments for
            // Season Four of the Feast".
            if (Regex.IsMatch(enName, @"^Season [A-Za-z-]+ .*(Wolf|Conflict|Framer's Kit|Trophy|Voucher|Chit)"))
                results.Add(Note(ItemSourceType.Other, "PvP season reward — handed out at the end of that Feast / Crystalline Conflict season for the rank reached; not obtainable after the season ended."));
            else if (Regex.IsMatch(enName, @"^(FRC|CCRC) \d{4} .*Certification$"))
                results.Add(Note(ItemSourceType.Other, "PvP tournament reward — given to placers of that year's Feast / Crystalline Conflict regional championship."));
            // 11e. Tales of Adventure — job/retainer level boosts, online store only
            else if (enName.StartsWith("Tales of Adventure:", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.MogStation, "Mog Station — Tales of Adventure (job/retainer level boost), purchased from the online store."));
            // 11f. Racing chocobo registrations — produced by breeding / retiring at the Chocobo Square
            else if (Regex.IsMatch(enName, @"^(Retired|Fledgling) Chocobo Registration"))
                results.Add(Note(ItemSourceType.Other, "Chocobo Racing (Gold Saucer) — a racing chocobo's registration, created by breeding or retiring a chocobo at the Chocobo Square; never sold."));
            // 11g. Retired tomestones
            else if (enName.StartsWith("Allagan Tomestone of ", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.Other, "Retired Allagan tomestone — no longer earned from any duty; each expansion rotates the older tomestone types out."));
            // 11h. Crafter/gatherer relic tool steps — three quest lines, all recognisable by the
            // step names on a *Primary/Secondary Tool item (327 tool items in the audit).
            else if (uiCat.EndsWith(" Tool", StringComparison.Ordinal) && Regex.IsMatch(enName, @"^(Skysteel|Dragonsung|Augmented Dragonsung|Skysung|Skybuilders'|Resplendent) "))
                results.Add(Note(ItemSourceType.Quest, "Skysteel relic tool step — obtained by progressing the Shadowbringers crafter/gatherer relic tool quests (Denys, Foundation); never sold."));
            else if (uiCat.EndsWith(" Tool", StringComparison.Ordinal) && Regex.IsMatch(enName, @"^(Splendorous|Augmented Splendorous|Crystalline|Chora-Zoi's Crystalline|Brilliant|Vrandtic Visionary's|Lodestar) "))
                results.Add(Note(ItemSourceType.Quest, "Splendorous relic tool step — obtained by progressing the Endwalker crafter/gatherer relic tool quests (Studium, Old Sharlayan); never sold."));
            else if (uiCat.EndsWith(" Tool", StringComparison.Ordinal) && Regex.IsMatch(enName, @"^(Cosmic|Stellar|Lunar) "))
                results.Add(Note(ItemSourceType.Quest, "Cosmotool relic step — earned and upgraded through Cosmic Exploration (Sinus Ardorum) missions; never sold."));
            else if (uiCat.EndsWith(" Tool", StringComparison.Ordinal) && enName.StartsWith("Novice's ", StringComparison.Ordinal))
                results.Add(Note(ItemSourceType.Quest, "Starter tool — handed out when unlocking the class at its guild; never sold."));
            else if (Regex.IsMatch(enName, @"^(Obsolete )?Resplendent .*(Material|Component) [A-Z]$"))
                results.Add(Note(ItemSourceType.Quest, "Resplendent tool quest item — made and handed in during the final Skysteel relic tool quests; \"Obsolete\" ones are leftovers from an earlier version of that quest."));
            // Save the Queen relic weapons, final "Blade's" step
            else if (enName.StartsWith("Blade's ", StringComparison.Ordinal) && (uiCat.EndsWith(" Arm", StringComparison.Ordinal) || uiCat == "Shield" || uiCat.EndsWith("Grimoire", StringComparison.Ordinal)))
                results.Add(Note(ItemSourceType.Quest, "Resistance relic weapon step — obtained by progressing the Shadowbringers Save the Queen relic quest line (Bozja); never sold."));
            // 11i. Triple Triad cards missing from the bundled NPC table (newer cards)
            else if (uiCat == "Triple Triad Card")
                results.Add(Note(ItemSourceType.Other, "Triple Triad card — not in the bundled NPC/drop table (newer card). Typical sources: Triple Triad NPC wins, Gold Saucer card packs, tournaments, or duty drops."));
            // 11j. Beast-tribe society quest items — the item text itself says so
            // ("※Only for use in Ixal society quests.")
            else if ((_englishItemSheet ??= _gameData.GetExcelSheet<Item>(Language.English))?.GetRowOrDefault(itemId)?.Description.ToString().Contains("society quests", StringComparison.Ordinal) == true)
                results.Add(Note(ItemSourceType.Other, "Tribal (beast tribe) society quest item — handed out and used within those quests, never sold or dropped."));
        }

        // 12. Generic fallback â€” nothing found, and not a known legacy/retired/superseded item either.
        // Verified against live game data (not just our own sheets): items that land here
        // genuinely have no current recipe/vendor/duty-drop entry.
        if (results.Count == 0)
        {
            // ponytail: this fallback used to always say "old gear rotated out of its vendor" —
            // technically true for equipment, but nonsensical when applied to non-equippable event
            // currencies/points (Mettle, Phantom EXP): those are earned by participating in current
            // content (Bozja/Zadnor Critical Engagements, Occult Crescent combat), not "old gear".
            var isEquipment = item.EquipSlotCategory.RowId > 0;
            results.Add(new ItemSourceDetail(
                ItemSourceType.Other,
                isEquipment
                    ? "No known current source. Often old gear that's been rotated out of its vendor over patches — may still be a rare drop, achievement, or account-bound reward we don't track."
                    : "No known current source. Likely an instance-bound currency/point earned by participating in specific content (e.g. combat engagements, event objectives) rather than bought, crafted, or dropped — we don't track those individually.",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        }

        return results;
    }

    // ---- duty -> drops: the reverse of _itemToDutyMap, for the Duty Drops tab (doku TODO) ----
    private Dictionary<uint, List<uint>>? _dutyToItems;
    private Dictionary<uint, List<uint>> DutyToItems
    {
        get
        {
            if (_dutyToItems != null) return _dutyToItems;
            var map = new Dictionary<uint, List<uint>>();
            foreach (var (itemId, duties) in _itemToDutyMap)
                foreach (var cfc in duties)
                {
                    if (!map.TryGetValue(cfc, out var list)) map[cfc] = list = new();
                    list.Add(itemId);
                }
            return _dutyToItems = map;
        }
    }

    public IReadOnlyList<DutyInfo> ListDutiesWithDrops()
    {
        var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null) return Array.Empty<DutyInfo>();
        var list = new List<DutyInfo>();
        foreach (var cfc in cfcSheet)
        {
            var cfcId = cfc.RowId;
            var name = cfc.Name.ToString();
            if (name.Length == 0) continue;
            // "Bezeichner für die Duty wie im Duty Finder": the raw sheet name is lowercase
            // ("the minstrel's ballad: ..."), Duty Finder capitalizes the leading word for its list
            name = CapitalizeFirst(name);
            // every dungeon / trial / raid / ultimate / deep dungeon, plus anything else we have
            // drop data for. Duties without local data get their drops live from Garland —
            // LuminaSupplemental's tables end around patch 7.1 ("Dawntrail Extreme nur 3 Stück").
            // ContentType 21 = Deep Dungeons (Palace of the Dead/Heaven-on-High/Eureka Orthos) —
            // live report: "bin gerade im deep dungeon, das fehlt bei dungeon drops", confirmed
            // missing from this filter (checked the real ContentType sheet, not guessed).
            var contentType = cfc.ContentType.RowId;
            var hasLocal = DutyToItems.TryGetValue(cfcId, out var items);
            if (!hasLocal && contentType is not (2 or 4 or 5 or 21 or 28)) continue;
            list.Add(new DutyInfo(cfcId, name, GetDutyType(cfcId), cfc.TerritoryType.RowId, hasLocal ? items!.Count : 0,
                cfc.Image, cfc.ClassJobLevelRequired, cfc.ItemLevelRequired, ExpansionName(cfc.ClassJobLevelRequired),
                Safe(() => (uint)(cfc.ContentType.ValueNullable?.Icon ?? 0), 0u), // Duty Finder category icon (61801 dungeons, ...)
                BossNamesByDuty.TryGetValue(cfcId, out var bossNames) ? bossNames : Array.Empty<string>(),
                DifficultyOf(name, Safe(() => cfc.AllianceRoulette, false))));
        }
        // Deep Dungeons split into one CFC row PER 10-floor set (Palace of the Dead alone is 12 rows)
        // — the browsable list showed one tile per floor range instead of one per dungeon. Live
        // report: "soviel platz und ich brauch ne lupe" was about item-detail cards, but the same
        // "jedes deep dungeon zusammenfassen" ask applies here too — verified live, this picker
        // showed 10 separate "Heaven-on-High (Floors X-Y)" tiles. Group by base name, merge into one.
        var merged = new List<DutyInfo>();
        foreach (var group in list.GroupBy(d => d.Type == "Deep Dungeon" ? DeepDungeonCheckpointSuffix.Replace(d.Name, "") : d.Name))
        {
            var groupList = group.ToList();
            if (groupList.Count == 1) { merged.Add(groupList[0]); continue; }
            var first = groupList.OrderBy(d => d.CfcId).First();
            var allDrops = groupList.SelectMany(d => DutyToItems.TryGetValue(d.CfcId, out var i) ? i : Enumerable.Empty<uint>()).Distinct().Count();
            var allBosses = groupList.SelectMany(d => d.Bosses).Distinct().ToList();
            merged.Add(first with { Name = group.Key, DropCount = allDrops, Bosses = allBosses });
        }

        // grouped for the tab: content type, difficulty, expansion — and inside that in RELEASE
        // order ("nach Release sortieren"): ContentFinderCondition row ids grow with the patches
        return merged.OrderBy(d => DutyTypeOrder(d.Type)).ThenBy(d => DifficultyOrder(d.Difficulty)).ThenBy(d => ExpansionOrder(d.Level))
            .ThenBy(d => d.CfcId).ToList();
    }

    // Duty Finder sub-folders from the name suffix; alliance raids have no suffix but sit in the
    // alliance roulette (ContentFinderCondition.AllianceRoulette).
    private static string DifficultyOf(string name, bool alliance) =>
        // ARR–EW extremes are "The Minstrel's Ballad: X" (no suffix), DT ones carry "(Extreme)"
        name.EndsWith("(Extreme)", StringComparison.Ordinal) || name.StartsWith("the Minstrel's Ballad:", StringComparison.OrdinalIgnoreCase) ? "Extreme"
        : name.EndsWith("(Savage)", StringComparison.Ordinal) ? "Savage"
        : name.EndsWith("(Unreal)", StringComparison.Ordinal) ? "Unreal"
        : alliance ? "Alliance" : "Normal";
    private static int DifficultyOrder(string d) => d switch { "Normal" => 0, "Extreme" => 1, "Savage" => 2, "Unreal" => 3, "Alliance" => 4, _ => 5 };

    private HashSet<uint>? _mountItemIds;
    private HashSet<uint> MountItemIds => _mountItemIds ??= _mountToItemId.Values.ToHashSet();

    // "susano eingeben und den Trial kriegen": boss names per duty from DungeonBoss -> BNpcName
    private Dictionary<uint, List<string>>? _bossNamesByDuty;
    private Dictionary<uint, List<string>> BossNamesByDuty
    {
        get
        {
            if (_bossNamesByDuty != null) return _bossNamesByDuty;
            var map = new Dictionary<uint, List<string>>();
            var bnpc = _gameData.GetExcelSheet<BNpcName>();
            foreach (var b in DutyTables.bosses.OrderBy(b => b.FightNo))
            {
                var name = bnpc?.GetRowOrDefault(b.BNpcNameId)?.Singular.ToString() ?? "";
                if (name.Length == 0) continue;
                name = CapitalizeFirst(name);
                if (!map.TryGetValue(b.ContentFinderConditionId, out var list)) map[b.ContentFinderConditionId] = list = new();
                if (!list.Contains(name)) list.Add(name);
            }
            return _bossNamesByDuty = map;
        }
    }

    private static int DutyTypeOrder(string type) => type switch { "Dungeon" => 0, "Deep Dungeon" => 1, "Trial" => 2, "Raid" => 3, "Ultimate" => 4, _ => 5 };
    // Expansion from the required level (ARR ≤50, HW ≤60, SB ≤70, ShB ≤80, EW ≤90, DT ≤100): holds
    // for every duty incl. ultimates, and needs no TerritoryType/ExVersion read (DalaMock can't).
    private static int ExpansionOrder(int level) => level <= 50 ? 0 : level <= 60 ? 1 : level <= 70 ? 2 : level <= 80 ? 3 : level <= 90 ? 4 : level <= 100 ? 5 : 6;
    private static string ExpansionName(int level) => ExpansionOrder(level) switch
    {
        0 => "A Realm Reborn", 1 => "Heavensward", 2 => "Stormblood", 3 => "Shadowbringers", 4 => "Endwalker", 5 => "Dawntrail", _ => "Later",
    };

    // the same four LuminaSupplemental CSVs BuildDutyDropCache flattens into item -> duty, kept
    // whole here because the tab wants them by boss / chest (loaded once, ~10k rows total)
    private (List<DungeonBoss> bosses, List<DungeonBossDrop> bossDrops, List<DungeonBossChest> chests, List<DungeonDrop> general)? _dutyTables;
    private (List<DungeonBoss> bosses, List<DungeonBossDrop> bossDrops, List<DungeonBossChest> chests, List<DungeonDrop> general) DutyTables => _dutyTables ??= (
        CsvLoader.LoadResource<DungeonBoss>(CsvLoader.DungeonBossResourceName, true, out _, out _, null).ToList(),
        CsvLoader.LoadResource<DungeonBossDrop>(CsvLoader.DungeonBossDropResourceName, true, out _, out _, null).ToList(),
        CsvLoader.LoadResource<DungeonBossChest>(CsvLoader.DungeonBossChestResourceName, true, out _, out _, null).ToList(),
        CsvLoader.LoadResource<DungeonDrop>(CsvLoader.DungeonDropItemResourceName, true, out _, out _, null).ToList());

    public DutyDetail? GetDutyDetail(uint cfcId)
    {
        var cfc = _gameData.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault(cfcId);
        if (cfc == null) return null;
        var (bosses, bossDrops, chests, general) = DutyTables;
        var itemSheet = _gameData.GetExcelSheet<Item>();
        var bnpc = _gameData.GetExcelSheet<BNpcName>();

        // Deep Dungeons split into one CFC row per 10-floor set — ListDutiesWithDrops now merges
        // those into one browsable tile per dungeon, so opening that tile must pull drops from every
        // floor's CFC row, not just the representative one it was called with (would've silently
        // shown only floors 1-10 of a 100-floor dungeon otherwise).
        var siblingCfcIds = new HashSet<uint> { cfcId };
        // live report: header still showed the representative CFC's raw name ("Heaven-on-High
        // (Floors 1-10)") even though the drops below are aggregated across ALL floor sets —
        // looked like the list was silently truncated to floors 1-10 again. Use the stripped base
        // name plus the REAL overall floor range (min start - max end across every merged row,
        // e.g. "1-100" for Heaven-on-High) instead of a generic "(all floors)".
        var displayName = CapitalizeFirst(cfc.Value.Name.ToString());
        if (cfc.Value.ContentType.RowId == 21)
        {
            var baseName = DeepDungeonCheckpointSuffix.Replace(displayName, "");
            var floorNums = new List<int>();
            void CollectFloors(string rawName)
            {
                var m = DeepDungeonCheckpointRange.Match(rawName);
                if (m.Success) { floorNums.Add(int.Parse(m.Groups[1].Value)); floorNums.Add(int.Parse(m.Groups[2].Value)); }
            }
            CollectFloors(displayName);
            foreach (var other in _gameData.GetExcelSheet<ContentFinderCondition>() ?? Enumerable.Empty<ContentFinderCondition>())
            {
                if (other.ContentType.RowId != 21 || other.Name.IsEmpty) continue;
                var otherName = CapitalizeFirst(other.Name.ToString());
                if (DeepDungeonCheckpointSuffix.Replace(otherName, "") == baseName)
                {
                    siblingCfcIds.Add(other.RowId);
                    CollectFloors(otherName);
                }
            }
            if (siblingCfcIds.Count > 1 && floorNums.Count > 0)
                displayName = $"{baseName} ({floorNums.Min()}-{floorNums.Max()})";
        }

        List<DutyDrop> Drops(IEnumerable<uint> ids)
        {
            var list = new List<DutyDrop>();
            foreach (var id in ids.Distinct())
            {
                var row = itemSheet?.GetRowOrDefault(id);
                if (row == null) continue;
                list.Add(MakeDrop(id, row.Value));
            }
            return list.OrderByDescending(d => d.ItemLevel).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var fights = bosses.Where(b => siblingCfcIds.Contains(b.ContentFinderConditionId)).Select(b => (int)b.FightNo)
            .Concat(bossDrops.Where(d => siblingCfcIds.Contains(d.ContentFinderConditionId)).Select(d => (int)d.FightNo))
            .Concat(chests.Where(c => siblingCfcIds.Contains(c.ContentFinderConditionId)).Select(c => (int)c.FightNo))
            .Distinct().OrderBy(f => f).ToList();
        var bossList = new List<DutyBoss>();
        foreach (var f in fights)
        {
            var boss = bosses.Where(b => siblingCfcIds.Contains(b.ContentFinderConditionId) && b.FightNo == f).ToList();
            // BNpcName.Singular is lowercase in the English sheet ("chopper") — capitalise for display
            var name = boss.Count == 0 ? "" : bnpc?.GetRowOrDefault(boss[0].BNpcNameId)?.Singular.ToString() ?? "";
            if (name.Length > 0) name = CapitalizeFirst(name);
            var drops = Drops(bossDrops.Where(d => siblingCfcIds.Contains(d.ContentFinderConditionId) && d.FightNo == f).Select(d => d.ItemId));
            // one merged chest per boss: the data splits savage/extreme loot into coffer 1/2/3 with the
            // same pool ("es gibt nur eine, wo alles drin ist") — the split means nothing to a player
            var chestItems = Drops(chests.Where(c => siblingCfcIds.Contains(c.ContentFinderConditionId) && c.FightNo == f).Select(c => c.ItemId));
            var chestList = chestItems.Count > 0 ? new List<DutyChest> { new(0, chestItems) } : new List<DutyChest>();
            if (drops.Count == 0 && chestList.Count == 0) continue;
            bossList.Add(new DutyBoss(f, name, drops, chestList));
        }
        // union with our own hand-compiled rows (Eureka Orthos / Pilgrim's Traverse — see
        // _deepDungeonNewFloorDrops' comment) — LuminaSupplemental's own `general` has nothing for
        // either, this is the only source their drops come from.
        var generalIds = general.Where(d => siblingCfcIds.Contains(d.ContentFinderConditionId)).Select(d => d.ItemId)
            .Concat(_deepDungeonNewFloorDrops.Where(d => siblingCfcIds.Contains(d.CfcId)).Select(d => d.ItemId));
        var generalDrops = Drops(generalIds);
        // "Mounts nach oben": mount and minion drops get their own section at the top instead of
        // hiding inside a boss chest list (the whole point of most Extreme trials)
        var featured = bossList.SelectMany(b => b.Drops.Concat(b.Chests.SelectMany(c => c.Items))).Concat(generalDrops)
            .Where(d => d.Kind.Length > 0).DistinctBy(d => d.ItemId)
            .OrderBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // mounts / minions FFXIV Collect attributes to this duty (matched by the English duty name)
        // — covers trials newer than the bundled drop tables (Garland has nothing for 7.2+ either)
        var enCfcName = Safe(() => _gameData.GetExcelSheet<ContentFinderCondition>(Language.English)?.GetRowOrDefault(cfcId)?.Name.ToString(), null);
        if (!string.IsNullOrEmpty(enCfcName))
        {
            foreach (var (collectItemId, entries) in _collectSources)
            {
                if (featured.Any(f => f.ItemId == collectItemId)) continue;
                if (!entries.Any(e => (e.Kind == "Mount" || e.Kind == "Minion") && e.SourceText.Equals(enCfcName, StringComparison.OrdinalIgnoreCase))) continue;
                if (itemSheet?.GetRowOrDefault(collectItemId) is { } collectRow)
                    featured.Add(MakeDrop(collectItemId, collectRow) with { Kind = MountItemIds.Contains(collectItemId) ? "Mount" : "Minion" });
            }
        }
        // exchange shops that belong to this duty — from the game's own shop sheets, so this stays
        // current when the bundled drop tables don't:
        //  a) tokens this duty drops (Dreadwyrm Totem, books ...) -> everything they buy
        //  b) the duty's mount / minion (FFXIV Collect) -> the totem that buys it -> everything it buys
        var exchanges = new List<DutyExchange>();
        void AddExchange(string shop, DutyDrop? token, IEnumerable<uint> ids)
        {
            var drops = new List<DutyDrop>();
            foreach (var id in ids.Distinct())
                if (itemSheet?.GetRowOrDefault(id) is { } r) drops.Add(MakeDrop(id, r));
            if (drops.Count == 0) return;
            var idx = exchanges.FindIndex(e => e.Shop == shop);
            if (idx >= 0) exchanges[idx] = exchanges[idx] with { Items = exchanges[idx].Items.Concat(drops).DistinctBy(d => d.ItemId).ToList() };
            else exchanges.Add(new DutyExchange(shop, token, drops));
        }
        var tokenIds = new HashSet<uint>();
        foreach (var d in bossList.SelectMany(b => b.Drops.Concat(b.Chests.SelectMany(c => c.Items))).Concat(generalDrops))
            if (itemSheet?.GetRowOrDefault(d.ItemId)?.EquipSlotCategory.RowId == 0) tokenIds.Add(d.ItemId);
        // the duty's own mount / minion is sold in its totem shop — so the cost item that buys it is
        // the duty's totem. No boss-name heuristics (those mis-filed brand-new trials); currencies
        // that buy hundreds of things (MGP, tomestones ...) are excluded by list size.
        foreach (var f in featured)
            foreach (var (costId, uses) in ExchangeByCost)
                if (uses.Count <= 60 && uses.Any(u => u.receiveId == f.ItemId)) tokenIds.Add(costId);
        foreach (var tokenId in tokenIds)
        {
            if (!ExchangeByCost.TryGetValue(tokenId, out var uses)) continue;
            var token = itemSheet?.GetRowOrDefault(tokenId) is { } tr ? MakeDrop(tokenId, tr) : null;
            foreach (var g in uses.GroupBy(u => u.shop)) AddExchange(g.Key, token, g.Select(u => u.receiveId));
        }
        // a totem usually feeds two shops with the same weapon list ("Totem Gear (X)" and the generic
        // "Primal/Auspice Gear (IL ...)") — drop an exchange whose items another one already covers
        for (var i = exchanges.Count - 1; i >= 0; i--)
        {
            var ids = exchanges[i].Items.Select(x => x.ItemId).ToHashSet();
            var covered = exchanges.Where((e, j) => j != i && ids.IsSubsetOf(e.Items.Select(x => x.ItemId)))
                .Any(e => e.Items.Count > ids.Count || exchanges.IndexOf(e) < i);
            if (covered) exchanges.RemoveAt(i);
        }
        if (featured.Count > 0)
        {
            bossList = bossList.Select(b => new DutyBoss(b.FightNo, b.Name, b.Drops.Where(d => d.Kind.Length == 0).ToList(),
                    b.Chests.Select(c => new DutyChest(c.CofferNo, c.Items.Where(d => d.Kind.Length == 0).ToList())).Where(c => c.Items.Count > 0).ToList()))
                .Where(b => b.Drops.Count > 0 || b.Chests.Count > 0).ToList();
            generalDrops = generalDrops.Where(d => d.Kind.Length == 0).ToList();
        }
        return new DutyDetail(cfcId, displayName, GetDutyType(cfcId), cfc.Value.Image,
            cfc.Value.ClassJobLevelRequired, cfc.Value.ItemLevelRequired, bossList, generalDrops,
            cfc.Value.TerritoryType.RowId,
            Safe(() => cfc.Value.TerritoryType.ValueNullable?.Map.RowId ?? 0, 0u), // TerritoryType sheet mismatches under DalaMock
            featured, exchanges);
    }

    public async Task<IReadOnlyList<DutyCoffer>> GetDutyCoffersAsync(uint cfcId)
    {
        if (_garland == null) return Array.Empty<DutyCoffer>();
        var cfc = _gameData.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault(cfcId);
        if (cfc == null || cfc.Value.Content.RowId == 0) return Array.Empty<DutyCoffer>();
        var raw = await _garland.GetCoffersAsync(cfc.Value.Content.RowId).ConfigureAwait(false);
        if (raw.Count == 0) return Array.Empty<DutyCoffer>();
        // Garland lists the boss coffers too (same items as our per-boss chests) — drop those; for
        // duties without local data (post-7.1) the fight coffers ARE the drop table. A fight coffer
        // whose items equal a placed coffer is the same chest — keep the placed one (has coordinates).
        var bossChestSets = (GetDutyDetail(cfcId)?.Bosses ?? Array.Empty<DutyBoss>())
            .SelectMany(b => b.Chests).Select(c => c.Items.Select(i => i.ItemId).ToHashSet()).ToList();
        var placedSets = raw.Where(c => c.FightNo < 0).Select(c => c.ItemIds.ToHashSet()).ToList();
        var itemSheet = _gameData.GetExcelSheet<Item>();
        var result = new List<DutyCoffer>();
        foreach (var c in raw)
        {
            if (bossChestSets.Any(set => c.ItemIds.All(set.Contains))) continue;
            if (c.FightNo >= 0 && placedSets.Any(set => set.SetEquals(c.ItemIds))) continue;
            var items = new List<DutyDrop>();
            foreach (var id in c.ItemIds)
            {
                var row = itemSheet?.GetRowOrDefault(id);
                if (row != null) items.Add(MakeDrop(id, row.Value));
            }
            if (items.Count > 0) result.Add(new DutyCoffer(c.X, c.Y, items, c.FightNo));
        }
        return result.OrderBy(c => c.FightNo < 0 ? 1 : 0).ThenBy(c => c.FightNo).ToList();
    }

    // cost item -> every (shop, received item) of the SpecialShop sheet, uncapped (TradeInUses keeps
    // 3 per item for the source cards; the Duty Drops exchange section wants the whole weapon set)
    private Dictionary<uint, List<(string shop, uint receiveId)>>? _exchangeByCost;
    private Dictionary<uint, List<(string shop, uint receiveId)>> ExchangeByCost => _exchangeByCost ??= Safe(BuildExchangeByCost, new());
    private Dictionary<uint, List<(string shop, uint receiveId)>> BuildExchangeByCost()
    {
        var map = new Dictionary<uint, List<(string, uint)>>();
        foreach (var shop in _gameData.GetExcelSheet<SpecialShop>() ?? Enumerable.Empty<SpecialShop>())
        {
            var shopName = shop.Name.ToString();
            if (string.IsNullOrEmpty(shopName)) shopName = "Item Exchange";
            foreach (var entry in shop.Item)
            {
                var recvId = entry.ReceiveItems.Where(r => r.Item.RowId != 0).Select(r => r.Item.RowId).FirstOrDefault();
                if (recvId == 0) continue;
                foreach (var cost in entry.ItemCosts)
                {
                    var costId = cost.ItemCost.RowId;
                    if (costId <= 19) continue; // gil / seals / tomestones: not duty tokens
                    if (!map.TryGetValue(costId, out var list)) map[costId] = list = new();
                    if (!list.Contains((shopName, recvId))) list.Add((shopName, recvId));
                }
            }
        }
        return map;
    }

    private DutyDrop MakeDrop(uint id, Item row)
    {
        // mount = item of a MountItemMap entry (ItemUICategory 63 is the generic "Other" bucket — gil,
        // seals, whistles all share it), minion = ItemUICategory 81 (verified: Wind-up Cursor)
        var kind = MountItemIds.Contains(id) ? "Mount" : row.ItemUICategory.RowId == 81 ? "Minion" : "";
        return new DutyDrop(id, row.Name.ToString(), row.Icon, (int)row.LevelItem.RowId, kind);
    }

    public uint? FindDutyByTerritory(uint territoryTypeId)
    {
        if (territoryTypeId == 0) return null;
        var cfcSheet = _gameData.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null) return null;
        uint? any = null;
        foreach (var cfc in cfcSheet)
        {
            if (cfc.TerritoryType.RowId != territoryTypeId || cfc.Name.IsEmpty) continue;
            if (DutyToItems.ContainsKey(cfc.RowId)) return cfc.RowId;
            any ??= cfc.RowId;
        }
        return any;
    }

    private static ItemSourceDetail Note(ItemSourceType type, string description, IReadOnlyList<CostEntry>? materials = null, uint? sourceItemId = null)
        => new(type, description, null, null, null, null, null, null, null, materials, null, null, null, null, null, null, null, SourceItemId: sourceItemId);

    // ---- lazy per-sheet indexes for the 7e–7l detectors (built on first use, a few ms each) ----
    // Sheet column-hash mismatches (DalaMock ships an older Lumina.Excel than the game) must not
    // kill every lookup — a detector with no index simply never matches.
    private static T Safe<T>(Func<T> build, T empty)
    {
        try { return build(); }
        catch (Exception) { return empty; }
    }

    private Dictionary<uint, List<(uint gc, string gcName, uint seals, uint rank)>>? _gcSealShop;
    private Dictionary<uint, List<(uint gc, string gcName, uint seals, uint rank)>> GcSealShop => _gcSealShop ??= Safe(BuildGcSealShop, new());
    private Dictionary<uint, List<(uint gc, string gcName, uint seals, uint rank)>> BuildGcSealShop()
    {
        var map = new Dictionary<uint, List<(uint, string, uint, uint)>>();
        var cats = _gameData.GetExcelSheet<GCScripShopCategory>();
        var gcs = _gameData.GetExcelSheet<GrandCompany>();
        var sheet = _gameData.GetSubrowExcelSheet<GCScripShopItem>();
        if (cats == null || sheet == null) return map;
        foreach (var rows in sheet)
        {
            foreach (var r in rows)
            {
                if (r.Item.RowId == 0) continue;
                var gcId = cats.GetRowOrDefault(r.RowId)?.GrandCompany.RowId ?? 0;
                var gcName = gcs?.GetRowOrDefault(gcId)?.Name.ToString() ?? "Grand Company";
                if (!map.TryGetValue(r.Item.RowId, out var list)) map[r.Item.RowId] = list = new();
                list.Add((gcId, gcName, r.CostGCSeals, r.RequiredGrandCompanyRank.RowId));
            }
        }
        return map;
    }

    private Dictionary<uint, List<(uint itemId, string name, uint icon)>>? _outfitPieces;
    private Dictionary<uint, List<(uint itemId, string name, uint icon)>> OutfitPieces => _outfitPieces ??= Safe(BuildOutfitPieces, new());
    private Dictionary<uint, List<(uint itemId, string name, uint icon)>> BuildOutfitPieces()
    {
        var map = new Dictionary<uint, List<(uint, string, uint)>>();
        var sheet = _gameData.GetExcelSheet<MirageStoreSetItem>();
        if (sheet == null) return map;
        foreach (var row in sheet)
        {
            var refs = new[] { row.MainHand, row.OffHand, row.Head, row.Body, row.Hands, row.Legs, row.Feet, row.Earrings, row.Necklace, row.Bracelets, row.Ring };
            var list = new List<(uint, string, uint)>();
            foreach (var r in refs)
                if (r.RowId != 0) list.Add((r.RowId, GetItemName(r.RowId) ?? $"#{r.RowId}", GetItemIconId(r.RowId)));
            if (list.Count > 0) map[row.RowId] = list;
        }
        return map;
    }

    private HashSet<uint>? _workshopResults;
    private HashSet<uint> WorkshopResults => _workshopResults ??=
        Safe(() => (_gameData.GetExcelSheet<CompanyCraftSequence>()?.Select(c => c.ResultItem.RowId).Where(id => id != 0) ?? Enumerable.Empty<uint>()).ToHashSet(), new());

    private HashSet<uint>? _cosmicItems;
    private HashSet<uint> CosmicItems => _cosmicItems ??=
        Safe(() => (_gameData.GetExcelSheet<WKSItemInfo>()?.Select(w => w.Item.RowId).Where(id => id != 0) ?? Enumerable.Empty<uint>()).ToHashSet(), new());

    private Dictionary<uint, List<(int level, string zone, uint territory)>>? _spearfishing;
    private Dictionary<uint, List<(int level, string zone, uint territory)>> Spearfishing => _spearfishing ??= Safe(BuildSpearfishing, new());
    private Dictionary<uint, List<(int level, string zone, uint territory)>> BuildSpearfishing()
    {
        var map = new Dictionary<uint, List<(int, string, uint)>>();
        foreach (var sp in _gameData.GetExcelSheet<SpearfishingItem>() ?? Enumerable.Empty<SpearfishingItem>())
        {
            if (sp.Item.RowId == 0) continue;
            var zone = sp.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ToString() ?? "";
            if (!map.TryGetValue(sp.Item.RowId, out var list)) map[sp.Item.RowId] = list = new();
            list.Add(((int)sp.GatheringItemLevel.RowId, zone, sp.TerritoryType.RowId));
        }
        return map;
    }

    private HashSet<uint>? _islandPouch;
    private HashSet<uint> IslandPouch => _islandPouch ??=
        Safe(() => (_gameData.GetExcelSheet<MJIItemPouch>()?.Select(m => m.Item.RowId).Where(id => id != 0) ?? Enumerable.Empty<uint>()).ToHashSet(), new());

    private Dictionary<uint, int>? _gatheringItemLevels;
    private Dictionary<uint, int> GatheringItemLevels => _gatheringItemLevels ??= Safe(BuildGatheringItemLevels, new());
    private Dictionary<uint, int> BuildGatheringItemLevels()
    {
        var map = new Dictionary<uint, int>();
        foreach (var g in _gameData.GetExcelSheet<GatheringItem>() ?? Enumerable.Empty<GatheringItem>())
            if (g.Item.RowId != 0 && !map.ContainsKey(g.Item.RowId)) map[g.Item.RowId] = (int)g.GatheringItemLevel.RowId;
        return map;
    }

    private HashSet<uint>? _fishLog;
    private HashSet<uint> FishLog => _fishLog ??=
        Safe(() => (_gameData.GetExcelSheet<FishParameter>()?.Where(f => f.IsInLog).Select(f => f.Item.RowId).Where(id => id != 0) ?? Enumerable.Empty<uint>()).ToHashSet(), new());

    private Dictionary<uint, List<(string shop, uint shopId, uint receiveId, string receiveName, uint costAmount)>>? _tradeInUses;
    private Dictionary<uint, List<(string shop, uint shopId, uint receiveId, string receiveName, uint costAmount)>> TradeInUses => _tradeInUses ??= Safe(BuildTradeInUses, new());
    private Dictionary<uint, List<(string shop, uint shopId, uint receiveId, string receiveName, uint costAmount)>> BuildTradeInUses()
    {
        var map = new Dictionary<uint, List<(string, uint, uint, string, uint)>>();
        foreach (var shop in _gameData.GetExcelSheet<SpecialShop>() ?? Enumerable.Empty<SpecialShop>())
        {
            var shopName = shop.Name.ToString();
            // same dev/QA-leftover skip as FindSpecialShopSources — "Currency Test" et al. aren't
            // reachable in-game (no NPC binds to them), so they shouldn't surface as a real source.
            if (shopName.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(shopName)) shopName = "Item Exchange";
            foreach (var entry in shop.Item)
            {
                // no FirstOrDefault on the struct itself: a default ReceiveItems struct has no page
                // behind it and its .Item accessor NREs — project to the id first
                var recvId = entry.ReceiveItems.Where(r => r.Item.RowId != 0).Select(r => r.Item.RowId).FirstOrDefault();
                if (recvId == 0) continue;
                foreach (var cost in entry.ItemCosts)
                {
                    var costId = cost.ItemCost.RowId;
                    if (costId == 0 || costId == 1) continue; // 1 = Gil
                    if (!map.TryGetValue(costId, out var list)) map[costId] = list = new();
                    // ponytail: was capped at 3 total — cut off real sets partway through (Guardian
                    // Scale exchanges for 5 "Hope [M]" pieces alone, plus 5 more "[F]" pieces at the
                    // same shop; only the first 3 ever made it in). Raised well past any known set
                    // size; the caller still groups these into one card per shop either way.
                    if (list.Count < 12 && !list.Any(x => x.Item3 == recvId))
                        list.Add((shopName, shop.RowId, recvId, GetItemName(recvId) ?? $"#{recvId}", (uint)cost.CurrencyCost));
                }
            }
        }
        return map;
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
            new CostEntry(0, "Gil", price, 65002) // Gil item icon (was 800, a speech bubble)
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

            // Empty-name shops are real: the Eureka gear exchange (Anemos/Elemental/Pyros/Hydatos),
            // Antiquated AF, Ryumyaku... all live in nameless SpecialShop rows. Skipping them sent
            // ~1000 gear/arms to the generic fallback (full-sheet audit 2026-09-02).
            var shopName = shop.Name.ToString();

            // "Currency Test" (and similarly-named rows) are leftover dev/QA SpecialShop entries —
            // no NPC in the game ever binds to them, so they're unreachable in-game but still sit in
            // the client's own SpecialShop sheet. Verified live: Bozjan Cluster (31135) surfaced as
            // "buyable for MGP" from this shop, which isn't how Zadnor currency actually works
            // (earned via Critical Engagements/Duels, not bought). Skip anything named like a test row.
            if (shopName.Contains("Test", StringComparison.OrdinalIgnoreCase))
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
                            : string.IsNullOrEmpty(shopName) ? "Item Exchange" : $"Shop: {shopName}";

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
        BuildHoardSackContentsCache();
        BuildDeepDungeonNewFloorDropsCache();
    }

    private void BuildDeepDungeonNewFloorDropsCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.DeepDungeonNewFloorDrops.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
                continue;
            if (!uint.TryParse(parts[0], out var itemId) || !uint.TryParse(parts[1], out var cfcId))
                continue;

            _deepDungeonNewFloorDrops.Add((itemId, cfcId));
            // same _itemToDutyMap merge BuildDutyDropCache does for LuminaSupplemental's own rows —
            // feeds the item-source-card ("which duty drops this") and DutyToItems' drop counts.
            if (!_itemToDutyMap.TryGetValue(itemId, out var duties))
                _itemToDutyMap[itemId] = duties = new();
            if (!duties.Contains(cfcId))
                duties.Add(cfcId);
        }
    }

    private void BuildHoardSackContentsCache()
    {
        var assembly = typeof(ItemDetailService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "GlamSource.Core.LuminaSupplemental.HoardSackContents.csv");
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
                continue;
            if (!uint.TryParse(parts[0], out var sackId))
                continue;
            if (!uint.TryParse(parts[1], out var containedId))
                continue;

            if (!_hoardSackContents.TryGetValue(sackId, out var list))
                _hoardSackContents[sackId] = list = new();
            list.Add(containedId);
        }
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

    // Item -> Mount/Companion RowId, built natively from Item.ItemAction — no external dataset.
    // Verified live against real Item/ItemAction sheet data: Chocobo Whistle (item 6001) has
    // ItemAction.Action.RowId 1322 (Mount), Data[0] 1 (Mount sheet row 1); Wind-up Cursor (item 6212)
    // has Action.RowId 853 (Companion/minion), Data[0] 51 (Companion sheet row 51).
    private const uint MountActionType = 1322;
    private const uint CompanionActionType = 853;
    private Dictionary<uint, uint>? _itemToMountRowId;
    private Dictionary<uint, uint>? _itemToCompanionRowId;
    private void EnsureUnlockMaps()
    {
        if (_itemToMountRowId != null) return;
        _itemToMountRowId = new();
        _itemToCompanionRowId = new();
        var itemSheet = _gameData.GetExcelSheet<Item>();
        if (itemSheet == null) return;
        foreach (var item in itemSheet)
        {
            if (!item.ItemAction.IsValid) continue;
            var ia = item.ItemAction.Value;
            if (ia.Data.Count == 0) continue;
            var target = ia.Data[0];
            if (target == 0) continue;
            if (ia.Action.RowId == MountActionType) _itemToMountRowId[item.RowId] = target;
            else if (ia.Action.RowId == CompanionActionType) _itemToCompanionRowId[item.RowId] = target;
        }
    }
    public uint? MountRowIdForItem(uint itemId) { EnsureUnlockMaps(); return _itemToMountRowId!.TryGetValue(itemId, out var id) ? id : null; }
    public uint? CompanionRowIdForItem(uint itemId) { EnsureUnlockMaps(); return _itemToCompanionRowId!.TryGetValue(itemId, out var id) ? id : null; }

    // Duty and NPC display names come back lowercase from the game's own sheets ("the minstrel's
    // ballad...", "chopper") — they're written to be embedded mid-sentence elsewhere in the UI, but
    // the Duty Finder capitalizes the leading word for its own standalone list ("The Minstrel's
    // Ballad..."). Capitalize just the first character to match — proper-noun words later in the
    // name are already capitalized in the source data, so title-casing the whole string isn't needed.
    private static string CapitalizeFirst(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // Minor words the wiki's own MediaWiki title-case convention keeps lowercase mid-title
    // (first word always capitalized regardless — handled below).
    private static readonly HashSet<string> WikiMinorWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "a", "an", "and", "in", "on", "to", "for" };

    private static string TitleCaseWikiName(string s)
    {
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length == 0) continue;
            words[i] = i > 0 && WikiMinorWords.Contains(words[i])
                ? words[i]
                : char.ToUpperInvariant(words[i][0]) + words[i][1..];
        }
        return string.Join(' ', words);
    }

    private Dictionary<uint, uint>? _itemToMountId;
    public string? GetWikiPageName(uint itemId)
    {
        _itemToMountId ??= _mountToItemId.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        if (_itemToMountId.TryGetValue(itemId, out var mountId))
        {
            var mount = Safe(() => _gameData.GetExcelSheet<Mount>(Language.English)?.GetRowOrDefault(mountId)?.Singular.ToString(), null);
            // Mount.Singular is fully lowercase ("lynx of eternal darkness") — MediaWiki page titles
            // are case-sensitive past the first letter, so a mount page needs proper title case
            // ("Lynx of Eternal Darkness": every word capitalized except minor words like "of",
            // verified against the wiki's own page title). .NET's ToTitleCase capitalizes ALL words
            // ("Lynx Of Eternal Darkness"), which would 404 just the same — hence TitleCaseWikiName.
            if (!string.IsNullOrEmpty(mount)) return TitleCaseWikiName(mount);
        }
        return GetEnglishName(itemId);
    }

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
            // Empty-name shops are real: the Eureka gear exchange (Anemos/Elemental/Pyros/Hydatos),
            // Antiquated AF, Ryumyaku... all live in nameless SpecialShop rows. Skipping them sent
            // ~1000 gear/arms to the generic fallback (full-sheet audit 2026-09-02).
            var shopName = shop.Name.ToString();

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
            21 => "Deep Dungeon",
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
            return 65002; // gil rows use itemId 0 — Gil's own icon, not Item row 0's speech bubble (800)

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
