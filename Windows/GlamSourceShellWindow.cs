using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamSource.Core;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Lumina.Excel.Sheets;

namespace GlamSource.Windows;

// ponytail: single shell window with an internal tab bar (Lookup / Character / Settings).
// Replaces three separate windows; state that used to live on each Window subclass is now fields here.
public sealed class GlamSourceShellWindow : Window, IDisposable
{
    public enum TabId
    {
        Lookup = 0,
        Character = 1,
        Settings = 2,
    }

    // Wired shared deps
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;
    private readonly IGlamourService _glamour;
    private readonly ItemDetailWindow _detailWindow;
    private readonly ITextureProvider _textures;
    private readonly IDataManager _data;
    private readonly IObjectTable _objectTable;
    private readonly IDalamudPluginInterface _pi;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    // Tab-select handling
    private int _pendingTab = -1;

    // Optional 3D preview window handle (wired by Plugin after ctor).
    public GlamourPreviewWindow? PreviewWindow { get; set; }

    // ---------- Lookup tab state (from MainWindow) ----------
    private string _lookupText = "";
    private IReadOnlyList<(uint id, string name)>? _lookupResults;

    // ---------- Character tab state (from CharacterGlamourWindow) ----------
    private string? _lastApplyStatus;
    private string? _lastPreviewStatus;
    private readonly Queue<(uint itemId, uint glamId)> _tryOnQueue = new();
    private int _tryOnDelay;
    private bool _frameworkHooked;
    private IReadOnlyList<EquipmentSlot> _snapshot = Array.Empty<EquipmentSlot>();
    private bool _pinned;
    private string _pinnedFor = "";
    private bool _selfMode = true;

    private static readonly EquipmentSlotType[] TopRow =
        { EquipmentSlotType.MainHand, EquipmentSlotType.OffHand };
    private static readonly EquipmentSlotType[] LeftCol =
        { EquipmentSlotType.Head, EquipmentSlotType.Body, EquipmentSlotType.Hands, EquipmentSlotType.Legs, EquipmentSlotType.Feet };
    private static readonly EquipmentSlotType[] RightCol =
        { EquipmentSlotType.Earrings, EquipmentSlotType.Necklace, EquipmentSlotType.Bracelets, EquipmentSlotType.RingRight, EquipmentSlotType.RingLeft };

    // ---------- Settings tab state (from ConfigWindow) ----------
    private List<(uint Id, string Name)>? _unlockedMountsCache;

