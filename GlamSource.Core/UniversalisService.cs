using System;
using System.Collections.Concurrent;
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
}

public sealed class UniversalisService : IUniversalisService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _worldName;
    private readonly string _dcName;
    private readonly ConcurrentDictionary<uint, MarketInfo?> _cache = new();
    private DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly long MinRequestIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

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
