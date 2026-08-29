using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using GlamSource.Core;
using GlamSource.Windows;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlamSource.Services;

// ponytail: optional HTML alternative UI (localhost only). Feature parity with the ImGui shell:
// search, item detail with sources, character snapshot, plus in-game actions (crafting log,
// map marker, duty finder) marshalled through the Framework thread. Off by default.
public sealed class WebUiService : IDisposable
{
    private const string Prefix = "http://127.0.0.1:23424/";

    private readonly IItemDetailService _detail;
    private readonly IGlamourService _glamour;
    private readonly GlamSourceShellWindow _shell;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WebUiService(IItemDetailService detail, IGlamourService glamour, GlamSourceShellWindow shell, IFramework framework, IPluginLog log)
    {
        _detail = detail;
        _glamour = glamour;
        _shell = shell;
        _framework = framework;
        _log = log;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && _listener == null) Start();
        else if (!enabled && _listener != null) Stop();
    }

    private void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => Loop(_cts.Token));
            _log.Information($"[WebUi] listening on {Prefix}");
        }
        catch (Exception ex)
        {
            _log.Error($"[WebUi] failed to start: {ex.Message}");
            _listener = null;
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _cts = null;
    }

    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            try { HandleRequest(ctx); }
            catch (Exception ex) { _log.Error($"[WebUi] request error: {ex.Message}"); }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        if (method == "GET" && (path == "/" || path == "/ui"))
        {
            WriteHtml(ctx, WebUiPage.Html);
            return;
        }

        if (method == "GET" && path == "/api/search")
        {
            var q = ctx.Request.QueryString["q"] ?? "";
            var itemSheet = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            object result = q.Length < 3
                ? Array.Empty<object>()
                : _glamour.SearchItems(q).Take(30)
                    .Select(r => new { r.id, r.name, iconId = (uint)(itemSheet?.GetRowOrDefault(r.id)?.Icon ?? 0) })
                    .ToArray();
            WriteJson(ctx, result);
            return;
        }

        if (method == "GET" && path.StartsWith("/api/item/") && uint.TryParse(path["/api/item/".Length..], out var itemId))
        {
            var detail = _detail.GetDetail(itemId);
            if (detail == null) { WriteJson(ctx, new { error = "not found" }, 404); return; }
            WriteJson(ctx, detail);
            return;
        }

        if (method == "GET" && path == "/api/snapshot")
        {
            WriteJson(ctx, new
            {
                slots = _shell.DebugSnapshot,
                activeRecentName = _shell.DebugActiveRecentName,
                isRecentOverrideActive = _shell.DebugIsRecentOverrideActive,
            });
            return;
        }

        if (method == "POST" && path.StartsWith("/api/action/craftlog/") && uint.TryParse(path["/api/action/craftlog/".Length..], out var craftId))
        {
            // HQ ids sit at NQ RowId + 1_000_000; the recipe log only knows the NQ id.
            var nq = craftId >= 1_000_000 ? craftId - 1_000_000 : craftId;
            _framework.RunOnFrameworkThread(() => ItemDetailWindow.TryOpenCraftingLog(nq));
            WriteJson(ctx, new { ok = true });
            return;
        }

        if (method == "POST" && path.StartsWith("/api/action/dutyfinder/") && uint.TryParse(path["/api/action/dutyfinder/".Length..], out var cfcId))
        {
            _framework.RunOnFrameworkThread(() => ItemDetailWindow.TryOpenDutyFinder(cfcId));
            WriteJson(ctx, new { ok = true });
            return;
        }

        if (method == "POST" && path == "/api/action/map")
        {
            var qs = ctx.Request.QueryString;
            if (uint.TryParse(qs["territory"], out var territory) && uint.TryParse(qs["map"], out var mapId)
                && float.TryParse(qs["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && float.TryParse(qs["y"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
            {
                _framework.RunOnFrameworkThread(() =>
                {
                    try { Plugin.GameGui.OpenMapWithMapLink(new MapLinkPayload(territory, mapId, x, y)); }
                    catch (Exception ex) { _log.Error($"[WebUi] map open failed: {ex.Message}"); }
                });
                WriteJson(ctx, new { ok = true });
            }
            else
            {
                WriteJson(ctx, new { error = "territory, map, x, y required" }, 400);
            }
            return;
        }

        WriteJson(ctx, new
        {
            error = "unknown endpoint",
            available = new[]
            {
                "GET /", "GET /api/search?q=", "GET /api/item/{id}", "GET /api/snapshot",
                "POST /api/action/craftlog/{id}", "POST /api/action/dutyfinder/{cfc}",
                "POST /api/action/map?territory=&map=&x=&y=",
            },
        }, 404);
    }

    private static void WriteJson(HttpListenerContext ctx, object payload, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose() => Stop();
}