    public GlamSourceShellWindow(
        Plugin plugin,
        IGlamourService glamour,
        ItemDetailWindow detailWindow,
        ITextureProvider textures,
        IDataManager data,
        IObjectTable objectTable,
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log)
        : base("GlamSource", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        _configuration = plugin.Configuration;
        _glamour = glamour;
        _detailWindow = detailWindow;
        _textures = textures;
        _data = data;
        _objectTable = objectTable;
        _pi = pi;
        _framework = framework;
        _log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
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

    public void SwitchToTab(TabId id)
    {
        _pendingTab = (int)id;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        // ponytail: reuse the movable flag from Configuration for the whole shell.
        Flags = _configuration.IsConfigWindowMovable
            ? Flags & ~ImGuiWindowFlags.NoMove
            : Flags | ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##GlamSourceShellTabs", ImGuiTabBarFlags.None))
        {
            DrawTab("Lookup",    TabId.Lookup,    DrawLookupTab);
            DrawTab("Character", TabId.Character, DrawCharacterTab);
            DrawTab("Settings",  TabId.Settings,  DrawSettingsTab);
            ImGui.EndTabBar();
        }
    }

    private void DrawTab(string label, TabId id, System.Action body)
    {
        var flags = ImGuiTabItemFlags.None;
        if (_pendingTab == (int)id)
        {
            flags |= ImGuiTabItemFlags.SetSelected;
            _pendingTab = -1;
        }
        if (ImGui.BeginTabItem(label, flags))
        {
            if (_configuration.SelectedTab != (int)id)
            {
                _configuration.SelectedTab = (int)id;
                _configuration.Save();
            }
            body();
            ImGui.EndTabItem();
        }
    }

    // =====================================================================
    // Lookup tab (formerly MainWindow.Draw)
    // =====================================================================
    private void DrawLookupTab()
    {
        if (ImGui.Button("Open Character Glamour"))
            SwitchToTab(TabId.Character);
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Item Lookup");
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputTextWithHint("##item_lookup", "Search any item...", ref _lookupText, 256))
        {
            if (_lookupText.Length >= 3)
                _lookupResults = _glamour.SearchItems(_lookupText).Take(20).ToList();
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
                        _detailWindow?.ShowItem(id);
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

        var slots = _glamour.GetTargetEquipment();

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

            foreach (var (slot, idx) in slots.Select((s, i) => (s, i)))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"{slot.Slot}");

                ImGui.TableSetColumnIndex(1);
                if (slot.ActualItemId > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
                    if (ImGui.Selectable($"{slot.ActualItemName} ({slot.ActualItemId})##worn_{idx}", false))
                        _detailWindow?.ShowItem(slot.ActualItemId);
                    ImGui.PopStyleColor(3);
                }
                else
                {
                    ImGui.TextDisabled("Empty");
                }

                ImGui.TableSetColumnIndex(2);
                if (slot.IsGlamoured && slot.GlamourItemId.HasValue)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.8f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
                    if (ImGui.Selectable($"{slot.GlamourItemName} ({slot.GlamourItemId})##glam_{idx}", false))
                        _detailWindow?.ShowItem(slot.GlamourItemId.Value);
                    ImGui.PopStyleColor(4);
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
                    if (slot.IsGlamoured && hasGlamourSources)
                    {
                        foreach (var src in slot.GlamourItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Glam: {FormatSource(src)}");
                        }
                    }

                    if (hasActualSources)
                    {
                        foreach (var src in slot.ActualItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Worn: {FormatSource(src)}");
                        }
                    }
                }

