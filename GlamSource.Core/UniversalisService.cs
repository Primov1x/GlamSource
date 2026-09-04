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

public interface IUniversalisService
{
    Task<MarketInfo?> GetMarketInfoAsync(uint itemId);
    Task<IReadOnlyDictionary<uint, uint>> GetBulkWorldPricesAsync(IReadOnlyCollection<uint> itemIds);
}

public sealed class UniversalisService : IUniversalisService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _worldName;
    private readonly string _dcName;
    private readonly ConcurrentDictionary<uint, MarketInfo?> _cache = new();
    // "preise fehlen mir bei items" — list rows (search results, duty drops) want a quick price
    // badge without hammering Universalis per-row. Separate lightweight cache: world min price
    // only, no DC/HQ breakdown (that detail is one click away on the full item page already).
    private readonly ConcurrentDictionary<uint, uint> _bulkPriceCache = new();
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

    public async Task<IReadOnlyDictionary<uint, uint>> GetBulkWorldPricesAsync(IReadOnlyCollection<uint> itemIds)
    {
        var ids = itemIds.Where(id => id > 0).Distinct().ToList();
        var uncached = ids.Where(id => !_bulkPriceCache.ContainsKey(id)).ToList();

        for (var i = 0; i < uncached.Count; i += BulkBatchSize)
        {
            var batch = uncached.Skip(i).Take(BulkBatchSize).ToList();
            try
            {
                await RateLimit();
                var url = $"https://universalis.app/api/v2/{_worldName}/{string.Join(",", batch)}?listings=1&entries=0";
                var json = await _httpClient.GetStringAsync(url);
                var items = JObject.Parse(json)["items"] as JObject;
                foreach (var id in batch)
                {
                    var price = items?[id.ToString()]?["minPriceNQ"]?.Value<uint>() ?? 0;
                    _bulkPriceCache[id] = price; // 0 = not marketable / no current listings, cached too so we don't re-ask
                }
            }
            catch
            {
                foreach (var id in batch) _bulkPriceCache.TryAdd(id, 0);
            }
        }

        var result = new Dictionary<uint, uint>();
        foreach (var id in ids)
            if (_bulkPriceCache.TryGetValue(id, out var price) && price > 0)
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
