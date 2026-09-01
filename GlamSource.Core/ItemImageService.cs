using System;
using System.Collections.Concurrent;
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
    }

    public async Task<string?> GetPreviewImageUrlAsync(uint itemId, string itemName)
    {
        if (_cache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            await RateLimit();

            var slug = Uri.EscapeDataString(itemName.Replace(' ', '_'));
            var html = await _httpClient.GetStringAsync($"https://ffxiv.consolegameswiki.com/wiki/{slug}");

            string? best = null;
            var bestWidth = 0;
            foreach (Match m in ImgTag.Matches(html))
            {
                var file = m.Groups["file"].Value;
                if (file.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(m.Groups["width"].Value, out var width)) continue;
                if (width < 150) continue; // UI chrome (class frames, store badges) tops out ~25px
                if (width <= bestWidth) continue;
                bestWidth = width;
                best = "https://ffxiv.consolegameswiki.com" + m.Groups["src"].Value;
            }

            _cache[itemId] = best;
            return best;
        }
        catch
        {
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
        catch
        {
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
