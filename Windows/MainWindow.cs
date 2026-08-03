using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GlamSource.Core;

namespace GlamSource.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly IGlamourService _glamourService;
    private readonly ItemDetailWindow? _itemDetailWindow;
    private string _lookupText = "";
    private IReadOnlyList<(uint id, string name)>? _lookupResults;

    public MainWindow(IGlamourService glamourService, ItemDetailWindow? itemDetailWindow = null)
        : base("GlamSource", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _glamourService = glamourService;
        _itemDetailWindow = itemDetailWindow;
    }

    public void Dispose() { }

    private static string FormatSource(ItemSource src)
    {
        return src.Type == ItemSourceType.Quest ? "Quest" : src.Description;
    }

    private static Vector4 GetSourceColor(ItemSourceType type)
    {
        return type switch
        {
            ItemSourceType.Crafted => new Vector4(1f, 0.5f, 0f, 1f),
            ItemSourceType.Vendor => new Vector4(0.5f, 0.5f, 1f, 1f),
            ItemSourceType.Quest => new Vector4(0.5f, 1f, 0.5f, 1f),
            ItemSourceType.Dungeon => new Vector4(1f, 0.3f, 0.3f, 1f),
            ItemSourceType.Trial => new Vector4(1f, 0.8f, 0f, 1f),
            ItemSourceType.Raid => new Vector4(0.8f, 0f, 1f, 1f),
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1f)
        };
    }

    private bool IsValidTarget(IGameObject obj)
    {
        if (obj is not ICharacter)
            return false;

        var ok = (int)obj.ObjectKind;
        return ok == (int)ObjectKind.Pc
            || ok == (int)ObjectKind.BattleNpc
            || ok == (int)ObjectKind.EventNpc;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Item Lookup");
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputTextWithHint("##item_lookup", "Search any item...",
            ref _lookupText, 256))
        {
            if (_lookupText.Length >= 3)
                _lookupResults = _glamourService.SearchItems(_lookupText)
                    .Take(20).ToList();
            else
                _lookupResults = null;
        }

        if (_lookupResults is { Count: > 0 })
        {
            if (ImGui.BeginChild("##lookup_results", new Vector2(350, 150), true))
            {
                foreach (var (id, name) in _lookupResults)
                {
                    if (ImGui.Selectable($"{name} ({id})##lookup_{id}"))
                    {
                        _itemDetailWindow?.ShowItem(id);
                        _lookupText = "";
                        _lookupResults = null;
                    }
                }
            }
            ImGui.EndChild();
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Target Equipment");
        ImGui.Separator();
        ImGui.Spacing();

        var slots = _glamourService.GetTargetEquipment();

        if (slots.Count == 0)
        {
            ImGui.Text("No equipment data available.");
            return;
        }

        var currentTarget = Plugin.TargetManager?.Target;
        var isOwnCharacter = currentTarget != null && currentTarget.GameObjectId == Plugin.ObjectTable.LocalPlayer?.GameObjectId;
        var examineAddon = Plugin.GameGui.GetAddonByName("CharacterInspect");
        var hasExamine = examineAddon != nint.Zero;
        var isDrawDataFallback = !isOwnCharacter && !hasExamine;

        if (isDrawDataFallback)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "[!] Equipment data from DrawData (incomplete). Right-click target -> Examine for full equipment data.");
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("EquipmentTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Worn Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Glamour", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Overlay", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            foreach (var slot in slots)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"{slot.Slot}");

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{slot.ActualItemName} ({slot.ActualItemId})");

                ImGui.TableSetColumnIndex(2);
                if (slot.IsGlamoured)
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), $"{slot.GlamourItemName} ({slot.GlamourItemId})");
                }
                else
                {
                    ImGui.TextDisabled("(none)");
                }

                ImGui.TableSetColumnIndex(3);
                var hasActualSources = slot.ActualItemSources != null && slot.ActualItemSources.Count > 0;
                var hasGlamourSources = slot.GlamourItemSources != null && slot.GlamourItemSources.Count > 0;

                if (!hasActualSources && !hasGlamourSources)
                {
                    ImGui.TextDisabled("Unknown");
                }
                else
                {
                    if (hasActualSources)
                    {
                        foreach (var src in slot.ActualItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Worn: {FormatSource(src)}");
                        }
                    }

                    if (hasGlamourSources)
                    {
                        foreach (var src in slot.GlamourItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Glam: {FormatSource(src)}");
                        }
                    }
                }

                ImGui.TableSetColumnIndex(4);
                if (slot.IsGlamoured)
                {
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "\u2713");
                }
                else
                {
                    ImGui.TextDisabled("-");
                }
            }

            ImGui.EndTable();
        }
    }
}
