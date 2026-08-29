using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using GlamSource.Core;
using GlamSource.Windows;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace GlamSource.Services;

// ponytail: optional HTML alternative UI (localhost only). Feature parity with the ImGui shell:
// search, item detail with sources, character snapshot, plus in-game actions (crafting log,
// map marker, duty finder) marshalled through the Framework thread. Off by default.
// Raw TcpListener instead of HttpListener — http.sys is not reliably emulated under Wine/XLCore.
public sealed class WebUiService : IDisposable
{
    private const int Port = 23424;

    private readonly IItemDetailService _detail;
    private readonly IGlamourService _glamour;
    private readonly GlamSourceShellWindow _shell;
    private readonly Configuration _configuration;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private TcpListener? _listener;
    private TcpListener? _listener6;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string? _browsingwayConfigPath;

    /// <summary>Human-readable state of the Browsingway inlay bootstrap, shown in Settings.</summary>
    public string? InlayStatus { get; private set; }

    public WebUiService(IItemDetailService detail, IGlamourService glamour, GlamSourceShellWindow shell, Configuration configuration, IFramework framework, Dalamud.Plugin.IDalamudPluginInterface pi, IPluginLog log)
    {
        _detail = detail;
        _glamour = glamour;
        _shell = shell;
        _configuration = configuration;
        _framework = framework;
        _log = log;
        // pluginConfigs/GlamSource.json -> sibling Browsingway.json
        var dir = pi.ConfigFile.DirectoryName;
        _browsingwayConfigPath = dir != null ? Path.Combine(dir, "Browsingway.json") : null;
    }

