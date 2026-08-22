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
    // ponytail: tracks the last target we PushRecent'd so we don't spam Configuration.Save every frame.
    private string _lastRecentKey = "";
    // ponytail: when non-null, Character tab renders this synthesized snapshot (from Recent click / hover).
    private IReadOnlyList<EquipmentSlot>? _recentOverride;
    // ponytail: entity id for hover-preview cleanup (0 = show self glam again).
    private uint _hoverTargetEntityId;
    // ponytail: Name (not index) of the clicked Recent — survives Recent list mutations.
    private string? _activeRecentName;
    // ponytail: last entity handed to the preview (0 = self); guards the per-frame dispatch.
    private uint _previewEntityId;
    // ponytail: live DrawData snapshot of the hovered Recent character while it is visible.
    private IReadOnlyList<EquipmentSlot>? _hoverSnapshot;

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
        UiStyle.PushWindow();
    }

    public override void PostDraw()
    {
        UiStyle.PopWindow();
    }

    public override void Draw()
    {
        using var _style = UiStyle.Push();

        // Header: plugin identity + subtle version chip; keeps every tab anchored to the same brand.
        ImGui.TextColored(UiStyle.Accent, "GlamSource");
        UiStyle.MutedHint("glamour source resolver");
        ImGui.Separator();
        ImGui.Spacing();

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
        UiStyle.SectionHeader("Item Lookup");
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 22f);
        if (ImGui.InputTextWithHint("##item_lookup", "Search any item...", ref _lookupText, 256))
        {
            if (_lookupText.Length >= 3)
                _lookupResults = _glamour.SearchItems(_lookupText).Take(20).ToList();
            else
                _lookupResults = null;
        }

        if (_lookupResults is { Count: > 0 })
        {
            var childSize = new Vector2(ImGui.GetFontSize() * 26f, ImGui.GetFontSize() * 11f);
            using (var card = UiStyle.BeginCard("##lookup_results", childSize))
            {
                if (card.Opened)
                {
                    foreach (var (id, name) in _lookupResults)
                    {
                        if (ImGui.Selectable($"{name}  ({id})##lookup_{id}"))
                        {
                            _detailWindow?.ShowItem(id);
                            _lookupText = "";
                            _lookupResults = null;
                        }
                    }
                }
            }
        }
        else if (!string.IsNullOrEmpty(_lookupText) && _lookupText.Length < 3)
        {
            ImGui.TextColored(UiStyle.Muted, "Type 3+ characters to search.");
        }

        ImGui.Spacing();
        UiStyle.SectionHeader("Target Equipment");

        var slots = _glamour.GetTargetEquipment();

        if (slots.Count == 0)
        {
            ImGui.TextColored(UiStyle.Muted, "No equipment data available — pick a target or examine a player.");
            return;
        }

        var currentTarget = Plugin.TargetManager?.Target;
        var isOwnCharacter = currentTarget != null && currentTarget.GameObjectId == Plugin.ObjectTable.LocalPlayer?.GameObjectId;
        var examineAddon = Plugin.GameGui.GetAddonByName("CharacterInspect");
        var hasExamine = examineAddon != nint.Zero;
        var isDrawDataFallback = !isOwnCharacter && !hasExamine;

        if (isDrawDataFallback)
        {
            ImGui.TextColored(UiStyle.Warning, "  [!]  DrawData fallback in use — Examine the target for full equipment data.");
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("EquipmentTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Worn Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Glamour", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Stain", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 8f);
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
                        _detailWindow?.Open(slot.ActualItemId, slot);
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
                        _detailWindow?.Open(slot.GlamourItemId.Value, slot);
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
                // ponytail: Stain0 primary channel only; secondary rare and adds clutter.
                DrawStainCell(slot.Stain0);
            }

            ImGui.EndTable();
        }
    }

    // ponytail: Lumina Stain sheet; Color is BGRA-packed uint.
    private void DrawStainCell(byte stainId)
    {
        if (stainId == 0)
        {
            ImGui.TextDisabled("Unbemalt");
            return;
        }

        var sheet = _data.GetExcelSheet<Stain>();
        var row = sheet?.GetRowOrDefault(stainId);
        if (row is null)
        {
            ImGui.Text($"#{stainId}");
            return;
        }

        var packed = row.Value.Color;
        var b = (packed >> 16) & 0xFF;
        var g = (packed >> 8) & 0xFF;
        var r = packed & 0xFF;
        var color = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        var sz = ImGui.GetFontSize();
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p, new Vector2(p.X + sz, p.Y + sz), ImGui.ColorConvertFloat4ToU32(color));
        ImGui.Dummy(new Vector2(sz, sz));
        ImGui.SameLine();
        ImGui.TextUnformatted(row.Value.Name.ExtractText());
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
    // Character tab — paperdoll layout (slot columns + inline 3D + recent sidebar)
    // =====================================================================
    private void DrawCharacterTab()
    {
        // Refresh live snapshot every frame unless pinned or Recent-override active.
        if (!_pinned && _recentOverride == null)
        {
            var target = Plugin.TargetManager?.Target;
            IReadOnlyList<EquipmentSlot>? live = null;
            if (target != null)
            {
                live = _glamour.TryGetVisibleGlamour(target.ObjectIndex) ?? _glamour.GetTargetEquipment();
                MaybePushRecentForTarget(target, live);
            }
            _snapshot = live ?? Array.Empty<EquipmentSlot>();
        }
        if (_hoverSnapshot != null)
            _snapshot = _hoverSnapshot;

        // Ensure the CharaView renderer is running as long as this tab is drawn.
        PreviewWindow?.EnsureInitializedForSelf();

        DrawCharacterToolbar();
        ImGui.Separator();

        var fontSize = ImGui.GetFontSize();
        var recentW = fontSize * 10f;
        var slotW = fontSize * 9f;
        var avail = ImGui.GetContentRegionAvail();
        var centerW = MathF.Max(fontSize * 12f, avail.X - (slotW * 2f) - recentW - fontSize * 1.5f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));

        // Left slot column
        if (ImGui.BeginChild("##char_left", new Vector2(slotW, 0), true))
        {
            DrawSlotColumn(LeftCol);
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // Center: 3D preview + toprow (weapons) below
        if (ImGui.BeginChild("##char_center", new Vector2(centerW, 0), true))
        {
            DrawInlinePreview();
            ImGui.Separator();
            DrawSlotRow(TopRow);
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // Right slot column
        if (ImGui.BeginChild("##char_right", new Vector2(slotW, 0), true))
        {
            DrawSlotColumn(RightCol);
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // Recents sidebar
        if (ImGui.BeginChild("##char_recents", new Vector2(recentW, 0), true))
        {
            DrawRecentSidebar();
        }
        ImGui.EndChild();

        ImGui.PopStyleVar();

        // Preview source: hovered player, else visible player of the clicked Recent, else self (0).
        var desired = _hoverTargetEntityId;
        if (desired == 0 && _activeRecentName != null)
            desired = FindVisiblePlayer(_activeRecentName)?.EntityId ?? 0u;
        if (desired != _previewEntityId)
        {
            _previewEntityId = desired;
            PreviewWindow?.ShowCharacterInPreview(desired);
        }
    }

    private void DrawCharacterToolbar()
    {
        var label = _pinned ? "Unpin" : "Pin";
        if (ImGui.SmallButton(label))
        {
            _pinned = !_pinned;
            if (_pinned)
            {
                _snapshot = _snapshot.ToList();
                _pinnedFor = $"snapshot ({_snapshot.Count} slots)";
            }
        }
        ImGui.SameLine();

        if (_recentOverride != null)
        {
            if (ImGui.SmallButton("Clear Recent"))
            {
                _recentOverride = null;
                _activeRecentName = null;
                ClearRecentHover();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Viewing recent snapshot");
        }
        else if (_pinned)
        {
            ImGui.TextDisabled($"Pinned — {_pinnedFor}");
        }
        else
        {
            ImGui.TextDisabled(Plugin.TargetManager?.Target != null ? "Live from target" : "Click somebody or pick from Recent");
        }

        var glamourerInstalled = IsGlamourerInstalled();
        var canApply = glamourerInstalled && _snapshot.Count > 0;

        ImGui.SameLine();
        if (!canApply) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Apply to Self"))
            ApplyTargetGlamourToSelf();
        if (!canApply) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            if (!glamourerInstalled) ImGui.SetTooltip("Requires Glamourer plugin");
            else ImGui.SetTooltip("Copy this snapshot (glamour where set, else actual) to your own character.\nWeapons are skipped.");
        }

        if (!string.IsNullOrEmpty(_lastApplyStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastApplyStatus);
        }

        ImGui.SameLine();
        var canPreview = _snapshot.Count > 0;
        if (!canPreview) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Fitting Room"))
            QueueTryOnPreview();
        if (!canPreview) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Queue each slot into the vanilla Fitting Room. Weapons skipped.");

        if (!string.IsNullOrEmpty(_lastPreviewStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastPreviewStatus);
        }
    }

    // ponytail: only push if we have a real player target and its glam differs from what we last stored.
    private void MaybePushRecentForTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject target, IReadOnlyList<EquipmentSlot>? live)
    {
        if (live == null || live.Count == 0) return;
        if (target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return;

        var name = pc.Name.TextValue;
        var world = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";
        var key = $"{name}@{world}";
        if (key == _lastRecentKey) return;
        _lastRecentKey = key;

        var itemIds = live.Select(s => s.GlamourItemId ?? s.ActualItemId).ToList();
        _configuration.PushRecent(name, world, pc.GameObjectId, itemIds);
    }

    private void DrawSlotColumn(EquipmentSlotType[] slots)
    {
        var iconEdge = ImGui.GetFontSize() * 1.8f;
        var iconVec = new Vector2(iconEdge, iconEdge);
        foreach (var st in slots)
        {
            var slot = _snapshot.FirstOrDefault(x => x.Slot == st);
            DrawSlotBlock(st, slot, iconVec);
        }
    }

    private void DrawSlotRow(EquipmentSlotType[] slots)
    {
        var iconEdge = ImGui.GetFontSize() * 1.8f;
        var iconVec = new Vector2(iconEdge, iconEdge);
        for (var i = 0; i < slots.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var slot = _snapshot.FirstOrDefault(x => x.Slot == slots[i]);
            ImGui.BeginGroup();
            DrawSlotBlock(slots[i], slot, iconVec);
            ImGui.EndGroup();
        }
    }

    private void DrawSlotBlock(EquipmentSlotType st, EquipmentSlot? slot, Vector2 iconVec)
    {
        ImGui.PushID($"slotblock_{st}");
        var itemId = slot?.GlamourItemId ?? slot?.ActualItemId ?? 0u;
        var iconId = itemId > 0 ? GetIconId(itemId) : 0u;
        if (iconId > 0)
        {
            var tex = _textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, iconVec);
            if (ImGui.IsItemClicked() && slot != null)
                _detailWindow?.Open(itemId, slot);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{st}\n{slot?.GlamourItemName ?? slot?.ActualItemName ?? "(none)"}");
        }
        else
        {
            var p = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRect(p, new Vector2(p.X + iconVec.X, p.Y + iconVec.Y),
                ImGui.ColorConvertFloat4ToU32(UiStyle.Muted));
            ImGui.Dummy(iconVec);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{st}\n(empty)");
        }
        ImGui.PopID();
    }

    private void DrawInlinePreview()
    {
        var renderer = PreviewWindow?.Renderer;
        if (renderer == null || !renderer.IsInitialized)
        {
            ImGui.TextDisabled("Preview initializing...");
            return;
        }

        var handle = renderer.GetTextureHandle();
        if (handle == 0)
        {
            ImGui.TextDisabled("Waiting for texture...");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var h = MathF.Max(ImGui.GetFontSize() * 12f, avail.Y - ImGui.GetFontSize() * 6f);
        var w = MathF.Max(ImGui.GetFontSize() * 8f, avail.X);
        // ponytail: character aspect ~0.6 wide/tall; fit whichever axis is tighter.
        var size = w / h > 0.6f ? new Vector2(h * 0.6f, h) : new Vector2(w, w / 0.6f);

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(handle), size);
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##inline_preview_drag", size);

        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                var newZoom = renderer.Zoom + wheel * 0.1f;
                _framework.RunOnFrameworkThread(() => renderer.SetZoom(newZoom));
            }
        }

        // ponytail: right-button orbit (inline view only); left drag above stays as-is.
        var io = ImGui.GetIO();
        if (io.MouseDown[1] && ImGui.IsItemHovered())
        {
            var dx = (float)io.MouseDelta.X;
            var dy = (float)io.MouseDelta.Y;
            PreviewWindow?.SetYawPitch(dx * 0.75f, dy * 0.75f);
        }

        if (ImGui.IsItemActive())
        {
            var mouse = ImGui.GetIO().MousePos;
            if (_previewDragLast.HasValue)
            {
                var delta = mouse - _previewDragLast.Value;
                if (delta.LengthSquared() > 0f)
                {
                    var yaw = delta.X * 0.75f;
                    var pitch = delta.Y * 0.75f;
                    _framework.RunOnFrameworkThread(() => renderer.SetYawPitch(yaw, pitch));
                }
            }
            _previewDragLast = mouse;
        }
        else
        {
            _previewDragLast = null;
        }
    }

    private Vector2? _previewDragLast;

    private void DrawRecentSidebar()
    {
        ImGui.TextColored(UiStyle.Accent, "Recent");
        ImGui.Separator();

        var recents = _configuration.RecentTargets;
        if (recents.Count == 0)
        {
            ImGui.TextDisabled("(none yet)");
            ClearRecentHover();
            return;
        }

        var anyHovered = false;
        for (var i = 0; i < recents.Count; i++)
        {
            var r = recents[i];
            var label = string.IsNullOrEmpty(r.World) ? r.Name : $"{r.Name}\n{r.World}";
            if (ImGui.Selectable($"{label}##recent_{i}", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFontSize() * 2.0f)))
            {
                _recentOverride = BuildSnapshotFromIds(r.ItemIds);
                _snapshot = _recentOverride;
                _pinned = false;
                _activeRecentName = r.Name;
            }
            if (ImGui.IsItemHovered())
            {
                anyHovered = true;
                var pc = FindVisiblePlayer(r.Name);
                _hoverTargetEntityId = pc?.EntityId ?? 0u;
                if (pc != null)
                {
                    // ponytail: refreshed every hovered frame, same cost as the live target scan already done per frame.
                    _hoverSnapshot = _glamour.TryGetVisibleGlamour(pc.ObjectIndex);
                }
                ImGui.SetTooltip(pc != null
                    ? "Click: view stored snapshot\nHover: live glam (currently visible)"
                    : "Click: view stored snapshot\n(not visible — no live data)");
            }
        }

        if (!anyHovered)
            ClearRecentHover();
    }

    // ponytail: linear ObjectTable scan, only while hovering; fine at <200 visible objects, names are zone-unique.
    private Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? FindVisiblePlayer(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var obj in _objectTable)
        {
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc &&
                pc.Name.TextValue == name)
                return pc;
        }
        return null;
    }

    private void ClearRecentHover()
    {
        if (_hoverTargetEntityId == 0 && _hoverSnapshot == null) return;
        _hoverTargetEntityId = 0;
        _hoverSnapshot = null;
    }

    // ponytail: minimal synthetic snapshot — just IDs from Recent; names resolved from Item sheet.
    private IReadOnlyList<EquipmentSlot> BuildSnapshotFromIds(IReadOnlyList<uint> ids)
    {
        var order = new List<EquipmentSlotType>();
        order.AddRange(TopRow); order.AddRange(LeftCol); order.AddRange(RightCol);

        var itemSheet = _data.GetExcelSheet<Item>();
        var result = new List<EquipmentSlot>();
        for (var i = 0; i < order.Count && i < ids.Count; i++)
        {
            var id = ids[i];
            var name = id > 0 && itemSheet != null && itemSheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}";
            result.Add(new EquipmentSlot(order[i], id, name, null, null));
        }
        return result;
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
        UiStyle.SectionHeader("Auto-Gathering");

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
