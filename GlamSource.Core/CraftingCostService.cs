using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlamSource.Core;

public interface ICraftingCostService
{
    Task<CraftingCostResult?> GetCostBreakdownAsync(uint itemId);
}

public record MaterialCost(
    uint ItemId,
    string Name,
    uint Count,
    uint IconId,
    uint? MarketPrice);

public record CraftingCostResult(
    IReadOnlyList<MaterialCost> Materials,
    uint? MarketNQPrice,
    uint? CraftedCost);

public sealed class CraftingCostService : ICraftingCostService, IDisposable
{
    private readonly IItemDetailService _itemDetailService;
    private readonly IUniversalisService _universalisService;
    private const int CacheTtlMinutes = 5;

    // ponytail: simple TTL cache; per-entry locks if concurrency becomes a problem
    private readonly ConcurrentDictionary<uint, (CraftingCostResult result, DateTime expires)> _cache = new();
    private readonly object _cacheLock = new();

    public CraftingCostService(IItemDetailService itemDetailService, IUniversalisService universalisService)
    {
        _itemDetailService = itemDetailService ?? throw new ArgumentNullException(nameof(itemDetailService));
        _universalisService = universalisService ?? throw new ArgumentNullException(nameof(universalisService));
    }

    public bool TryGetCachedResult(uint itemId, out CraftingCostResult? result)
    {
        if (_cache.TryGetValue(itemId, out var cached) && cached.expires > DateTime.UtcNow)
        {
            result = cached.result;
            return true;
        }
        result = null;
        return false;
    }

    public async Task<CraftingCostResult?> GetCostBreakdownAsync(uint itemId)
    {
        // Fast path: check cache without locking
        if (_cache.TryGetValue(itemId, out var cached) && cached.expires > DateTime.UtcNow)
            return cached.result;

        var result = await ComputeAsync(itemId);

        // ponytail: write-through cache; race between concurrent callers is benign — stale writes are overwritten
        lock (_cacheLock)
        {
            // Double-check: another caller may have cached while we were computing
            if (!_cache.TryGetValue(itemId, out var existing) || existing.expires <= DateTime.UtcNow)
            {
                _cache[itemId] = (result!, DateTime.UtcNow.AddMinutes(CacheTtlMinutes));
            }
        }

        return result;
    }

    private async Task<CraftingCostResult?> ComputeAsync(uint itemId)
    {
        var detail = _itemDetailService.GetDetail(itemId);
        if (detail == null)
            return null;

        var craftedSource = detail.Sources.FirstOrDefault(s => s.Type == ItemSourceType.Crafted && s.Materials != null);
        if (craftedSource == null || craftedSource.Materials == null || craftedSource.Materials.Count == 0)
            return null;

        var materials = new List<MaterialCost>();
        uint totalCraftedCost = 0;

        // Fetch market prices in parallel
        var priceTasks = craftedSource.Materials!.Select(m =>
            _universalisService.GetMarketInfoAsync(m.ItemId).ContinueWith(t =>
                (m, price: t.Result?.WorldMinPrice ?? 0)));

        var priceResults = await Task.WhenAll(priceTasks);

        foreach (var (material, marketPrice) in priceResults)
        {
            var cost = marketPrice * material.Count;
            totalCraftedCost += cost;
            materials.Add(new MaterialCost(
                material.ItemId,
                material.Name,
                material.Count,
                material.IconId,
                marketPrice > 0 ? marketPrice : null));
        }

        // ponytail: crafted cost approximated as sum of material market prices
        // A more accurate value would need Gil/recipe-level multipliers from the game sheets
        return new CraftingCostResult(materials, null, totalCraftedCost);
    }

    public void Dispose()
    {
        // No unmanaged resources to dispose
    }
}
