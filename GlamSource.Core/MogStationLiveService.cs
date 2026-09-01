using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;

namespace GlamSource.Core;

// ponytail: "heute kam neue glam in den mog... kriegen wir das immer aktuell?" — the old
// MogstationItems.csv was a ONE-TIME static scrape, baked into the DLL at build time; a brand-new
// item never showed up until someone manually re-scraped and shipped a new release. Replaced with
// a live lookup against Gamer Escape's own MediaWiki category API (structured JSON, no HTML
// scraping — found via action=query&list=allcategories: "Mog Station Sold Item" is the real
// category name). Known limit, confirmed live: the wiki itself lags behind same-day patch
// releases too (tested "Yozakura's Joi", item 52409 the day it launched — not in the category
// yet either) — a live lookup can only be as fresh as the source it reads from.
public sealed class MogStationLiveService : IDisposable
{
    private const string ApiBase = "https://ffxiv.gamerescape.com/w/api.php";
    private const string Category = "Mog Station Sold Item";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly HttpClient _httpClient;
    // item NAME (as the wiki titles it) -> wiki page title (for the deep link — the old CSV's
    // "ShopUrl" was always just the generic store front page, never a real per-item URL)
    private readonly ConcurrentDictionary<string, string> _members = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefresh = DateTime.MinValue;
    private Task? _refreshTask;
    public string? LastError { get; private set; }

    public MogStationLiveService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    /// <summary>Non-blocking: kicks off a background refresh if the cache is empty/stale, returns
    /// whatever's cached RIGHT NOW (empty on the very first call this session — fills in shortly
    /// after, subsequent lookups for other items pick it up automatically once loaded).</summary>
    public bool TryGetShopUrl(string englishName, out string? wikiUrl)
    {
        EnsureFresh();
        if (_members.TryGetValue(englishName, out var title))
        {
            wikiUrl = "https://ffxiv.gamerescape.com/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));
            return true;
        }
        wikiUrl = null;
        return false;
    }

    private void EnsureFresh()
    {
        if (DateTime.UtcNow - _lastRefresh < RefreshInterval) return;
        if (_refreshTask is { IsCompleted: false }) return; // already refreshing
        _lastRefresh = DateTime.UtcNow; // stamp before the fetch so a slow/failed run doesn't retry every call
        _refreshTask = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        try
        {
            string? cmcontinue = null;
            var fresh = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                var url = $"{ApiBase}?action=query&list=categorymembers&cmtitle=Category:{Uri.EscapeDataString(Category)}" +
                           $"&cmlimit=500&format=json" + (cmcontinue != null ? $"&cmcontinue={Uri.EscapeDataString(cmcontinue)}" : "");
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("query", out var query) &&
                    query.TryGetProperty("categorymembers", out var members))
                {
                    foreach (var m in members.EnumerateArray())
                    {
                        var title = m.GetProperty("title").GetString();
                        if (string.IsNullOrEmpty(title)) continue;
                        fresh[title] = title; // display name == wiki title on this wiki (no redirects observed)
                    }
                }
                cmcontinue = doc.RootElement.TryGetProperty("continue", out var cont) &&
                             cont.TryGetProperty("cmcontinue", out var cc) ? cc.GetString() : null;
            } while (cmcontinue != null);

            _members.Clear();
            foreach (var (k, v) in fresh) _members[k] = v;
        }
        catch (Exception ex)
        {
            LastError = $"{DateTime.Now:HH:mm:ss} {ex.Message}";
            // keep whatever was cached before — a failed refresh shouldn't wipe a working cache
        }
    }

    public void Dispose() => _httpClient?.Dispose();
}