                ImGui.TableSetColumnIndex(4);
                if (slot.IsGlamoured)
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "✓");
                else
                    ImGui.TextDisabled("-");
            }

            ImGui.EndTable();
        }
    }

    private static string FormatSource(ItemSource src)
        => src.Type == ItemSourceType.Quest ? "Quest" : src.Description;

    private static Vector4 GetSourceColor(ItemSourceType type) => type switch
    {
        ItemSourceType.Crafted => new Vector4(1f, 0.5f, 0f, 1f),
        ItemSourceType.Vendor => new Vector4(0.5f, 0.5f, 1f, 1f),
        ItemSourceType.Quest => new Vector4(0.5f, 1f, 0.5f, 1f),
        ItemSourceType.Dungeon => new Vector4(1f, 0.3f, 0.3f, 1f),
        ItemSourceType.Trial => new Vector4(1f, 0.8f, 0f, 1f),
        ItemSourceType.Raid => new Vector4(0.8f, 0f, 1f, 1f),
        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
    };

    // =====================================================================
    // Character tab (formerly CharacterGlamourWindow.Draw)
    // =====================================================================
    private void DrawCharacterTab()
    {
        if (!_pinned)
            _snapshot = _selfMode ? _glamour.GetSelfEquipment() : _glamour.GetTargetEquipment();

        DrawCharacterToolbar();
        ImGui.Separator();

        if (_snapshot.Count == 0)
        {
            ImGui.TextDisabled(_pinned ? "Pinned but empty." : (_selfMode ? "No local player." : "No target."));
            return;
        }

        DrawVanillaLayout();
    }

    private void DrawCharacterToolbar()
    {
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

        var glamourerInstalled = IsGlamourerInstalled();
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

        ImGui.SameLine();
        if (ImGui.SmallButton("Preview 3D") && PreviewWindow != null)
            PreviewWindow.OpenForCurrentTarget();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open a live 3D preview of the current target (or self if no target).");

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
        catch { return false; }
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
                if (slot.Slot == EquipmentSlotType.MainHand || slot.Slot == EquipmentSlotType.OffHand)
                    continue;

                var apiSlot = MapToApiSlot(slot.Slot);
                if (apiSlot == ApiEquipSlot.Unknown)
                    continue;

                var itemId = slot.GlamourItemId ?? slot.ActualItemId;
                if (itemId == 0) continue;

                byte stain0 = 0, stain1 = 0;
                var ret = setItem.Invoke(0, apiSlot, itemId, new List<byte> { stain0, stain1 }, 0, ApplyFlag.Once);
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
            if (slot.Slot == EquipmentSlotType.MainHand || slot.Slot == EquipmentSlotType.OffHand)
                continue;

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
            var agent = AgentTryon.Instance();
            if (agent != null)
            {
                var flag = (AgentTryonSaveFlag*)agent;
                flag->SaveDeleteOutfit = true;
            }

            var (itemId, _) = _tryOnQueue.Dequeue();
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
        var slotWidth = iconSize * 2 + 6;
        var colWidth = slotWidth + ImGui.GetStyle().ItemSpacing.X;

        foreach (var s in TopRow)
        {
            var slot = _snapshot.FirstOrDefault(x => x.Slot == s);
            DrawSlot(s, slot, iconVec);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.Spacing();

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

    // =====================================================================
    // Settings tab (formerly ConfigWindow.Draw)
    // =====================================================================
    private void DrawSettingsTab()
    {
        var configValue = _configuration.SomePropertyToBeSavedAndWithADefault;
        if (ImGui.Checkbox("Random Config Bool", ref configValue))
        {
            _configuration.SomePropertyToBeSavedAndWithADefault = configValue;
            _configuration.Save();
        }

        var movable = _configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Window", ref movable))
        {
            _configuration.IsConfigWindowMovable = movable;
            _configuration.Save();
        }

        var showCraftingSavings = _configuration.ShowCraftingSavings;
        if (ImGui.Checkbox("Show Crafting Savings", ref showCraftingSavings))
        {
            _configuration.ShowCraftingSavings = showCraftingSavings;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Auto-Gathering");
        ImGui.Separator();

        DrawMountPicker();

        var mountDist = _configuration.MountUpDistance;
        if (ImGui.SliderFloat("Mount up when farther than (m)", ref mountDist, 0f, 100f))
        {
            _configuration.MountUpDistance = mountDist;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Gearsets (live from game):");

        DrawGearsetCombo("Miner set",    16, _configuration.MinerSetName,    n => _configuration.MinerSetName = n);
        DrawGearsetCombo("Botanist set", 17, _configuration.BotanistSetName, n => _configuration.BotanistSetName = n);
        DrawGearsetCombo("Fisher set",   18, _configuration.FisherSetName,   n => _configuration.FisherSetName = n);
    }

    private unsafe void DrawGearsetCombo(string label, byte classJobId, string current, Action<string> setter)
    {
        var preview = string.IsNullOrEmpty(current) ? "<none>" : current;
        if (!ImGui.BeginCombo(label, preview)) return;

        var mod = RaptureGearsetModule.Instance();
        if (mod != null)
        {
            for (var i = 0; i < 100; i++)
            {
                var e = mod->GetGearset(i);
                if (e == null || !e->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
                if (e->ClassJob != classJobId) continue;

                var name = e->NameString;
                var selected = name == current;
                if (ImGui.Selectable($"{name}##{i}", selected))
                {
                    setter(name);
                    _configuration.Save();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
        }
        ImGui.EndCombo();
    }

    private void DrawMountPicker()
    {
        _unlockedMountsCache ??= BuildUnlockedMounts();

        var current = _unlockedMountsCache.FirstOrDefault(m => m.Id == _configuration.AutoGatherMountId);
        var preview = current.Name ?? "Mount Roulette";
        if (ImGui.BeginCombo("Mount", preview))
        {
            foreach (var (id, name) in _unlockedMountsCache)
            {
                var selected = id == _configuration.AutoGatherMountId;
                if (ImGui.Selectable(name, selected))
                {
                    _configuration.AutoGatherMountId = id;
                    _configuration.Save();
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