    // ponytail: Browsingway has no IPC and no create-overlay command; it reads its config once at
    // load. So we seed the inlay entry directly into Browsingway.json — active after the next
    // Browsingway load (game restart or plugin reload), no manual setup.
    public void EnsureBrowsingwayInlay()
    {
        try
        {
            if (_browsingwayConfigPath == null || !File.Exists(_browsingwayConfigPath))
            {
                InlayStatus = "Browsingway config not found — open /bw once, then re-toggle Web UI.";
                return;
            }

            var root = JsonNode.Parse(File.ReadAllText(_browsingwayConfigPath));
            if (root == null) { InlayStatus = "Browsingway config unreadable."; return; }

            var inlays = root["Inlays"] as JsonArray;
            if (inlays == null)
            {
                inlays = new JsonArray();
                root["Inlays"] = inlays;
            }

            if (inlays.Any(n => string.Equals((string?)n?["Name"], "GlamSource", StringComparison.OrdinalIgnoreCase)))
            {
                InlayStatus = "Overlay ready.";
                return;
            }

            inlays.Add(new JsonObject
            {
                ["Guid"] = Guid.NewGuid().ToString(),
                ["Name"] = "GlamSource",
                ["Url"] = $"http://127.0.0.1:{Port}/",
                ["Hidden"] = true,
                ["Locked"] = false,
                ["TypeThrough"] = false,
                ["ClickThrough"] = false,
                ["Fullscreen"] = false,
                ["Muted"] = false,
                ["Disabled"] = false,
                ["ActOptimizations"] = false,
                ["HideOutOfCombat"] = false,
                ["HideInPvP"] = false,
                ["HideDelay"] = 0,
                ["Framerate"] = 60,
                ["Opacity"] = 100.0,
                ["Zoom"] = 100.0,
                ["CustomCss"] = "",
            });
            File.WriteAllText(_browsingwayConfigPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            InlayStatus = "Overlay created — restart the game or reload Browsingway once to activate.";
            _log.Information("[WebUi] seeded GlamSource inlay into Browsingway.json");
        }
        catch (Exception ex)
        {
            InlayStatus = $"Overlay setup failed: {ex.Message}";
            _log.Error($"[WebUi] EnsureBrowsingwayInlay failed: {ex.Message}");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && _listener == null) { Start(); EnsureBrowsingwayInlay(); }
        else if (!enabled && _listener != null) Stop();
    }

    private void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
            // ponytail: browsers resolve "localhost" to ::1 first; bind v6 loopback too (best effort).
            try
            {
                _listener6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                _listener6.Start();
                _ = Task.Run(() => AcceptLoop(_listener6, _cts.Token));
            }
            catch { _listener6 = null; }
            _log.Information($"[WebUi] listening on http://127.0.0.1:{Port}/");
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
        _listener6?.Stop();
        _listener = null;
        _listener6 = null;
        _cts = null;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { return; }

            _ = Task.Run(() =>
            {
                try { HandleClient(client); }
                // ponytail: browsers/Browsingway routinely open a probe connection and never send a
                // full request (keep-alive checks, speculative preconnects) — that's an IOException
                // from the read timeout, not a real error. Only log genuine failures.
                catch (IOException) { }
                catch (Exception ex) { _log.Error($"[WebUi] request error: {ex.Message}"); }
                finally { client.Dispose(); }
            }, token);
        }
    }

    private void HandleClient(TcpClient client)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

        // request line + headers (bodies ignored — all POST params travel in the query string)
        var requestLine = reader.ReadLine();
        if (string.IsNullOrEmpty(requestLine)) return;
        while (!string.IsNullOrEmpty(reader.ReadLine())) { }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var method = parts[0];
        var rawUrl = parts[1];
        var qIdx = rawUrl.IndexOf('?');
        var path = qIdx >= 0 ? rawUrl[..qIdx] : rawUrl;
        var query = HttpUtility.ParseQueryString(qIdx >= 0 ? rawUrl[(qIdx + 1)..] : "");

        var (status, contentType, body) = Route(method, path, query);
        // ponytail: no-store — Browsingway's CEF page is long-lived and any caching here means an
        // update ships but the overlay silently keeps serving the old HTML/JS.
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.Write(body);
        stream.Flush();
    }

    private (string status, string contentType, byte[] body) Route(string method, string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (method == "GET" && (path == "/" || path == "/ui"))
            return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebUiPage.Html));

        if (method == "GET" && path == "/api/search")
        {
            var q = query["q"] ?? "";
            var itemSheet = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            object result = q.Length < 3
                ? Array.Empty<object>()
                : _glamour.SearchItems(q).Take(30)
                    .Select(r => new { r.id, r.name, iconId = (uint)(itemSheet?.GetRowOrDefault(r.id)?.Icon ?? 0) })
                    .ToArray();
            return Json(result);
        }

        if (method == "GET" && path.StartsWith("/api/item/") && uint.TryParse(path["/api/item/".Length..], out var itemId))
        {
            var detail = _detail.GetDetail(itemId);
            return detail == null ? Json(new { error = "not found" }, "404 Not Found") : Json(detail);
        }

        if (method == "GET" && path == "/api/snapshot")
        {
            var itemSheet = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            uint IconOf(uint id) => id == 0 ? 0u : (uint)(itemSheet?.GetRowOrDefault(id)?.Icon ?? 0);
            return Json(new
            {
                slots = _shell.DebugSnapshot.Select(s => new
                {
                    s.Slot,
                    s.ActualItemId,
                    s.ActualItemName,
                    s.GlamourItemId,
                    s.GlamourItemName,
                    s.IsGlamoured,
                    iconId = IconOf(s.GlamourItemId ?? s.ActualItemId),
                }),
                activeRecentName = _shell.DebugActiveRecentName,
                isRecentOverrideActive = _shell.DebugIsRecentOverrideActive,
            });
        }

        if (method == "POST" && path.StartsWith("/api/action/craftlog/") && uint.TryParse(path["/api/action/craftlog/".Length..], out var craftId))
        {
            // HQ ids sit at NQ RowId + 1_000_000; the recipe log only knows the NQ id.
            var nq = craftId >= 1_000_000 ? craftId - 1_000_000 : craftId;
            _framework.RunOnFrameworkThread(() => ItemDetailWindow.TryOpenCraftingLog(nq));
            return Json(new { ok = true });
        }

        if (method == "POST" && path.StartsWith("/api/action/dutyfinder/") && uint.TryParse(path["/api/action/dutyfinder/".Length..], out var cfcId))
        {
            _framework.RunOnFrameworkThread(() => ItemDetailWindow.TryOpenDutyFinder(cfcId));
            return Json(new { ok = true });
        }

        if (method == "GET" && path == "/api/preview3d")
        {
            if (!_configuration.WebUiLive3DPreview)
                return ("404 Not Found", "application/octet-stream", Array.Empty<byte>());

            // ponytail: NEVER call D3D11 readback from here — the immediate context must only be
            // touched in-line with Present (see PreviewRenderer.CaptureFrameForWeb, called from
            // Draw()). This just reads the last frame it cached; plain field read, no GPU call,
            // no Framework-thread hop. An earlier version called TryCapturePixels() via
            // RunOnFrameworkThread from this HTTP worker thread and corrupted D3D11 state badly
            // enough to crash the game (unrelated plugin's own D3D11 hook faulted downstream) — do
            // not reintroduce that.
            var frame = _shell.PreviewWindow?.Renderer.LatestWebFrame;

            if (frame == null)
                return ("404 Not Found", "application/octet-stream", Array.Empty<byte>());

            // header: width(4) height(4) rowPitch(4) isBgra(1), then raw pixel bytes
            var f = frame.Value;
            var head = new byte[13];
            BitConverter.GetBytes(f.Width).CopyTo(head, 0);
            BitConverter.GetBytes(f.Height).CopyTo(head, 4);
            BitConverter.GetBytes(f.RowPitch).CopyTo(head, 8);
            head[12] = (byte)(f.IsBgra ? 1 : 0);
            var payload = new byte[head.Length + f.Pixels.Length];
            head.CopyTo(payload, 0);
            f.Pixels.CopyTo(payload, head.Length);
            return ("200 OK", "application/octet-stream", payload);
        }

        if (method == "POST" && path == "/api/action/preview3d/rotate" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["dx"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dyaw)
            && float.TryParse(query["dy"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dpitch))
        {
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetYawPitch(dyaw, dpitch));
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/zoom" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["delta"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoomDelta))
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var renderer = _shell.PreviewWindow?.Renderer;
                if (renderer != null) renderer.SetZoom(renderer.Zoom + zoomDelta);
            });
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/overlay/hide")
        {
            _framework.RunOnFrameworkThread(() =>
                Plugin.CommandManager.ProcessCommand("/bw overlay glamsource hidden on"));
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/overlay/lock")
        {
            // ponytail: Browsingway has no title bar — unlocked means the WHOLE overlay body is
            // ImGui-draggable, which eats every mousedown-drag before it reaches this page (that's
            // why camera-drag didn't work). Lock briefly while dragging the 3D view, unlock to move
            // the overlay itself. Two separate commands (on/off), not a toggle, so repeated calls
            // from a flaky client can't desync from the real state.
            var locked = query["locked"] == "true";
            _framework.RunOnFrameworkThread(() =>
                Plugin.CommandManager.ProcessCommand($"/bw overlay glamsource locked {(locked ? "on" : "off")}"));
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/map")
        {
            if (uint.TryParse(query["territory"], out var territory) && uint.TryParse(query["map"], out var mapId)
                && float.TryParse(query["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && float.TryParse(query["y"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
            {
                _framework.RunOnFrameworkThread(() =>
                {
                    try { Plugin.GameGui.OpenMapWithMapLink(new MapLinkPayload(territory, mapId, x, y)); }
                    catch (Exception ex) { _log.Error($"[WebUi] map open failed: {ex.Message}"); }
                });
                return Json(new { ok = true });
            }
            return Json(new { error = "territory, map, x, y required" }, "400 Bad Request");
        }

        return Json(new
        {
            error = "unknown endpoint",
            available = new[]
            {
                "GET /", "GET /api/search?q=", "GET /api/item/{id}", "GET /api/snapshot", "GET /api/preview3d",
                "POST /api/action/craftlog/{id}", "POST /api/action/dutyfinder/{cfc}",
                "POST /api/action/map?territory=&map=&x=&y=",
            },
        }, "404 Not Found");
    }

    private static (string, string, byte[]) Json(object payload, string status = "200 OK")
        => (status, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts)));

    public void Dispose() => Stop();
}
