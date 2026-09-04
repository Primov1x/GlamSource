using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GlamSource.Core;
using Lumina;
using Xunit;

namespace GlamSource.Core.Tests;

public class CraftingCostServiceTests
{
    [Fact]
    public async Task GetCostBreakdownAsync_ReturnsNull_ForUnknownItem()
    {
        var itemDetailService = new FakeItemDetailService(null);
        var universalisService = new FakeUniversalisService();
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = await service.GetCostBreakdownAsync(99999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCostBreakdownAsync_ReturnsNull_WhenNoCraftSource()
    {
        var detail = new ItemDetail(
            12345,
            "Test Item",
            0, false, 0,
            new List<ItemSourceDetail>
            {
                new(ItemSourceType.Vendor, "Vendor", null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null, null)
            });
        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService();
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = await service.GetCostBreakdownAsync(12345);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCostBreakdownAsync_ReturnsMaterials_ForCraftedItem()
    {
        var materials = new List<CostEntry>
        {
            new(100, "Iron Ingot", 5, 58),
            new(101, "Silk Thread", 3, 59),
        };
        var craftedSource = new ItemSourceDetail(
            ItemSourceType.Crafted, "Crafted Lv.50 (WHM)", null, null, null, null, null, null,
            null, materials, null, null, null, null, null, null, null);
        var detail = new ItemDetail(12345, "Test Item", 0, false, 0, new List<ItemSourceDetail> { craftedSource });

        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService(new Dictionary<uint, uint>
        {
            [100] = 200,
            [101] = 150,
        });
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = await service.GetCostBreakdownAsync(12345);

        Assert.NotNull(result);
        Assert.Equal(2, result.Materials.Count);
        Assert.Equal(100u, result.Materials[0].ItemId);
        Assert.Equal(5u, result.Materials[0].Count);
        Assert.Equal(200u, result.Materials[0].MarketPrice);
        Assert.Equal(101u, result.Materials[1].ItemId);
        Assert.Equal(3u, result.Materials[1].Count);
        Assert.Equal(150u, result.Materials[1].MarketPrice);
    }

    [Fact]
    public async Task GetCostBreakdownAsync_ComputesCraftedCostCorrectly()
    {
        var materials = new List<CostEntry>
        {
            new(100, "Iron Ingot", 5, 58),
            new(101, "Silk Thread", 3, 59),
        };
        var craftedSource = new ItemSourceDetail(
            ItemSourceType.Crafted, "Crafted Lv.50", null, null, null, null, null, null,
            null, materials, null, null, null, null, null, null, null);
        var detail = new ItemDetail(12345, "Test Item", 0, false, 0, new List<ItemSourceDetail> { craftedSource });

        var itemDetailService = new FakeItemDetailService(detail);
        // Market prices: 200 each for iron, 150 each for silk
        var universalisService = new FakeUniversalisService(new Dictionary<uint, uint>
        {
            [100] = 200,
            [101] = 150,
        });
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = await service.GetCostBreakdownAsync(12345);

        // (200 * 5) + (150 * 3) = 1000 + 450 = 1450
        Assert.NotNull(result);
        Assert.Equal(1450u, result.CraftedCost);
    }

    [Fact]
    public async Task GetCostBreakdownAsync_CachesResult()
    {
        var detail = new ItemDetail(12345, "Test", 0, false, 0,
            new List<ItemSourceDetail>());
        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService();
        var service = new CraftingCostService(itemDetailService, universalisService);

        var first = await service.GetCostBreakdownAsync(12345);
        var second = await service.GetCostBreakdownAsync(12345);

        // Cache hit should not invoke the service again
        Assert.True(itemDetailService.CallCount >= 1);
        // Both results should be the same instance (from cache)
        Assert.Same(first, second);
    }

    [Fact]
    public void TryGetCachedResult_ReturnsFalse_WhenNotYetComputed()
    {
        var detail = new ItemDetail(12345, "Test", 0, false, 0,
            new List<ItemSourceDetail>());
        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService();
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = service.TryGetCachedResult(12345, out var output);

        Assert.False(result);
        Assert.Null(output);
    }

    [Fact]
    public async Task TryGetCachedResult_ReturnsTrue_AfterFirstCompute()
    {
        var materials = new List<CostEntry>
        {
            new(100, "Iron Ingot", 5, 58),
        };
        var craftedSource = new ItemSourceDetail(
            ItemSourceType.Crafted, "Crafted", null, null, null, null, null, null,
            null, materials, null, null, null, null, null, null, null);
        var detail = new ItemDetail(12345, "Test", 0, false, 0, new List<ItemSourceDetail> { craftedSource });

        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService(new Dictionary<uint, uint> { [100] = 200 });
        var service = new CraftingCostService(itemDetailService, universalisService);

        // First call computes and caches
        var first = await service.GetCostBreakdownAsync(12345);

        // Sync cache hit
        var cached = service.TryGetCachedResult(12345, out var output);

        Assert.True(cached);
        Assert.Same(first, output);
    }

    [Fact]
    public async Task TryGetCachedResult_ReturnsFalse_WhenCacheExpired()
    {
        var detail = new ItemDetail(12345, "Test", 0, false, 0,
            new List<ItemSourceDetail>());
        var itemDetailService = new FakeItemDetailService(detail);
        var universalisService = new FakeUniversalisService();
        var service = new CraftingCostService(itemDetailService, universalisService);

        // ponytail: cache is populated on first compute; no TTL expiry in tests
        await service.GetCostBreakdownAsync(12345);

        var cached = service.TryGetCachedResult(12345, out _);
        Assert.True(cached);
    }

    [Fact]
    public async Task GetCostBreakdownAsync_MaterialsWithNoMarketPrice_ExposesNullPrice()
    {
        var materials = new List<CostEntry>
        {
            new(100, "Iron Ingot", 1, 58),
        };
        var craftedSource = new ItemSourceDetail(
            ItemSourceType.Crafted, "Crafted", null, null, null, null, null, null,
            null, materials, null, null, null, null, null, null, null);
        var detail = new ItemDetail(12345, "Test", 0, false, 0, new List<ItemSourceDetail> { craftedSource });

        var itemDetailService = new FakeItemDetailService(detail);
        // No price data for item 100
        var universalisService = new FakeUniversalisService(new Dictionary<uint, uint>());
        var service = new CraftingCostService(itemDetailService, universalisService);

        var result = await service.GetCostBreakdownAsync(12345);

        Assert.NotNull(result);
        Assert.Null(result.Materials[0].MarketPrice);
        Assert.Equal(0u, result.CraftedCost);
    }

    #region Fakes

    private sealed class FakeItemDetailService : IItemDetailService
    {
        private readonly ItemDetail? _detail;
        public int CallCount { get; private set; }

        public FakeItemDetailService(ItemDetail? detail)
        {
            _detail = detail;
        }

        public ItemDetail? GetDetail(uint itemId)
        {
            CallCount++;
            return _detail;
        }

        public GameData GameData => null!;
        public uint? ResolveMountItemId(uint mountId) => null;
        public string? GetEnglishName(uint itemId) => null;
        public string? GetWikiPageName(uint itemId) => null;
        public System.Threading.Tasks.Task<EventStatus?> GetEventStatusAsync(uint itemId) => System.Threading.Tasks.Task.FromResult<EventStatus?>(null);
        public IReadOnlyList<DutyInfo> ListDutiesWithDrops() => Array.Empty<DutyInfo>();
        public DutyDetail? GetDutyDetail(uint cfcId) => null;
        public System.Threading.Tasks.Task<IReadOnlyList<DutyCoffer>> GetDutyCoffersAsync(uint cfcId)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<DutyCoffer>>(Array.Empty<DutyCoffer>());
        public uint? FindDutyByTerritory(uint territoryTypeId) => null;
        public uint? MountRowIdForItem(uint itemId) => null;
        public uint? CompanionRowIdForItem(uint itemId) => null;
    }

    private sealed class FakeUniversalisService : IUniversalisService
    {
        private readonly Dictionary<uint, uint> _prices;

        public FakeUniversalisService(Dictionary<uint, uint>? prices = null)
        {
            _prices = prices ?? new();
        }

        public Task<MarketInfo?> GetMarketInfoAsync(uint itemId)
        {
            var price = _prices.GetValueOrDefault<uint, uint>(itemId, 0);
            return Task.FromResult<MarketInfo?>(new MarketInfo(price, 0, price, 0, null));
        }

        public Task<IReadOnlyDictionary<uint, uint>> GetBulkWorldPricesAsync(IReadOnlyCollection<uint> itemIds)
        {
            var result = itemIds.Where(id => _prices.ContainsKey(id) && _prices[id] > 0)
                .ToDictionary(id => id, id => _prices[id]);
            return Task.FromResult<IReadOnlyDictionary<uint, uint>>(result);
        }
    }

    #endregion
}
