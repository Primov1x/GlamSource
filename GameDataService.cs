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
    private bool _debugLogged;

    public GameDataService(IDataManager dataManager, ITargetManager targetManager)
    {
        _dataManager = dataManager;
        _targetManager = targetManager;
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
        if (!_debugLogged)
        {
            _debugLogged = true;
            try
            {
                var debugItem = _dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(46523);
                if (debugItem.HasValue)
                {
                    Plugin.Log.Information("[DEBUG] Item 46523 (Historia Cap of Healing): ModelMain={ModelMain} ({ModelMainType}), ModelSub={ModelSub} ({ModelSubType})",
                        debugItem.Value.ModelMain,
                        debugItem.Value.ModelMain.GetType().Name,
                        debugItem.Value.ModelSub,
                        debugItem.Value.ModelSub.GetType().Name);
                }

                var debugSheet = _dataManager.GetExcelSheet<Item>();
                if (debugSheet != null)
                {
                    var count = 0;
                    foreach (var item in debugSheet)
                    {
                        if (item.ModelMain != 0 || item.ModelSub != 0)
                        {
                            Plugin.Log.Information("[DEBUG] Item {RowId} \"{Name}\": ModelMain={ModelMain} ({ModelMainType}), ModelSub={ModelSub} ({ModelSubType})",
                                item.RowId,
                                item.Name.ToString(),
                                item.ModelMain,
                                item.ModelMain.GetType().Name,
                                item.ModelSub,
                                item.ModelSub.GetType().Name);
                            count++;
                            if (count >= 5) break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[DEBUG] Failed to log item debug info");
            }
        }
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

            var matchedItem = itemSheet?
                .FirstOrDefault(item => (i == 0 && item.ModelMain == modelId) || (i == 1 && item.ModelSub == modelId)) ?? default;

            var itemRowId = matchedItem.RowId;
            var itemName = itemRowId > 0 ? matchedItem.Name.ToString() : "Unknown";

            result.Add(new EquipmentSlot(
                Slot: glamourSlot,
                ActualItemId: itemRowId,
                ActualItemName: itemName,
                GlamourItemId: null,
                GlamourItemName: null));
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
                .FirstOrDefault(item => item.ModelMain == modelId || item.ModelSub == modelId) ?? default;

            var itemRowId = matchedItem.RowId;
            var itemName = itemRowId > 0 ? matchedItem.Name.ToString() : "Unknown";

            result.Add(new EquipmentSlot(
                Slot: glamourSlot,
                ActualItemId: itemRowId,
                ActualItemName: itemName,
                GlamourItemId: null,
                GlamourItemName: null));
        }

        return result.AsReadOnly();
    }
}
