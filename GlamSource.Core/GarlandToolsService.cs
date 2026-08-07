using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GlamSource.Core;

public record GarlandItemInfo(
    uint ItemId,
    string Name,
    IReadOnlyList<uint> Models,
    IReadOnlyList<uint> Instances,
    IReadOnlyList<uint> Treasure,
    IReadOnlyList<uint> Loot,
    IReadOnlyList<string> SourceTypes);

public interface IGarlandToolsService
{
    Task<GarlandItemInfo?> GetItemInfoAsync(uint itemId);
}

public sealed class GarlandToolsService : IGarlandToolsService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, GarlandItemInfo?> _cache = new();
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private bool _disposed;

    public GarlandToolsService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<GarlandItemInfo?> GetItemInfoAsync(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        if (_disposed)
            return null;

        await _rateLimiter.WaitAsync();
        try
        {
            if (_cache.TryGetValue(itemId, out cached))
                return cached;

            var url = $"https://www.garlandtools.org/db/doc/item/en/3/{itemId}.json";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _cache[itemId] = null;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var info = ParseItemInfo(itemId, json);

            if (info == null)
            {
                _cache[itemId] = null;
                return null;
            }

            _cache[itemId] = info;
            return info;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static GarlandItemInfo? ParseItemInfo(uint itemId, string json)
    {
        try
        {
            var root = JObject.Parse(json);
            var item = root["item"] as JObject;
            if (item == null)
                return null;

            var name = item["name"]?.ToString() ?? string.Empty;
            var models = ParseIdList(item["models"]);
            var instances = ParseIdList(item["instances"]);
            var treasure = ParseIdList(item["treasure"]);
            var loot = ParseIdList(item["loot"]);
            var sourceTypes = item["alla"]?.Value<JObject>()?["source"]?.Values<string>().Cast<string>().ToList() ?? new List<string>();

            return new GarlandItemInfo(
                itemId,
                name,
                models.AsReadOnly(),
                instances.AsReadOnly(),
                treasure.AsReadOnly(),
                loot.AsReadOnly(),
                sourceTypes.AsReadOnly());
        }
        catch
        {
            return null;
        }
    }

    private static List<uint> ParseIdList(JToken? token)
    {
        if (token == null)
            return new List<uint>();

        return token.Values<uint>().ToList();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rateLimiter.Dispose();
        _httpClient?.Dispose();
    }
}
