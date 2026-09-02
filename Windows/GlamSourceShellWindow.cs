using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
    public Action<bool>? OnDebugApiToggle { get; set; }
    public Action<bool>? OnWebUiToggle { get; set; }
    public Func<string?>? WebUiInlayStatus { get; set; }

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
    // ponytail: Name (not index) of the clicked Recent — survives Recent list mutations.
    private string? _activeRecentName;
    // ponytail: last entity handed to the preview (0 = self); guards the per-frame dispatch.
    private uint _previewEntityId;
    // ponytail: debounce for TargetManager.Target null-flicker while cursor moves over plugin window.
    private uint _lastLiveTarget;
    private long _lastSelfSnapshotMs;
    private int _targetNullFrames;
    private const int TargetNullGraceFrames = 20;
    // ponytail: ignore live target for the first N frames after open — else random hardtarget bleeds in.
    private int _openGraceFrames;
    private const int OpenGraceFrames = 30;
    // ponytail: live DrawData snapshot of the hovered Recent character while it is visible.

    // ponytail: read-only accessors for DebugApiService. No setters exposed on purpose.
    public IReadOnlyList<EquipmentSlot> DebugSnapshot => _recentOverride ?? _snapshot;
    public string? DebugActiveRecentName => _activeRecentName;
    public bool DebugIsRecentOverrideActive => _recentOverride != null;
    public bool DebugPinned => _pinned;
    // ponytail: web-UI Recents sidebar — same read-only-accessor convention as the Debug* family
    // above, just this one mutates (activates a stored snapshot), matching what the native ImGui
    // sidebar's own click handler does (see DrawRecentSidebar/ActivateRecent).
    public IReadOnlyList<RecentTarget> DebugRecentTargets => _configuration.RecentTargets;
    // ponytail: which snapshot source Renderer currently uses; guards provider re-install on state change.
    private enum ProviderKind { None, Recent, Pinned, Target, Self }
    private ProviderKind _lastProviderKind = ProviderKind.None;
    private ProviderKind CurrentProviderKind() =>
        _recentOverride != null ? ProviderKind.Recent :
        _pinned ? ProviderKind.Pinned :
        _previewEntityId != 0 ? ProviderKind.Target : ProviderKind.Self;

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
            MinimumSize = ImGuiHelpers.ScaledVector2(500, 480),
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

    private bool IsBrowsingwayLoaded()
        => _pi.InstalledPlugins.Any(p => p.InternalName == "Browsingway" && p.IsLoaded);

    // ponytail: Browsingway has no IPC and no create-via-command; we can only drive an EXISTING
    // overlay (user creates one named "GlamSource" once). Command name = display name lowercased.
    private void SetWebUiOverlay(bool visible)
    {
        if (!_configuration.WebUiEnabled || !_configuration.WebUiAutoOverlay || !IsBrowsingwayLoaded())
            return;
        if (visible)
        {
            Plugin.CommandManager.ProcessCommand("/bw overlay glamsource url http://127.0.0.1:23424/");
            Plugin.CommandManager.ProcessCommand("/bw overlay glamsource hidden off");
        }
        else
        {
            Plugin.CommandManager.ProcessCommand("/bw overlay glamsource hidden on");
        }
    }

    public override void OnClose() => SetWebUiOverlay(false);

    public override void OnOpen()
    {
        SetWebUiOverlay(true);
        // ponytail: first frame after open shows self, not whatever the game is targeting.
        // User must explicitly re-target (or click Recent) to switch away.
        _lastLiveTarget = 0;
        _targetNullFrames = 0;
        _previewEntityId = 0;
        _snapshot = Array.Empty<EquipmentSlot>();
        _openGraceFrames = OpenGraceFrames;
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
        Loc.Language = _configuration.Language;

        DrawLanguageToggle();

        if (_configuration.WebUiEnabled && _configuration.WebUiAutoOverlay && IsBrowsingwayLoaded())
        {
            using (ImRaii.PushColor(ImGuiCol.Button, UiStyle.Accent))
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.05f, 0.05f, 0.06f, 1f)))
            {
                if (ImGui.Button(Loc.T("Open Web UI")))
                    SetWebUiOverlay(true);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Re-show the in-game web overlay — use this if it was hidden/closed."));
            ImGui.Separator();
        }

        if (ImGui.BeginTabBar("##GlamSourceShellTabs", ImGuiTabBarFlags.None))
        {
            DrawTab(Loc.T("Lookup"),    TabId.Lookup,    DrawLookupTab);
            DrawTab(Loc.T("Character"), TabId.Character, DrawCharacterTab);
            DrawTab(Loc.T("Settings"),  TabId.Settings,  DrawSettingsTab);
            ImGui.EndTabBar();
        }
    }

    // ponytail: top-right, above everything else — closest ImGui gets to "next to the window's
    // minimize control" since the actual collapse button belongs to Dalamud's own title bar, which
    // plugins can't add buttons to.
    private void DrawLanguageToggle()
    {
        var label = _configuration.Language == "de" ? "EN" : "DE";
        var avail = ImGui.GetContentRegionAvail();
        var w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, avail.X - w));
        if (ImGui.SmallButton(label))
        {
            _configuration.Language = _configuration.Language == "de" ? "en" : "de";
            _configuration.Save();
            Loc.Language = _configuration.Language;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_configuration.Language == "de" ? "Switch to English" : "Auf Deutsch umschalten");
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
            body();
            ImGui.EndTabItem();
        }
    }

    // =====================================================================
    // Lookup tab (formerly MainWindow.Draw)
    // =====================================================================
    private void DrawLookupTab()
    {
        UiStyle.SectionHeader(Loc.T("Item Lookup"));
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 22f);
        if (ImGui.InputTextWithHint("##item_lookup", Loc.T("Search any item..."), ref _lookupText, 256))
        {
            if (_lookupText.Length >= 3)
                _lookupResults = _glamour.SearchItems(_lookupText).Take(20).ToList();
            else
                _lookupResults = null;
        }
        if (!string.IsNullOrEmpty(_lookupText))
        {
            ImGui.SameLine();
            using (ImRaii.PushId("lookup_clear"))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
                {
                    _lookupText = "";
                    _lookupResults = null;
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Clear search"));
            if (_lookupResults != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(UiStyle.Muted, $"{_lookupResults.Count} results");
            }
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
                        var resultIconId = GetIconId(id);
                        if (resultIconId > 0)
                        {
                            var tex = _textures.GetFromGameIcon(new GameIconLookup(resultIconId)).GetWrapOrEmpty();
                            var edge = ImGui.GetFontSize() * 1.2f;
                            ImGui.Image(tex.Handle, new Vector2(edge, edge));
                            ImGui.SameLine();
                        }
                        if (ImGui.Selectable($"{name}##lookup_{id}"))
                        {
                            _detailWindow?.ShowItem(id);
                            _lookupText = "";
                            _lookupResults = null;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Item ID {id} — click for details");
                    }
                }
            }
        }
        else if (!string.IsNullOrEmpty(_lookupText) && _lookupText.Length < 3)
        {
            ImGui.TextColored(UiStyle.Muted, Loc.T("Type 3+ characters to search."));
        }
        else if (_lookupResults is { Count: 0 })
        {
            ImGui.TextColored(UiStyle.Muted, Loc.T("No items found."));
        }

        ImGui.Spacing();
        UiStyle.SectionHeader(Loc.T("Target Equipment"));

        var slots = _glamour.GetTargetEquipment();

        if (slots.Count == 0)
        {
            ImGui.TextColored(UiStyle.Muted, Loc.T("No equipment data available — pick a target or examine a player."));
            return;
        }

        var currentTarget = Plugin.TargetManager?.Target;
        var isOwnCharacter = currentTarget != null && currentTarget.GameObjectId == Plugin.ObjectTable.LocalPlayer?.GameObjectId;
        var examineAddon = Plugin.GameGui.GetAddonByName("CharacterInspect");
        var hasExamine = examineAddon != nint.Zero;
        var isDrawDataFallback = !isOwnCharacter && !hasExamine;

        if (isDrawDataFallback)
        {
            ImGui.TextColored(UiStyle.Warning, Loc.T("  [!]  DrawData fallback in use — Examine the target for full equipment data."));
            ImGui.Spacing();
        }

        if (ImGui.BeginTable("EquipmentTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(Loc.T("Slot"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 7f);
            ImGui.TableSetupColumn(Loc.T("Worn Item"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Glamour"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Source"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Stain"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 8f);
            ImGui.TableHeadersRow();

            foreach (var (slot, idx) in slots.Select((s, i) => (s, i)))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"{slot.Slot}");

                ImGui.TableSetColumnIndex(1);
                if (slot.ActualItemId > 0)
                {
                    if (ImGui.Selectable($"{slot.ActualItemName}##worn_{idx}", false))
                        _detailWindow?.Open(slot.ActualItemId, slot);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Item ID {slot.ActualItemId} — click for details");
                }
                else
                {
                    ImGui.TextDisabled(Loc.T("Empty"));
                }

                ImGui.TableSetColumnIndex(2);
                if (slot.IsGlamoured && slot.GlamourItemId.HasValue)
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, UiStyle.Success))
                    {
                        if (ImGui.Selectable($"{slot.GlamourItemName}##glam_{idx}", false))
                            _detailWindow?.Open(slot.GlamourItemId.Value, slot);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Item ID {slot.GlamourItemId} — click for details");
                }
                else
                {
                    ImGui.TextDisabled(Loc.T("(none)"));
                }

                ImGui.TableSetColumnIndex(3);
                var hasActualSources = slot.ActualItemSources != null && slot.ActualItemSources.Count > 0;
                var hasGlamourSources = slot.GlamourItemSources != null && slot.GlamourItemSources.Count > 0;

                if (!hasActualSources && !hasGlamourSources)
                {
                    ImGui.TextDisabled(Loc.T("Unknown"));
                }
                else
                {
                    if (slot.IsGlamoured && hasGlamourSources)
                    {
                        foreach (var src in slot.GlamourItemSources!)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Glam: {FormatSource(src)}");
                        }
                    }

                    if (hasActualSources)
                    {
                        foreach (var src in slot.ActualItemSources!)
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
            ImGui.TextDisabled(Loc.T("Undyed"));
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
        SyncLiveTargetSnapshot();

        DrawCharacterToolbar();
        ImGui.Separator();

        var fontSize = ImGui.GetFontSize();
        var recentW = fontSize * 8f;
        // ponytail: matches DrawSlotColumn's iconEdge (2.2x) + small margin.
        var slotW = fontSize * 2.6f;
        var avail = ImGui.GetContentRegionAvail();
        var centerW = MathF.Max(fontSize * 12f, avail.X - (slotW * 2f) - recentW - fontSize * 0.5f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ImGui.GetFontSize() * 0.25f, ImGui.GetFontSize() * 0.25f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(fontSize * 0.3f, fontSize * 0.2f));

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

        ImGui.PopStyleVar(2);

        SyncPreviewTargetDispatch();
    }

    // ponytail: extracted from DrawCharacterTab (was inline there only) — reported live: web-UI
    // preview didn't follow a fresh in-game target-click at all, only updating after opening the
    // native ImGui window's Character tab once. Root cause: this logic only ever ran as part of
    // THAT tab's own Draw() call, so with the native window closed (web-UI-only usage) it simply
    // never executed. Now called both from DrawCharacterTab() (unchanged behavior when the native
    // window is open) AND from an always-on Framework.Update hook (see SyncPreviewForWeb/Plugin.cs)
    // so the web preview stays live regardless of window visibility.
    private void SyncLiveTargetSnapshot()
    {
        // ponytail: a fresh hardtarget overrides a stuck Recent selection.
        var currentTarget = Plugin.TargetManager?.Target;
        var currentTargetId = currentTarget?.EntityId ?? 0;
        if (currentTargetId != 0 && currentTargetId != _lastLiveTarget && _recentOverride != null)
        {
            _recentOverride = null;
            _activeRecentName = null;
        }
        // ponytail: track live target even when override active — otherwise every frame
        // sees "new" hardtarget and wipes the just-clicked Recent.
        if (currentTargetId != 0) _lastLiveTarget = currentTargetId;

        // Refresh live snapshot every frame unless pinned or Recent-override active.
        // ponytail: only overwrite _snapshot when we actually have a target; null-flicker keeps the last one.
        if (!_pinned && _recentOverride == null)
        {
            IReadOnlyList<EquipmentSlot>? live = null;
            if (currentTarget != null)
            {
                live = _glamour.TryGetVisibleGlamour(currentTarget.ObjectIndex) ?? _glamour.GetTargetEquipment();
                MaybePushRecentForTarget(currentTarget, live);
                if (live != null && live.Count > 0) _snapshot = live;
            }
            // SELF fallback also when a target yields nothing — targeting an NPC/object made the
            // player-path return empty AND starved the self-path forever: empty slot list, naked
            // clone (reported live as "bin trotzdem nackt"). Throttled: this walks the inventory
            // every call, per-frame it measurably cost fps.
            if ((live == null || live.Count == 0) && Environment.TickCount64 - _lastSelfSnapshotMs > 2000)
            {
                _lastSelfSnapshotMs = Environment.TickCount64;
                var self = _glamour.GetSelfEquipment();
                if (self.Count > 0) _snapshot = self;
            }
        }
        // Ensure the CharaView renderer is running as long as this tab is drawn.
        PreviewWindow?.EnsureInitializedForSelf();
    }

    // ponytail: see SyncLiveTargetSnapshot's comment — same extraction, same reason.
    private void SyncPreviewTargetDispatch()
    {
        // Preview = live target (or none). Recent click drives CharaView directly via
        // SetCharaViewItemSlot in ApplyRecentGlamOverride, so overlay must stay cleared then.
        // ponytail: debounce target null-flicker (mouse-over plugin loses hardtarget briefly).
        // Only accept a null target after N consecutive frames; non-null wins instantly.
        uint desired;
        if (_recentOverride != null || _pinned)
        {
            desired = 0;
            _targetNullFrames = 0;
        }
        else if (_openGraceFrames > 0)
        {
            _openGraceFrames--;
            desired = 0;
        }
        else
        {
            var live = Plugin.TargetManager?.Target?.EntityId ?? 0;
            if (live != 0)
            {
                desired = live;
                _lastLiveTarget = live;
                _targetNullFrames = 0;
            }
            else if (_lastLiveTarget != 0 && _targetNullFrames < TargetNullGraceFrames)
            {
                _targetNullFrames++;
                desired = _lastLiveTarget;
            }
            else
            {
                desired = 0;
                _lastLiveTarget = 0;
            }
        }
        // ponytail: unified snapshot dispatch. Provider installed every state change; Renderer overlay
        // is the ONLY writer to CharaView._items (what render pipeline reads). Priority: recent > pinned > target > self.
        if (desired != _previewEntityId || _lastProviderKind != CurrentProviderKind())
        {
            _previewEntityId = desired;
            if (_recentOverride != null)
            {
                // ponytail: closure reads _recentOverride live so subsequent Recent clicks
                // pick up new snapshot without needing dispatch re-fire.
                PreviewWindow?.SetSnapshotProvider(() => _recentOverride);
                _lastProviderKind = ProviderKind.Recent;
            }
            else if (_pinned)
            {
                var snap = _snapshot;
                PreviewWindow?.SetSnapshotProvider(() => snap);
                _lastProviderKind = ProviderKind.Pinned;
            }
            else if (desired != 0)
            {
                PreviewWindow?.ShowCharacterInPreview(desired);
                _lastProviderKind = ProviderKind.Target;
            }
            else
            {
                // ponytail: self-view — ObjectTable[0] is LocalPlayer; resolve every tick, no caching.
                PreviewWindow?.SetSnapshotProvider(() =>
                {
                    var lp = _objectTable[0];
                    return lp == null ? null : _glamour.TryGetVisibleGlamour(lp.ObjectIndex);
                });
                _lastProviderKind = ProviderKind.Self;
            }
        }
    }

    /// <summary>Runs the same live-target sync + dispatch DrawCharacterTab() does, for callers that
    /// want the web preview kept in sync WITHOUT the native ImGui window being open at all — see
    /// SyncLiveTargetSnapshot's comment for why this needed to exist as a standalone entry point.
    /// Safe to call every Framework.Update tick; identical dedup/debounce logic as the Draw() path.</summary>
    public void SyncPreviewForWeb()
    {
        SyncLiveTargetSnapshot();
        SyncPreviewTargetDispatch();
    }

    private void DrawCharacterToolbar()
    {
        // ponytail: buttons in a stable row; free-form status texts live on one line below,
        // so the toolbar width no longer jumps with every action.
        var label = _pinned ? Loc.T("Unpin") : Loc.T("Pin");
        if (ImGui.SmallButton(label))
        {
            _pinned = !_pinned;
            if (_pinned)
            {
                _snapshot = _snapshot.ToList();
                _pinnedFor = $"snapshot ({_snapshot.Count} slots)";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_pinned ? Loc.T("Release the pinned snapshot") : Loc.T("Freeze the current snapshot"));

        if (_recentOverride != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Clear Recent")))
            {
                _recentOverride = null;
                _activeRecentName = null;
            }
        }

        var glamourerInstalled = IsGlamourerInstalled();
        var canApply = glamourerInstalled && _snapshot.Count > 0;

        ImGui.SameLine();
        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.SmallButton(Loc.T("Apply to Self")))
                ApplyTargetGlamourToSelf();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (!glamourerInstalled) ImGui.SetTooltip(Loc.T("Requires Glamourer plugin"));
            else ImGui.SetTooltip(Loc.T("Copy this snapshot (glamour where set, else actual) to your own character.\nWeapons are skipped."));
        }

        ImGui.SameLine();
        var canPreview = _snapshot.Count > 0;
        using (ImRaii.Disabled(!canPreview))
        {
            if (ImGui.SmallButton(Loc.T("Fitting Room")))
                QueueTryOnPreview();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("Queue each slot into the vanilla Fitting Room. Weapons skipped."));

        // single status line: mode + last action feedback
        var mode = _recentOverride != null ? Loc.T("Viewing recent snapshot")
            : _pinned ? $"{Loc.T("Pinned")} — {_pinnedFor}"
            : Plugin.TargetManager?.Target != null ? Loc.T("Live from target") : Loc.T("Click somebody or pick from Recent");
        var feedback = !string.IsNullOrEmpty(_lastApplyStatus) ? _lastApplyStatus
            : !string.IsNullOrEmpty(_lastPreviewStatus) ? _lastPreviewStatus : null;
        ImGui.TextDisabled(feedback != null ? $"{mode}  \u00b7  {feedback}" : mode);
    }

    // ponytail: only push if we have a real player target and its glam differs from what we last stored.
    private void MaybePushRecentForTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject target, IReadOnlyList<EquipmentSlot>? live)
    {
        if (live == null || live.Count == 0) return;
        if (target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return;

        var name = pc.Name.TextValue;
        var world = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";

        // ponytail: MUST match BuildSnapshotFromIds order (TopRow + LeftCol + RightCol) —
        // otherwise stains land on wrong slots after reload.
        var order = new List<EquipmentSlotType>();
        order.AddRange(TopRow); order.AddRange(LeftCol); order.AddRange(RightCol);
        var itemIds = new List<uint>(order.Count);
        var stain0s = new List<byte>(order.Count);
        var stain1s = new List<byte>(order.Count);
        foreach (var st in order)
        {
            var s = live.FirstOrDefault(x => x.Slot == st);
            itemIds.Add(s == null ? 0u : (s.GlamourItemId ?? s.ActualItemId));
            stain0s.Add(s?.Stain0 ?? 0);
            stain1s.Add(s?.Stain1 ?? 0);
        }

        // ponytail: key includes glam content, not just who — otherwise a dye/item change
        // on an already-seen target never re-pushes and Recent keeps the stale snapshot.
        var key = $"{name}@{world}|{string.Join(',', itemIds)}|{string.Join(',', stain0s)}|{string.Join(',', stain1s)}";
        if (key == _lastRecentKey) return;
        _lastRecentKey = key;

        _configuration.PushRecent(name, world, pc.GameObjectId, itemIds, stain0s, stain1s);
    }

    private void DrawSlotColumn(EquipmentSlotType[] slots)
    {
        // ponytail: viewer is static, icons don't need to be this large — compacted from 2.8x.
        var iconEdge = ImGui.GetFontSize() * 2.2f;
        var iconVec = new Vector2(iconEdge, iconEdge);
        foreach (var st in slots)
        {
            var slot = _snapshot.FirstOrDefault(x => x.Slot == st);
            DrawSlotBlock(st, slot, iconVec);
        }
    }

    private void DrawSlotRow(EquipmentSlotType[] slots)
    {
        var iconEdge = ImGui.GetFontSize() * 2.2f;
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
            var iconPos = ImGui.GetCursorScreenPos();
            ImGui.Image(tex.Handle, iconVec);
            if (ImGui.IsItemClicked() && slot != null)
                _detailWindow?.Open(itemId, slot);
            if (ImGui.IsItemHovered())
            {
                // hover affordance: accent border around the icon
                ImGui.GetWindowDrawList().AddRect(iconPos,
                    new Vector2(iconPos.X + iconVec.X, iconPos.Y + iconVec.Y),
                    ImGui.ColorConvertFloat4ToU32(UiStyle.Accent));
                ImGui.SetTooltip($"{st}\n{slot?.GlamourItemName ?? slot?.ActualItemName ?? "(none)"}\nClick to open");
            }
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
            ImGui.TextDisabled(Loc.T("Preview initializing..."));
            return;
        }

        var handle = renderer.GetTextureHandle();
        if (handle == 0)
        {
            ImGui.TextDisabled(Loc.T("Waiting for texture..."));
            return;
        }


        if (ImGui.Checkbox(Loc.T("Show Weapon/Tool"), ref _weaponDrawn))
        {
            var drawn = _weaponDrawn;
            _framework.RunOnFrameworkThread(() => renderer.SetWeaponDrawn(drawn));
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("Drag: rotate · Right-drag: orbit · Wheel: zoom to cursor"));

        var avail = ImGui.GetContentRegionAvail();
        var h = MathF.Max(ImGui.GetFontSize() * 12f, avail.Y - ImGui.GetFontSize() * 6f);
        var w = MathF.Max(ImGui.GetFontSize() * 8f, avail.X);
        // ponytail: character aspect ~0.6 wide/tall; fit whichever axis is tighter.
        var size = w / h > 0.6f ? new Vector2(h * 0.6f, h) : new Vector2(w, w / 0.6f);

        // ponytail: center horizontally in the child instead of hugging the left edge.
        var centerX = MathF.Max(0f, (avail.X - size.X) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + centerX);
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(handle), size);
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##inline_preview_drag", size);

        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                var newZoom = renderer.Zoom + wheel * 0.2f;
                // ponytail: zoom-to-cursor — pan toward the point under the mouse by its offset
                // from image center, scaled with the zoom step. Screen-space approximation, not a
                // true unprojection, but keeps the hovered spot roughly anchored while zooming.
                var mousePos = ImGui.GetMousePos();
                var offsetX = mousePos.X - (cursor.X + size.X * 0.5f);
                var offsetY = mousePos.Y - (cursor.Y + size.Y * 0.5f);
                _framework.RunOnFrameworkThread(() =>
                {
                    renderer.SetZoom(newZoom);
                    renderer.PanCamera(offsetX * wheel * 0.2f, offsetY * wheel * 0.2f);
                });
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
    private bool _weaponDrawn;

    private void DrawRecentSidebar()
    {
        UiStyle.SectionHeader(Loc.T("Recent"));

        var recents = _configuration.RecentTargets;
        if (recents.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("(none yet)"));
            return;
        }

        int? removeIdx = null;
        var xW = ImGui.GetFrameHeight();
        for (var i = 0; i < recents.Count; i++)
        {
            var r = recents[i];
            var label = string.IsNullOrEmpty(r.World) ? r.Name : $"{r.Name}\n{r.World}";
            var rowH = ImGui.GetFontSize() * 2.0f;
            // ponytail: enforce min-width so zero content-region doesn't zero-out the click area.
            var selW = MathF.Max(ImGui.GetFontSize() * 2.5f, ImGui.GetContentRegionAvail().X - xW - ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.Selectable($"{label}##recent_{i}", false, ImGuiSelectableFlags.None, new Vector2(selW, rowH)))
                ActivateRecent(i);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("View stored snapshot"));
            ImGui.SameLine();
            using (ImRaii.PushId($"recent_del_{i}"))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
                    removeIdx = i;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Remove from Recent"));
        }
        if (removeIdx is int idx)
            RemoveRecent(idx);
    }

    /// <summary>Delete a stored Recent — the native sidebar's X-button handler, extracted so the
    /// web UI's Recents sidebar can do the same thing (was ImGui-only before).</summary>
    public void RemoveRecent(int index)
    {
        var recents = _configuration.RecentTargets;
        if (index < 0 || index >= recents.Count) return;
        var removed = recents[index];
        recents.RemoveAt(index);
        _configuration.Save();
        if (_activeRecentName == removed.Name)
        {
            _recentOverride = null;
            _activeRecentName = null;
        }
    }

    /// <summary>View a stored Recent snapshot — the native sidebar's click handler, extracted so
    /// the web UI's own Recents sidebar (WebUiService's /api/action/recent/{index}) can trigger the
    /// exact same thing without duplicating the logic.</summary>
    public void ActivateRecent(int index)
    {
        var recents = _configuration.RecentTargets;
        if (index < 0 || index >= recents.Count) return;
        var r = recents[index];
        _recentOverride = BuildSnapshotFromIds(r.ItemIds, r.Stain0s, r.Stain1s);
        _snapshot = _recentOverride;
        _pinned = false;
        _activeRecentName = r.Name;
        // ponytail: force provider re-install so subsequent Recent clicks push new snapshot even
        // when dispatch guard sees no state change.
        var snap = _recentOverride;
        PreviewWindow?.SetSnapshotProvider(() => snap);
    }

    // ponytail: minimal synthetic snapshot — IDs + stains from Recent; names resolved from Item sheet.
    // Stain lists may be shorter (configs saved before stain persistence) — missing index falls back to 0.
    private IReadOnlyList<EquipmentSlot> BuildSnapshotFromIds(IReadOnlyList<uint> ids, IReadOnlyList<byte> stain0s, IReadOnlyList<byte> stain1s)
    {
        var order = new List<EquipmentSlotType>();
        order.AddRange(TopRow); order.AddRange(LeftCol); order.AddRange(RightCol);

        var itemSheet = _data.GetExcelSheet<Item>();
        var result = new List<EquipmentSlot>();
        for (var i = 0; i < order.Count && i < ids.Count; i++)
        {
            var id = ids[i];
            var name = id > 0 && itemSheet != null && itemSheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}";
            result.Add(new EquipmentSlot(order[i], id, name, null, null,
                Stain0: i < stain0s.Count ? stain0s[i] : (byte)0,
                Stain1: i < stain1s.Count ? stain1s[i] : (byte)0));
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

                var ret = setItem.Invoke(0, apiSlot, itemId, new List<byte> { slot.Stain0, slot.Stain1 }, 0, ApplyFlag.Once);
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

    // ponytail: CharaView._items slot ordering — 14 slots, 0..12 used, 13 unused (Soul).
    // NOT verified in-game yet; if gear appears in wrong slot, swap here first.
    // -1 = unmapped, caller skips.
    private static int MapToCharaViewItemSlot(EquipmentSlotType s) => s switch
    {
        EquipmentSlotType.MainHand => 0,
        EquipmentSlotType.OffHand => 1,
        EquipmentSlotType.Head => 2,
        EquipmentSlotType.Body => 3,
        EquipmentSlotType.Hands => 4,
        EquipmentSlotType.Legs => 6,
        EquipmentSlotType.Feet => 7,
        EquipmentSlotType.Earrings => 8,
        EquipmentSlotType.Necklace => 9,
        EquipmentSlotType.Bracelets => 10,
        EquipmentSlotType.RingRight => 11,
        EquipmentSlotType.RingLeft => 12,
        _ => -1,
    };

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
        UiStyle.SectionHeader("General");

        var movable = _configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox(Loc.T("Movable Window"), ref movable))
        {
            _configuration.IsConfigWindowMovable = movable;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("Allow dragging this window by its body."));

        var showCraftingSavings = _configuration.ShowCraftingSavings;
        if (ImGui.Checkbox(Loc.T("Show Crafting Savings"), ref showCraftingSavings))
        {
            _configuration.ShowCraftingSavings = showCraftingSavings;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("Compare market price vs. crafting cost in the item detail window."));

        var debugApiEnabled = _configuration.DebugApiEnabled;
        if (ImGui.Checkbox(Loc.T("Debug API"), ref debugApiEnabled))
        {
            _configuration.DebugApiEnabled = debugApiEnabled;
            _configuration.Save();
            OnDebugApiToggle?.Invoke(debugApiEnabled);
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("Read-only HTTP API on localhost:23423 for external tools."));

        var webUiEnabled = _configuration.WebUiEnabled;
        if (ImGui.Checkbox(Loc.T("Web UI"), ref webUiEnabled))
        {
            _configuration.WebUiEnabled = webUiEnabled;
            _configuration.Save();
            OnWebUiToggle?.Invoke(webUiEnabled);
        }
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("HTML alternative UI on http://localhost:23424 — open in a browser or via Browsingway for an in-game overlay."));

        if (webUiEnabled)
        {
            var bwLoaded = IsBrowsingwayLoaded();
            ImGui.Indent(ImGui.GetFontSize());
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(bwLoaded ? UiStyle.Success : UiStyle.Warning,
                    (bwLoaded ? FontAwesomeIcon.Check : FontAwesomeIcon.ExclamationTriangle).ToIconString());
            ImGui.SameLine();
            if (bwLoaded)
                ImGui.TextColored(UiStyle.Success, Loc.T("Browsingway found"));
            else
                ImGui.TextColored(UiStyle.Warning, Loc.T("Browsingway not installed — in-game overlay unavailable"));

            var inlayStatus = WebUiInlayStatus?.Invoke();
            if (!string.IsNullOrEmpty(inlayStatus))
                ImGui.TextColored(UiStyle.Muted, inlayStatus);

            if (bwLoaded)
            {
                var autoOverlay = _configuration.WebUiAutoOverlay;
                if (ImGui.Checkbox(Loc.T("Auto-Overlay"), ref autoOverlay))
                {
                    _configuration.WebUiAutoOverlay = autoOverlay;
                    _configuration.Save();
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker(Loc.T("Overlay is created automatically in Browsingway's config;\nGlamSource then sets its URL, shows it when this window opens,\nhides it on close. Drag/resize it like any Browsingway overlay."));
            }

            var live3D = _configuration.WebUiLive3DPreview;
            using (ImRaii.PushColor(ImGuiCol.Text, UiStyle.Warning))
                ImGui.Checkbox(Loc.T("3D Preview (experimental)"), ref live3D);
            if (ImGui.IsItemEdited())
            {
                _configuration.WebUiLive3DPreview = live3D;
                _configuration.Save();
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker(Loc.T("Streams the live 3D character view into the web UI, like the inline preview above.\nUses raw D3D11 GPU texture readback — riskier than the rest of GlamSource.\nDisable if you notice crashes; report so it can be fixed."));
            ImGui.Unindent(ImGui.GetFontSize());
        }

        ImGui.Spacing();
        UiStyle.SectionHeader(Loc.T("Auto-Gathering"));

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14f);
        DrawMountPicker();

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14f);
        var mountDist = _configuration.MountUpDistance;
        if (ImGui.SliderFloat("##mountdist", ref mountDist, 0f, 100f, "%.0f m"))
        {
            _configuration.MountUpDistance = mountDist;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("Mount-up distance"));
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Loc.T("Mount up when the gathering node is farther away than this."));

        ImGui.Spacing();
        UiStyle.SectionHeader(Loc.T("Gearsets"));

        DrawGearsetCombo(Loc.T("Miner set"),    16, _configuration.MinerSetName,    n => _configuration.MinerSetName = n);
        DrawGearsetCombo(Loc.T("Botanist set"), 17, _configuration.BotanistSetName, n => _configuration.BotanistSetName = n);
        DrawGearsetCombo(Loc.T("Fisher set"),   18, _configuration.FisherSetName,   n => _configuration.FisherSetName = n);
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
