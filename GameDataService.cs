using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using GlamSource.Core;

namespace GlamSource;

public unsafe class GameDataService : IGlamourService
{
    private readonly IDataManager _dataManager;
    private readonly ITargetManager _targetManager;
    private readonly IItemSourceService _sourceService;
    private bool _debugLogged;

    public GameDataService(IDataManager dataManager, ITargetManager targetManager, IItemSourceService? sourceService = null)
    {
        _dataManager = dataManager;
        _targetManager = targetManager;
        _sourceService = sourceService ?? new LuminaItemSourceService(dataManager.GameData);
        FindAshShortbow();
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
        if (target is not IPlayerCharacter playerChar || playerChar.Address == nint.Zero)
        {
            return Array.Empty<EquipmentSlot>();
        }

        var charPtr = (Character*)playerChar.Address;
        if (charPtr == null)
        {
            return Array.Empty<EquipmentSlot>();
        }

        var drawData = charPtr->DrawData;
        var itemSheet = _dataManager.GetExcelSheet<Item>();

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

            var weaponModelMain = i == 0;
            var matchedItem = itemSheet?
                .FirstOrDefault(item =>
                {
                    // Unpacking-Logik nach Vorbild Penumbra.GameData (Ottermandias), MIT-lizenziert
                    var primaryId    = (ushort)(item.ModelMain & 0xFFFF);
                    var secondaryId  = (ushort)((item.ModelMain >> 16) & 0xFFFF);
                    var variant      = (byte)(item.ModelMain >> 32);
                    var primaryId2   = (ushort)(item.ModelSub & 0xFFFF);
                    var secondaryId2 = (ushort)((item.ModelSub >> 16) & 0xFFFF);
                    var variant2     = (byte)(item.ModelSub >> 32);
                    _ = secondaryId;
                    _ = secondaryId2;
                    return (weaponModelMain && primaryId == modelId && variant == variant2) ||
                           (!weaponModelMain && primaryId2 == modelId && variant2 == variant);
                }) ?? default;

            if (!_debugLogged)
            {
                _debugLogged = true;
                Plugin.Log.Information("[DEBUG] Weapon slot {Slot}: DrawData modelId={ModelId} => matched RowId={RowId} Name={Name}",
                    i, modelId, matchedItem.RowId, matchedItem.Name.ToString());
            }

            var itemRowId = matchedItem.RowId;
            var itemName = itemRowId > 0 ? matchedItem.Name.ToString() : "Unknown";

            result.Add(CreateSlot(glamourSlot, itemRowId, itemName, null, null));
        }

        // Rüstung: 10 Slots — FFXIV-Index → GlamSource.Core.EquipmentSlotType
        //   0=Head   → Head
        //   1=Body   → Body
        //   2=Hands  → Hands
        //   3=Legs   → Legs
        //   4=Feet   → Feet
        //   5=Ears   → Earrings
        //   6=Neck   → Necklace
        //   7=Wrists → Bracelets
        //   8=RFinger→ RingRight
        //   9=LFinger→ RingLeft
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

            var matchedItem = itemSheet?
                .FirstOrDefault(item =>
                {
                    // Unpacking-Logik nach Vorbild Penumbra.GameData (Ottermandias), MIT-lizenziert
                    // Armor: Variant in Bits 16-31 (Waffen: Bits 32-39)
                    var primaryId  = (ushort)(item.ModelMain & 0xFFFF);
                    var variant    = (byte)(item.ModelMain >> 16);
                    var primaryId2 = (ushort)(item.ModelSub & 0xFFFF);
                    var variant2   = (byte)(item.ModelSub >> 16);
                    return (primaryId == modelId && variant == variant2) ||
                           (primaryId2 == modelId && variant == variant2);
                }) ?? default;

            var itemRowId = matchedItem.RowId;
            var itemName = itemRowId > 0 ? matchedItem.Name.ToString() : "Unknown";

            result.Add(CreateSlot(glamourSlot, itemRowId, itemName, null, null));
        }

        return result.AsReadOnly();
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
