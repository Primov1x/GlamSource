using System.Text.RegularExpressions;

namespace GlamSource.Core;

public interface ILodestoneEventService
{
    /// True if `eventName` appears live on the Lodestone's current Topics feed right now, false if
    /// the feed was reachable and it doesn't, null if the feed couldn't be checked at all (no claim).
    Task<bool?> IsEventActiveAsync(string eventName);
}

// Live "is this seasonal event running right now" check — nothing in Lumina or the bundled CSVs
// carries a calendar, so this scrapes the Lodestone's own Topics news page (the same site the game
// itself links players to), the closest thing to an official live source. Best-effort: any failure
// (network, markup change) returns null, never a wrong guess.
public sealed class LodestoneEventService : ILodestoneEventService
{
    private readonly HttpClient _http;
    private List<string>? _headlines; // cached for the plugin session — one fetch, not one per item
    // the Atom feed (not the HTML news page, whose CSS classes aren't documented and drift) —
    // verified reachable 2026-09-03, real <entry><title> elements, no scraping guesswork
    private static readonly Regex HeadlineRx = new("<entry>.*?<title>(?<t>[^<]+)</title>", RegexOptions.Compiled | RegexOptions.Singleline);

    public LodestoneEventService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.Timeout = TimeSpan.FromSeconds(6);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; GlamSource-Dalamud-Plugin)");
    }

    public async Task<bool?> IsEventActiveAsync(string eventName)
    {
        var headlines = await GetHeadlinesAsync().ConfigureAwait(false);
        if (headlines == null) return null;
        return headlines.Any(h => h.Contains(eventName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<string>?> GetHeadlinesAsync()
    {
        if (_headlines != null) return _headlines;
        try
        {
            var xml = await _http.GetStringAsync("https://na.finalfantasyxiv.com/lodestone/news/news.xml").ConfigureAwait(false);
            var list = HeadlineRx.Matches(xml).Select(m => m.Groups["t"].Value.Trim()).ToList();
            return _headlines = list.Count > 0 ? list : null; // an empty parse means the markup moved, not "no news"
        }
        catch (Exception)
        {
            return null;
        }
    }
}
