using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace GlamSource.Core;

/// FightNo >= 0: the boss coffer of that fight (no coordinates); -1: a placed coffer with X/Y.
public sealed record GarlandCoffer(float X, float Y, IReadOnlyList<uint> ItemIds, int FightNo = -1);

public interface IGarlandInstanceService
{
    /// Treasure coffers of one instance with map coordinates and contents. Empty when Garland has
    /// no doc for it, it lists no coffers, or the request fails.
    Task<IReadOnlyList<GarlandCoffer>> GetCoffersAsync(uint instanceContentId);
}

// Garland Tools has the one thing no local sheet has: WHERE the treasure coffers stand inside a
// duty (map coordinates) and what they hold. Verified 2026-09-02: Garland's instance id is
// ContentFinderCondition.Content (Sastasha CFC 4 -> 4, Syrcus Tower CFC 102 -> 30011, Castrum
// Fluminis CFC 537 -> 20055). One request per instance per session; a failure is cached as empty
// too (ponytail: no retry logic — reopen the plugin if Garland was down).
public sealed class GarlandInstanceService : IGarlandInstanceService
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<uint, Task<IReadOnlyList<GarlandCoffer>>> _cache = new();

    public GarlandInstanceService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.Timeout = TimeSpan.FromSeconds(6);
    }

    public Task<IReadOnlyList<GarlandCoffer>> GetCoffersAsync(uint instanceContentId)
        => _cache.GetOrAdd(instanceContentId, Fetch);

    private async Task<IReadOnlyList<GarlandCoffer>> Fetch(uint id)
    {
        try
        {
            var json = await _http.GetStringAsync($"https://garlandtools.org/db/doc/instance/en/2/{id}.json").ConfigureAwait(false);
            var instance = JObject.Parse(json)["instance"];
            var list = new List<GarlandCoffer>();
            if (instance?["coffers"] is JArray coffers)
            {
                foreach (var c in coffers)
                {
                    var coords = c["coords"] as JArray;
                    var items = (c["items"] as JArray)?.Select(i => (uint)i).ToList() ?? new List<uint>();
                    if (coords == null || coords.Count < 2 || items.Count == 0) continue;
                    list.Add(new GarlandCoffer(
                        float.Parse((string)coords[0]!, CultureInfo.InvariantCulture),
                        float.Parse((string)coords[1]!, CultureInfo.InvariantCulture),
                        items));
                }
            }
            // boss coffers per fight — for trials / raids (no placed coffers) and for every duty
            // newer than the bundled LuminaSupplemental tables this IS the drop table
            if (instance?["fights"] is JArray fights)
            {
                for (var i = 0; i < fights.Count; i++)
                {
                    var items = (fights[i]["coffer"]?["items"] as JArray)?.Select(x => (uint)x).ToList() ?? new List<uint>();
                    if (items.Count > 0) list.Add(new GarlandCoffer(0, 0, items, i));
                }
            }
            return list;
        }
        catch (Exception)
        {
            return Array.Empty<GarlandCoffer>();
        }
    }
}
