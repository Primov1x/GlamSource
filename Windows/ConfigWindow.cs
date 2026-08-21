using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace GlamSource.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    // (mountId, displayName) — built lazily on first Draw. Roulette = 0.
    private List<(uint Id, string Name)>? _unlockedMountsCache;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("GlamSource Settings")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 480),
        };
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        Flags = configuration.IsConfigWindowMovable
            ? Flags & ~ImGuiWindowFlags.NoMove
            : Flags | ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        // Can't ref a property, so use a local copy
        var configValue = configuration.SomePropertyToBeSavedAndWithADefault;
        if (ImGui.Checkbox("Random Config Bool", ref configValue))
        {
            configuration.SomePropertyToBeSavedAndWithADefault = configValue;
            // Can save immediately on change if you don't want to provide a "Save and Close" button
            configuration.Save();
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        var showCraftingSavings = configuration.ShowCraftingSavings;
        if (ImGui.Checkbox("Show Crafting Savings", ref showCraftingSavings))
        {
            configuration.ShowCraftingSavings = showCraftingSavings;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Auto-Gathering");
        ImGui.Separator();

        DrawMountPicker();

        var mountDist = configuration.MountUpDistance;
        if (ImGui.SliderFloat("Mount up when farther than (m)", ref mountDist, 0f, 100f))
        {
            configuration.MountUpDistance = mountDist;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Gearset names (exact, as shown in-game):");

        var miner = configuration.MinerSetName;
        if (ImGui.InputText("Miner set", ref miner, 64))
        {
            configuration.MinerSetName = miner;
            configuration.Save();
        }

        var botanist = configuration.BotanistSetName;
        if (ImGui.InputText("Botanist set", ref botanist, 64))
        {
            configuration.BotanistSetName = botanist;
            configuration.Save();
        }

        var fisher = configuration.FisherSetName;
        if (ImGui.InputText("Fisher set", ref fisher, 64))
        {
            configuration.FisherSetName = fisher;
            configuration.Save();
        }
    }

    private void DrawMountPicker()
    {
        // ponytail: cache once. Player unlocking a new mount mid-session is rare enough that
        // a plugin reload covers it; add a refresh button when someone complains.
        _unlockedMountsCache ??= BuildUnlockedMounts();

        var current = _unlockedMountsCache.FirstOrDefault(m => m.Id == configuration.AutoGatherMountId);
        var preview = current.Name ?? "Mount Roulette";
        if (ImGui.BeginCombo("Mount", preview))
        {
            foreach (var (id, name) in _unlockedMountsCache)
            {
                var selected = id == configuration.AutoGatherMountId;
                if (ImGui.Selectable(name, selected))
                {
                    configuration.AutoGatherMountId = id;
                    configuration.Save();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private static unsafe List<(uint Id, string Name)> BuildUnlockedMounts()
    {
        var list = new List<(uint Id, string Name)> { (0u, "Mount Roulette") };
        var sheet = Plugin.DataManager.GameData.GetExcelSheet<Mount>();
        if (sheet == null) return list;

        var ps = PlayerState.Instance();
        foreach (var m in sheet)
        {
            if (m.RowId == 0) continue;
            var name = m.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (ps == null || !ps->IsMountUnlocked(m.RowId)) continue;
            list.Add((m.RowId, name));
        }
        list.Sort((a, b) => a.Id == 0 ? -1 : b.Id == 0 ? 1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
