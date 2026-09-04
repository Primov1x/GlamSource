using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource.Core;
using GlamSource.Services;
using Lumina.Excel.Sheets;

namespace GlamSource.Windows;

public class ItemDetailWindow : Window, IDisposable
{
    private readonly IItemDetailService _detailService;
    internal IItemDetailService DetailService => _detailService; // shell's Duty Drops tab reads duty tables through it
    private readonly IItemSourceService _sourceService;
    private readonly IUniversalisService _universalisService;
    private readonly ITextureProvider? _textureProvider;
    private readonly IDataManager? _data;
    private readonly IItemImageService? _imageService;
    // ponytail: same wiki preview image the web UI shows (see ItemImageService), just decoded to a
    // real GPU texture here instead of an <img src>. Keyed + disposed per item id; null value means
    // "looked it up, nothing found" so we don't re-request every Draw() frame.
    private readonly Dictionary<uint, Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap?> _previewTextureCache = new();
    private readonly HashSet<uint> _previewLoading = new();
    private Plugin _plugin = null!;
    // ponytail: optional per-slot context (Gear/Glamour/Stain snapshot). null = old single-item mode.
    private EquipmentSlot? _slotContext;
    private readonly Stack<uint> _history = new();
    private uint? _showingItemId;
    private bool _isOpen;
    private uint? _navigateToItemId;
    private Task<EventStatus?>? _eventTask; // polled in Draw, same pattern as the shell's duty coffers
    private EventStatus? _eventStatus;
    private int _navigateToSourceIdx = -1;

    private MarketInfo? _marketInfo;
    private bool _marketLoading;
    private uint _marketItemId;
    private Action<string, string, float, float>? _onOpenMap;
    private CraftingCostResult? _craftingResult;
    // ponytail: same fast-click race as the web UI's annotateMarket/annotateEvent (found and fixed
    // there first, 1.0.20.0/1.0.21.0) — click item A, then B before A's async fetch resolves, A's
    // result overwrites the shared field after _marketItemId/_craftingItemId already point at B.
    // Renders under B's header. _craftingItemId didn't even have the market path's partial guard.
    private uint _craftingItemId;

    // Gather button debouncing and feedback state
    private enum GatherOutcome
    {
        Failed,
        Started,
        Pending,
    }
    private GatherOutcome _gatherOutcome = GatherOutcome.Failed;
    private string _gatherOutcomeDetail = string.Empty;
    private long _lastGatherTimestamp = 0;  // TickCount

    private const int GatherFeedbackDurationMs = 3000;  // Show feedback for 3s
    private const int GatherButtonCooldownMs = 2000;    // Button disabled for 2s after click

    // Per-material gather cooldown tracking (ItemId -> last click timestamp)
    private readonly Dictionary<uint, long> _lastMaterialGatherTimestamp = new();
    // Per-cost gather cooldown tracking (ItemId -> last click timestamp)
    private readonly Dictionary<uint, long> _lastCostGatherTimestamp = new();

    private static readonly Dictionary<ItemSourceType, (Vector4 Border, Vector4 BadgeBg, string Label)> SourceStyles = new()
    {
        [ItemSourceType.Crafted] = (
            new Vector4(1f, 0.65f, 0.15f, 1f),
            new Vector4(0.24f, 0.18f, 0.06f, 1f),
            "CRAFTED"),
        [ItemSourceType.Vendor] = (
            new Vector4(0.36f, 0.42f, 0.75f, 1f),
            new Vector4(0.10f, 0.10f, 0.24f, 1f),
            "VENDOR"),
        [ItemSourceType.Trial] = (
            new Vector4(1f, 0.3f, 0.3f, 1f),
            new Vector4(0.24f, 0.08f, 0.08f, 1f),
            "TRIAL"),
        [ItemSourceType.Raid] = (
            new Vector4(1f, 0.3f, 0.3f, 1f),
            new Vector4(0.24f, 0.08f, 0.08f, 1f),
            "RAID"),
        [ItemSourceType.Dungeon] = (
            new Vector4(0.3f, 0.7f, 1f, 1f),
            new Vector4(0.06f, 0.14f, 0.24f, 1f),
            "DUNGEON"),
        [ItemSourceType.Quest] = (
            new Vector4(0.3f, 1f, 0.3f, 1f),
            new Vector4(0.06f, 0.20f, 0.06f, 1f),
            "QUEST"),
        [ItemSourceType.Unknown] = (
            new Vector4(0.5f, 0.5f, 0.5f, 1f),
            new Vector4(0.15f, 0.15f, 0.15f, 1f),
            "UNKNOWN"),
        [ItemSourceType.Achievement] = (
            new Vector4(0.9f, 0.9f, 0f, 1f),
            new Vector4(0.20f, 0.20f, 0.05f, 1f),
            "ACHIEVEMENT"),
        [ItemSourceType.MogStation] = (
            new Vector4(0.6f, 0.4f, 0.8f, 1f),
            new Vector4(0.15f, 0.10f, 0.20f, 1f),
            "MOG STATION"),
        [ItemSourceType.PvP] = (
            new Vector4(1f, 0.2f, 0.2f, 1f),
            new Vector4(0.24f, 0.05f, 0.05f, 1f),
            "PvP"),
        [ItemSourceType.TreasureHunt] = (
            new Vector4(0.2f, 0.8f, 0.8f, 1f),
            new Vector4(0.05f, 0.20f, 0.20f, 1f),
            "TREASURE HUNT"),
        [ItemSourceType.Shop] = (
            new Vector4(0.8f, 0.6f, 0.2f, 1f),
            new Vector4(0.18f, 0.14f, 0.05f, 1f),
            "SHOP"),
        [ItemSourceType.Gathering] = (
            new Vector4(0.2f, 0.8f, 0.5f, 1f),
            new Vector4(0.05f, 0.20f, 0.10f, 1f),
            "GATHERING"),
        [ItemSourceType.Other] = (
            new Vector4(0.5f, 0.5f, 0.5f, 1f),
            new Vector4(0.15f, 0.15f, 0.15f, 1f),
            "OTHER"),
        [ItemSourceType.TripleTriad] = (
            new Vector4(0.85f, 0.55f, 0.2f, 1f),
            new Vector4(0.22f, 0.14f, 0.05f, 1f),
            "TRIPLE TRIAD"),
    };

