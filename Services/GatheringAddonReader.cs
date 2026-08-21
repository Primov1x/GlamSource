using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace GlamSource.Services;

/// <summary>
/// Native reimplementation of GBR's AtkReader/GatheringReader/Callback.Fire pattern for the
/// "Gathering" addon window — reads only what Phase 4 needs (item slot -> ItemId) and clicks a slot.
/// ponytail: not reused from GatherBuddyReborn.Automation.* — those classes depend on GBR's own
/// static service locators (GatherBuddy.Log/GameData) which are only populated when GBR's plugin
/// constructor runs. Reimplemented independently against GlamSource's own IGameGui/IPluginLog.
/// </summary>
public static unsafe class GatheringAddonReader
{
    private const string AddonName = "Gathering";
    private const int ItemSlotStart = 5;
    private const int ItemSlotStride = 11;
    private const int ItemSlotCount = 8;
    private const int ItemIdOffset = 1; // relative to each slot's start

    private static delegate* unmanaged<AtkUnitBase*, uint, AtkValue*, bool, bool> _fireCallbackPtr;

    /// <summary>Returns the open "Gathering" addon, or null if not present/ready.</summary>
    public static AtkUnitBase* GetAddon(IGameGui gameGui)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName);
        if (addon == null)
            return null;

        return addon->IsFullyLoaded() && addon->IsVisible ? addon : null;
    }

    /// <summary>Finds the item slot index (0-7) for the given ItemId in the open Gathering addon, or -1.</summary>
    public static int FindItemSlot(AtkUnitBase* addon, uint itemId)
    {
        if (addon == null)
            return -1;

        var values = addon->AtkValues;
        var valueCount = addon->AtkValuesCount;

        for (var slot = 0; slot < ItemSlotCount; slot++)
        {
            var idIndex = ItemSlotStart + slot * ItemSlotStride + ItemIdOffset;
            if (idIndex < 0 || idIndex >= valueCount)
                continue;

            var atkValue = values[idIndex];
            if (atkValue.Type != ValueType.UInt &&
                atkValue.Type != ValueType.Int)
                continue;

            if (atkValue.UInt == itemId)
                return slot;
        }

        return -1;
    }

    /// <summary>Clicks the given item slot in the Gathering addon (fires the same callback as a manual click).</summary>
    public static void ClickSlot(AtkUnitBase* addon, int slot, IPluginLog log)
    {
        if (addon == null)
            return;

        Fire(addon, true, log, (uint)slot, 0);
    }

    private static void Fire(AtkUnitBase* unitBase, bool updateState, IPluginLog log, params object[] values)
    {
        if (_fireCallbackPtr == null)
            _fireCallbackPtr = AtkUnitBase.MemberFunctionPointers.FireCallback;

        var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case uint u:
                        atkValues[i].Type = ValueType.UInt;
                        atkValues[i].UInt = u;
                        break;
                    case int n:
                        atkValues[i].Type = ValueType.Int;
                        atkValues[i].Int = n;
                        break;
                    default:
                        throw new ArgumentException($"Unsupported AtkValue type: {values[i]?.GetType()}");
                }
            }

            _fireCallbackPtr(unitBase, (uint)values.Length, atkValues, updateState);
        }
        catch (Exception e)
        {
            log.Error($"[GatheringAddonReader] Callback fire failed: {e.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)atkValues);
        }
    }
}
