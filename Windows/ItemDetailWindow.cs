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
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource.Core;

namespace GlamSource.Windows;

public class ItemDetailWindow : Window, IDisposable
{
    private readonly IItemDetailService _detailService;
    private readonly IItemSourceService _sourceService;
    private readonly IUniversalisService _universalisService;
    private readonly ITextureProvider _textureProvider;
    private readonly Stack<uint> _history = new();
    private uint? _showingItemId;
    private bool _isOpen;
    private uint? _navigateToItemId;
    private int _navigateToSourceIdx = -1;

    private MarketInfo? _marketInfo;
    private bool _marketLoading;
    private uint _marketItemId;
    private Action<string, string, float, float>? _onOpenMap;

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
        [ItemSourceType.Other] = (
            new Vector4(0.5f, 0.5f, 0.5f, 1f),
            new Vector4(0.15f, 0.15f, 0.15f, 1f),
            "OTHER"),
    };

    public ItemDetailWindow(IItemDetailService detailService, IItemSourceService sourceService, IUniversalisService universalisService, ITextureProvider? textureProvider = null)
        : base($"Item Detail###ItemDetailWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _detailService = detailService;
        _sourceService = sourceService;
        _universalisService = universalisService;
        _textureProvider = textureProvider;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 250),
            MaximumSize = new Vector2(700, float.MaxValue)
        };
    }

    public void SetMapCallback(Action<string, string, float, float> callback)
    {
        _onOpenMap = callback;
    }

    public void ShowItem(uint itemId)
    {
        _history.Clear();
        LoadItemDetail(itemId);
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
                    _marketInfo = await _universalisService.GetMarketInfoAsync(itemId);
                    _marketLoading = false;
                });
            }
        }
        else
        {
            Console.WriteLine($"[DETAIL] ShowItem({itemId}) NOT FOUND");
        }
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

        if (_navigateToItemId.HasValue && _navigateToSourceIdx >= 0)
        {
            NavigateToItem(_navigateToItemId.Value);
            _navigateToItemId = null;
            _navigateToSourceIdx = -1;
            return;
        }

        if (_history.Count > 0)
        {
            if (ImGui.SmallButton("← Back"))
            {
                var previousId = _history.Pop();
                LoadItemDetail(previousId);
            }
            ImGui.SameLine();
        }

        DrawItemHeader(detail);
        ImGui.Spacing();

        if (_marketInfo != null && _marketItemId == detail.ItemId)
            DrawMarketPricesCompact(_marketInfo);
        else if (_marketLoading && _marketItemId == detail.ItemId)
            ImGui.TextDisabled("Loading prices...");

        ImGui.Separator();
        ImGui.TextDisabled("SOURCES");
        ImGui.Spacing();

        DrawSourceCards(detail);
    }

    private void DrawItemHeader(ItemDetail detail)
    {
        if (_textureProvider != null && detail.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(detail.IconId)).GetWrapOrEmpty();
            var iconSize = new Vector2(ImGui.GetTextLineHeight());
            ImGui.Image(iconTexture.Handle, iconSize);
            ImGui.SameLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.31f, 0.76f, 0.97f, 1f));
        ImGui.SetWindowFontScale(1.1f);
        ImGui.Text(detail.Name);
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled($"({detail.ItemId})  |  iLvl {detail.ItemLevel}");

        var rightX = ImGui.GetContentRegionAvail().X > 0
            ? ImGui.GetWindowPos().X + ImGui.GetContentRegionAvail().X - 120
            : ImGui.GetCursorPosX() + 120;
        ImGui.SameLine();
        if (ImGui.SmallButton($"Wiki##wiki_{detail.ItemId}"))
        {
            OpenWiki(detail.Name, detail.ItemId);
        }
        ImGui.SameLine();
        if (detail.IsMarketable && ImGui.SmallButton($"Market##market_{detail.ItemId}"))
        {
            OpenMarketPrices(detail.ItemId);
        }
    }

    private void DrawMarketPricesCompact(MarketInfo market)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.25f, 1f));
        if (ImGui.BeginChild("##market", new Vector2(ImGui.GetContentRegionAvail().X, 24),
            true))
        {
            ImGui.TextDisabled("Shiva:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.84f, 0.25f, 1f),
                $"{FormatNumber(market.WorldMinPrice)}");
            ImGui.SameLine();
            ImGui.TextDisabled("Gil (NQ)  |  Light DC:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.41f, 0.94f, 0.68f, 1f),
                $"{FormatNumber(market.DcMinPrice)}");
            ImGui.SameLine();
            ImGui.TextDisabled($"Gil on {market.DcWorldName}");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawSourceCards(ItemDetail detail)
    {
        Console.WriteLine($"[UI] Drawing {detail.Sources.Count} sources for item {_showingItemId}");

        if (detail.Sources.Count == 0)
        {
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
            ImGui.TextColored(grey, "No vendor/crafting source found.");
            ImGui.TextColored(grey, "This item may drop from duties, raids,");
            ImGui.TextColored(grey, "or other content.");
            return;
        }

        var vendorSources = detail.Sources
            .Select((s, i) => (source: s, index: i))
            .Where(x => x.source.Type == ItemSourceType.Vendor)
            .ToList();

        var craftedSources = detail.Sources
            .Select((s, i) => (source: s, index: i))
            .Where(x => x.source.Type == ItemSourceType.Crafted)
            .ToList();

        var otherSources = detail.Sources
            .Select((s, i) => (source: s, index: i))
            .Where(x => x.source.Type != ItemSourceType.Vendor && x.source.Type != ItemSourceType.Crafted)
            .ToList();

        foreach (var (src, idx) in otherSources)
        {
            var fallbackStyle = SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
            DrawSourceCard(src, idx, fallbackStyle.Item1);
        }

        if (craftedSources.Count > 0)
        {
            var craftedGroups = craftedSources
                .GroupBy(s => GetMaterialKey(s.source))
                .ToList();

            for (int g = 0; g < craftedGroups.Count; g++)
            {
                var group = craftedGroups[g];
                var sources = group.Select(x => x.source).ToList();
                DrawCraftedCard(sources, g);
            }
        }

        if (vendorSources.Count > 0)
        {
            var vendorGroups = vendorSources
                .GroupBy(s => GetCostKey(s.source))
                .ToList();

            for (int g = 0; g < vendorGroups.Count; g++)
            {
                var group = vendorGroups[g];
                var npcs = group.Select(x => x.source).ToList();
                DrawVendorCard(npcs, g, SourceStyles[ItemSourceType.Vendor].Item1);
            }
        }
    }

    private void DrawSourceCard(ItemSourceDetail src, int sourceIdx, Vector4 borderColor, string? titleOverride = null)
    {
        var contentMin = ImGui.GetWindowContentRegionMin() + ImGui.GetWindowPos();
        var contentMax = ImGui.GetWindowContentRegionMax() + ImGui.GetWindowPos();
        ImGui.GetWindowDrawList().PushClipRect(contentMin, contentMax, true);

        var bgDrawList = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var startY = ImGui.GetCursorPosY();
        var startScreenX = ImGui.GetCursorScreenPos().X;

        ImGui.Indent(12f);
        ImGui.BeginGroup();
        ImGui.Spacing();

        var srcStyle = SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
        DrawBadge(srcStyle.Item3, srcStyle.Item2);

        ImGui.SameLine();
        ImGui.Text($"  {titleOverride ?? src.Description}");
        if (src.SourceItemId.HasValue)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"[i]##source_{sourceIdx}"))
            {
                _navigateToItemId = src.SourceItemId.Value;
                _navigateToSourceIdx = sourceIdx;
            }
        }
        ImGui.Spacing();

        if (src.NpcName != null && (src.ZoneName != null || src.MapX.HasValue))
        {
            DrawNpcRow(src, sourceIdx, 0);
        }

        if (src.Materials != null && src.Materials.Count > 0)
        {
            ImGui.TextDisabled("    Materials:");
            for (int matIdx = 0; matIdx < src.Materials.Count; matIdx++)
            {
                var mat = src.Materials[matIdx];
                var status = mat.ItemId > 19 ? GetInventoryStatus(mat.ItemId, (int)mat.Count) : "";
                var breakdown = mat.ItemId > 19 ? GetInventoryBreakdown(mat.ItemId) : new Dictionary<string, int>();
                var showGatherBtn = ShouldShowGatherButton(mat.ItemId);

                if (_textureProvider != null && mat.IconId > 0)
                {
                    var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(mat.IconId)).GetWrapOrEmpty();
                    var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f);
                    ImGui.Image(iconTexture.Handle, iconSize);
                    ImGui.SameLine();
                }
                ImGui.TextDisabled($"      \u2022 {mat.Name} x{FormatNumber(mat.Count)}{status}");

                if (showGatherBtn)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Gather##gather_{sourceIdx}_{matIdx}"))
                    {
                        try
                        {
                            var gb = Plugin.PluginInterface
                                .GetIpcSubscriber<string, bool>("GatherBuddy.IPC.SearchItem");
                            if (gb.HasFunction)
                                gb.InvokeFunc(mat.Name);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.Information($"[GATHER] GatherBuddy not available: {ex.Message}");
                        }
                    }
                }

                if (breakdown.Count > 0)
                {
                    ImGui.Indent(20f);
                    ImGui.TextDisabled($"        Breakdown: {string.Join(", ", breakdown.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                    ImGui.Unindent(20f);
                }
            }

            if (src.Type == ItemSourceType.Crafted)
            {
                if (ImGui.SmallButton($"Open Crafting Log##craft_{sourceIdx}"))
                {
                    TryOpenCraftingLog(_showingItemId ?? 0);
                }
            }
        }

        if (src.Costs != null && src.Costs.Count > 0)
        {
            ImGui.TextDisabled("    Cost:");
            int costIdx = 0;
            foreach (var cost in src.Costs)
            {
                if (cost.ItemId == 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"      \u2022 {FormatNumber(cost.Count)} Gil");
                }
                else
                {
                    var status = cost.ItemId > 19 ? GetInventoryStatus(cost.ItemId, (int)cost.Count) : "";
                    var breakdown = cost.ItemId > 19 ? GetInventoryBreakdown(cost.ItemId) : new Dictionary<string, int>();

                    if (_textureProvider != null && cost.IconId > 0)
                    {
                        var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(cost.IconId)).GetWrapOrEmpty();
                        var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f);
                        ImGui.Image(iconTexture.Handle, iconSize);
                        ImGui.SameLine();
                    }
                    ImGui.TextDisabled($"      \u2022 {FormatNumber(cost.Count)} {cost.Name}{status}");

                    if (breakdown.Count > 0)
                    {
                        ImGui.Indent(20f);
                        ImGui.TextDisabled($"        Breakdown: {string.Join(", ", breakdown.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                        ImGui.Unindent(20f);
                    }

                    var showInfoButton = cost.ItemId > 19
                        && !string.IsNullOrEmpty(cost.Name);

                    if (showInfoButton)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"[i]##cost_{sourceIdx}_{costIdx}"))
                        {
                            _navigateToItemId = cost.ItemId;
                            _navigateToSourceIdx = sourceIdx;
                        }
                    }

                    if (ShouldShowGatherButton(cost.ItemId))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Gather##gather_{sourceIdx}_{costIdx}"))
                        {
                            try
                            {
                                var gb = Plugin.PluginInterface
                                    .GetIpcSubscriber<string, bool>("GatherBuddy.IPC.SearchItem");
                                if (gb.HasFunction)
                                    gb.InvokeFunc(cost.Name);
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log?.Information($"[GATHER] GatherBuddy not available: {ex.Message}");
                            }
                        }
                    }
                }
                costIdx++;
            }
        }

        if (src.Type == ItemSourceType.Trial || src.Type == ItemSourceType.Raid || src.Type == ItemSourceType.Dungeon)
        {
            if (src.CfcRowId.HasValue && src.CfcName != null)
            {
                if (CheckUnlockStatus(src.CfcRowId.Value))
                {
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"  \u2713 {src.CfcName} unlocked");
                }
                if (ImGui.SmallButton($"Duty Finder##duty_{sourceIdx}"))
                {
                    TryOpenDutyFinder(src.CfcRowId.Value);
                }
            }
        }

        if (src.Type == ItemSourceType.Quest && src.QuestName != null)
        {
            if (src.NpcName != null && src.ZoneName != null)
            {
                ImGui.SameLine();
                DrawNpcRow(src, sourceIdx, -1);
            }

            var questLocked = src.QuestForUnlock.HasValue && IsQuestLockedByQuestionable(src.QuestForUnlock.Value);
            if (questLocked)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"  \u2717 Locked (prerequisites incomplete)");
                if (ImGui.SmallButton($"Start Quest##quest_{sourceIdx}"))
                {
                    TryStartWithQuestionable(src.QuestForUnlock.Value);
                }
            }
            else if (src.QuestForUnlock.HasValue)
            {
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"  \u2713 Quest unlocked");
            }
        }

        ImGui.Spacing();
        ImGui.EndGroup();
        ImGui.Unindent(12f);

        var endY = ImGui.GetCursorPosY();
        var height = endY - startY;

        if (height > 0)
        {
            var bgLeft = startScreenX;
            var bgRight = startScreenX + width;

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgRight, startY + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.22f, 1f)),
                3f);

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgLeft + 3, startY + height),
                ImGui.ColorConvertFloat4ToU32(borderColor));
        }

        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.GetWindowDrawList().PopClipRect();
    }

    private void DrawBadge(string label, Vector4 bgColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(label);
        var padding = new Vector2(6, 2);

        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + textSize.X + padding.X * 2, pos.Y + textSize.Y + padding.Y * 2),
            ImGui.ColorConvertFloat4ToU32(bgColor),
            2f);

        var textColor = new Vector4(1f, 1f, 1f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + padding.X, pos.Y + padding.Y));
        ImGui.Text(label);
        ImGui.PopStyleColor();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + textSize.X + padding.X * 2);
    }

    private void DrawNpcRow(ItemSourceDetail src, int groupIdx, int npcIdx)
    {
        ImGui.Text(src.NpcName ?? "Unknown vendor");
        if (src.ZoneName != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($" \u00b7 {src.ZoneName}");
        }
        if (src.MapX.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($" ({src.MapX:F1}, {src.MapY:F1})");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Map##map_{groupIdx}_{npcIdx}"))
                TryOpenMap(src.NpcName, src.ZoneName, src.TerritoryTypeId, src.MapId, src.MapX.Value, src.MapY.Value);
        }
    }

    private void DrawVendorCard(List<ItemSourceDetail> vendors, int groupIdx, Vector4 borderColor)
    {
        var first = vendors[0];
        var bgDrawList = ImGui.GetBackgroundDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var startY = ImGui.GetCursorPosY();
        var startScreenX = ImGui.GetCursorScreenPos().X;

        ImGui.Indent(12f);
        ImGui.BeginGroup();
        ImGui.Spacing();

        var style = SourceStyles.GetValueOrDefault(first.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
        DrawBadge(style.Item3, style.Item2);
        ImGui.SameLine();

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

        DrawNpcRow(first, groupIdx, 0);

        if (vendors.Count > 1)
        {
            var moreLabel = vendors.Count == 2 ? "1 more vendor" : $"{vendors.Count - 1} more vendors";
            if (ImGui.TreeNode($"{moreLabel}##vg_{groupIdx}"))
            {
                for (int i = 1; i < vendors.Count; i++)
                {
                    DrawNpcRow(vendors[i], groupIdx, i);
                    ImGui.Spacing();
                }
                ImGui.TreePop();
            }
        }

        if (!isGilOnly && first.Costs?.Count > 0)
        {
            ImGui.TextDisabled("    Cost:");
            foreach (var cost in first.Costs)
            {
                if (cost.ItemId == 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"      \u2022 {FormatNumber(cost.Count)} Gil");
                }
                else
                {
                    var status = cost.ItemId > 19 ? GetInventoryStatus(cost.ItemId, (int)cost.Count) : "";

                    if (_textureProvider != null && cost.IconId > 0)
                    {
                        var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(cost.IconId)).GetWrapOrEmpty();
                        var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f);
                        ImGui.Image(iconTexture.Handle, iconSize);
                        ImGui.SameLine();
                    }
                    ImGui.TextDisabled($"      \u2022 {FormatNumber(cost.Count)} {cost.Name}{status}");

                    var breakdown = cost.ItemId > 19 ? GetInventoryBreakdown(cost.ItemId) : new Dictionary<string, int>();
                    if (breakdown.Count > 0)
                    {
                        ImGui.Indent(20f);
                        ImGui.TextDisabled($"        Breakdown: {string.Join(", ", breakdown.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                        ImGui.Unindent(20f);
                    }

                    if (cost.ItemId > 19 && !string.IsNullOrEmpty(cost.Name))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"[i]##cost_{groupIdx}_{0}"))
                        {
                            _navigateToItemId = cost.ItemId;
                            _navigateToSourceIdx = groupIdx;
                        }
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.EndGroup();
        ImGui.Unindent(12f);

        var endY = ImGui.GetCursorPosY();
        var height = endY - startY;

        if (height > 0)
        {
            var bgLeft = startScreenX;
            var bgRight = startScreenX + width;

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgRight, startY + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.22f, 1f)),
                3f);

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgLeft + 3, startY + height),
                ImGui.ColorConvertFloat4ToU32(borderColor));
        }

        ImGui.Spacing();
        ImGui.Spacing();
    }

    private bool ShouldShowGatherButton(uint itemId)
    {
        if (itemId == 0 || itemId > 500000)
            return false;

        try
        {
            var itemSheet = _detailService.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet?.TryGetRow(itemId, out var item) != true)
                return false;

            var searchCat = item.ItemSearchCategory.RowId;
            return searchCat == 35 || searchCat == 36;
        }
        catch
        {
            return false;
        }
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
                var label = style.Item3;
                ImGui.TextDisabled($"  {label}: {s.Description}");
                ImGui.Spacing();
            }
        }
    }

    private static unsafe void TryOpenCraftingLog(uint itemId)
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

    private static bool CheckUnlockStatus(uint questId)
    {
        try
        {
            return QuestManager.IsQuestComplete(questId);
        }
        catch
        {
            return false;
        }
    }

    private static unsafe void TryOpenDutyFinder(uint cfcRowId)
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

            int total = 0;
            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
                InventoryType.Crystals,
                InventoryType.Currency,
                InventoryType.RetainerPage1,
                InventoryType.RetainerPage2,
                InventoryType.RetainerPage3,
                InventoryType.RetainerPage4,
                InventoryType.RetainerPage5,
                InventoryType.RetainerPage6,
                InventoryType.RetainerPage7,
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

            int bags = 0, retainers = 0, saddlebag = 0;

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
            Scan(InventoryType.RetainerPage1, ref retainers);
            Scan(InventoryType.RetainerPage2, ref retainers);
            Scan(InventoryType.RetainerPage3, ref retainers);
            Scan(InventoryType.RetainerPage4, ref retainers);
            Scan(InventoryType.RetainerPage5, ref retainers);
            Scan(InventoryType.RetainerPage6, ref retainers);
            Scan(InventoryType.RetainerPage7, ref retainers);
            Scan(InventoryType.SaddleBag1, ref saddlebag);
            Scan(InventoryType.SaddleBag2, ref saddlebag);

            if (bags > 0) breakdown["Bags"] = bags;
            if (retainers > 0) breakdown["Retainers"] = retainers;
            if (saddlebag > 0) breakdown["Saddlebag"] = saddlebag;
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

    private string GetCostTitle(ItemSourceDetail src)
    {
        if (src.Type != ItemSourceType.Vendor)
            return src.Description;

        if (src.Costs == null || src.Costs.Count == 0)
            return src.Description;

        return string.Join(", ", src.Costs
            .Select(c => c.ItemId == 0
                ? $"{FormatNumber(c.Count)} Gil"
                : $"{FormatNumber(c.Count)} {c.Name}"));
    }

    private void DrawCraftedCard(List<ItemSourceDetail> sources, int groupIdx)
    {
        var first = sources[0];
        var bgDrawList = ImGui.GetBackgroundDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var startY = ImGui.GetCursorPosY();
        var startScreenX = ImGui.GetCursorScreenPos().X;

        ImGui.Indent(12f);
        ImGui.BeginGroup();
        ImGui.Spacing();

        var style = SourceStyles.GetValueOrDefault(first.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
        DrawBadge(style.Item3, style.Item2);
        ImGui.SameLine();

        var levels = sources.Select(s => ExtractLevel(s.Description)).Where(l => l > 0).Distinct().OrderBy(l => l);
        var jobs = sources.Select(s => ExtractJobName(s.Description)).Where(j => !string.IsNullOrEmpty(j)).Distinct();
        var levelStr = levels.Any() ? $"Lv.{levels.Min()}" : "";
        var jobStr = string.Join(", ", jobs.Any() ? jobs : (object?)levelStr);
        var title = levels.Any() && jobs.Any()
            ? $"{levelStr} ({string.Join(", ", jobs)})"
            : (levelStr ?? jobStr ?? "Crafted");
        ImGui.Text(title);

        ImGui.Spacing();

        if (first.Materials != null && first.Materials.Count > 0)
        {
            ImGui.TextDisabled("    Materials:");
            foreach (var mat in first.Materials)
            {
                var status = mat.ItemId > 19 ? GetInventoryStatus(mat.ItemId, (int)mat.Count) : "";
                var showGatherBtn = ShouldShowGatherButton(mat.ItemId);

                if (_textureProvider != null && mat.IconId > 0)
                {
                    var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(mat.IconId)).GetWrapOrEmpty();
                    var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f);
                    ImGui.Image(iconTexture.Handle, iconSize);
                    ImGui.SameLine();
                }
                ImGui.TextDisabled($"      \u2022 {mat.Name} x{FormatNumber(mat.Count)}{status}");

                if (showGatherBtn)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Gather##gather_{groupIdx}_{mat.ItemId}"))
                    {
                        try
                        {
                            var gb = Plugin.PluginInterface
                                .GetIpcSubscriber<string, bool>("GatherBuddy.IPC.SearchItem");
                            if (gb.HasFunction)
                                gb.InvokeFunc(mat.Name);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.Information($"[GATHER] GatherBuddy not available: {ex.Message}");
                        }
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.EndGroup();
        ImGui.Unindent(12f);

        var endY = ImGui.GetCursorPosY();
        var height = endY - startY;

        if (height > 0)
        {
            var bgLeft = startScreenX;
            var bgRight = startScreenX + width;

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgRight, startY + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.22f, 1f)),
                3f);

            bgDrawList.AddRectFilled(
                new Vector2(bgLeft, startY),
                new Vector2(bgLeft + 3, startY + height),
                ImGui.ColorConvertFloat4ToU32(style.Item1));
        }

        ImGui.Spacing();
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

    public void Dispose()
    {
    }
}
