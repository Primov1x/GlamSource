using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource.Core;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;

namespace GlamSource.Windows;

// ponytail: vanilla FFXIV Character-menu style layout. Self/Target toggle; pin freezes snapshot.
public class CharacterGlamourWindow : Window, IDisposable
{
    private readonly IGlamourService _glamour;
    private readonly ItemDetailWindow _detailWindow;
    private readonly ITextureProvider _textures;
    private readonly IDataManager _data;
    private readonly IObjectTable _objectTable;
    private readonly IDalamudPluginInterface _pi;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private string? _lastApplyStatus;
    private string? _lastPreviewStatus;

    // ponytail: single-tick queue drained via Framework.Update; mirrors CriticalCommonLib TryOn.cs pattern.
    // AgentTryon replaces the fitting-room's item for the target slot per call, so we spread the calls across ticks.
    private readonly Queue<(uint itemId, uint glamId)> _tryOnQueue = new();
    private int _tryOnDelay;
    private bool _frameworkHooked;

    private IReadOnlyList<EquipmentSlot> _snapshot = Array.Empty<EquipmentSlot>();
    private bool _pinned;
    private string _pinnedFor = "";
    private bool _selfMode = true; // ponytail: default to local player view.

    private static readonly EquipmentSlotType[] TopRow =
        { EquipmentSlotType.MainHand, EquipmentSlotType.OffHand };
    private static readonly EquipmentSlotType[] LeftCol =
        { EquipmentSlotType.Head, EquipmentSlotType.Body, EquipmentSlotType.Hands, EquipmentSlotType.Legs, EquipmentSlotType.Feet };
    private static readonly EquipmentSlotType[] RightCol =
        { EquipmentSlotType.Earrings, EquipmentSlotType.Necklace, EquipmentSlotType.Bracelets, EquipmentSlotType.RingRight, EquipmentSlotType.RingLeft };

