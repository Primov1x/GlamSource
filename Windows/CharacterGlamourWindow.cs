using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using GlamSource.Core;
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

    public CharacterGlamourWindow(IGlamourService glamour, ItemDetailWindow detailWindow, ITextureProvider textures, IDataManager data, IObjectTable objectTable)
        : base("Character Glamour")
    {
        _glamour = glamour;
        _detailWindow = detailWindow;
        _textures = textures;
        _data = data;
        _objectTable = objectTable;

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 320) };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

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
    }

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

        // 3-column body
        if (ImGui.BeginTable("##charLayout", 3, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, colWidth);
            ImGui.TableSetupColumn("center", ImGuiTableColumnFlags.WidthStretch);
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
        // ponytail: text-only card. No custom font scaling. Only shown for Self; fallback string for Target.
        string name = "-";
        string job = "-";
        string level = "-";

        if (_selfMode)
        {
            var lp = _objectTable.LocalPlayer;
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
        ImGui.Text(job);
        ImGui.Text(level);
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
