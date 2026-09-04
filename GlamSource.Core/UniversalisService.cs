using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GlamSource.Core;

public record MarketInfo(
    uint WorldMinPrice,
    uint WorldMinPriceHQ,
    uint DcMinPrice,
    uint DcMinPriceHQ,
    string? DcWorldName);

// list-row badge shape — just enough to answer "what do I pay here, what's the cheapest on the DC"
public record BulkPrice(uint WorldMinPrice, uint DcMinPrice);

public interface IUniversalisService
{
    Task<MarketInfo?> GetMarketInfoAsync(uint itemId);
    Task<IReadOnlyDictionary<uint, BulkPrice>> GetBulkWorldPricesAsync(IReadOnlyCollection<uint> itemIds);
}

public sealed class UniversalisService : IUniversalisService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _worldName;
    private readonly string _dcName;
    private readonly ConcurrentDictionary<uint, MarketInfo?> _cache = new();
    // "preise fehlen mir bei items" — list rows (search results, duty drops) want a quick price
    // badge without hammering Universalis per-row: current server + DC-wide cheapest, no HQ
    // breakdown (that detail is one click away on the full item page already).
    private readonly ConcurrentDictionary<uint, BulkPrice> _bulkPriceCache = new();
    private DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly long MinRequestIntervalTicks = TimeSpan.FromSeconds(1).Ticks;
    // Universalis' own documented cap on items per multi-item request.
    private const int BulkBatchSize = 100;

    public UniversalisService(HttpClient httpClient, string worldName = "Shiva", string dcName = "Light")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _worldName = worldName;
        _dcName = dcName;
    }

    public async Task<MarketInfo?> GetMarketInfoAsync(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            await RateLimit();

            var worldUrl = $"https://universalis.app/api/v2/{_worldName}/{itemId}?listings=1&entries=0";
            var worldJson = await _httpClient.GetStringAsync(worldUrl);

            await RateLimit();

            var dcUrl = $"https://universalis.app/api/v2/{_dcName}/{itemId}?listings=1&entries=0";
            var dcJson = await _httpClient.GetStringAsync(dcUrl);

            var worldData = JObject.Parse(worldJson);
            var dcData = JObject.Parse(dcJson);

            var result = new MarketInfo(
                worldData["minPriceNQ"]?.Value<uint>() ?? 0,
                worldData["minPriceHQ"]?.Value<uint>() ?? 0,
                dcData["minPriceNQ"]?.Value<uint>() ?? 0,
                dcData["minPriceHQ"]?.Value<uint>() ?? 0,
                dcData["listings"]?[0]?["worldName"]?.Value<string>());

            _cache[itemId] = result;
            return result;
        }
        catch
        {
            _cache[itemId] = null;
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<uint, BulkPrice>> GetBulkWorldPricesAsync(IReadOnlyCollection<uint> itemIds)
    {
        var ids = itemIds.Where(id => id > 0).Distinct().ToList();
        var uncached = ids.Where(id => !_bulkPriceCache.ContainsKey(id)).ToList();

        for (var i = 0; i < uncached.Count; i += BulkBatchSize)
        {
            var batch = uncached.Skip(i).Take(BulkBatchSize).ToList();
            var joined = string.Join(",", batch);
            JObject? worldItems = null, dcItems = null;
            try
            {
                await RateLimit();
                var worldJson = await _httpClient.GetStringAsync($"https://universalis.app/api/v2/{_worldName}/{joined}?listings=1&entries=0");
                worldItems = JObject.Parse(worldJson)["items"] as JObject;
            }
            catch { /* world side failed — still try DC below, batch just gets 0 for world */ }
            try
            {
                await RateLimit();
                var dcJson = await _httpClient.GetStringAsync($"https://universalis.app/api/v2/{_dcName}/{joined}?listings=1&entries=0");
                dcItems = JObject.Parse(dcJson)["items"] as JObject;
            }
            catch { /* same for DC */ }

            foreach (var id in batch)
            {
                var worldPrice = worldItems?[id.ToString()]?["minPriceNQ"]?.Value<uint>() ?? 0;
                var dcPrice = dcItems?[id.ToString()]?["minPriceNQ"]?.Value<uint>() ?? 0;
                _bulkPriceCache[id] = new BulkPrice(worldPrice, dcPrice); // (0,0) = not marketable / no listings, cached too so we don't re-ask
            }
        }

        var result = new Dictionary<uint, BulkPrice>();
        foreach (var id in ids)
            if (_bulkPriceCache.TryGetValue(id, out var price) && (price.WorldMinPrice > 0 || price.DcMinPrice > 0))
                result[id] = price;
        return result;
    }

    private async Task RateLimit()
    {
        var elapsed = DateTime.UtcNow - _lastRequestTime;
        if (elapsed.TotalSeconds < 1.0)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.0 - elapsed.TotalSeconds));
        }
        _lastRequestTime = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
