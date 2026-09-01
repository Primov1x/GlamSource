using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace GlamSource.Core;

public interface IItemImageService
{
    Task<string?> GetPreviewImageUrlAsync(uint itemId, string itemName);
    Task<byte[]?> GetPreviewImageBytesAsync(uint itemId, string itemName);
}

// ponytail: scrapes ffxiv.consolegameswiki.com on demand (only for items actually opened in the
// UI, not a bulk pre-scrape of ~40k items — most items never get viewed) and caches the resolved
// image URL in memory. Same HttpClient/rate-limit/ConcurrentDictionary shape as UniversalisService.
public sealed class ItemImageService : IItemImageService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, string?> _cache = new();
    private DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1);

    // ponytail: both catches used to swallow silently — a live report of "keine Bilder" with no
    // diagnostic anywhere. Most likely cause: the GAME PROCESS itself needs outbound HTTPS to
    // ffxiv.consolegameswiki.com (this HttpClient runs in-process, not the browser/CEF), which a
    // firewall/AV can block per-executable while the game's own traffic to SE stays whitelisted.
    // Exposed via /api/debug/imageerror so a failure is at least checkable after the fact.
    public string? LastError { get; private set; }

    // the wiki's infobox "worn" screenshot is a JPEG named "<Item Name>_Male.jpeg" / "_Female.jpeg"
    // (280px+ wide thumbnail); icons/banners/class-frame decorations are PNGs under 100px and don't
    // match this. Verified against Abes Jacket: real preview is "Abes_Jacket_Male.jpeg" @ 280px,
    // vs. the item icon "Abes_jacket_icon1.png" @ 40px and misc UI chrome at 20-25px.
    private static readonly Regex ImgTag = new(
        "<img[^>]+src=\"(?<src>/mediawiki/images/thumb/[^\"]+/(?<width>\\d+)px-(?<file>[^\"/]+))\"",
        RegexOptions.Compiled);

    public ItemImageService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        // .NET HttpClient sends NO User-Agent by default — Cloudflare-fronted wikis like this one
        // commonly 403 that (bot-detection heuristic, often IP-reputation-dependent — could work
        // for one person and fail for another with no other difference). Only header ever needed
        // to look like a normal browser request; harmless if this wasn't the actual cause.
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task<string?> GetPreviewImageUrlAsync(uint itemId, string itemName)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            await RateLimit();

            var underscored = itemName.Replace(' ', '_');
            var slug = Uri.EscapeDataString(underscored);
            var html = await _httpClient.GetStringAsync($"https://ffxiv.consolegameswiki.com/wiki/{slug}");

            // Bundle/set pages (e.g. "Abes Attire") embed the SAME wide promo gallery images
            // (*_img1..4.jpg, 400px) on every individual piece's page — "widest image wins" always
            // picked those over the actual per-item "<Name>_Male.jpeg" portrait (280px), so every
            // piece in a set returned byte-identical images (live: 16042/16043 both 36049 bytes,
            // confirmed as the shared Abes_Attire_img4.jpg, not the distinct real portraits).
            // The item's own worn-portrait filename always starts with the item's name — match on
            // that FIRST, only fall back to widest-non-icon if no name match exists on the page.
            string? best = null;
            var bestWidth = 0;
            string? nameMatch = null;
            foreach (Match m in ImgTag.Matches(html))
            {
                var file = m.Groups["file"].Value;
                if (file.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(m.Groups["width"].Value, out var width)) continue;
                if (width < 150) continue; // UI chrome (class frames, store badges) tops out ~25px
                if (file.StartsWith(underscored, StringComparison.OrdinalIgnoreCase))
                    nameMatch ??= "https://ffxiv.consolegameswiki.com" + m.Groups["src"].Value;
                if (width <= bestWidth) continue;
                bestWidth = width;
                best = "https://ffxiv.consolegameswiki.com" + m.Groups["src"].Value;
            }

            best = nameMatch ?? best;
            _cache[itemId] = best;
            return best;
        }
        catch (Exception ex)
        {
            LastError = $"{DateTime.Now:HH:mm:ss} url-lookup '{itemName}': {ex.Message}";
            _cache[itemId] = null;
            return null;
        }
    }

    // ponytail: used by ItemDetailWindow (ImGui) to decode a real texture via
    // ITextureProvider.CreateFromImageAsync — the web UI just hotlinks the URL in an <img> tag,
    // ImGui needs the actual bytes. Separate rate-limited GET, same cache-key convention.
    public async Task<byte[]?> GetPreviewImageBytesAsync(uint itemId, string itemName)
    {
        var url = await GetPreviewImageUrlAsync(itemId, itemName);
        if (url == null) return null;

        try
        {
            await RateLimit();
            return await _httpClient.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            LastError = $"{DateTime.Now:HH:mm:ss} image-fetch '{itemName}': {ex.Message}";
            return null;
        }
    }

    private async Task RateLimit()
    {
        var elapsed = DateTime.UtcNow - _lastRequestTime;
        if (elapsed < MinRequestInterval)
            await Task.Delay(MinRequestInterval - elapsed);
        _lastRequestTime = DateTime.UtcNow;
    }

    public void Dispose() => _httpClient?.Dispose();
}
