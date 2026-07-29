using System;
using System.Collections.Generic;

using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GlamSource.Core;

namespace GlamSource;

public unsafe class GameDataService : IGlamourService
{
    private readonly IDataManager _dataManager;
    private readonly ITargetManager _targetManager;
    private readonly IObjectTable _objectTable;
    private readonly IItemSourceService _sourceService;
    private bool _debugLogged;
    private Dictionary<EquipmentSlotType, List<Item>>? _itemsBySlot;

    public GameDataService(IDataManager dataManager, ITargetManager targetManager, IObjectTable objectTable, IItemSourceService? sourceService = null)
    {
        _dataManager = dataManager;
        _targetManager = targetManager;
        _objectTable = objectTable;
        _sourceService = sourceService ?? new LuminaItemSourceService(dataManager.GameData);
        FindAshShortbow();
        BuildItemSlotCache();
    }

    private void FindAshShortbow()
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Item>();
            if (sheet == null) return;
            foreach (var item in sheet)
            {
                if (item.Name.ToString().Contains("Ash Shortbow"))
                {
                    Plugin.Log.Information("[DEBUG-AshShortbow] RowId={RowId} Name={Name} ModelMain={ModelMain} (raw={RawMain}) ModelSub={ModelSub} (raw={RawSub})",
                        item.RowId,
                        item.Name.ToString(),
                        item.ModelMain,
                        item.ModelMain,
                        item.ModelSub,
                        item.ModelSub);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DEBUG-AshShortbow] Failed");
        }
    }

    private void BuildItemSlotCache()
    {
        _itemsBySlot = new Dictionary<EquipmentSlotType, List<Item>>();
        var itemSheet = _dataManager.GetExcelSheet<Item>();
        if (itemSheet == null) return;

        foreach (var item in itemSheet)
        {
            if (string.IsNullOrEmpty(item.Name.ToString())) continue;
            var esc = item.EquipSlotCategory;
            if (!esc.IsValid) continue;
            var cat = esc.Value;

            AddIfSlot(cat.MainHand, EquipmentSlotType.MainHand, item);
            AddIfSlot(cat.OffHand, EquipmentSlotType.OffHand, item);
            AddIfSlot(cat.Head, EquipmentSlotType.Head, item);
            AddIfSlot(cat.Body, EquipmentSlotType.Body, item);
            AddIfSlot(cat.Gloves, EquipmentSlotType.Hands, item);
            AddIfSlot(cat.Legs, EquipmentSlotType.Legs, item);
            AddIfSlot(cat.Feet, EquipmentSlotType.Feet, item);
            AddIfSlot(cat.Ears, EquipmentSlotType.Earrings, item);
            AddIfSlot(cat.Neck, EquipmentSlotType.Necklace, item);
            AddIfSlot(cat.Wrists, EquipmentSlotType.Bracelets, item);
            AddIfSlot(cat.FingerR, EquipmentSlotType.RingRight, item);
            AddIfSlot(cat.FingerL, EquipmentSlotType.RingLeft, item);
        }
    }

    private void AddIfSlot(sbyte value, EquipmentSlotType slot, Item item)
    {
        if (value <= 0) return;
        if (!_itemsBySlot!.ContainsKey(slot))
            _itemsBySlot[slot] = new List<Item>();
        _itemsBySlot[slot].Add(item);
    }

    public string? GetLocationName(uint territoryId)
    {
        var sheet = _dataManager.GetExcelSheet<TerritoryType>();
        if (sheet?.TryGetRow(territoryId, out var territory) == true)
        {
            return territory.PlaceName.Value.Name.ToString();
        }

        return null;
    }

    public IReadOnlyList<EquipmentSlot> GetTargetEquipment()
    {
        var target = _targetManager.Target;

        if (target == null || target.Address == nint.Zero)
            return Array.Empty<EquipmentSlot>();

        var localPlayer = _objectTable.LocalPlayer;
        Plugin.Log.Information("[EQUIP] target.Id={TId} lp.Id={LpId} match={M}",
            target.GameObjectId,
            localPlayer?.GameObjectId ?? 0,
            localPlayer != null && target.GameObjectId == localPlayer.GameObjectId);

        if (localPlayer != null && target.GameObjectId == localPlayer.GameObjectId)
            return GetOwnEquipment();

        var examineData = GetExamineEquipment();
        if (examineData.Count > 0)
            return examineData;

        return GetDrawDataEquipment(target);
    }

    private unsafe IReadOnlyList<EquipmentSlot> GetOwnEquipment()
    {
        Plugin.Log.Information("[EQUIP] GetOwnEquipment called");
        var im = InventoryManager.Instance();
        if (im == null)
            return Array.Empty<EquipmentSlot>();

        var container = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null)
            return Array.Empty<EquipmentSlot>();

        var result = new List<EquipmentSlot>();
        var itemSheet = _dataManager.GetExcelSheet<Item>();

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->Items[i];
            var itemId = item.ItemId;
            var slotType = MapInventorySlotToEquipmentSlot(i);
            if (slotType == null)
                continue;

            var itemName = ResolveItemName(itemSheet, itemId);

            uint? glamourId = item.GlamourId > 0 ? item.GlamourId : null;
            string? glamourName = glamourId.HasValue
                ? ResolveItemName(itemSheet, glamourId.Value) : null;

            result.Add(CreateSlot(slotType.Value, itemId, itemName, glamourId, glamourName));
        }

        return result;
    }

    private unsafe IReadOnlyList<EquipmentSlot> GetExamineEquipment()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return Array.Empty<EquipmentSlot>();

        var container = im->GetInventoryContainer(InventoryType.Examine);
        if (container == null)
            return Array.Empty<EquipmentSlot>();

        var result = new List<EquipmentSlot>();
        var itemSheet = _dataManager.GetExcelSheet<Item>();

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->Items[i];
            var itemId = item.ItemId;
            var slotType = MapInventorySlotToEquipmentSlot(i);
            if (slotType == null)
                continue;

            var itemName = ResolveItemName(itemSheet, itemId);

            uint? glamourId = item.GlamourId > 0 ? item.GlamourId : null;
            string? glamourName = glamourId.HasValue
                ? ResolveItemName(itemSheet, glamourId.Value) : null;

            result.Add(CreateSlot(slotType.Value, itemId, itemName, glamourId, glamourName));
        }

        return result;
    }

    private static string ResolveItemName(ExcelSheet<Item>? itemSheet, uint itemId)
    {
        if (itemId == 0)
            return "Empty";

        if (itemSheet == null)
            return "Unknown";

        foreach (var row in itemSheet)
        {
            if (row.RowId == itemId)
                return row.Name.ToString();
        }

        return "Unknown";
    }

    private static EquipmentSlotType? MapInventorySlotToEquipmentSlot(int index)
    {
        return index switch
        {
            0 => EquipmentSlotType.MainHand,
            1 => EquipmentSlotType.OffHand,
            2 => EquipmentSlotType.Head,
            3 => EquipmentSlotType.Body,
            4 => EquipmentSlotType.Hands,
            5 => EquipmentSlotType.Legs,
            6 => EquipmentSlotType.Feet,
            7 => EquipmentSlotType.Earrings,
            8 => EquipmentSlotType.Necklace,
            9 => EquipmentSlotType.Bracelets,
            10 => EquipmentSlotType.RingRight,
            11 => EquipmentSlotType.RingLeft,
            _ => null
        };
    }

    private IReadOnlyList<EquipmentSlot> GetDrawDataEquipment(IGameObject target)
    {
        Plugin.Log.Information("[EQUIP] GetDrawDataEquipment FALLBACK");
        if (target is not IPlayerCharacter playerChar)
            return Array.Empty<EquipmentSlot>();

        var charPtr = (Character*)playerChar.Address;
        if (charPtr == null || charPtr->DrawData.OwnerObject == null)
            return Array.Empty<EquipmentSlot>();

        var drawData = charPtr->DrawData;
        var result = new List<EquipmentSlot>();

        for (var i = 0; i < 2; i++)
        {
            var weaponSlot = (DrawDataContainer.WeaponSlot)i;
            var glamourSlot = i == 0 ? EquipmentSlotType.MainHand : EquipmentSlotType.OffHand;
            var modelId = drawData.Weapon(weaponSlot).ModelId.Id;

            if (modelId == 0)
            {
                result.Add(new EquipmentSlot(
                    Slot: glamourSlot,
                    ActualItemId: 0,
                    ActualItemName: "Empty",
                    GlamourItemId: null,
                    GlamourItemName: null));
                continue;
            }

            var slotType = glamourSlot;
            var candidates = _itemsBySlot?.GetValueOrDefault(slotType);
            var matchedItem = FindItemByModelId(candidates, modelId, isWeapon: true, weaponModelMain: i == 0);

            if (!_debugLogged)
            {
                _debugLogged = true;
                Plugin.Log.Information("[DEBUG] Weapon slot {Slot}: DrawData modelId={ModelId} => matched RowId={RowId} Name={Name}",
                    i, modelId, matchedItem?.RowId ?? 0, matchedItem != null && matchedItem.Value.RowId > 0 ? matchedItem.Value.Name.ToString() : "none");
            }

            var itemRowId = matchedItem?.RowId ?? 0;
            var itemName = itemRowId > 0 ? (matchedItem?.Name.ToString() ?? "Unknown") : "Unknown";

            result.Add(CreateSlot(glamourSlot, itemRowId, itemName, null, null));
        }

        for (var i = 0; i < 10; i++)
        {
            var ffxivSlot = (DrawDataContainer.EquipmentSlot)i;
            var glamourSlot = i switch
            {
                0 => EquipmentSlotType.Head,
                1 => EquipmentSlotType.Body,
                2 => EquipmentSlotType.Hands,
                3 => EquipmentSlotType.Legs,
                4 => EquipmentSlotType.Feet,
                5 => EquipmentSlotType.Earrings,
                6 => EquipmentSlotType.Necklace,
                7 => EquipmentSlotType.Bracelets,
                8 => EquipmentSlotType.RingRight,
                9 => EquipmentSlotType.RingLeft,
                _ => throw new Exception("unreachable")
            };

            var modelId = drawData.Equipment(ffxivSlot).Id;
            if (modelId == 0)
                continue;

            var candidates = _itemsBySlot?.GetValueOrDefault(glamourSlot);
            var matchedItem = FindItemByModelId(candidates, modelId, isWeapon: false);

            var itemRowId = matchedItem?.RowId ?? 0;
            var itemName = itemRowId > 0 ? (matchedItem?.Name.ToString() ?? "Unknown") : "Unknown";

            result.Add(CreateSlot(glamourSlot, itemRowId, itemName, null, null));
        }

        return result;
    }

    private static Item? FindItemByModelId(IReadOnlyList<Item>? candidates, uint modelId, bool isWeapon = false, bool weaponModelMain = false)
    {
        if (candidates == null)
            return null;

        foreach (var item in candidates)
        {
            if (isWeapon)
            {
                var primaryId = (ushort)(item.ModelMain & 0xFFFF);
                var variant = (byte)(item.ModelMain >> 32);
                var primaryId2 = (ushort)(item.ModelSub & 0xFFFF);
                var variant2 = (byte)(item.ModelSub >> 32);
                if (weaponModelMain && primaryId == modelId && variant == variant2)
                    return item;
                if (!weaponModelMain && primaryId2 == modelId && variant2 == variant)
                    return item;
            }
            else
            {
                var primaryId = (ushort)(item.ModelMain & 0xFFFF);
                var variant = (byte)(item.ModelMain >> 16);
                var primaryId2 = (ushort)(item.ModelSub & 0xFFFF);
                var variant2 = (byte)(item.ModelSub >> 16);
                if ((primaryId == modelId && variant == variant2) ||
                    (primaryId2 == modelId && variant == variant2))
                    return item;
            }
        }

        return null;
    }

    private EquipmentSlot CreateSlot(EquipmentSlotType slot, uint actualItemId, string actualItemName, uint? glamourItemId, string? glamourItemName)
    {
        var actualSources = actualItemId > 0 ? _sourceService.GetSources(actualItemId) : Array.Empty<ItemSource>();
        IReadOnlyList<ItemSource>? glamourSources = null;
        if (glamourItemId.HasValue && glamourItemId.Value > 0)
        {
            glamourSources = _sourceService.GetSources(glamourItemId.Value);
        }
        return new EquipmentSlot(
            Slot: slot,
            ActualItemId: actualItemId,
            ActualItemName: actualItemName,
            GlamourItemId: glamourItemId,
            GlamourItemName: glamourItemName,
            ActualItemSources: actualSources,
            GlamourItemSources: glamourSources);
    }
}