    public ItemDetailWindow(IItemDetailService detailService, IItemSourceService sourceService, IUniversalisService universalisService, ITextureProvider? textureProvider = null, IDataManager? data = null, IItemImageService? imageService = null)
        // AlwaysAutoResize — "bissl enger gestalten": with a fixed size the window kept whatever
        // height it was last manually dragged to, leaving a big empty gap below short content (live
        // screenshot: 3 short cards, ~half the window empty below them). Bounded by SizeConstraints
        // below, same as before.
        : base($"Item Detail###ItemDetailWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _detailService = detailService;
        _sourceService = sourceService;
        _universalisService = universalisService;
        _textureProvider = textureProvider;
        _data = data;
        _imageService = imageService;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 250),
            MaximumSize = new Vector2(700f, 800f)
        };
    }

    public void SetPlugin(Plugin plugin)
    {
        _plugin = plugin;
    }

    private Func<uint, string>? _applyToSelf; // shell's Glamourer IPC, set by Plugin
    private string? _applyStatus;
    public void SetApplyCallback(Func<uint, string> callback) => _applyToSelf = callback;

    public void SetMapCallback(Action<string, string, float, float> callback)
    {
        _onOpenMap = callback;
    }

    // ponytail: "kein extra Fenster" — the shell embeds Draw()'s content inline (see
    // GlamSourceShellWindow.DrawItemDetailInline) instead of registering this as its own floating
    // WindowSystem window. There's no native title-bar X to close it anymore, so the shell's own
    // Close button needs an explicit way to clear both open-flags Draw() checks.
    public void CloseInline()
    {
        _isOpen = false;
        IsOpen = false;
    }

    public void ShowItem(uint itemId)
    {
        // ponytail: no slot context in single-item mode.
        _slotContext = null;
        _history.Clear();
        LoadItemDetail(itemId);
        _craftingResult = null;
        _craftingItemId = itemId;
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            var result = service != null ? await service.GetCostBreakdownAsync(itemId) : null;
            if (_craftingItemId == itemId) _craftingResult = result; // still the shown item? see field comment
        });
    }

    // ponytail: slot-aware entry; renders 3-row Gear/Glamour/Stain diff above item info.
    public void Open(uint itemId, EquipmentSlot slot)
    {
        _history.Clear();
        _slotContext = slot;
        LoadItemDetail(itemId);
        _craftingResult = null;
        _craftingItemId = itemId;
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            var result = service != null ? await service.GetCostBreakdownAsync(itemId) : null;
            if (_craftingItemId == itemId) _craftingResult = result; // still the shown item? see field comment
        });
    }

    private void LoadPreviewImage(uint itemId, string itemName)
    {
        if (_imageService == null || _textureProvider == null) return;
        if (_previewTextureCache.ContainsKey(itemId) || !_previewLoading.Add(itemId)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // the wiki has no localized page titles — a non-English client name 404s there
                // (live-confirmed: "Freiherrliche Jacke"). Use the English name regardless of the
                // caller's (locale-dependent) itemName.
                var wikiName = _detailService.GetWikiPageName(itemId) ?? itemName; // mount page for mount items
                var bytes = await _imageService.GetPreviewImageBytesAsync(itemId, wikiName);
                var tex = bytes != null ? await _textureProvider.CreateFromImageAsync(bytes, $"ItemPreview_{itemId}") : null;
                _previewTextureCache[itemId] = tex;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[PREVIEW] Failed to load preview image for item {Id}", itemId);
                _previewTextureCache[itemId] = null;
            }
            finally
            {
                _previewLoading.Remove(itemId);
            }
        });
    }

    private void DrawPreviewImage(uint itemId)
    {
        if (!_previewTextureCache.TryGetValue(itemId, out var tex) || tex == null)
            return;

        // ponytail: cap displayed size in font units, same idea as the web UI's `max-width:280px`
        // on .preview img — a wide wiki screenshot shouldn't blow out the window.
        var maxEdge = ImGui.GetFontSize() * 14f;
        var scale = MathF.Min(1f, maxEdge / MathF.Max(tex.Width, tex.Height));
        ImGui.Image(tex.Handle, new Vector2(tex.Width * scale, tex.Height * scale));
        ImGui.Spacing();
    }

    private void NavigateToItem(uint itemId)
    {
        if (_showingItemId.HasValue && _showingItemId.Value > 0)
            _history.Push(_showingItemId.Value);
        LoadItemDetail(itemId);
    }

    private void LoadItemDetail(uint itemId)
    {
        _showingItemId = itemId;
        _eventStatus = null;
        _eventTask = _detailService.GetEventStatusAsync(itemId);
        // Reset GatherBuddy button state when showing a new item
        _lastGatherTimestamp = 0;
        _gatherOutcome = GatherOutcome.Failed;
        _gatherOutcomeDetail = string.Empty;
        _lastMaterialGatherTimestamp.Clear();
        _lastCostGatherTimestamp.Clear();
        _isOpen = true;
        IsOpen = true;

        var detail = _detailService.GetDetail(itemId);
        if (detail != null)
        {
            WindowName = $"{detail.Name} ({detail.ItemId})###ItemDetailWindow";
            Console.WriteLine($"[DETAIL] ShowItem({itemId}) sources={detail.Sources.Count}");

            if (detail.IsMarketable)
            {
                _marketLoading = true;
                _marketItemId = itemId;
                _marketInfo = null;
                _ = Task.Run(async () =>
                {
                    var result = await _universalisService.GetMarketInfoAsync(itemId);
                    if (_marketItemId == itemId) _marketInfo = result; // still the shown item? see field comment
                    _marketLoading = false;
                });
            }

            LoadPreviewImage(itemId, detail.Name);
        }
        else
        {
            Console.WriteLine($"[DETAIL] ShowItem({itemId}) NOT FOUND");
        }
    }

    public override void Draw()
    {
        using var _style = UiStyle.Push();

        UpdateGatherFeedback();

        if (!_isOpen || _showingItemId == null)
        {
            IsOpen = false;
            return;
        }

        var detail = _detailService.GetDetail(_showingItemId.Value);
        if (detail == null)
        {
            ImGui.TextDisabled(Loc.T("Item not found."));
            return;
        }

        if (_navigateToItemId.HasValue && _navigateToSourceIdx >= 0)
        {
            NavigateToItem(_navigateToItemId.Value);
            _navigateToItemId = null;
            _navigateToSourceIdx = -1;
            return;
        }

        DrawItemHeader(detail);
        DrawPreviewImage(detail.ItemId);

        if (_slotContext != null)
            DrawSlotContext(_slotContext);

        if (_marketInfo != null && _marketItemId == detail.ItemId)
            DrawMarketPricesCompact(_marketInfo);
        else if (_marketLoading && _marketItemId == detail.ItemId)
            ImGui.TextDisabled(Loc.T("Loading prices..."));

        // ponytail: a bordered child sized (0,0) fills the PARENT's remaining space, not its own
        // content — that fights AlwaysAutoResize (which sizes the window FROM content) in a
        // feedback loop, leaving oversized empty cards ("die karten nicht so riesig"). Drawing
        // directly in the window instead — its height is then simply the cards' real height, no
        // separate sizing to reconcile. NoScrollbar was already set; SizeConstraints still caps it.
        SectionHeader(Loc.T("SOURCES"));
        ImGui.Spacing();
        DrawSourceCards(detail);
        DrawGatheringActionButton(detail);

        if (_plugin?.Configuration?.ShowCraftingSavings == true && _craftingResult != null && _craftingItemId == detail.ItemId)
        {
            SectionHeader(Loc.T("CRAFTING SAVINGS"));
            ImGui.Spacing();
            DrawCraftingSavings();
        }
    }

    // ponytail: window-chrome rounding must be pushed before ImGui.Begin — Draw() is too late.
    public override void PreDraw()  => UiStyle.PushWindow();
    public override void PostDraw() => UiStyle.PopWindow();

    private void DrawItemHeader(ItemDetail detail)
    {
        // ponytail: icon size in font units — respects user Dalamud font scale, no hardcoded pixels.
        var iconEdge = ImGui.GetFontSize() * 3f;
        var iconSize = new Vector2(iconEdge, iconEdge);
        if (_textureProvider != null && detail.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(detail.IconId)).GetWrapOrEmpty();
            ImGui.Image(iconTexture.Handle, iconSize);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Text(detail.Name);
        var metaLine = $"{Loc.T("Item ID")} {detail.ItemId}  \u00b7  {Loc.T("iLvl")} {detail.ItemLevel}";
        if (!string.IsNullOrEmpty(detail.SetName))
            metaLine += $"  \u00b7  {Loc.T("Set")}: {detail.SetName}";
        // "hat man das mount oder minion schon unlocked" \u2014 null unless the item itself is one.
        // ponytail: same Mock-hang class as CheckUnlockStatus (1.0.19.0) \u2014 PlayerState.Instance()/
        // UIState.Instance() inside UnlockCheckService are raw ClientStructs Service<T> calls, hang
        // outside a real ffxiv_dx11.exe. _plugin is only ever set by the real Plugin.cs.
        var unlocked = _plugin == null ? null : UnlockCheckService.CheckUnlocked(_detailService, detail.ItemId);
        if (unlocked.HasValue)
            metaLine += unlocked.Value ? $"  \u00b7  \u2713 {Loc.T("Unlocked")}" : $"  \u00b7  {Loc.T("Not unlocked")}";
        ImGui.TextDisabled(metaLine);
        if (_eventTask is { IsCompleted: true })
        {
            _eventStatus = _eventTask.IsCompletedSuccessfully ? _eventTask.Result : null;
            _eventTask = null;
        }
        if (_eventStatus != null)
        {
            var kind = _eventStatus.Recurring ? Loc.T("Recurring event") : Loc.T("One-time event");
            var status = _eventStatus.Active == true ? Loc.T("active now")
                : _eventStatus.Active == false ? Loc.T(_eventStatus.Recurring ? "not running right now" : "no longer obtainable")
                : Loc.T("live status unknown — check in-game");
            var color = _eventStatus.Active == true ? UiStyle.Success
                : _eventStatus.Active == false && !_eventStatus.Recurring ? UiStyle.Muted : UiStyle.Warning;
            ImGui.TextColored(color, $"{kind}: {_eventStatus.EventName} — {status}");
        }
        if (_applyToSelf != null && detail.IsEquippable) // mounts, minions, materials: nothing to apply
        {
            if (ImGui.SmallButton(Loc.T("Apply to Self")))
                _applyStatus = _applyToSelf(detail.ItemId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Put this piece on your own character via Glamourer (weapons skipped)"));
            if (!string.IsNullOrEmpty(_applyStatus))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(_applyStatus);
            }
        }
        ImGui.EndGroup();

        ImGui.Spacing();

        // ponytail: same "Item.ItemSeries" grouping the web UI shows \u2014 clickable chips, same
        // navigation as the [i] info buttons elsewhere in this window (push history, load new item).
        if (detail.SetMembers is { Count: > 0 })
        {
            ImGui.TextDisabled(Loc.T("Rest of the set:"));
            for (var i = 0; i < detail.SetMembers.Count; i++)
            {
                var member = detail.SetMembers[i];
                if (i > 0) ImGui.SameLine();
                using (ImRaii.PushId($"setmember_{member.ItemId}"))
                {
                    if (_textureProvider != null && member.IconId > 0)
                    {
                        var tex = _textureProvider.GetFromGameIcon(new GameIconLookup(member.IconId)).GetWrapOrEmpty();
                        ImGui.Image(tex.Handle, new Vector2(RowIconSize, RowIconSize));
                        ImGui.SameLine();
                    }
                    if (ImGui.SmallButton(member.Name))
                        NavigateToItem(member.ItemId);
                }
            }
            ImGui.Spacing();
        }

        if (detail.Contents is { Count: > 0 })
        {
            ImGui.TextDisabled(Loc.T("Can contain:"));
            for (var i = 0; i < detail.Contents.Count; i++)
            {
                var member = detail.Contents[i];
                if (i > 0) ImGui.SameLine();
                using (ImRaii.PushId($"content_{member.ItemId}"))
                {
                    if (_textureProvider != null && member.IconId > 0)
                    {
                        var tex = _textureProvider.GetFromGameIcon(new GameIconLookup(member.IconId)).GetWrapOrEmpty();
                        ImGui.Image(tex.Handle, new Vector2(RowIconSize, RowIconSize));
                        ImGui.SameLine();
                    }
                    if (ImGui.SmallButton(member.Name))
                        NavigateToItem(member.ItemId);
                }
            }
            ImGui.Spacing();
        }

        if (detail.ContentsSummary is { Count: > 0 })
        {
            ImGui.TextDisabled(Loc.T("Can contain:"));
            foreach (var cat in detail.ContentsSummary)
            {
                using (ImRaii.PushId($"contentsum_{cat.Label}"))
                {
                    if (_textureProvider != null && cat.IconId > 0)
                    {
                        var tex = _textureProvider.GetFromGameIcon(new GameIconLookup(cat.IconId)).GetWrapOrEmpty();
                        ImGui.Image(tex.Handle, new Vector2(RowIconSize, RowIconSize));
                        ImGui.SameLine();
                    }
                    // click to see which items — was a dead-end "13x Minion" count before.
                    if (ImGui.TreeNode($"{cat.Items.Count}x {cat.Label}##tree"))
                    {
                        for (var i = 0; i < cat.Items.Count; i++)
                        {
                            var member = cat.Items[i];
                            using (ImRaii.PushId($"catitem_{member.ItemId}"))
                            {
                                if (_textureProvider != null && member.IconId > 0)
                                {
                                    var tex = _textureProvider.GetFromGameIcon(new GameIconLookup(member.IconId)).GetWrapOrEmpty();
                                    ImGui.Image(tex.Handle, new Vector2(RowIconSize, RowIconSize));
                                    ImGui.SameLine();
                                }
                                if (ImGui.SmallButton(member.Name))
                                    NavigateToItem(member.ItemId);
                            }
                        }
                        ImGui.TreePop();
                    }
                }
            }
            ImGui.Spacing();
        }

        if (_history.Count > 0)
        {
            if (ImGui.SmallButton(Loc.T("← Back")))
            {
                var previousId = _history.Pop();
                LoadItemDetail(previousId);
            }
            ImGui.SameLine();
        }

        if (ImGui.SmallButton(Loc.T("Wiki")))
        {
            OpenWiki(detail.Name, detail.ItemId);
        }
        ImGui.SameLine();
        if (detail.IsMarketable && ImGui.SmallButton(Loc.T("Market prices")))
        {
            OpenMarketPrices(detail.ItemId);
        }
        ImGui.Spacing();
    }

    // ponytail: three-row per-slot diff shown when opened with slot context.
    // Gear row = ActualItem, Glamour row muted when not glamoured (or equals gear), Stain row = Stain0 swatch.
    private void DrawSlotContext(EquipmentSlot slot)
    {
        var iconEdge = ImGui.GetFontSize() * 1.6f;
        var iconVec = new Vector2(iconEdge, iconEdge);

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), $"{Loc.T("Slot")}: {slot.Slot}");

        // Gear row
        DrawSlotIconRow(Loc.T("Gear"), slot.ActualItemId, slot.ActualItemName, iconVec, muted: false);

        // Glamour row
        var glamId = slot.GlamourItemId ?? 0u;
        var glamName = slot.GlamourItemName ?? Loc.T("(none)");
        var glamMuted = !slot.IsGlamoured || glamId == slot.ActualItemId;
        DrawSlotIconRow(Loc.T("Glam"), glamId, glamName, iconVec, muted: glamMuted);

        // Stain row
        ImGui.TextDisabled(Loc.T("Stain:"));
        ImGui.SameLine();
        DrawSlotStain(slot.Stain0);

        ImGui.Spacing();
    }

    private void DrawSlotIconRow(string tag, uint itemId, string name, Vector2 iconVec, bool muted)
    {
        ImGui.TextDisabled(tag + ":");
        ImGui.SameLine();
        if (_textureProvider != null && itemId > 0 && _data != null)
        {
            var sheet = _data.GetExcelSheet<Item>();
            uint iconId = 0;
            if (sheet != null && sheet.TryGetRow(itemId, out var row)) iconId = row.Icon;
            if (iconId > 0)
            {
                var tex = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, iconVec);
                ImGui.SameLine();
            }
        }
        if (muted) ImGui.TextDisabled(name);
        else ImGui.TextUnformatted(name);
    }

    private void DrawSlotStain(byte stainId)
    {
        if (stainId == 0 || _data == null)
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

    private void DrawMarketPricesCompact(MarketInfo market)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize($"World: {FormatNumber(market.WorldMinPrice)} Gil  |  DC ({market.DcWorldName}): {FormatNumber(market.DcMinPrice)} Gil");
        var boxHeight = ImGui.GetTextLineHeightWithSpacing();
        var boxWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowPos().X - 10f;
        var boxColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.12f, 1f));
        drawList.AddRectFilled(
            new Vector2(pos.X, pos.Y),
            new Vector2(pos.X + boxWidth, pos.Y + boxHeight),
            boxColor,
            4f);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 300f);
        ImGui.TextDisabled(Loc.T("World:"));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.84f, 0.25f, 1f),
            $"{FormatNumber(market.WorldMinPrice)} Gil  |  ");
        ImGui.TextDisabled($"DC ({market.DcWorldName}):");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.41f, 0.94f, 0.68f, 1f),
            $"{FormatNumber(market.DcMinPrice)} Gil");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(boxWidth, boxHeight));
        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawGatheringActionButton(ItemDetail detail)
    {
        // Prüfe ob eine der Sources ein Gathering-Source ist
        if (!detail.Sources.Any(s => s.Type == ItemSourceType.Gathering))
            return;

        var now = Environment.TickCount64;
        bool isCooldown = (now - _lastGatherTimestamp) < GatherButtonCooldownMs;

        ImGui.SameLine();

        if (isCooldown)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.4f, 0.4f, 0.4f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.SmallButton("⛏ " + Loc.T("Gather (Cooling down...)"));
            ImGui.PopStyleColor(2);
        }
        else if (ImGui.SmallButton("⛏ " + Loc.T("Gather")))
        {
            _lastGatherTimestamp = now;
            TriggerGather(detail.ItemId, detail.Name);
        }

        DrawGatherFeedback(now, detail.Name);
    }

    // ponytail: single entry point for all gather buttons. SimpleGatherService drives everything.
    private void TriggerGather(uint itemId, string itemName)
    {
        try
        {
            var result = _plugin.GatherService.TryStartGathering(itemId);
            if (result.Started)
            {
                _gatherOutcome = GatherOutcome.Started;
                _gatherOutcomeDetail = itemName;
                Plugin.Log?.Information("[GATHER] Started for {Name} (ID={Id})", itemName, itemId);
            }
            else
            {
                _gatherOutcome = GatherOutcome.Failed;
                _gatherOutcomeDetail = result.Reason;
                Plugin.Log?.Warning("[GATHER] Cannot start for {Name} (ID={Id}): {Reason}", itemName, itemId, result.Reason);
            }
        }
        catch (Exception ex)
        {
            _gatherOutcome = GatherOutcome.Failed;
            _gatherOutcomeDetail = ex.Message;
            Plugin.Log?.Error(ex, "[GATHER] Exception starting gather for {Name}", itemName);
        }
    }

    private void UpdateGatherFeedback()
    {
        // ponytail: SimpleGatherService drives itself. Refresh outcome detail from live state
        // so the feedback line tracks progress instead of freezing on the click frame.
        if (_gatherOutcome != GatherOutcome.Started) return;
        var s = _plugin.GatherService.State;
        _gatherOutcomeDetail = s.ToString();
    }

    private void DrawGatherFeedback(long now, string itemName)
    {
        if (_lastGatherTimestamp <= 0 || (now - _lastGatherTimestamp) >= GatherFeedbackDurationMs)
            return;
        ImGui.SameLine();
        switch (_gatherOutcome)
        {
            case GatherOutcome.Started:
                ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), $"✓ Gathering {itemName}");
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Status: " + _gatherOutcomeDetail);
                break;
            case GatherOutcome.Failed:
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Gather failed: " + _gatherOutcomeDetail);
                break;
            default:
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.1f, 1f), "Pending...");
                break;
        }
    }

    // ponytail: draw-list channels so the card background paints behind the content,
    // same pattern as Questionable's CardScope (QstWidgets).
    // ponytail: one size for all row/source icons; header keeps its own 3x size.
    private static float RowIconSize => ImGui.GetFontSize() * 1.5f;

    private sealed class SourceCardScope : IDisposable
    {
        private static float Rounding => ImGui.GetFontSize() * 0.5f;
        private static readonly Vector4 FillColor = new(0f, 0f, 0f, 0.35f);

        private readonly ImDrawListPtr _drawList;
        private readonly Vector2 _topLeft;
        private readonly float _width;
        private readonly float _padding;
        private readonly uint _borderColor;

        public SourceCardScope(Vector4 borderColor)
        {
            _drawList = ImGui.GetWindowDrawList();
            _topLeft = ImGui.GetCursorScreenPos();
            _width = ImGui.GetContentRegionAvail().X;
            _padding = ImGui.GetFontSize() * 0.45f;
            _borderColor = ImGui.ColorConvertFloat4ToU32(borderColor);

            _drawList.ChannelsSplit(2);
            _drawList.ChannelsSetCurrent(1);
            ImGui.SetCursorScreenPos(_topLeft + new Vector2(_padding, _padding));
            ImGui.BeginGroup();
        }

        public void Dispose()
        {
            ImGui.EndGroup();
            var bottomRight = new Vector2(_topLeft.X + _width, ImGui.GetItemRectMax().Y + _padding);

            _drawList.ChannelsSetCurrent(0);
            _drawList.AddRectFilled(_topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(FillColor), Rounding);
            _drawList.AddRect(_topLeft, bottomRight, _borderColor, Rounding);
            _drawList.ChannelsMerge();

            ImGui.SetCursorScreenPos(new Vector2(_topLeft.X, bottomRight.Y));
            ImGui.Dummy(Vector2.Zero);
        }
    }

    private bool TryDrawSourceIcon(uint iconId, float size)
    {
        if (_textureProvider == null || iconId == 0)
            return false;
        var icon = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        var cursorPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosY(cursorPos.Y + (ImGui.GetTextLineHeight() - size) * 0.5f);
        ImGui.Image(icon.Handle, new Vector2(size, size));
        ImGui.SameLine();
        ImGui.SetCursorPosY(cursorPos.Y);
        return true;
    }

    private void DrawSourceCards(ItemDetail detail)
    {
        if (detail.Sources.Count == 0)
        {
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
            ImGui.TextColored(grey, "No known source found.");
            ImGui.TextColored(grey, "This item may drop from duties, raids,");
            ImGui.TextColored(grey, "or other content.");
            return;
        }

        var priority = new Dictionary<ItemSourceType, int>
        {
            { ItemSourceType.Crafted, 0 },
            { ItemSourceType.Vendor, 1 },
            { ItemSourceType.Shop, 2 },
            { ItemSourceType.Quest, 3 },
            { ItemSourceType.Trial, 4 },
            { ItemSourceType.Raid, 5 },
            { ItemSourceType.Dungeon, 6 },
            { ItemSourceType.Gathering, 7 },
            { ItemSourceType.Other, 9 }
        };

        var sortedSources = detail.Sources
            .OrderBy(s => priority.GetValueOrDefault(s.Type, 9))
            .ToList();

        var vendorSources = sortedSources
            .Where(s => s.Type == ItemSourceType.Vendor)
            .ToList();

        var craftedSources = sortedSources
            .Where(s => s.Type == ItemSourceType.Crafted)
            .ToList();

        var questSources = sortedSources
            .Where(s => s.Type == ItemSourceType.Quest)
            .ToList();

        var dutySources = sortedSources
            .Where(s => s.Type == ItemSourceType.Trial || s.Type == ItemSourceType.Raid || s.Type == ItemSourceType.Dungeon)
            .ToList();

        var otherSources = sortedSources
            .Where(s => s.Type != ItemSourceType.Vendor && s.Type != ItemSourceType.Crafted
                     && s.Type != ItemSourceType.Quest && s.Type != ItemSourceType.Trial
                     && s.Type != ItemSourceType.Raid && s.Type != ItemSourceType.Dungeon)
            .ToList();

        if (craftedSources.Count > 0)
        {
            var craftedGroups = craftedSources
                .GroupBy(s => GetMaterialKey(s))
                .ToList();

            for (int g = 0; g < craftedGroups.Count; g++)
            {
                var group = craftedGroups[g];
                var sources = group.ToList();
                DrawCraftedCard(sources, g, detail.IconId);
            }
        }

        if (vendorSources.Count > 0)
        {
            var vendorGroups = vendorSources
                .GroupBy(s => GetCostKey(s))
                .ToList();

            for (int g = 0; g < vendorGroups.Count; g++)
            {
                var group = vendorGroups[g];
                var npcs = group.ToList();
                DrawVendorCard(npcs, g, detail.IconId);
            }
        }

        foreach (var src in questSources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), detail.IconId);
        }

        foreach (var src in dutySources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), detail.IconId);
        }

        foreach (var src in otherSources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), detail.IconId);
        }
    }

    private void DrawSourceCard(ItemSourceDetail src, int sourceIdx, uint itemIconId, string? titleOverride = null)
    {
        var srcStyle = SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
        var hasContent = (src.Materials != null && src.Materials.Count > 0)
                      || (src.Costs != null && src.Costs.Count > 0)
                      || (src.NpcName != null && (src.ZoneName != null || src.MapX.HasValue))
                      || (src.QuestName != null)
                      || (src.CfcRowId.HasValue && src.CfcName != null)
                      || (src.SourceItemId.HasValue && src.SourceItemId.Value > 0)
                      || src.ShopUrl != null;

        using (new SourceCardScope(srcStyle.Item1))
        {
            DrawBadge(srcStyle.Item3, srcStyle.Item2);
            ImGui.SameLine();
            TryDrawSourceIcon(itemIconId, RowIconSize);

            if (!hasContent)
            {
                ImGui.TextDisabled(titleOverride ?? src.Description);
            }
            else
            {
                ImGui.Text(titleOverride ?? src.Description);
                if (src.SourceItemId.HasValue)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"[i]##source_{sourceIdx}"))
                    {
                        _navigateToItemId = src.SourceItemId.Value;
                        _navigateToSourceIdx = sourceIdx;
                    }
                }
                // Actions row: Crafting Log (for crafted sources)
                if (src.Type == ItemSourceType.Crafted)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{Loc.T("Open Crafting Log")}##craft_{sourceIdx}"))
                    {
                        // ponytail: HQ items sit at NQ RowId + 1_000_000; the vanilla recipe log only knows the NQ id.
                        var craftItemId = _showingItemId ?? 0;
                        if (craftItemId >= 1_000_000)
                            craftItemId -= 1_000_000;
                        TryOpenCraftingLog(craftItemId);
                    }
                }
                if (src.NpcName != null && (src.ZoneName != null || src.MapX.HasValue))
                {
                    ImGui.Spacing();
                    DrawNpcRow(src, sourceIdx, 0);
                }

                if (src.Materials != null && src.Materials.Count > 0)
                {
                    ImGui.Spacing();
                    // an "Other" card with materials is an outfit set (ItemDetailService 7f): its rows are pieces
                    ImGui.TextDisabled(Loc.T(src.Type == ItemSourceType.Other ? "Pieces:" : "Materials:"));
                    for (int matIdx = 0; matIdx < src.Materials.Count; matIdx++)
                    {
                        DrawMaterialRow(src.Materials[matIdx], sourceIdx, matIdx);
                    }
                }

                if (src.Costs != null && src.Costs.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled(Loc.T("Cost:"));
                    int costIdx = 0;
                    foreach (var cost in src.Costs)
                    {
                        DrawCostRow(cost, sourceIdx, costIdx);
                        costIdx++;
                    }
                }

                DrawDutyFinderRow(src, sourceIdx);
                DrawQuestRow(src, sourceIdx);
                DrawMogstationRow(src, sourceIdx);
            }
        }
        ImGui.Spacing();
    }

    private void DrawMaterialRow(CostEntry mat, int sourceIdx, int matIdx, bool showCheckmark = true, string? prefix = null)
        => DrawEntryRow(mat, sourceIdx, matIdx, "mat", showInfoButton: true, _lastMaterialGatherTimestamp);

    private void DrawCostRow(CostEntry cost, int sourceIdx, int costIdx, bool showInfoButton = true, string? prefix = null)
        => DrawEntryRow(cost, sourceIdx, costIdx, "cost", showInfoButton, _lastCostGatherTimestamp);

    // ponytail: shared material/cost row — icon, name, (have/need), info + gather buttons.
    // Sufficient/insufficient communicated via color; inventory breakdown lives in the count tooltip.
    private void DrawEntryRow(CostEntry entry, int sourceIdx, int rowIdx, string idPrefix,
        bool showInfoButton, Dictionary<uint, long> gatherCooldowns)
    {
        if (entry.ItemId == 0)
        {
            if (_textureProvider != null)
            {
                // cost rows carry itemId 0 for gil — show the real Gil icon (Item 1, icon 65002)
                var gil = _textureProvider.GetFromGameIcon(new GameIconLookup(65002)).GetWrapOrEmpty();
                ImGui.Image(gil.Handle, new Vector2(RowIconSize, RowIconSize));
                ImGui.SameLine();
            }
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"{FormatNumber(entry.Count)} Gil");
            return;
        }

        var have = entry.ItemId > 19 ? GetItemCount(entry.ItemId) : 0;
        var sufficient = have >= entry.Count;

        if (_textureProvider != null && entry.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(entry.IconId)).GetWrapOrEmpty();
            var size = RowIconSize;
            var cursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(cursorPos.Y + (ImGui.GetTextLineHeight() - size) * 0.5f);
            ImGui.Image(iconTexture.Handle, new Vector2(size, size));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.Name);
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorPos.Y);
        }

        ImGui.TextColored(sufficient ? UiStyle.Success : UiStyle.Muted, $"{entry.Name} x{FormatNumber(entry.Count)}");

        if (entry.ItemId > 19)
        {
            ImGui.SameLine();
            ImGui.TextColored(sufficient ? UiStyle.Success : UiStyle.Muted, $"({have}/{entry.Count})");
            var breakdown = GetInventoryBreakdown(entry.ItemId);
            if (ImGui.IsItemHovered() && breakdown.Count > 0)
                ImGui.SetTooltip(string.Join("\n", breakdown.Select(kv => $"{kv.Key}: {kv.Value}")));
            // "man sieht welches mat wo liegt": inline too — the tooltip alone was easy to miss
            if (breakdown.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(UiStyle.Muted, "· " + string.Join(" · ", breakdown.Select(kv => $"{kv.Key} {kv.Value}")));
            }

            if (showInfoButton && !string.IsNullOrEmpty(entry.Name))
            {
                ImGui.SameLine();
                using (ImRaii.PushId($"info_{idPrefix}_{sourceIdx}_{rowIdx}"))
                {
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.InfoCircle))
                    {
                        _navigateToItemId = entry.ItemId;
                        _navigateToSourceIdx = sourceIdx;
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.T("Show item details"));
            }
        }

        if (ShouldShowGatherButton(entry.ItemId))
        {
            var now = Environment.TickCount64;
            var hasCooldown = gatherCooldowns.TryGetValue(entry.ItemId, out var ts)
                && (now - ts) < GatherButtonCooldownMs;

            ImGui.SameLine();
            using (ImRaii.Disabled(hasCooldown))
            {
                if (ImGui.SmallButton($"{Loc.T("Gather")}##gather_{idPrefix}_{sourceIdx}_{rowIdx}") && !hasCooldown)
                {
                    gatherCooldowns[entry.ItemId] = now;
                    TriggerGather(entry.ItemId, entry.Name);
                }
            }
            if (hasCooldown && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.T("Gathering..."));
        }
    }

    private void DrawBadge(string label, Vector4 bgColor)
    {
        label = Loc.T(label);
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(label);
        var padX = ImGui.GetFontSize() * 0.5f;
        var padY = ImGui.GetFontSize() * 0.15f;
        var height = textSize.Y + padY * 2;
        var width = textSize.X + padX * 2;
        var radius = height / 2;

        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + width, pos.Y + height),
            ImGui.ColorConvertFloat4ToU32(bgColor),
            radius);

        drawList.AddText(
            new Vector2(pos.X + padX, pos.Y + padY),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)),
            label);

        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawNpcRow(ItemSourceDetail src, int groupIdx, int npcIdx)
        => DrawNpcTable(new[] { src }, groupIdx * 1000 + npcIdx + 1);

    // ponytail: aligned vendor list \u2014 NPC | zone (x,y) | map button. Right-click a location copies it.
    private void DrawNpcTable(IReadOnlyList<ItemSourceDetail> npcs, int tableId)
    {
        if (!ImGui.BeginTable($"##npcs_{tableId}", 3,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;
        ImGui.TableSetupColumn("npc", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("loc", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("map", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());

        for (int i = 0; i < npcs.Count; i++)
        {
            var src = npcs[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(src.NpcName ?? Loc.T("Unknown vendor"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            var loc = src.ZoneName ?? "";
            if (src.MapX.HasValue && src.MapY.HasValue)
                loc = $"{loc} ({src.MapX:F1}, {src.MapY:F1})".TrimStart();
            ImGui.TextDisabled(loc);
            if (loc.Length > 0 && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("Right-click to copy"));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText($"{src.NpcName} \u2014 {loc}");
            }

            ImGui.TableNextColumn();
            if (src.MapX.HasValue && src.MapY.HasValue)
            {
                using (ImRaii.PushId($"map_{tableId}_{i}"))
                {
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.MapMarkerAlt))
                        TryOpenMap(src.NpcName, src.ZoneName, src.TerritoryTypeId, src.MapId, src.MapX.Value, src.MapY.Value);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.T("Open map"));
            }
        }
        ImGui.EndTable();
    }

    // ponytail: colored FontAwesome glyph + text on one line.
    private static void IconStatus(FontAwesomeIcon icon, Vector4 color, string text)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(color, text);
    }

    private void DrawDutyFinderRow(ItemSourceDetail src, int sourceIdx)
    {
        if (src.Type != ItemSourceType.Trial && src.Type != ItemSourceType.Raid && src.Type != ItemSourceType.Dungeon)
            return;
        if (!src.CfcRowId.HasValue || src.CfcName == null)
            return;
        if (CheckUnlockStatus(src.CfcRowId.Value))
        {
            IconStatus(FontAwesomeIcon.Check, UiStyle.Success, Loc.T("Unlocked"));
        }
        if (ImGui.SmallButton($"{Loc.T("Duty Finder")}##duty_{sourceIdx}"))
        {
            TryOpenDutyFinder(src.CfcRowId.Value);
        }
    }

    private void DrawQuestRow(ItemSourceDetail src, int sourceIdx)
    {
        if (src.Type != ItemSourceType.Quest || src.QuestName == null)
            return;
        if (src.NpcName != null && src.ZoneName != null)
        {
            DrawNpcRow(src, sourceIdx, -1);
        }
        var questId = src.QuestForUnlock;
        var questLocked = questId.HasValue && IsQuestLockedByQuestionable(questId.Value);
        if (questLocked)
        {
            IconStatus(FontAwesomeIcon.Lock, UiStyle.Warning, Loc.T("Locked (prerequisites incomplete)"));
            if (ImGui.SmallButton($"{Loc.T("Start quest chain")}##quest_{sourceIdx}"))
            {
                TryStartWithQuestionable(questId!.Value);
            }
        }
        else if (src.QuestForUnlock.HasValue)
        {
            IconStatus(FontAwesomeIcon.Check, UiStyle.Success, Loc.T("Quest unlocked"));
        }
    }

    private void DrawMogstationRow(ItemSourceDetail src, int sourceIdx)
    {
        if (src.Type != ItemSourceType.MogStation || src.ShopUrl == null)
            return;
        if (ImGui.SmallButton($"{Loc.T("Open Mog Station")}##mogstation_{sourceIdx}"))
        {
            OpenShopUrl(src.ShopUrl);
        }
    }

    private void DrawVendorCard(List<ItemSourceDetail> vendors, int groupIdx, uint itemIconId)
    {
        var first = vendors[0];

        using (new SourceCardScope(SourceStyles[ItemSourceType.Vendor].Item1))
        {
            var style = SourceStyles.GetValueOrDefault(first.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
            DrawBadge(style.Item3, style.Item2);
            ImGui.SameLine();
            TryDrawSourceIcon(itemIconId, RowIconSize);

            var isGilOnly = first.Costs != null && first.Costs.All(c => c.ItemId == 0);

            if (isGilOnly && first.Costs?.Count > 0)
            {
                ImGui.Text($"{FormatNumber(first.Costs[0].Count)} Gil");
            }
            else
            {
                ImGui.Text(first.Description);
            }

            ImGui.Spacing();

            // ponytail: <=4 vendors shown outright; larger lists collapse behind a tree node.
            if (vendors.Count <= 4)
            {
                DrawNpcTable(vendors, groupIdx);
            }
            else
            {
                DrawNpcTable(new[] { first }, groupIdx);
                if (ImGui.TreeNode($"{vendors.Count - 1} more vendors##vg_{groupIdx}"))
                {
                    DrawNpcTable(vendors.Skip(1).ToList(), groupIdx + 100000);
                    ImGui.TreePop();
                }
            }

            if (!isGilOnly && first.Costs?.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled(Loc.T("Cost:"));
                for (int costIdx = 0; costIdx < first.Costs.Count; costIdx++)
                {
                    DrawCostRow(first.Costs[costIdx], groupIdx, costIdx, showInfoButton: false, prefix: "\u2022");
                }
            }
        }
        ImGui.Spacing();
    }

    private bool ShouldShowGatherButton(uint itemId)
    {
        if (itemId == 0 || _plugin == null) return false;
        return _plugin.GatheringLocationService.GetLocations(itemId).Count > 0;
    }

    private void DrawFallbackSources(uint itemId)
    {
        var fallbackSources = _sourceService.GetSources(itemId);
        if (fallbackSources.Count == 0)
            return;

        var shownTypes = new HashSet<ItemSourceType>();
        foreach (var s in fallbackSources)
        {
            if (!shownTypes.Contains(s.Type))
            {
                shownTypes.Add(s.Type);
                var style = SourceStyles.GetValueOrDefault(s.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
                var label = Loc.T(style.Item3);
                ImGui.TextDisabled($"  {label}: {s.Description}");
                ImGui.Spacing();
            }
        }
    }

    // ponytail: delegate to shared UiStyle so every window gets the accent-dot header.
    private static void SectionHeader(string title) => UiStyle.SectionHeader(title);

    internal static unsafe void TryOpenCraftingLog(uint itemId)
    {
        try
        {
            var agent = AgentRecipeNote.Instance();
            if (agent != null)
            {
                agent->SearchRecipeByItemId(itemId, 0);
                Console.WriteLine($"[CRAFTING] Opened RecipeNote for item {itemId}");
            }
            else
            {
                Console.WriteLine($"[CRAFTING] AgentRecipeNote.Instance() returned null for item {itemId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRAFTING] Failed to open RecipeNote for item {itemId}: {ex.Message}");
        }
    }

    private static bool IsQuestLockedByQuestionable(uint questRowId)
    {
        try
        {
            var questId = (uint)(questRowId & 0xFFFF);
            var lockCheck = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestLocked");
            if (lockCheck.HasFunction)
                return lockCheck.InvokeFunc(questId.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QUESTIONABLE] IsQuestLocked failed: {ex.Message}");
        }
        return false;
    }

    private static void TryStartWithQuestionable(uint questRowId)
    {
        try
        {
            var stop = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
            if (stop.HasFunction)
                stop.InvokeFunc("GlamSource");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[QUESTIONABLE] Stop failed for {QRow}", questRowId);
        }

        try
        {
            var questionable = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
            if (questionable.HasFunction)
            {
                var questId = (uint)(questRowId & 0xFFFF);
                var qResult = questionable.InvokeFunc(questId.ToString());
                Plugin.Log.Information("[QUESTIONABLE] questRowId={QRow} questId={QId} result={R}", questRowId, questId, qResult);
                Plugin.Log.Information("[QUESTIONABLE] StartQuest questId={QuestId}", questRowId);
            }
            else
            {
                Plugin.Log.Information("[QUESTIONABLE] StartQuest IPC not available");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[QUESTIONABLE] IPC call failed");
        }
    }

    // ponytail: live crash — Mock hung ("Not Responding") the moment ANY Duty Drops item was
    // clicked. Root cause: this ran on every Draw() frame for a Dungeon/Trial/Raid source, not
    // gated behind the Duty Finder button — QuestManager.IsQuestComplete() is a raw ClientStructs
    // Service<T> call, which HANGS (not throws) outside a real ffxiv_dx11.exe process, exactly the
    // documented gotcha in GlamSource.Mock/Program.cs. A try/catch can't rescue a hang. _plugin is
    // only ever set by the real Plugin.cs (SetPlugin) — never in Mock — so it's the cheapest
    // existing signal for "is a live game actually running", no new state needed.
    private bool CheckUnlockStatus(uint questId)
    {
        if (_plugin == null) return false;
        try
        {
            return QuestManager.IsQuestComplete(questId);
        }
        catch
        {
            return false;
        }
    }

    internal static unsafe void TryOpenDutyFinder(uint cfcRowId)
    {
        var agent = AgentContentsFinder.Instance();
        if (agent != null)
            agent->OpenRegularDuty(cfcRowId);
    }

    private void TryOpenMap(string? npcName, string? zoneName, uint? territoryTypeId, uint? mapId, float mapX, float mapY)
    {
        if (_onOpenMap != null)
        {
            _onOpenMap(npcName ?? "", zoneName ?? "", mapX, mapY);
        }
        else if (territoryTypeId.HasValue && mapId.HasValue)
        {
            try
            {
                var mapLink = new MapLinkPayload(
                    territoryTypeId.Value,
                    mapId.Value,
                    mapX,
                    mapY);
                Plugin.GameGui.OpenMapWithMapLink(mapLink);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[MAP] OpenMapWithMapLink failed");
            }
        }
        else
        {
            Console.WriteLine($"[MAP] Territory={territoryTypeId} Map={mapId} ({mapX:F1}, {mapY:F1})");
        }
    }

    private static string FormatNumber(uint value)
    {
        return value.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("N1", CultureInfo.GetCultureInfo("de-DE"));
    }

    private static void OpenWiki(string itemName, uint itemId)
    {
        try
        {
            var url = $"https://ffxiv.consolegameswiki.com/wiki/{itemName.Replace(' ', '_')}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[WIKI] Failed to open wiki for {Name}", itemName);
        }
    }

    private static void OpenShopUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MOGSTATION] Failed to open shop URL {Url}", url);
        }
    }

    private void OpenMarketPrices(uint itemId)
    {
        try
        {
            var url = $"https://universalis.app/docs/index.html#/marketData?itemId={itemId}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MARKET] Failed to open market for item {Id}", itemId);
        }
    }

    private static unsafe int GetItemCount(uint itemId)
    {
        if (itemId == 0 || itemId > 500000)
            return 0;

        try
        {
            var im = InventoryManager.Instance();
            if (im == null)
                return 0;

            // RetainerPageN removed from this live scan — RetainerInventoryCache (added below)
            // already covers the currently-open retainer (Plugin's Framework tick keeps it fresh)
            // PLUS every other retainer visited this session; scanning here too would double-count
            // whichever retainer happens to be open right now.
            int total = RetainerInventoryCache.GetTotal(itemId);
            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
                InventoryType.Crystals,
                InventoryType.Currency,
                InventoryType.SaddleBag1,
                InventoryType.SaddleBag2,
            };

            foreach (var type in containers)
            {
                var container = im->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var item = container->Items[i];
                    if (item.ItemId == itemId)
                    {
                        total += (int)item.Quantity;
                    }
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetInventoryStatus(uint itemId, int required)
    {
        var have = GetItemCount(itemId);
        if (have >= required)
            return $" ({have}/{required})";
        if (have > 0)
            return $" ({have}/{required})";
        return $" ({have}/{required})";
    }

    private static unsafe Dictionary<string, int> GetInventoryBreakdown(uint itemId)
    {
        try
        {
            if (itemId == 0 || itemId > 500000)
                return new();

            var breakdown = new Dictionary<string, int>();
            var im = InventoryManager.Instance();
            if (im == null)
                return breakdown;

            int bags = 0, saddlebag = 0;

            void Scan(InventoryType type, ref int accumulator)
            {
                var container = im->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    return;
                for (int i = 0; i < container->Size; i++)
                {
                    if (container->Items[i].ItemId == itemId)
                        accumulator += (int)container->Items[i].Quantity;
                }
            }

            Scan(InventoryType.Inventory1, ref bags);
            Scan(InventoryType.Inventory2, ref bags);
            Scan(InventoryType.Inventory3, ref bags);
            Scan(InventoryType.Inventory4, ref bags);
            Scan(InventoryType.Crystals, ref bags);
            Scan(InventoryType.Currency, ref bags);
            Scan(InventoryType.SaddleBag1, ref saddlebag);
            Scan(InventoryType.SaddleBag2, ref saddlebag);

            if (bags > 0) breakdown[Loc.T("Bags")] = bags;
            if (saddlebag > 0) breakdown[Loc.T("Saddlebag")] = saddlebag;
            // "man sieht welches mat wo liegt" — RetainerInventoryCache snapshots EVERY retainer
            // visited this session (persists after you close its window), not just whichever one's
            // currently open — a real per-name breakdown instead of one lumped "Retainers: N".
            foreach (var (retainerName, count) in RetainerInventoryCache.GetHolders(itemId))
                breakdown[retainerName] = count;
            return breakdown;
        }
        catch
        {
            return new();
        }
    }

    private string GetCostKey(ItemSourceDetail src)
    {
        if (src.Costs == null || src.Costs.Count == 0)
            return "free";
        return string.Join("+", src.Costs
            .OrderBy(c => c.ItemId)
            .Select(c => $"{c.ItemId}x{c.Count}"));
    }

    private void DrawCraftedCard(List<ItemSourceDetail> sources, int groupIdx, uint itemIconId)
    {
        var first = sources[0];

        using (new SourceCardScope(SourceStyles[ItemSourceType.Crafted].Item1))
        {
            var style = SourceStyles.GetValueOrDefault(first.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
            DrawBadge(style.Item3, style.Item2);
            ImGui.SameLine();
            TryDrawSourceIcon(itemIconId, RowIconSize);

            var levels = sources.Select(s => ExtractLevel(s.Description)).Where(l => l > 0).Distinct().OrderBy(l => l);
            var jobs = sources.Select(s => ExtractJobName(s.Description)).Where(j => !string.IsNullOrEmpty(j)).Distinct();
            var levelStr = levels.Any() ? $"Lv.{levels.Min()}" : "";
            var jobStr = string.Join(", ", jobs.Any() ? jobs : (object?)levelStr);
            var title = levels.Any() && jobs.Any()
                ? $"{levelStr} ({string.Join(", ", jobs)})"
                : (levelStr ?? jobStr ?? Loc.T("Crafted"));
            ImGui.Text(title);
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Loc.T("Open Crafting Log")}##craft_{groupIdx}"))
            {
                // ponytail: HQ items sit at NQ RowId + 1_000_000; the vanilla recipe log only knows the NQ id.
                var craftItemId = _showingItemId ?? 0;
                if (craftItemId >= 1_000_000)
                    craftItemId -= 1_000_000;
                TryOpenCraftingLog(craftItemId);
            }

            // ponytail: batch button dropped — SimpleGatherService is single-item; per-material
            // Gather buttons cover it. Add batch queue when actually needed.

            ImGui.Spacing();

            if (first.Materials != null && first.Materials.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled(Loc.T("Materials:"));
                for (int matIdx = 0; matIdx < first.Materials.Count; matIdx++)
                {
                    DrawMaterialRow(first.Materials[matIdx], groupIdx, matIdx, showCheckmark: false, prefix: "\u2022");
                }
            }
        }
        ImGui.Spacing();
    }

    private string GetMaterialKey(ItemSourceDetail src)
    {
        if (src.Materials == null || src.Materials.Count == 0)
            return "none";
        return string.Join("+", src.Materials
            .Where(m => m.ItemId > 19 && m.Count > 0)
            .OrderBy(m => m.ItemId)
            .Select(m => $"{m.ItemId}x{m.Count}"));
    }

    private string ExtractJobName(string description)
    {
        var openParen = description.LastIndexOf('(');
        var closeParen = description.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
            return description.Substring(openParen + 1, closeParen - openParen - 1);
        return "";
    }

    private int ExtractLevel(string description)
    {
        var lvIndex = description.IndexOf("Lv.");
        if (lvIndex >= 0)
        {
            var start = lvIndex + 3;
            var end = description.IndexOf(' ', start);
            if (end < 0) end = description.Length;
            if (int.TryParse(description.Substring(start, end - start), out var level))
                return level;
        }
        return 0;
    }

    public void ShowCraftingSavings()
    {
        if (_showingItemId == null) return;
        _craftingResult = null;
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            _craftingResult = service != null ? await service.GetCostBreakdownAsync(_showingItemId.Value) : null;
        });
    }

    private void QueryCraftingSavings(uint itemId)
    {
        _craftingResult = null;
        _craftingItemId = itemId;
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            var result = service != null ? await service.GetCostBreakdownAsync(itemId) : null;
            if (_craftingItemId == itemId) _craftingResult = result; // still the shown item? see field comment
        });
    }

    private void DrawCraftingSavings()
    {
        var result = _craftingResult!;
        var saved = result.MarketNQPrice.HasValue
            ? result.MarketNQPrice.Value - result.CraftedCost
            : (long?)null;
        var savingsColor = saved.HasValue && saved.Value > 0
            ? new Vector4(0.4f, 1f, 0.4f, 1f)
            : new Vector4(0.8f, 0.8f, 0.8f, 1f);

        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), Loc.T("Materials:"));
        ImGui.Separator();
        foreach (var (name, count, marketPrice) in result.Materials.Select(m => (m.Name, m.Count, m.MarketPrice)))
        {
            var priceStr = marketPrice.HasValue ? $" @ {FormatNumber(marketPrice.Value)}" : "";
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  • {name} x{FormatNumber(count)}{priceStr}");
        }

        ImGui.Spacing();
        if (result.MarketNQPrice.HasValue)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), Loc.T("Comparison:"));
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  {Loc.T("Market (NQ):")} {FormatNumber(result.MarketNQPrice.Value)} Gil");
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  {Loc.T("Crafted cost:")} {FormatNumber(result.CraftedCost ?? 0)} Gil");
            if (saved.HasValue)
            {
                ImGui.TextColored(savingsColor, $"  {Loc.T("Savings:")} {FormatNumber((uint)Math.Max(0, saved.Value))} Gil");
            }
        }
        else
        {
            ImGui.TextDisabled("  " + Loc.T("No market price available for comparison."));
        }
    }

    public void Dispose()
    {
        foreach (var tex in _previewTextureCache.Values)
            tex?.Dispose();
        _previewTextureCache.Clear();
    }
}
