using System;
using System.Collections.Generic;

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

            var itemName = "Unknown";
            if (itemSheet?.TryGetRow(modelId, out var item) == true)
            {
                itemName = item.Name.ToString();
            }

            result.Add(new EquipmentSlot(
                Slot: glamourSlot,
                ActualItemId: modelId,
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

            var itemName = "Unknown";
            if (itemSheet?.TryGetRow(modelId, out var item) == true)
            {
                itemName = item.Name.ToString();
            }

            result.Add(new EquipmentSlot(
                Slot: glamourSlot,
                ActualItemId: modelId,
                ActualItemName: itemName,
                GlamourItemId: null,
                GlamourItemName: null));
        }

        return result.AsReadOnly();
    }
}
