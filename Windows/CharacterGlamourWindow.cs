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

// ponytail: dedicated character glam view. Auto-refresh from target; pin freezes snapshot so it survives target port/loss.
public class CharacterGlamourWindow : Window, IDisposable
{
    private readonly IGlamourService _glamour;
    private readonly ItemDetailWindow _detailWindow;
    private readonly ITextureProvider _textures;
    private readonly IDataManager _data;

    private IReadOnlyList<EquipmentSlot> _snapshot = Array.Empty<EquipmentSlot>();
    private bool _pinned;
    private string _pinnedFor = "";

    private static readonly EquipmentSlotType[] Row1 =
        { EquipmentSlotType.Head, EquipmentSlotType.Body, EquipmentSlotType.Hands, EquipmentSlotType.Legs, EquipmentSlotType.Feet };
    private static readonly EquipmentSlotType[] Row2 =
        { EquipmentSlotType.Earrings, EquipmentSlotType.Necklace, EquipmentSlotType.Bracelets, EquipmentSlotType.RingRight, EquipmentSlotType.RingLeft };

    public CharacterGlamourWindow(IGlamourService glamour, ItemDetailWindow detailWindow, ITextureProvider textures, IDataManager data)
        : base("Character Glamour")
    {
        _glamour = glamour;
        _detailWindow = detailWindow;
        _textures = textures;
        _data = data;

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 200) };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Refresh live unless pinned
        if (!_pinned)
        {
            var live = _glamour.GetTargetEquipment();
            _snapshot = live;
        }

        DrawToolbar();
        ImGui.Separator();

        if (_snapshot.Count == 0)
        {
            ImGui.TextDisabled(_pinned ? "Pinned but empty." : "No target.");
            return;
        }

        DrawRow(Row1);
        ImGui.Spacing();
        DrawRow(Row2);
    }

    private void DrawToolbar()
    {
        var label = _pinned ? "Unpin" : "Pin";
        if (ImGui.SmallButton(label))
        {
            _pinned = !_pinned;
            if (_pinned)
            {
                _snapshot = _glamour.GetTargetEquipment().ToList();
                _pinnedFor = $"snapshot ({_snapshot.Count} slots)";
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_pinned ? $"Pinned — {_pinnedFor}" : "Live from target");
    }

    private void DrawRow(EquipmentSlotType[] slots)
    {
        var iconSize = ImGui.GetFontSize() * 2.5f;
        var iconVec = new Vector2(iconSize, iconSize);

        foreach (var slotType in slots)
        {
            var slot = _snapshot.FirstOrDefault(s => s.Slot == slotType);
            DrawSlot(slotType, slot, iconVec);
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    private void DrawSlot(EquipmentSlotType slotType, EquipmentSlot? slot, Vector2 iconVec)
    {
        var itemId = slot?.GlamourItemId ?? slot?.ActualItemId ?? 0u;
        var itemName = slot?.GlamourItemName ?? slot?.ActualItemName ?? "-";
        var iconId = itemId > 0 ? GetIconId(itemId) : 0u;

        ImGui.BeginGroup();

        ImGui.PushID($"slot{slotType}");
        if (iconId > 0)
        {
            var tex = _textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, iconVec);
            if (ImGui.IsItemClicked() && itemId > 0)
                _detailWindow.ShowItem(itemId);
        }
        else
        {
            // ponytail: dummy button so layout stays aligned even with empty slots.
            ImGui.Button("empty", iconVec);
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{slotType}\n{itemName}{(slot?.IsGlamoured == true ? "\n(glamoured)" : "")}");

        ImGui.EndGroup();
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
    }
}