    public CharacterGlamourWindow(IGlamourService glamour, ItemDetailWindow detailWindow, ITextureProvider textures, IDataManager data, IObjectTable objectTable, IDalamudPluginInterface pi, IFramework framework, IPluginLog log)
        : base("Character Glamour")
    {
        _glamour = glamour;
        _detailWindow = detailWindow;
        _textures = textures;
        _data = data;
        _objectTable = objectTable;
        _pi = pi;
        _framework = framework;
        _log = log;

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 320) };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        if (_frameworkHooked)
        {
            _framework.Update -= OnFrameworkDrainTryOn;
            _frameworkHooked = false;
        }
    }

    public override void Draw()
    {
        if (!_pinned)
            _snapshot = _selfMode ? _glamour.GetSelfEquipment() : _glamour.GetTargetEquipment();

        DrawToolbar();
        ImGui.Separator();

        if (_snapshot.Count == 0)
        {
            ImGui.TextDisabled(_pinned ? "Pinned but empty." : (_selfMode ? "No local player." : "No target."));
            return;
        }

        DrawVanillaLayout();
    }

    private void DrawToolbar()
    {
        // Self/Target toggle
        if (ImGui.SmallButton(_selfMode ? "Self" : "Target"))
        {
            _selfMode = !_selfMode;
            if (_pinned) { _pinned = false; _pinnedFor = ""; }
        }
        ImGui.SameLine();

        var label = _pinned ? "Unpin" : "Pin";
        if (ImGui.SmallButton(label))
        {
            _pinned = !_pinned;
            if (_pinned)
            {
                var live = _selfMode ? _glamour.GetSelfEquipment() : _glamour.GetTargetEquipment();
                _snapshot = live.ToList();
                _pinnedFor = $"snapshot ({_snapshot.Count} slots)";
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_pinned ? $"Pinned — {_pinnedFor}" : (_selfMode ? "Live from local player" : "Live from target"));

        // Apply Target Glamour to Self
        var glamourerInstalled = IsGlamourerInstalled();
        // ponytail: enabled only when viewing target data (either target-mode live, or a pinned target snapshot) AND Glamourer present.
        var canApply = glamourerInstalled && !_selfMode && _snapshot.Count > 0;

        if (!canApply) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Apply Target Glamour to Self"))
            ApplyTargetGlamourToSelf();
        if (!canApply) ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            if (!glamourerInstalled)
                ImGui.SetTooltip("Requires Glamourer plugin");
            else if (_selfMode)
                ImGui.SetTooltip("Switch to Target view to copy a target's glamour onto yourself");
            else
                ImGui.SetTooltip("Copy the target's equipment (glamour where set, else actual) to your own character.\nWeapons are skipped.");
        }

        if (!string.IsNullOrEmpty(_lastApplyStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastApplyStatus);
        }

        // ponytail: Try-On preview — opens the game's Fitting Room with target's glamour queued slot-by-slot.
        ImGui.SameLine();
        var canPreview = _snapshot.Count > 0;
        if (!canPreview) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Preview Target (Save Mode)"))
            QueueTryOnPreview();
        if (!canPreview) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens the vanilla Fitting Room and queues each slot (glamour if set, else actual).\nWeapons are skipped. This is the game's own 3D preview — separate window.");

        if (!string.IsNullOrEmpty(_lastPreviewStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastPreviewStatus);
        }
    }

    private bool IsGlamourerInstalled()
    {
        try
        {
            var (major, minor) = new ApiVersion(_pi).Invoke();
            return major > 0 || minor > 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTargetGlamourToSelf()
    {
        try
        {
            var setItem = new SetItem(_pi);
            var applied = 0;
            var failed = 0;
            foreach (var slot in _snapshot)
            {
                // ponytail: skip weapons — MainHand/OffHand model matching is fragile and cross-class swaps can crash the character.
                if (slot.Slot == EquipmentSlotType.MainHand || slot.Slot == EquipmentSlotType.OffHand)
                    continue;

                var apiSlot = MapToApiSlot(slot.Slot);
                if (apiSlot == ApiEquipSlot.Unknown)
                    continue;

                // Prefer the glamoured appearance; fall back to the actual item if no glamour set.
                var itemId = slot.GlamourItemId ?? slot.ActualItemId;
                if (itemId == 0) continue;

                // Glamourer's StainIds ctor NREs on null/empty; always pass 2 bytes.
                // Snapshot has no stain data → default both slots to 0 (undyed).
                byte stain0 = 0, stain1 = 0;
                var ret = setItem.Invoke(0, apiSlot, itemId, new byte[] { stain0, stain1 }, 0, ApplyFlag.Once);
                if (ret == GlamourerApiEc.Success) applied++;
                else { failed++; _log.Warning($"[GlamSource] SetItem {apiSlot} id={itemId} -> {ret}"); }
            }
            _lastApplyStatus = failed == 0 ? $"Applied {applied}." : $"Applied {applied}, {failed} failed (see /xllog).";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[GlamSource] Apply target glamour failed");
            _lastApplyStatus = "Failed — Glamourer IPC error.";
        }
    }

    // ponytail: reinterpret AgentTryon at 0x366 = SaveDeleteOutfit flag.
    // ClientStructs vX exposes it as SaveDeleteOutfit@870, but the shipped Dalamud struct may lag —
    // matches CriticalCommonLib/Services/AgentTryOn2.cs pattern for version-safety.
    [StructLayout(LayoutKind.Explicit, Size = 0x368)]
    private struct AgentTryonSaveFlag
    {
        [FieldOffset(0x366)] public bool SaveDeleteOutfit;
    }

    private unsafe void QueueTryOnPreview()
    {
        _tryOnQueue.Clear();
        var queued = 0;
        foreach (var slot in _snapshot)
        {
            // Weapons: same reason as apply — model-family mismatches can fail loudly.
            if (slot.Slot == EquipmentSlotType.MainHand || slot.Slot == EquipmentSlotType.OffHand)
                continue;

            // Prefer the visible (glamoured) appearance; fall back to actual gear.
            var itemId = slot.GlamourItemId ?? slot.ActualItemId;
            if (itemId == 0) continue;

            _tryOnQueue.Enqueue((itemId, 0));
            queued++;
        }

        if (queued == 0)
        {
            _lastPreviewStatus = "Nothing to preview.";
            return;
        }

        if (!_frameworkHooked)
        {
            _framework.Update += OnFrameworkDrainTryOn;
            _frameworkHooked = true;
        }
        _tryOnDelay = 0;
        _lastPreviewStatus = $"Queued {queued} slots — check Fitting Room.";
    }

    private unsafe void OnFrameworkDrainTryOn(IFramework framework)
    {
        if (_tryOnQueue.Count == 0)
        {
            _framework.Update -= OnFrameworkDrainTryOn;
            _frameworkHooked = false;
            return;
        }
        if (_tryOnDelay-- > 0) return;

        try
        {
            // ponytail: force save-outfit mode ON each tick so items accumulate (default: replace).
            // Reinterpret because ClientStructs field name/offset differ across versions; 0x366 is the stable IDA offset.
            var agent = AgentTryon.Instance();
            if (agent != null)
            {
                var flag = (AgentTryonSaveFlag*)agent;
                flag->SaveDeleteOutfit = true;
            }

            var (itemId, _) = _tryOnQueue.Dequeue();
            // AgentTryon.TryOn(openerAddonId, itemId, stain0, stain1, glamourItemId, applyCompanyCrest)
            AgentTryon.TryOn(0, itemId, 0, 0, 0, false);
            _tryOnDelay = 1;
        }
        catch (Exception ex)
        {
            _log.Warning($"[GlamSource] TryOn drain error: {ex.Message}");
            _tryOnDelay = 5;
        }
    }

    private static ApiEquipSlot MapToApiSlot(EquipmentSlotType s) => s switch
    {
        EquipmentSlotType.Head => ApiEquipSlot.Head,
        EquipmentSlotType.Body => ApiEquipSlot.Body,
        EquipmentSlotType.Hands => ApiEquipSlot.Hands,
        EquipmentSlotType.Legs => ApiEquipSlot.Legs,
        EquipmentSlotType.Feet => ApiEquipSlot.Feet,
        EquipmentSlotType.Earrings => ApiEquipSlot.Ears,
        EquipmentSlotType.Necklace => ApiEquipSlot.Neck,
        EquipmentSlotType.Bracelets => ApiEquipSlot.Wrists,
        EquipmentSlotType.RingRight => ApiEquipSlot.RFinger,
        EquipmentSlotType.RingLeft => ApiEquipSlot.LFinger,
        _ => ApiEquipSlot.Unknown,
    };

    private void DrawVanillaLayout()
    {
        var iconSize = ImGui.GetFontSize() * 2.4f;
        var iconVec = new Vector2(iconSize, iconSize);
        // slot cell = actual + glam side-by-side + 2px gap
        var slotWidth = iconSize * 2 + 6;
        var colWidth = slotWidth + ImGui.GetStyle().ItemSpacing.X;

        // Top row: MainHand + OffHand, centered-ish
        foreach (var s in TopRow)
        {
            var slot = _snapshot.FirstOrDefault(x => x.Slot == s);
            DrawSlot(s, slot, iconVec);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.Spacing();

        // 3-column body. ponytail: fixed center width derived from font size — WidthStretch inside SizingFixedFit collapses to 0 (BUG 1).
        var centerWidth = ImGui.GetFontSize() * 12f;
        if (ImGui.BeginTable("##charLayout", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, colWidth);
            ImGui.TableSetupColumn("center", ImGuiTableColumnFlags.WidthFixed, centerWidth);
            ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthFixed, colWidth);

            var rows = Math.Max(LeftCol.Length, RightCol.Length);
            for (var r = 0; r < rows; r++)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                if (r < LeftCol.Length)
                {
                    var s = LeftCol[r];
                    DrawSlot(s, _snapshot.FirstOrDefault(x => x.Slot == s), iconVec);
                }

                ImGui.TableSetColumnIndex(1);
                if (r == 0) DrawCenterCard();

                ImGui.TableSetColumnIndex(2);
                if (r < RightCol.Length)
                {
                    var s = RightCol[r];
                    DrawSlot(s, _snapshot.FirstOrDefault(x => x.Slot == s), iconVec);
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawCenterCard()
    {
        // ponytail: text-only card. No custom font scaling. Fills from local player in Self mode, else shows target name if available.
        string name = "Loading...";
        string job = "-";
        string level = "-";

        var lp = _objectTable.LocalPlayer;
        if (_selfMode)
        {
            if (lp != null)
            {
                name = lp.Name.TextValue;
                var cj = lp.ClassJob.ValueNullable;
                job = cj.HasValue ? cj.Value.Name.ExtractText() : "-";
                level = $"Lv. {lp.Level}";
            }
        }
        else
        {
            name = "(target)";
        }

        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), name);
        ImGui.Separator();
        ImGui.TextUnformatted(job);
        ImGui.TextUnformatted(level);
    }

    private void DrawSlot(EquipmentSlotType slotType, EquipmentSlot? slot, Vector2 iconVec)
    {
        var actualId = slot?.ActualItemId ?? 0u;
        var actualName = slot?.ActualItemName ?? "-";
        var glamId = slot?.GlamourItemId ?? 0u;
        var glamName = slot?.GlamourItemName;

        ImGui.BeginGroup();
        ImGui.PushID($"slot{slotType}");
        DrawIcon("actual", actualId, actualName, slotType, iconVec);
        ImGui.SameLine(0, 2);
        DrawIcon("glam", glamId, glamName ?? "(no glamour)", slotType, iconVec);
        ImGui.PopID();
        ImGui.EndGroup();
    }

    private void DrawIcon(string tag, uint itemId, string tooltipName, EquipmentSlotType slotType, Vector2 iconVec)
    {
        ImGui.PushID(tag);
        var iconId = itemId > 0 ? GetIconId(itemId) : 0u;
        if (iconId > 0)
        {
            var tex = _textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, iconVec);
            if (ImGui.IsItemClicked() && itemId > 0)
                _detailWindow.ShowItem(itemId);
        }
        else
        {
            // ponytail: dummy keeps layout aligned when slot empty.
            ImGui.Button("-", iconVec);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{slotType} ({tag})\n{tooltipName}");
        ImGui.PopID();
    }

    private uint GetIconId(uint itemId)
    {
        var sheet = _data.GetExcelSheet<Item>();
        if (sheet == null) return 0;
        return sheet.TryGetRow(itemId, out var row) ? row.Icon : 0u;
    }
}

static class _CharacterGlamourWindowSelfCheck
{
    // ponytail: assert-only sanity — enum members referenced exist.
    static _CharacterGlamourWindowSelfCheck()
    {
        _ = EquipmentSlotType.Head;
        _ = EquipmentSlotType.RingLeft;
        _ = EquipmentSlotType.MainHand;
        _ = EquipmentSlotType.OffHand;
    }
}
