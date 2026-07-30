using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource.Core;

namespace GlamSource.Windows;

public class ItemDetailWindow : Window, IDisposable
{
    private readonly IItemDetailService _detailService;
    private readonly IItemSourceService _sourceService;
    private readonly IUniversalisService _universalisService;
    private readonly Stack<uint> _history = new();
    private uint? _showingItemId;
    private bool _isOpen;
    private uint? _navigateToItemId;
    private int _navigateToSourceIdx = -1;

    private MarketInfo? _marketInfo;
    private bool _marketLoading;
    private uint _marketItemId;

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

    public ItemDetailWindow(IItemDetailService detailService, IItemSourceService sourceService, IUniversalisService universalisService)
        : base($"Item Detail###ItemDetailWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _detailService = detailService;
        _sourceService = sourceService;
        _universalisService = universalisService;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 250),
            MaximumSize = new Vector2(700, float.MaxValue)
        };
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
        ImGui.Separator();
        ImGui.Spacing();

        DrawMarketPrices(detail);
        ImGui.Spacing();
        DrawSources(detail);
    }

    private void DrawMarketPrices(ItemDetail detail)
    {
        if (!detail.IsMarketable)
            return;

        if (_marketLoading && _marketItemId == detail.ItemId)
        {
            ImGui.TextDisabled("Loading market prices...");
            return;
        }

        if (_marketInfo == null)
            return;

        var yellow = new Vector4(1f, 0.84f, 0f, 1f);
        ImGui.TextColored(yellow, "Market Prices:");

        if (_marketInfo.WorldMinPrice > 0)
            ImGui.TextColored(yellow, $"  Shiva: {FormatNumber(_marketInfo.WorldMinPrice)} Gil (NQ) / {FormatNumber(_marketInfo.WorldMinPriceHQ)} Gil (HQ)");
        if (_marketInfo.DcMinPrice > 0)
            ImGui.TextColored(yellow, $"  Light DC cheapest: {FormatNumber(_marketInfo.DcMinPrice)} Gil (NQ) on {_marketInfo.DcWorldName ?? "unknown"}");
        if (_marketInfo.DcMinPriceHQ > 0)
            ImGui.TextColored(yellow, $"  Light DC cheapest HQ: {FormatNumber(_marketInfo.DcMinPriceHQ)} Gil on {_marketInfo.DcWorldName ?? "unknown"}");
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

        ImGui.SameLine();
        if (ImGui.SmallButton($"Wiki##wiki_{detail.ItemId}"))
        {
            OpenWiki(detail.Name, detail.ItemId);
        }

        if (detail.IsMarketable)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Market Prices##market_{detail.ItemId}"))
            {
                OpenMarketPrices(detail.ItemId);
            }
        }
    }

    private static void OpenWiki(string itemName, uint itemId)
    {
        var name = itemName.Replace(" ", "_");
        var url = $"https://ffxiv.consolegameswiki.com/wiki/{name}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine($"[WIKI] {url}"); }
    }

    private static void OpenMarketPrices(uint itemId)
    {
        var url = $"https://universalis.app/market/{itemId}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine($"[MARKET] {url}"); }
    }

    private static void OpenRecipeBook(uint itemId)
    {
        var url = $"https://garlandtools.org/db/#item/{itemId}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine($"[RECIPEBOOK] {url}"); }
    }

    private void DrawSources(ItemDetail detail)
    {
        if (detail.Sources.Count == 0)
        {
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);
            ImGui.TextColored(grey, "No vendor/crafting source found.");
            ImGui.TextColored(grey, "This item may drop from duties, raids,");
            ImGui.TextColored(grey, "or other content.");
            return;
        }

        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), "Sources:");
        ImGui.Spacing();

        for (int i = 0; i < detail.Sources.Count; i++)
        {
            DrawSourceDetail(detail.Sources[i], i);
            ImGui.Spacing();
        }
    }

    private void DrawSourceDetail(ItemSourceDetail src, int sourceIdx)
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
                var status = mat.ItemId > 19 ? GetInventoryStatus(mat.ItemId, (int)mat.Count) : "";
                ImGui.TextDisabled($"      \u2022 {mat.Name} x{FormatNumber(mat.Count)}{status}");
            }
            ImGui.Unindent(20f);

            // Open Crafting Log button
            if (src.Type == ItemSourceType.Crafted)
            {
                ImGui.Indent(20f);
                if (ImGui.SmallButton("Open Crafting Log##crafting"))
                {
                    TryOpenCraftingLog(_showingItemId ?? 0);
                }
                ImGui.Unindent(20f);
            }
        }

        // Vendor costs
        if (src.Costs != null && src.Costs.Count > 0)
        {
            ImGui.Indent(20f);
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
                    ImGui.TextDisabled($"      \u2022 {FormatNumber(cost.Count)} {cost.Name}{status}");

                    var showInfoButton = cost.ItemId > 19
                        && !string.IsNullOrEmpty(cost.Name);

                    if (showInfoButton)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"[i]##cost_{costIdx}"))
                        {
                            _navigateToItemId = cost.ItemId;
                            _navigateToSourceIdx = sourceIdx;
                        }
                    }
                }
                costIdx++;
            }
            ImGui.Unindent(20f);
        }

        // Zone name + Coords + Map button
        if (!string.IsNullOrEmpty(src.ZoneName) || (src.MapX.HasValue && src.MapY.HasValue))
        {
            ImGui.Indent(20f);
            var zoneStr = src.ZoneName ?? "Unknown Zone";
            var coordStr = (src.MapX.HasValue && src.MapY.HasValue)
                ? $" ({src.MapX:F1}, {src.MapY:F1})"
                : "";
            ImGui.TextDisabled($"    Zone: {zoneStr}{coordStr}");

            if (src.MapX.HasValue && src.MapY.HasValue)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Open Map##map_{src.Type}"))
                {
                    TryOpenMap(src.TerritoryTypeId, src.MapId, src.MapX.Value, src.MapY.Value);
                }
            }
            ImGui.Unindent(20f);
        }

        // Quest requirements
        if (!string.IsNullOrEmpty(src.QuestName))
        {
            ImGui.Indent(20f);
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), $"    Requires: Quest \"{src.QuestName}\"");
            ImGui.Unindent(20f);
        }

        // Questionable quest button (Feature D)
        if (src.Type == ItemSourceType.Quest)
        {
            ImGui.Indent(20f);
            if (ImGui.SmallButton("Start with Questionable"))
            {
                TryStartWithQuestionable();
            }
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

    private static void TryStartWithQuestionable()
    {
        Console.WriteLine("[QUESTIONABLE] IPC stub — plugin not found");
    }

    private static void TryOpenMap(uint? territoryTypeId, uint? mapId, float mapX, float mapY)
    {
        Console.WriteLine($"[MAP] Territory={territoryTypeId} Map={mapId} ({mapX:F1}, {mapY:F1})");
    }

    private static string FormatNumber(uint value)
    {
        return value.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("N1", CultureInfo.GetCultureInfo("de-DE"));
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
            };

            foreach (var type in containers)
            {
                var container = im->GetInventoryContainer(type);
                if (container == null)
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

    public void Dispose()
    {
    }
}
