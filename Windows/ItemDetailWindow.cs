using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GlamSource.Core;

namespace GlamSource.Windows;

public class ItemDetailWindow : Window, IDisposable
{
    private readonly IItemDetailService _detailService;
    private readonly IItemSourceService _sourceService;
    private uint? _showingItemId;
    private bool _isOpen;

    private static readonly Dictionary<ItemSourceType, Vector4> SourceColors = new()
    {
        [ItemSourceType.Crafted] = new Vector4(1f, 0.5f, 0f, 1f),
        [ItemSourceType.Vendor] = new Vector4(0.5f, 0.5f, 1f, 1f),
        [ItemSourceType.Quest] = new Vector4(0.5f, 1f, 0.5f, 1f),
        [ItemSourceType.Dungeon] = new Vector4(1f, 0.3f, 0.3f, 1f),
        [ItemSourceType.Trial] = new Vector4(1f, 0.8f, 0f, 1f),
        [ItemSourceType.Raid] = new Vector4(0.8f, 0f, 1f, 1f),
        [ItemSourceType.Unknown] = new Vector4(0.7f, 0.7f, 0.7f, 1f),
        [ItemSourceType.Achievement] = new Vector4(0.9f, 0.9f, 0f, 1f),
        [ItemSourceType.MogStation] = new Vector4(0.6f, 0.4f, 0.8f, 1f),
        [ItemSourceType.PvP] = new Vector4(1f, 0.2f, 0.2f, 1f),
        [ItemSourceType.TreasureHunt] = new Vector4(0.2f, 0.8f, 0.8f, 1f),
        [ItemSourceType.Other] = new Vector4(0.7f, 0.7f, 0.7f, 1f),
    };

    public ItemDetailWindow(IItemDetailService detailService, IItemSourceService sourceService)
        : base($"Item Detail {0x100000}###ItemDetailWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _detailService = detailService;
        _sourceService = sourceService;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 250),
            MaximumSize = new Vector2(700, float.MaxValue)
        };
    }

    public void ShowItem(uint itemId)
    {
        _showingItemId = itemId;
        _isOpen = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (!_isOpen || _showingItemId == null)
        {
            IsOpen = false;
            return;
        }

        var detail = _detailService.GetDetail(_showingItemId.Value);
        if (detail == null)
        {
            ImGui.TextDisabled("Item not found.");
            ImGui.End();
            return;
        }

        DrawItemHeader(detail);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSources(detail);

        DrawFallbackSources(detail.ItemId);
    }

    private void DrawItemHeader(ItemDetail detail)
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), detail.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"({detail.ItemId})");

        if (detail.ItemLevel > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($" | iLvl {detail.ItemLevel}");
        }
    }

    private void DrawSources(ItemDetail detail)
    {
        if (detail.Sources.Count == 0)
        {
            ImGui.TextDisabled("No sources found.");
            return;
        }

        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), "Sources:");
        ImGui.Spacing();

        foreach (var src in detail.Sources)
        {
            DrawSourceDetail(src);
            ImGui.Spacing();
        }
    }

    private void DrawSourceDetail(ItemSourceDetail src)
    {
        var color = SourceColors.TryGetValue(src.Type, out var c) ? c : new Vector4(0.7f, 0.7f, 0.7f, 1f);

        ImGui.TextColored(color, $"  \u25CE {src.Description}");

        // Crafting materials
        if (src.Materials != null && src.Materials.Count > 0)
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled("    Materials:");
            foreach (var mat in src.Materials)
            {
                ImGui.TextDisabled($"      \u2022 {mat.name} x{mat.count}");
            }
            ImGui.Unindent(20f);
        }

        // Vendor costs
        if (src.Costs != null && src.Costs.Count > 0)
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled("    Cost:");
            foreach (var cost in src.Costs)
            {
                if (cost.itemId == 0 && cost.name == "Gil")
                {
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"      \u2022 {cost.count} \uE02D");
                }
                else
                {
                    ImGui.TextDisabled($"      \u2022 {cost.count} {cost.name}");
                }
            }
            ImGui.Unindent(20f);
        }

        // NPC name
        if (!string.IsNullOrEmpty(src.NpcName))
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled($"    NPC: {src.NpcName}");
            ImGui.Unindent(20f);
        }

        // Zone name
        if (!string.IsNullOrEmpty(src.ZoneName))
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled($"    Zone: {src.ZoneName}");
            ImGui.Unindent(20f);
        }

        // Coordinates (nice-to-have, info only)
        if (src.MapX.HasValue && src.MapY.HasValue)
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled($"    Coords: ({src.MapX:F1}, {src.MapY:F1})");
            ImGui.Unindent(20f);
        }
    }

    private void DrawFallbackSources(uint itemId)
    {
        var fallbackSources = _sourceService.GetSources(itemId);
        if (fallbackSources.Count == 0)
            return;

        // Check if any sources are already shown
        var shownTypes = new HashSet<ItemSourceType>();
        foreach (var s in fallbackSources)
        {
            if (!shownTypes.Contains(s.Type))
            {
                shownTypes.Add(s.Type);
                var color = SourceColors.TryGetValue(s.Type, out var c) ? c : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                ImGui.TextColored(color, $"  \u25CE {s.Description}");
                ImGui.Spacing();
            }
        }
    }

    public void Dispose()
    {
    }
}
