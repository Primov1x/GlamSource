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
    private Plugin _plugin = null!;
    private readonly Stack<uint> _history = new();
    private uint? _showingItemId;
    private bool _isOpen;
    private uint? _navigateToItemId;
    private int _navigateToSourceIdx = -1;

    private MarketInfo? _marketInfo;
    private bool _marketLoading;
    private uint _marketItemId;
    private Action<string, string, float, float>? _onOpenMap;
    private CraftingCostResult? _craftingResult;

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
            MaximumSize = new Vector2(700f, 800f)
        };
    }

    public void SetPlugin(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void SetMapCallback(Action<string, string, float, float> callback)
    {
        _onOpenMap = callback;
    }

    public void ShowItem(uint itemId)
    {
        _history.Clear();
        LoadItemDetail(itemId);
        _craftingResult = null;
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            _craftingResult = service != null ? await service.GetCostBreakdownAsync(itemId) : null;
        });
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
        UpdateGatherFeedback();

        if (!_isOpen || _showingItemId == null)
        {
            IsOpen = false;
            return;
        }

        var detail = _detailService.GetDetail(_showingItemId.Value);
        if (detail == null)
        {
            ImGui.TextDisabled("Item not found.");
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

        if (_marketInfo != null && _marketItemId == detail.ItemId)
            DrawMarketPricesCompact(_marketInfo);
        else if (_marketLoading && _marketItemId == detail.ItemId)
            ImGui.TextDisabled("Loading prices...");

        SectionHeader("SOURCES");
        ImGui.Spacing();

        DrawSourceCards(detail);
        DrawGatheringActionButton(detail);

        if (_plugin?.Configuration?.ShowCraftingSavings == true && _craftingResult != null)
        {
            SectionHeader("CRAFTING SAVINGS");
            ImGui.Spacing();
            DrawCraftingSavings();
        }
    }

    private void DrawItemHeader(ItemDetail detail)
    {
        var iconSize = new Vector2(40f, 40f);
        if (_textureProvider != null && detail.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(detail.IconId)).GetWrapOrEmpty();
            ImGui.Image(iconTexture.Handle, iconSize);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Text(detail.Name);
        ImGui.TextDisabled($"Item ID {detail.ItemId}  \u00b7  iLvl {detail.ItemLevel}");
        ImGui.EndGroup();

        ImGui.Spacing();

        if (_history.Count > 0)
        {
            if (ImGui.SmallButton("← Back"))
            {
                var previousId = _history.Pop();
                LoadItemDetail(previousId);
            }
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Wiki"))
        {
            OpenWiki(detail.Name, detail.ItemId);
        }
        ImGui.SameLine();
        if (detail.IsMarketable && ImGui.SmallButton("Market prices"))
        {
            OpenMarketPrices(detail.ItemId);
        }
        ImGui.Spacing();
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
        ImGui.TextDisabled("World:");
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
            ImGui.SmallButton("⛏ Gather (Cooling down...)");
            ImGui.PopStyleColor(2);
        }
        else if (ImGui.SmallButton("⛏ Gather"))
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
            var started = _plugin.GatherService.StartGathering(itemId);
            if (started)
            {
                _gatherOutcome = GatherOutcome.Started;
                _gatherOutcomeDetail = itemName;
                Plugin.Log?.Information("[GATHER] Started for {Name} (ID={Id})", itemName, itemId);
            }
            else
            {
                _gatherOutcome = GatherOutcome.Failed;
                _gatherOutcomeDetail = "No known location or no reachable node";
                Plugin.Log?.Warning("[GATHER] StartGathering returned false for {Name} (ID={Id})", itemName, itemId);
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
                DrawCraftedCard(sources, g);
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
                DrawVendorCard(npcs, g, SourceStyles[ItemSourceType.Vendor].Item1);
            }
        }

        foreach (var src in questSources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN")).Item1);
        }

        foreach (var src in dutySources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN")).Item1);
        }

        foreach (var src in otherSources)
        {
            DrawSourceCard(src, sortedSources.IndexOf(src), SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN")).Item1);
        }
    }

    private void DrawSourceCard(ItemSourceDetail src, int sourceIdx, Vector4 borderColor, string? titleOverride = null)
    {
        var srcStyle = SourceStyles.GetValueOrDefault(src.Type, (Vector4.One, Vector4.One, "UNKNOWN"));
        var hasContent = (src.Materials != null && src.Materials.Count > 0)
                      || (src.Costs != null && src.Costs.Count > 0)
                      || (src.NpcName != null && (src.ZoneName != null || src.MapX.HasValue))
                      || (src.QuestName != null)
                      || (src.CfcRowId.HasValue && src.CfcName != null)
                      || (src.SourceItemId.HasValue && src.SourceItemId.Value > 0);

        ImGui.Separator();
        ImGui.Spacing();

        if (!hasContent)
        {
            DrawBadge(srcStyle.Item3, srcStyle.Item2);
            ImGui.SameLine();
            ImGui.TextDisabled($" {titleOverride ?? src.Description}");
            ImGui.Spacing();
            return;
        }

        DrawBadge(srcStyle.Item3, srcStyle.Item2);
        ImGui.SameLine();
        ImGui.Text($" {titleOverride ?? src.Description}");
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
            if (ImGui.SmallButton($"Open Crafting Log##craft_{sourceIdx}"))
            {
                TryOpenCraftingLog(_showingItemId ?? 0);
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
            ImGui.TextDisabled("Materials:");
            for (int matIdx = 0; matIdx < src.Materials.Count; matIdx++)
            {
                DrawMaterialRow(src.Materials[matIdx], sourceIdx, matIdx);
            }
        }

        if (src.Costs != null && src.Costs.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Cost:");
            int costIdx = 0;
            foreach (var cost in src.Costs)
            {
                DrawCostRow(cost, sourceIdx, costIdx);
                costIdx++;
            }
        }

        DrawDutyFinderRow(src, sourceIdx);
        DrawQuestRow(src, sourceIdx);

        ImGui.Spacing();
    }

    private void DrawMaterialRow(CostEntry mat, int sourceIdx, int matIdx, bool showCheckmark = true, string? prefix = null)
    {
        var have = mat.ItemId > 19 ? GetItemCount(mat.ItemId) : 0;
        var sufficient = have >= mat.Count;
        var breakdown = mat.ItemId > 19 ? GetInventoryBreakdown(mat.ItemId) : new Dictionary<string, int>();
        var showGatherBtn = ShouldShowGatherButton(mat.ItemId);

        const float IconSize = 32f;
        if (_textureProvider != null && mat.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(mat.IconId)).GetWrapOrEmpty();
            var iconSize = new Vector2(IconSize, IconSize);
            // Vertically center icon with text
            float textHeight = ImGui.GetTextLineHeight();
            float offsetY = (textHeight - IconSize) * 0.5f;
            var cursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(cursorPos.Y + offsetY);
            ImGui.Image(iconTexture.Handle, iconSize);
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorPos.Y);
        }

        var nameColor = sufficient
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.75f, 0.25f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
        ImGui.Text($"{mat.Name} x{FormatNumber(mat.Count)}");
        ImGui.PopStyleColor();
        if (mat.ItemId > 19)
        {
            ImGui.SameLine();
            var countColor = sufficient
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.8f, 0.3f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, countColor);
            ImGui.Text($"({have}/{mat.Count})");
            ImGui.PopStyleColor();
            if (showCheckmark)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 1f);
                var checkColor = sufficient
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.75f, 0.25f, 1f))
                    : ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Text, checkColor);
                ImGui.Text(sufficient ? "\u2713" : "\u25CB");
                ImGui.PopStyleColor();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"[i]##mat_{sourceIdx}_{matIdx}"))
            {
                _navigateToItemId = mat.ItemId;
                _navigateToSourceIdx = sourceIdx;
            }
        }

        if (showGatherBtn)
        {
            // Check per-material cooldown (keyed by ItemId, same 2s window as main Gather button)
            var now = Environment.TickCount64;
            var hasCooldown = _lastMaterialGatherTimestamp.TryGetValue(mat.ItemId, out var ts)
                && (now - ts) < GatherButtonCooldownMs;

            ImGui.SameLine();

            if (hasCooldown)
            {
                // Disabled button during cooldown — clicks are ignored
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.4f, 0.4f, 0.4f, 0.5f));
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
                ImGui.SmallButton($"Gathering...##gathering_{sourceIdx}_{matIdx}");
                ImGui.PopStyleColor(2);
            }
            else
            {
                if (ImGui.SmallButton($"Gather##gather_{sourceIdx}_{matIdx}"))
                {
                    _lastMaterialGatherTimestamp[mat.ItemId] = now;
                    TriggerGather(mat.ItemId, mat.Name);
                }
            }
        }

        if (breakdown.Count > 0)
        {
            ImGui.Indent(20f);
            ImGui.TextDisabled($"Breakdown: {string.Join(", ", breakdown.Select(kv => $"{kv.Key}: {kv.Value}"))}");
            ImGui.Unindent(20f);
        }
    }

    private void DrawCostRow(CostEntry cost, int sourceIdx, int costIdx, bool showInfoButton = true, string? prefix = null)
    {
        if (cost.ItemId == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"{prefix} {FormatNumber(cost.Count)} Gil");
        }
        else
        {
            const float IconSize = 32f;
            var have = GetItemCount(cost.ItemId);
            var sufficient = have >= cost.Count;
            var breakdown = cost.ItemId > 19 ? GetInventoryBreakdown(cost.ItemId) : new Dictionary<string, int>();

if (_textureProvider != null && cost.IconId > 0)
        {
            var iconTexture = _textureProvider.GetFromGameIcon(new GameIconLookup(cost.IconId)).GetWrapOrEmpty();
            var iconSize = new Vector2(IconSize, IconSize);
            // Vertically center icon with text
            float textHeight = ImGui.GetTextLineHeight();
            float offsetY = (textHeight - IconSize) * 0.5f;
            var cursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(cursorPos.Y + offsetY);
            ImGui.Image(iconTexture.Handle, iconSize);
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorPos.Y);
        }

            var nameColor = sufficient
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.75f, 0.25f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
            ImGui.Text($"{cost.Name} x{FormatNumber(cost.Count)}");
            ImGui.PopStyleColor();

            ImGui.SameLine();
            var countColor = sufficient
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.8f, 0.3f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, countColor);
            ImGui.Text($"({have}/{cost.Count})");
            ImGui.PopStyleColor();

            if (breakdown.Count > 0)
            {
                ImGui.Indent(20f);
                ImGui.TextDisabled($"Breakdown: {string.Join(", ", breakdown.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                ImGui.Unindent(20f);
            }

            if (showInfoButton && cost.ItemId > 19 && !string.IsNullOrEmpty(cost.Name))
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
                // Check per-cost cooldown (keyed by ItemId, same 2s window as main/material Gather buttons)
                var now = Environment.TickCount64;
                var hasCooldown = _lastCostGatherTimestamp.TryGetValue(cost.ItemId, out var ts)
                    && (now - ts) < GatherButtonCooldownMs;

                ImGui.SameLine();

                if (hasCooldown)
                {
                    // Disabled button during cooldown — clicks are ignored
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.4f, 0.4f, 0.4f, 0.5f));
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
                    ImGui.SmallButton($"Gathering...##gathering_{sourceIdx}_{costIdx}");
                    ImGui.PopStyleColor(2);
                }
                else
                {
                    if (ImGui.SmallButton($"Gather##gather_{sourceIdx}_{costIdx}"))
                    {
                        _lastCostGatherTimestamp[cost.ItemId] = now;
                        TriggerGather(cost.ItemId, cost.Name);
                    }
                }
            }
        }
    }

    private void DrawBadge(string label, Vector4 bgColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(label);
        var padX = 10f;
        var padY = 3f;
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
    {
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "👤");
        ImGui.SameLine();
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

    private void DrawDutyFinderRow(ItemSourceDetail src, int sourceIdx)
    {
        if (src.Type != ItemSourceType.Trial && src.Type != ItemSourceType.Raid && src.Type != ItemSourceType.Dungeon)
            return;
        if (!src.CfcRowId.HasValue || src.CfcName == null)
            return;
        if (CheckUnlockStatus(src.CfcRowId.Value))
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "✓ Unlocked");
        }
        if (ImGui.SmallButton($"Duty Finder##duty_{sourceIdx}"))
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
            ImGui.SameLine();
            DrawNpcRow(src, sourceIdx, -1);
        }
        var questLocked = src.QuestForUnlock.HasValue && IsQuestLockedByQuestionable(src.QuestForUnlock.Value);
        if (questLocked)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "🔒 Locked (prerequisites incomplete)");
            if (ImGui.SmallButton($"▶ Start quest chain##quest_{sourceIdx}"))
            {
                TryStartWithQuestionable(src.QuestForUnlock.Value);
            }
        }
        else if (src.QuestForUnlock.HasValue)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "✓ Quest unlocked");
        }
    }

    private void DrawVendorCard(List<ItemSourceDetail> vendors, int groupIdx, Vector4 borderColor)
    {
        var first = vendors[0];

        ImGui.Separator();
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
            ImGui.Spacing();
            ImGui.TextDisabled("Cost:");
            for (int costIdx = 0; costIdx < first.Costs.Count; costIdx++)
            {
                DrawCostRow(first.Costs[costIdx], groupIdx, costIdx, showInfoButton: false, prefix: "\u2022");
            }
        }

        ImGui.Spacing();
    }

    private bool ShouldShowGatherButton(uint itemId)
    {
        // ponytail: SimpleGatherService checks its own preconditions in StartGathering.
        return itemId > 0 && itemId < 500000;
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

    // ponytail: simple header helper to avoid duplicate TextColored+Separator
    private static void SectionHeader(string title)
    {
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), title);
        ImGui.Separator();
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

        ImGui.Separator();
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

        // ponytail: batch button dropped — SimpleGatherService is single-item; per-material
        // Gather buttons cover it. Add batch queue when actually needed.

        ImGui.Spacing();

        if (first.Materials != null && first.Materials.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Materials:");
            for (int matIdx = 0; matIdx < first.Materials.Count; matIdx++)
            {
                DrawMaterialRow(first.Materials[matIdx], groupIdx, matIdx, showCheckmark: false, prefix: "\u2022");
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
        Task.Run(async () =>
        {
            var service = _plugin?.CraftingCostService;
            _craftingResult = service != null ? await service.GetCostBreakdownAsync(itemId) : null;
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

        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Materials:");
        ImGui.Separator();
        foreach (var (name, count, marketPrice) in result.Materials.Select(m => (m.Name, m.Count, m.MarketPrice)))
        {
            var priceStr = marketPrice.HasValue ? $" @ {FormatNumber(marketPrice.Value)}" : "";
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  • {name} x{FormatNumber(count)}{priceStr}");
        }

        ImGui.Spacing();
        if (result.MarketNQPrice.HasValue)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Comparison:");
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  Market (NQ): {FormatNumber(result.MarketNQPrice.Value)} Gil");
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  Crafted cost: {FormatNumber(result.CraftedCost ?? 0)} Gil");
            if (saved.HasValue)
            {
                ImGui.TextColored(savingsColor, $"  Savings: {FormatNumber((uint)Math.Max(0, saved.Value))} Gil");
            }
        }
        else
        {
            ImGui.TextDisabled("  No market price available for comparison.");
        }
    }

    public void Dispose()
    {
    }
}
