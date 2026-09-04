using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using GlamSource.Core;
using GlamSource.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly ModelExport.ModelExportService _modelExport;
    private readonly Lumina.GameData _modelExportGameData;
    private readonly GlamourerColorIpc _glamourerColors;
    // ponytail: on-demand wiki image lookup (see ItemImageService) — self-owned HttpClient, same as
    // how UniversalisService/CraftingCostService are wired elsewhere, no need to route through Plugin.cs.
    private readonly ItemImageService _imageService;
    // ponytail: "universalis preise fehlen im webview" — ImGui's ItemDetailWindow has had this
    // since forever (Plugin.cs wires its own UniversalisService instance into it), the web UI just
    // never got an equivalent call wired in. Same hardcoded world/DC as everywhere else in this
    // codebase (Plugin.cs, GlamSource.Mock/Program.cs) — not configurable anywhere today, so no
    // new gap introduced by matching that.
    private readonly IUniversalisService _universalis;
    // ponytail: "push" an item into the web UI from a native trigger (Examine-window right-click,
    // /glamsource mount) — there's no live socket to the browser tab, so this is a one-slot mailbox
    // the page polls and clears (GET /api/pendingitem below), same shape as _webPreviewGear above.
    private uint? _pendingWebItemId;
    // ponytail: named pose snapshots (e.g. "idle", "weapon"), self-refreshed whenever the framework
    // thread happens to observe the character in that state — no user action needed. Concurrent
    // dictionary because writes happen on the framework thread while reads happen on arbitrary
    // HTTP worker threads. In-memory only, resets on plugin reload (re-populates within seconds).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelExport.SkeletonPose> _savedPoses = new();
    // ponytail: web-driven hypothetical gear preview for the CharaView MJPEG stream — separate from
    // the live "whatever you're actually wearing" default (empty = show that, existing behavior).
    // Reuses GlamourPreviewWindow's existing SetSnapshotProvider/SetEquipmentSnapshot plumbing
    // (already used by the ImGui window's hover-preview) instead of inventing a second path —
    // CharaView reads real per-slot overlay data the same way either caller feeds it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EquipmentSlotType, EquipmentSlot> _webPreviewGear = new();
    // ponytail: MJPEG stream session stats — was only logged (see StreamPreviewMjpeg's finally
    // block), meaning "is it smooth" required opening /xllog. Exposed here too so /api/preview3d/debug
    // answers it directly, live while streaming or from the last completed session.
    private volatile bool _streamActive;
    private long _streamSessionStartMs;
    private long _streamSessionFrames;
    private long _lastStreamFrames;
    private double _lastStreamDurationS;
    private double _lastStreamAvgFps;
    private ItemSearchIndex? _search; // lazy: first /api/search builds the ~40k-row index
    private readonly IClientState _clientState; // Duty Drops tab: current territory -> duty
    private TcpListener? _listener;
    private TcpListener? _listener6;
    private CancellationTokenSource? _cts;

    // JsonStringEnumConverter: without it ItemSourceDetail.Type serializes as a bare number
    // (e.g. 1), but WebUiPage.cs's JS sniffs the source type via /craft/i, /vendor|shop/i etc.
    // regexes against that string — a number never matches, so the "Open Crafting Log" button
    // (and the vendor/quest/duty badge coloring) silently never appeared for ANY item.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _browsingwayConfigPath;

    /// <summary>Human-readable state of the Browsingway inlay bootstrap, shown in Settings.</summary>
    public string? InlayStatus { get; private set; }

    public WebUiService(IItemDetailService detail, IGlamourService glamour, GlamSourceShellWindow shell, Configuration configuration, IFramework framework, Dalamud.Plugin.IDalamudPluginInterface pi, IPluginLog log, IClientState clientState, Action<string>? onImageError = null)
    {
        _detail = detail;
        _glamour = glamour;
        _clientState = clientState;
        _shell = shell;
        _configuration = configuration;
        _framework = framework;
        _log = log;
        _modelExport = new ModelExport.ModelExportService(detail.GameData);
        _modelExportGameData = detail.GameData;
        _glamourerColors = new GlamourerColorIpc(pi);
        // pluginConfigs/GlamSource.json -> sibling Browsingway.json
        var dir = pi.ConfigFile.DirectoryName;
        _browsingwayConfigPath = dir != null ? Path.Combine(dir, "Browsingway.json") : null;
        // same on-disk ImageCache as Plugin.cs's own ItemImageService instance (see its comment —
        // separate in-memory caches are fine and cheap, but both should read/write the SAME files
        // on disk so a lookup done via one surface isn't re-fetched by the other)
        _imageService = new ItemImageService(new System.Net.Http.HttpClient(), dir != null ? Path.Combine(dir, "ImageCache") : null, onImageError);
        _universalis = new UniversalisService(new System.Net.Http.HttpClient());
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

    // ponytail: called from Plugin.cs (context-menu "Item Source"/"Check Mount" clicks, /glamsource
    // mount) to push an item into whichever tab the web UI happens to be showing.
    public void PushItemToWeb(uint itemId) => _pendingWebItemId = itemId;

    public void SetEnabled(bool enabled)
    {
        if (enabled && _listener == null) { Start(); EnsureBrowsingwayInlay(); }
        else if (!enabled && _listener != null)
        {
            Stop();
            // server going away = close the overlay too — otherwise Browsingway keeps showing a
            // dead page ("wenn der Server ausgeschaltet ist, soll das Fenster sich schließen").
            // Plugin unload does the same in DisposeAsync; this covers the settings toggle.
            try
            {
                _framework.RunOnFrameworkThread(() =>
                    Plugin.CommandManager.ProcessCommand("/bw overlay glamsource disabled on"));
            }
            catch { /* Browsingway not installed — nothing to close */ }
        }
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
                try { HandleClient(client, token); }
                // ponytail: browsers/Browsingway routinely open a probe connection and never send a
                // full request (keep-alive checks, speculative preconnects) — that's an IOException
                // from the read timeout, not a real error. Only log genuine failures. The MJPEG
                // stream below also exits via IOException (client closed the connection) — same
                // catch, same reasoning, it's not a real error either.
                catch (IOException) { }
                catch (Exception ex) { _log.Error($"[WebUi] request error: {ex.Message}"); }
                finally { client.Dispose(); }
            }, token);
        }
    }

    // security: same-origin check for the /api/ guard above. This server is only ever meant to be
    // called by its own page (127.0.0.1/localhost, this exact port) — no legitimate cross-origin
    // caller exists, so no allowlist beyond "is it actually this server".
    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true; // no Origin header — see caller's comment
        return origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}" || origin == $"http://[::1]:{Port}";
    }

    private void HandleClient(TcpClient client, CancellationToken token)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

        // request line + headers (bodies ignored — all POST params travel in the query string).
        // Origin is the one header worth keeping — see IsAllowedOrigin below.
        var requestLine = reader.ReadLine();
        if (string.IsNullOrEmpty(requestLine)) return;
        string? originHeader = null;
        string? headerLine;
        while (!string.IsNullOrEmpty(headerLine = reader.ReadLine()))
        {
            if (headerLine.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
                originHeader = headerLine["Origin:".Length..].Trim();
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var method = parts[0];
        var rawUrl = parts[1];
        var qIdx = rawUrl.IndexOf('?');
        var path = qIdx >= 0 ? rawUrl[..qIdx] : rawUrl;
        var query = HttpUtility.ParseQueryString(qIdx >= 0 ? rawUrl[(qIdx + 1)..] : "");

        // security: this server has zero auth (by design — it's meant to be hit by the page it
        // itself serves), and used to send Access-Control-Allow-Origin: * with no origin check at
        // all, on EVERY /api/ route including state-changing ones (Glamourer apply, Duty Finder
        // open, ...). Loopback-only binding stops LAN/remote attackers, but not a malicious page
        // open in the SAME browser while the game runs — a plain cross-origin <form>/fetch POST
        // needs no preflight and was previously neither blocked nor even noticed. Reject any /api/
        // request whose Origin header (browsers send this on cross-origin requests) doesn't match
        // this server itself; a MISSING Origin (curl, direct navigation, this page's own top-level
        // load) is allowed through — there's nothing more to check against in that case, same
        // baseline every other localhost dev server (Vite, Jupyter, ...) works with.
        if (path.StartsWith("/api/", StringComparison.Ordinal) && !IsAllowedOrigin(originHeader))
        {
            var forbidden = Encoding.UTF8.GetBytes("cross-origin request blocked");
            stream.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nContent-Length: {forbidden.Length}\r\nConnection: close\r\n\r\n"));
            stream.Write(forbidden);
            stream.Flush();
            return;
        }

        // ponytail: long-lived multipart stream, not a single (status, body) response — handled
        // separately from Route()'s request/response model. See StreamPreviewMjpeg's doc comment.
        if (method == "GET" && path == "/api/preview3d/stream")
        {
            StreamPreviewMjpeg(stream, token);
            return;
        }

        var (status, contentType, body) = Route(method, path, query);
        // ponytail: no-store by default — Browsingway's CEF page is long-lived and any caching here
        // means an update ships but the overlay silently keeps serving the old HTML/JS. Icon/item
        // preview bytes are the one exception: content-addressed by a numeric id that never changes
        // for that id, so every re-render (search results, duty tiles, ...) was refetching the exact
        // same bytes every time ("doof jedes mal 'neu' zu laden") — cache those aggressively instead.
        var cacheControl = path.StartsWith("/api/icon/", StringComparison.Ordinal) || path.StartsWith("/api/itemimage/", StringComparison.Ordinal)
            ? "public, max-age=604800, immutable"
            : "no-store";
        // no Access-Control-Allow-Origin: this page only ever calls itself (same-origin, no CORS
        // needed) — the wildcard used to make every response readable by any website's JS too.
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: {cacheControl}\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.Write(body);
        stream.Flush();
    }

    /// <summary>Serves the live CharaView preview as a standard MJPEG (multipart/x-mixed-replace)
    /// stream — the browser's &lt;img&gt; element decodes and repaints this natively, replacing the
    /// old client-side setTimeout+fetch poll loop and raw-pixel JS decode entirely. Runs inline on
    /// this connection's own pooled thread (see AcceptLoop) for as long as the client stays
    /// connected; a write failure once the browser closes the connection throws IOException, caught
    /// by the same handler AcceptLoop already uses for every other request.
    /// NEVER touches D3D11 itself — only reads PreviewRenderer.LatestWebJpeg, a plain field last
    /// written from Draw(). An earlier version of this feature called the capture routine via
    /// Framework.RunOnFrameworkThread from an HTTP worker thread and corrupted D3D11 state badly
    /// enough to crash the game (an unrelated plugin's own D3D11 hook faulted downstream) — do not
    /// reintroduce that; capture must stay inline with Draw()/Present().</summary>
    private void StreamPreviewMjpeg(NetworkStream stream, CancellationToken token)
    {
        if (!_configuration.WebUiLive3DPreview)
        {
            _log.Information("[WebUi] preview3d/stream request rejected — WebUiLive3DPreview is off");
            stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            return;
        }

        const string boundary = "glamsourceframe";
        stream.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary={boundary}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n"));
        stream.Flush();

        var swStart = Environment.TickCount64;
        var framesSent = 0;
        byte[]? lastSent = null;
        _streamActive = true;
        _streamSessionStartMs = swStart;
        _streamSessionFrames = 0;
        // ponytail: full-rate capture right away instead of waiting up to IdleEncodeThrottleMs for
        // the first frame — opening the Character tab should feel instant, not laggy-until-idle-
        // window-expires.
        _shell.PreviewWindow?.Renderer.NotifyInteraction();
        _log.Information("[WebUi] preview3d/stream connected");
        try
        {
            while (!token.IsCancellationRequested && _configuration.WebUiLive3DPreview)
            {
                var renderer = _shell.PreviewWindow?.Renderer;
                var jpeg = renderer?.LatestWebJpeg;
                if (jpeg != null && !ReferenceEquals(jpeg, lastSent))
                {
                    lastSent = jpeg;
                    // ponytail: per-part Content-Type — the experimental transparent-backdrop mode
                    // (see PreviewRenderer.SetTransparentBackdrop) switches individual frames to PNG,
                    // multipart/x-mixed-replace allows each part to declare its own type.
                    var contentType = renderer!.LatestWebFrameIsPng ? "image/png" : "image/jpeg";
                    stream.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: {contentType}\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
                    stream.Write(jpeg);
                    stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                    stream.Flush();
                    framesSent++;
                    _streamSessionFrames = framesSent; // live progress, readable via /api/preview3d/debug while still connected
                }
                Thread.Sleep(16); // ~60Hz check of the in-memory latest frame; actual cadence gated by PreviewRenderer's encode rate
            }
        }
        finally
        {
            _streamActive = false;
            var elapsedS = (Environment.TickCount64 - swStart) / 1000.0;
            _lastStreamFrames = framesSent;
            _lastStreamDurationS = elapsedS;
            _lastStreamAvgFps = elapsedS > 0 ? framesSent / elapsedS : 0;
            _log.Information($"[WebUi] preview3d/stream disconnected — sent {framesSent} frame(s) over {elapsedS:F1}s ({_lastStreamAvgFps:F1} fps avg)");
        }
    }

    private (string status, string contentType, byte[] body) Route(string method, string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (method == "GET" && (path == "/" || path == "/ui"))
            return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebUiPage.Html));

        if (method == "GET" && path == "/api/pendingitem")
        {
            var id = _pendingWebItemId;
            _pendingWebItemId = null; // one-shot — polling picks it up once, not repeatedly
            return Json(new { itemId = id });
        }

        // ponytail: mirrors the straightforward Configuration-backed toggles from
        // GlamSourceShellWindow.DrawSettingsTab — deliberately NOT "Web UI" itself (toggling that
        // off from the page you're currently looking at would just kill your own connection) and
        // NOT "Movable Window" (an ImGui-window-only concept, meaningless for a browser page). The
        // Gearset/Mount pickers need live native reads (RaptureGearsetModule, unlocked mounts) —
        // same shape as the mount-lookup work, just not built out here yet.
        if (method == "GET" && path == "/api/settings")
        {
            return Json(new
            {
                _configuration.ShowCraftingSavings,
                _configuration.DebugApiEnabled,
                _configuration.WebUiAutoOverlay,
                _configuration.WebUiLive3DPreview,
                _configuration.MountUpDistance,
                _configuration.ContextMenuOpensInWebUi,
            });
        }

        if (method == "POST" && path == "/api/action/settings/showcraftingsavings" && bool.TryParse(query["value"], out var scs))
        {
            _configuration.ShowCraftingSavings = scs;
            _configuration.Save();
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/settings/debugapi" && bool.TryParse(query["value"], out var dae))
        {
            _configuration.DebugApiEnabled = dae;
            _configuration.Save();
            _shell.OnDebugApiToggle?.Invoke(dae);
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/settings/autooverlay" && bool.TryParse(query["value"], out var awo))
        {
            _configuration.WebUiAutoOverlay = awo;
            _configuration.Save();
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/settings/contextmenuweb" && bool.TryParse(query["value"], out var cmw))
        {
            _configuration.ContextMenuOpensInWebUi = cmw;
            _configuration.Save();
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/settings/live3dpreview" && bool.TryParse(query["value"], out var l3d))
        {
            _configuration.WebUiLive3DPreview = l3d;
            _configuration.Save();
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/settings/mountupdistance" && float.TryParse(query["value"], System.Globalization.CultureInfo.InvariantCulture, out var mud))
        {
            _configuration.MountUpDistance = mud;
            _configuration.Save();
            return Json(new { ok = true });
        }

        if (method == "GET" && path == "/api/search")
        {
            var q = query["q"] ?? "";
            var itemSheet = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();

            // ponytail: a pure-digit query means "look this item ID up directly" (handy when
            // cross-checking specific IDs), not a 3+ char name substring search.
            if (uint.TryParse(q, out var directId))
            {
                var item = itemSheet?.GetRowOrDefault(directId);
                object idResult = item is { } i
                    ? new[] { new { id = directId, name = i.Name.ToString(), iconId = (uint)i.Icon } }
                    : Array.Empty<object>();
                return Json(idResult);
            }

            int? Int(string key) => int.TryParse(query[key], out var v) ? v : null;
            var hits = (_search ??= new ItemSearchIndex(_detail.GameData))
                .Search(q, query["slot"], query["job"], Int("ilvlmin"), Int("ilvlmax"), 60);
            return Json(hits.Select(h => new { id = h.Id, name = h.Name, iconId = h.IconId, ilvl = h.ItemLevel }).ToArray());
        }

        // Duty Drops tab (doku TODO): list of duties with known drops, the one we're standing in,
        // and one duty's drop table. Territory is game state -> read it on the framework thread.
        // Glamourer IPC (same code path as the ImGui buttons, framework thread): the whole shown
        // outfit, or one piece from an item detail
        if (method == "POST" && path == "/api/action/glamourer/apply")
            return Json(new { status = _framework.RunOnFrameworkThread(() => _shell.ApplyToSelfFromWeb()).GetAwaiter().GetResult() });
        if (method == "POST" && path.StartsWith("/api/action/glamourer/item/") && uint.TryParse(path["/api/action/glamourer/item/".Length..], out var applyItemId))
            return Json(new { status = _framework.RunOnFrameworkThread(() => _shell.ApplyItemToSelf(applyItemId)).GetAwaiter().GetResult() });

        if (method == "GET" && path == "/api/duties")
            return Json(_detail.ListDutiesWithDrops().Select(d => new { id = d.CfcId, name = d.Name, type = d.Type, drops = d.DropCount, imageId = d.ImageId, level = d.Level, itemLevel = d.ItemLevel, expansion = d.Expansion, typeIcon = d.TypeIconId, bosses = d.Bosses, difficulty = d.Difficulty }).ToArray());
        if (method == "GET" && path == "/api/duty/current")
        {
            var territory = _framework.RunOnFrameworkThread(() => (uint)_clientState.TerritoryType).GetAwaiter().GetResult();
            return Json(new { id = _detail.FindDutyByTerritory(territory) ?? 0 });
        }
        if (method == "GET" && path.StartsWith("/api/duty/") && path.EndsWith("/coffers")
            && uint.TryParse(path["/api/duty/".Length..^"/coffers".Length], out var cofferDutyId))
            return Json(_detail.GetDutyCoffersAsync(cofferDutyId).GetAwaiter().GetResult()); // request thread, blocking is fine
        if (method == "GET" && path.StartsWith("/api/duty/") && uint.TryParse(path["/api/duty/".Length..], out var dutyId))
        {
            var dd = _detail.GetDutyDetail(dutyId);
            return dd == null ? ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("duty not found")) : Json(AnnotateUnlocks(dd));
        }

        // Outfit shopping list (prototype): the shown character's slots -> one best source per item,
        // merged into stops, summed costs. Reads the shell's snapshot, no game state of its own.
        if (method == "GET" && path == "/api/shoppinglist")
        {
            var itemSheetSl = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var outfit = _shell.DebugSnapshot
                .Select(s => (itemId: s.GlamourItemId ?? s.ActualItemId, name: s.GlamourItemName ?? s.ActualItemName ?? "",
                    iconId: (uint)(itemSheetSl?.GetRowOrDefault(s.GlamourItemId ?? s.ActualItemId)?.Icon ?? 0)))
                .ToList();
            return Json(ShoppingListBuilder.Build(outfit, _detail.GetDetail));
        }

        if (method == "GET" && path.StartsWith("/api/item/") && path.EndsWith("/event") && uint.TryParse(path["/api/item/".Length..^"/event".Length], out var eventItemId))
        {
            var ev = _detail.GetEventStatusAsync(eventItemId).GetAwaiter().GetResult(); // request thread, blocking is fine
            return Json(ev == null ? null! : new { eventName = ev.EventName, recurring = ev.Recurring, active = ev.Active });
        }

        if (method == "GET" && path == "/api/jobs")
            return Json((_search ??= new ItemSearchIndex(_detail.GameData)).Jobs().Select(j => new { j.abbr, j.name }).ToArray());

        if (method == "GET" && path.StartsWith("/api/item/") && uint.TryParse(path["/api/item/".Length..], out var itemId))
        {
            var detail = _detail.GetDetail(itemId);
            if (detail == null) return Json(new { error = "not found" }, "404 Not Found");
            // "hat man das schon unlocked" — only meaningful when the viewed item itself IS a
            // mount/minion unlock item; null (omitted) for everything else, not a false "locked".
            var unlocked = UnlockCheckService.CheckUnlocked(_detail, itemId);
            if (unlocked is not { } u) return Json(detail);
            var node = JsonSerializer.SerializeToNode(detail, JsonOpts)!.AsObject();
            node["unlocked"] = u;
            return Json(node);
        }

        if (method == "GET" && path == "/api/market/bulk")
        {
            // "preise fehlen mir bei items" — list rows (search results, duty drops) want a quick
            // price badge without one Universalis round trip per row. ids as a comma-separated
            // query param, one bulk request for the lot (Universalis' own multi-item endpoint).
            var ids = (query["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => uint.TryParse(s, out var id) ? id : 0).Where(id => id > 0).ToList();
            var prices = _universalis.GetBulkWorldPricesAsync(ids).GetAwaiter().GetResult();
            return Json(prices);
        }

        if (method == "GET" && path.StartsWith("/api/market/") && uint.TryParse(path["/api/market/".Length..], out var marketItemId))
        {
            // same blocking-await-on-request-thread pattern as /api/itemimage/ above — fine here
            // too, this is a ThreadPool request thread, not the UI/Framework one. Rate-limited
            // (1 req/sec per world+DC call) same as ImGui's own UniversalisService instance —
            // separate in-memory cache, but Universalis prices don't change fast enough for that
            // to matter for a "click an item, see roughly what it costs" use case.
            var market = _universalis.GetMarketInfoAsync(marketItemId).GetAwaiter().GetResult();
            return market == null ? Json(new { error = "not marketable or lookup failed" }, "404 Not Found") : Json(market);
        }

        if (method == "GET" && path.StartsWith("/api/inventory/") && uint.TryParse(path["/api/inventory/".Length..], out var invItemId))
        {
            // "die währung in grün" (ImGui already colors cost rows by have>=need) + "man sieht
            // welches mat wo liegt" — Web UI had neither: costs/materials always rendered plain,
            // no ownership data at all. RetainerInventoryCache.GetOwnedBreakdown needs native
            // InventoryManager reads, framework thread only.
            var b = _framework.RunOnFrameworkThread(() => RetainerInventoryCache.GetOwnedBreakdown(invItemId)).GetAwaiter().GetResult();
            return Json(new
            {
                total = b.Total,
                bags = b.Bags,
                saddlebag = b.Saddlebag,
                retainers = b.Retainers.Select(r => new { name = r.Name, count = r.Count }),
            });
        }

        if (method == "GET" && path.StartsWith("/api/itemimage/") && uint.TryParse(path["/api/itemimage/".Length..], out var imgItemId))
        {
            // ponytail: proxies the actual image bytes (not just the URL) so the browser loads it
            // same-origin — hotlinking the wiki URL directly from an <img src> got blocked by
            // Chromium's Opaque Response Blocking inside the Browsingway overlay (reported live).
            // Same reasoning as /api/icon/ above already proxying game icon bytes instead of trying
            // to point at an external CDN. Blocking .GetAwaiter().GetResult() is fine — this whole
            // Route() call already runs on a ThreadPool request thread, not the UI/Framework one.
            var detail = _detail.GetDetail(imgItemId);
            if (detail == null) return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("item not found"));
            // the wiki has no localized page titles — a German/French/JP client name 404s there
            // ("Freiherrliche Jacke", live-confirmed via /api/debug/imageerror). English regardless
            // of client language.
            var wikiName = _detail.GetWikiPageName(imgItemId) ?? detail.Name; // mount page for mount items
            var bytes = _imageService.GetPreviewImageBytesAsync(imgItemId, wikiName).GetAwaiter().GetResult();
            // 204 not 404: the <img onerror> already hides it, a 404 only spams the browser console (one per row)
            if (bytes == null) return ("204 No Content", "text/plain", Array.Empty<byte>());
            return ("200 OK", "image/jpeg", bytes);
        }

        if (method == "GET" && path.StartsWith("/api/icon/") && uint.TryParse(path["/api/icon/".Length..], out var iconId))
        {
            // Icons straight from the GAME data — xivapi's icon CDN is frozen and 404s on every
            // item newer than its snapshot (reported live: new weapons/gear showed no icon at all).
            try
            {
                var folder = iconId / 1000 * 1000;
                var tex = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>($"ui/icon/{folder:D6}/{iconId:D6}_hr1.tex")
                    ?? Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>($"ui/icon/{folder:D6}/{iconId:D6}.tex");
                if (tex == null) return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("no icon"));
                var w = tex.Header.Width;
                var h = tex.Header.Height;
                var bgra = tex.ImageData; // Lumina converts to B8G8R8A8
                if (bgra.Length < w * h * 4) return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("bad tex"));
                var raw = new byte[h * (1 + w * 4)];
                for (var y = 0; y < h; y++)
                {
                    var dst = y * (1 + w * 4);
                    raw[dst++] = 0;
                    var src = y * w * 4;
                    for (var x = 0; x < w; x++, src += 4, dst += 4)
                    {
                        raw[dst] = bgra[src + 2]; raw[dst + 1] = bgra[src + 1]; raw[dst + 2] = bgra[src]; raw[dst + 3] = bgra[src + 3];
                    }
                }
                return ("200 OK", "image/png", PreviewRenderer.WritePngRgba(raw, w, h));
            }
            catch (Exception ex)
            {
                // a handful of icons killed the connection outright (curl code 000, reported live
                // as "icons fehlen wieder") — an unguarded decode exception tore the socket down
                _log.Warning($"[WebUi] icon {iconId} failed: {ex.Message}");
                return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes(ex.Message));
            }
        }

        if (method == "GET" && path == "/api/snapshot")
        {
            var itemSheet = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            uint IconOf(uint id) => id == 0 ? 0u : (uint)(itemSheet?.GetRowOrDefault(id)?.Icon ?? 0);
            // ponytail: cheap content hash so the client can poll without re-rendering (and
            // reloading every icon <img>) unless something actually changed — "icons aktualisieren
            // soll nur passieren wenn aktiv ein switch passiert", not on every poll tick.
            ulong hash = 17;
            foreach (var s in _shell.DebugSnapshot)
                hash = hash * 31 + (s.GlamourItemId ?? s.ActualItemId) * 131 + s.Stain0 + (ulong)s.Stain1 * 7;
            hash = hash * 31 + (ulong)(_shell.DebugActiveRecentName?.GetHashCode() ?? 0);
            return Json(new
            {
                hash,
                slots = _shell.DebugSnapshot.Select(s => new
                {
                    Slot = s.Slot.ToString(), // enum name, not its number — the web UI shows this raw
                    s.ActualItemId,
                    s.ActualItemName,
                    s.GlamourItemId,
                    s.GlamourItemName,
                    s.IsGlamoured,
                    iconId = IconOf(s.GlamourItemId ?? s.ActualItemId),
                }),
                activeRecentName = _shell.DebugActiveRecentName,
                isRecentOverrideActive = _shell.DebugIsRecentOverrideActive,
                pinned = _shell.DebugPinned,
            });
        }

        if (method == "GET" && path == "/api/recents")
        {
            // ponytail: web-UI Recents sidebar — mirrors the native ImGui sidebar
            // (GlamSourceShellWindow.DrawRecentSidebar), same list, index-addressed activation.
            return Json(_shell.DebugRecentTargets.Select((r, i) => new
            {
                index = i,
                r.Name,
                r.World,
                active = r.Name == _shell.DebugActiveRecentName,
            }));
        }

        if (method == "POST" && path.StartsWith("/api/action/recent/") && path.EndsWith("/remove")
            && int.TryParse(path["/api/action/recent/".Length..^"/remove".Length], out var removeRecentIdx))
        {
            _framework.RunOnFrameworkThread(() => _shell.RemoveRecent(removeRecentIdx));
            return Json(new { ok = true });
        }

        if (method == "POST" && path.StartsWith("/api/action/recent/") && int.TryParse(path["/api/action/recent/".Length..], out var recentIdx))
        {
            _framework.RunOnFrameworkThread(() => _shell.ActivateRecent(recentIdx));
            return Json(new { ok = true });
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

        if (method == "POST" && path == "/api/action/preview3d/rotate" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["dx"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dyaw)
            && float.TryParse(query["dy"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dpitch))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetYawPitch(dyaw, dpitch));
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/zoom" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["delta"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoomDelta))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() =>
            {
                var renderer = _shell.PreviewWindow?.Renderer;
                // ponytail: multiplicative, not additive. Camera DISTANCE is inversely proportional
                // to the zoom value (SetZoom's own math), so a fixed +delta per scroll tick means
                // each tick moves the camera a shrinking absolute distance the further in you already
                // are — "umso näher, umso langsamer", reported live, worse the higher the zoom range
                // goes (which raising the cap to 20 made much more noticeable). A percentage step
                // keeps the FELT zoom speed constant across the whole range instead.
                if (renderer != null) renderer.SetZoom(renderer.Zoom * (1f + zoomDelta));
            });
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/pan" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["dx"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var panDx)
            && float.TryParse(query["dy"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var panDy))
        {
            // ponytail: "wenn man ranzoomt will ich mit rechtsklick die Höhe der Kamera verstellen"
            // — direct PanCamera call, independent of zoomat's zoom-triggered pan. Right-click-drag
            // on the client (see WebUiPage.cs) — makes sense mainly once zoomed in, since yaw/pitch
            // alone can't look at a DIFFERENT point on the model without re-centering first.
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.PanCamera(panDx, panDy));
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/zoomat" && _configuration.WebUiLive3DPreview
            && float.TryParse(query["delta"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoomAtDelta)
            && float.TryParse(query["px"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoomAtPx)
            && float.TryParse(query["py"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoomAtPy))
        {
            // ponytail: "zoom to a specific point" — PanCamera already existed for exactly this
            // (its own doc comment says "used for zoom-to-cursor") but nothing ever called it. px/py
            // are the cursor's position relative to canvas CENTER, -1..1 each axis. Only pan toward
            // the cursor while actually zooming IN (delta>0) — zooming back out should pull the view
            // back toward center, not keep pushing further off-axis. Pan scale is untuned/guessed,
            // same as the existing rotate/zoom handlers' own constants — tune live if it feels off.
            // Multiplicative zoom step — see /zoom's comment just above for why.
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() =>
            {
                var renderer = _shell.PreviewWindow?.Renderer;
                if (renderer == null) return;
                renderer.SetZoom(renderer.Zoom * (1f + zoomAtDelta));
                if (zoomAtDelta > 0) renderer.PanCamera(zoomAtPx * zoomAtDelta * 2f, zoomAtPy * zoomAtDelta * 2f);
            });
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/freeze" && _configuration.WebUiLive3DPreview
            && bool.TryParse(query["on"], out var freezeOn))
        {
            // ponytail: third attempt — SuspendCharacterCopy and DoUpdate=false both failed live
            // (character kept animating). SetFreezePose stomps the skeleton's hkaPose bone
            // transforms every Tick with a snapshot, the technique Brio actually ships (see
            // PreviewRenderer's freeze block). Camera (rotate/zoom/pan) keeps working either way.
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetFreezePose(freezeOn));
            _log.Information($"[WebUi] preview3d pose frozen: {freezeOn}");
            return Json(new { ok = true, frozen = freezeOn });
        }

        if (method == "GET" && path == "/api/preview3d/emotes" && _configuration.WebUiLive3DPreview)
        {
            // Emote sheet: ActionTimeline[0] = the standing loop timeline. No unlock filter on
            // purpose — the timeline layer has no ownership gate, unowned emotes render fine
            // (purely client-side, see PreviewRenderer's emote comment).
            // EmoteMode != 0 = persistent stateful poses (sit/ground-sit/doze/pose variants) —
            // exactly the "static" set; one-shot emotes and dances carry mode 0 and are excluded
            // ("nimm alle raus, nur welche die statisch sind, keine Tänze")
            var emotes = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()!
                .Where(e => e.EmoteMode.RowId != 0
                    && e.ActionTimeline.Count > 0 && e.ActionTimeline[0].RowId != 0 && !string.IsNullOrEmpty(e.Name.ExtractText()))
                .Select(e => new { name = e.Name.ExtractText(), timelineId = e.ActionTimeline[0].RowId })
                .OrderBy(e => e.name)
                .ToArray();
            return Json(emotes);
        }

        if (method == "POST" && path == "/api/action/preview3d/emote" && _configuration.WebUiLive3DPreview
            && ushort.TryParse(query["timelineId"], out var emoteTimelineId))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetEmoteTimeline(emoteTimelineId));
            _log.Information($"[WebUi] preview3d emote timeline: {emoteTimelineId}");
            return Json(new { ok = true, timelineId = emoteTimelineId });
        }

        // ponytail: deaktiviert (final, nach 21 Anläufen) — siehe doku/character-preview.md.
        // "false &&" kurzschließt ohne den Rest zu löschen, für eine spätere GPose-Neuauflage.
        if (false && method == "POST" && path == "/api/action/preview3d/weapononly" && _configuration.WebUiLive3DPreview
            && bool.TryParse(query["on"], out var weaponOnlyOn))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetWeaponOnly(weaponOnlyOn));
            _log.Information($"[WebUi] preview3d weapon-only: {weaponOnlyOn}");
            return Json(new { ok = true, weaponOnly = weaponOnlyOn });
        }

        if (method == "POST" && path == "/api/action/overlay/minimize" && bool.TryParse(query["on"], out var minimizeOn))
        {
            // the page collapses its own content; this tells the PLUGIN to shrink the actual
            // Browsingway ImGui window too (see Plugin.PinBrowsingwayOverlaySize)
            Plugin.BwOverlayMinimized = minimizeOn;
            return Json(new { ok = true, minimized = minimizeOn });
        }

        if (method == "POST" && path == "/api/action/overlay/compact" && bool.TryParse(query["on"], out var compactOn))
        {
            // "Item Search darf klein bleiben bis man sachen sucht" — the page itself has no fixed
            // height, only the Browsingway ImGui window does (Plugin.PinBrowsingwayOverlaySize);
            // this tells it to use the small pre-results height instead of the full one.
            Plugin.BwOverlayCompact = compactOn;
            return Json(new { ok = true, compact = compactOn });
        }

        // ponytail: deaktiviert, siehe weapononly-Endpoint oben.
        if (false && method == "POST" && path == "/api/action/preview3d/weapon" && _configuration.WebUiLive3DPreview
            && bool.TryParse(query["on"], out var weaponOn))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            // silent try-on: fill the agent's internal TryOnItems with the current mainhand —
            // the ONLY channel vf10 renders weapons from (see SetAgentTryOnWeapon)
            uint mhId = 0, mhIcon = 0; byte mhCat = 0, mhS0 = 0, mhS1 = 0;
            if (weaponOn)
            {
                var mh = _shell.DebugSnapshot.FirstOrDefault(s => s.Slot == EquipmentSlotType.MainHand);
                if (mh != null)
                {
                    mhId = mh.GlamourItemId ?? mh.ActualItemId;
                    mhS0 = mh.Stain0; mhS1 = mh.Stain1;
                    var row = _detail.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(mhId);
                    mhCat = (byte)(row?.EquipSlotCategory.RowId ?? 1);
                    mhIcon = row?.Icon ?? 0;
                }
            }
            _framework.RunOnFrameworkThread(() =>
            {
                var r = _shell.PreviewWindow?.Renderer;
                if (r == null) return;
                r.SetAgentTryOnWeapon(weaponOn ? mhId : 0, mhCat, mhS0, mhS1, mhIcon);
                r.SetWeaponDrawn(weaponOn);
            });
            _log.Information($"[WebUi] preview3d weapon shown: {weaponOn} (silent tryon item {mhId}, cat {mhCat})");
            return Json(new { ok = true, weapon = weaponOn, mainHandId = mhId });
        }

        if (method == "POST" && path == "/api/action/preview3d/ortho" && _configuration.WebUiLive3DPreview
            && bool.TryParse(query["on"], out var orthoOn))
        {
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetOrtho(orthoOn));
            _log.Information($"[WebUi] preview3d ortho: {orthoOn}");
            return Json(new { ok = true, ortho = orthoOn });
        }

        if (method == "POST" && path == "/api/action/preview3d/transparent" && _configuration.WebUiLive3DPreview
            && bool.TryParse(query["on"], out var transparentOn))
        {
            // full-rate window so the first PNG frame lands immediately — without this the idle
            // throttle kept serving the old opaque JPEG for up to a second after toggling
            // (reported live as "ich muss erst leicht ranzoomen damit transparent greift")
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            // ponytail: experimental — see PreviewRenderer.SetTransparentBackdrop's doc comment for
            // the naive-chroma-key caveat. "mach mal nen Aus/An-Schalter für trans, dann kann ich es
            // mir anschauen" — this is that switch.
            _shell.PreviewWindow?.Renderer.SetTransparentBackdrop(transparentOn);
            _log.Information($"[WebUi] preview3d transparent backdrop: {transparentOn}");
            return Json(new { ok = true, transparent = transparentOn });
        }

        if (method == "POST" && path == "/api/action/preview3d/setitem" && _configuration.WebUiLive3DPreview
            && Enum.TryParse<EquipmentSlotType>(query["slot"], out var setSlot)
            && uint.TryParse(query["itemId"], out var setItemId))
        {
            byte.TryParse(query["stain0"], out var setStain0);
            byte.TryParse(query["stain1"], out var setStain1);
            // ponytail: display names are cosmetic-only for CharaView (it renders from ItemId/stains,
            // not the strings) — empty is fine, nothing here reads EquipmentSlot.ActualItemName back.
            _webPreviewGear[setSlot] = new EquipmentSlot(setSlot, setItemId, "", null, null, Stain0: setStain0, Stain1: setStain1);
            // Re-register every call instead of once at construction — GlamourPreviewWindow may not
            // be fully wired yet at WebUiService construction time, and SetSnapshotProvider itself
            // handles "already registered" cheaply (ensures init, then a plain field write).
            _shell.PreviewWindow?.SetSnapshotProvider(() => _webPreviewGear.IsEmpty ? null : _webPreviewGear.Values.ToList());
            _shell.PreviewWindow?.Renderer.NotifyInteraction(); // show the new item promptly, don't wait for the idle throttle
            _log.Information($"[WebUi] preview3d setitem: slot={setSlot} itemId={setItemId} stain0={setStain0} stain1={setStain1} (now {_webPreviewGear.Count} slot(s) overridden)");
            return Json(new { ok = true, slots = _webPreviewGear.Count });
        }

        if (method == "POST" && path == "/api/action/preview3d/cleargear" && _configuration.WebUiLive3DPreview)
        {
            _webPreviewGear.Clear();
            _shell.PreviewWindow?.SetSnapshotProvider(null);
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _log.Information("[WebUi] preview3d cleargear — back to live self-worn gear");
            return Json(new { ok = true });
        }

        if (method == "POST" && path == "/api/action/preview3d/reset" && _configuration.WebUiLive3DPreview)
        {
            // ponytail: escape hatch for "CharaView is stuck showing the wrong thing and nothing in
            // the UI un-sticks it" — seen live: a native UI grabbed the shared AgentTryon slot via a
            // path IsAgentActive() doesn't catch (see PreviewRenderer.Tick()'s doc comment) and never
            // released it as far as we could tell, leaving self-recovery with nothing to trigger on.
            // Full Release()+re-Initialize(), not just a recopy — matches what a full plugin reload
            // was doing as the only prior workaround.
            _webPreviewGear.Clear();
            _shell.PreviewWindow?.SetSnapshotProvider(null);
            _shell.PreviewWindow?.ForceReinitializeForSelf();
            _shell.PreviewWindow?.Renderer.NotifyInteraction();
            _log.Information("[WebUi] preview3d/reset — forced Release()+Initialize()");
            return Json(new { ok = true });
        }

        if (method == "GET" && path == "/api/debug/agenttail")
        {
            var hex = _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.GetAgentTailHex() ?? "no renderer").GetAwaiter().GetResult();
            return Json(new { baseOffset = "0x328", hex });
        }

        if (method == "POST" && path == "/api/debug/tryon" && uint.TryParse(query["itemId"], out var tryOnItemId))
        {
            _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.DebugTryOn(tryOnItemId));
            return Json(new { ok = true, itemId = tryOnItemId });
        }

        if (method == "GET" && path == "/api/debug/weaponstate")
        {
            var dump = _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.GetWeaponStateDump() ?? "no renderer").GetAwaiter().GetResult();
            return Json(new { dump });
        }

        if (method == "GET" && path == "/api/debug/imageerror")
        {
            return Json(new { lastError = _imageService.LastError });
        }

        if (method == "POST" && path == "/api/debug/seed")
        {
            var report = _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.SeedSelfEquipmentOnceProbe() ?? "no preview window").GetAwaiter().GetResult();
            return Json(new { report });
        }

        if (method == "GET" && path == "/api/debug/selfequip")
        {
            // direct probe: what does GetSelfEquipment actually return right now?
            try
            {
                var eq = _framework.RunOnFrameworkThread(() => _glamour.GetSelfEquipment()).GetAwaiter().GetResult();
                return Json(new { count = eq.Count, slots = eq.Select(s => new { slot = s.Slot.ToString(), id = s.GlamourItemId ?? s.ActualItemId }).ToArray() });
            }
            catch (Exception ex) { return Json(new { error = ex.ToString() }); }
        }

        if (method == "POST" && path == "/api/debug/kill")
        {
            // stutter-bisect switches — toggled via curl while the user reports live.
            // sys=tick|capture|depth|freezehook|texhook|bwpin, on=true kills, on=false revives.
            var sys = query["sys"] ?? "";
            _ = bool.TryParse(query["on"], out var killOn);
            if (sys == "bwpin") { Plugin.BwPinKilled = killOn; return Json(new { ok = true, state = $"bwpin={(killOn ? "KILLED" : "running")}" }); }
            var state = _framework.RunOnFrameworkThread(() => _shell.PreviewWindow?.Renderer.SetDebugKill(sys, killOn) ?? "no renderer").GetAwaiter().GetResult();
            _log.Information($"[WebUi] debug kill: {state}");
            return Json(new { ok = true, state });
        }

        if (method == "GET" && path == "/api/preview3d/debug")
        {
            // ponytail: mirrors /api/model3d/debug's "don't guess, look at the actual counters"
            // philosophy — for the MJPEG pipeline (untested in practice at time of writing), so a bad
            // first live run is diagnosable here instead of squinting at the video feed.
            var renderer = _shell.PreviewWindow?.Renderer;
            var stats = renderer?.GetWebCaptureStats();
            // ponytail: "nicht ganz nah... stopp wie vanilla?" — reads live camera distance
            // (touches game memory, so it goes through the framework thread like ResolveModelInputs
            // does, same established pattern) to tell "hit MY 20.0 zoom clamp" from "native camera
            // distance floor, same as vanilla Examine/Fitting Room" apart.
            float? cameraDistance = null;
            (bool isOrtho, float orthoHeight, float fov)? camState = null;
            try { camState = _framework.RunOnFrameworkThread(() => renderer?.GetRenderCameraState()).GetAwaiter().GetResult(); }
            catch { /* best effort */ }
            try { cameraDistance = _framework.RunOnFrameworkThread(() => renderer?.GetCameraDistance()).GetAwaiter().GetResult(); }
            catch { /* best-effort diagnostic field */ }
            return Json(new
            {
                enabled = _configuration.WebUiLive3DPreview,
                rendererInitialized = renderer?.IsInitialized ?? false,
                zoom = renderer?.Zoom,
                ortho = renderer?.OrthoEnabled,
                // engine readback — is our ortho write actually sticking?
                engineIsOrtho = camState?.isOrtho,
                engineOrthoHeight = camState?.orthoHeight,
                engineFov = camState?.fov,
                cameraDistance,
                stagingReady = stats?.StagingReady ?? false,
                stagingWidth = stats?.StagingWidth ?? 0,
                stagingHeight = stats?.StagingHeight ?? 0,
                framesEncoded = stats?.FramesEncoded ?? 0,
                framesSkipped = stats?.FramesSkipped ?? 0,
                captureErrors = stats?.CaptureErrors ?? 0,
                lastFrameBytes = stats?.LastFrameBytes ?? 0,
                lastEncodeDurationMs = stats?.LastEncodeDurationMs ?? 0,
                lastError = stats?.LastError,
                nativeUiOwnsSlot = stats?.NativeUiOwnsSlot ?? false,
                drawCallsPerSecond = Math.Round(stats?.DrawCallsPerSecond ?? 0, 1),
                // alpha probe of the raw render target — min==max==255 means no usable mask;
                // any spread means the game hands us the character cutout for free (see
                // PreviewRenderer._alphaMin doc comment)
                alphaMin = stats?.AlphaMin ?? 0,
                alphaMax = stats?.AlphaMax ?? 0,
                // depth-mask status for the transparent mode (see PreviewRenderer._depthStaging)
                depthMaskReady = stats?.DepthMaskReady ?? false,
                depthFormat = stats?.DepthFormat,
                charTouchesBorder = stats?.CharTouchesBorder ?? false,
                debugKills = renderer?.DebugKillState,
                latestFrameAvailable = renderer?.LatestWebJpeg != null,
                previewGearOverrideSlots = _webPreviewGear.Keys.Select(s => s.ToString()).ToArray(),
                // the actual "is it smooth" answer, no /xllog needed — live while a stream is
                // connected (Character tab open), else the last completed session's numbers.
                streamActive = _streamActive,
                streamCurrentFrames = _streamActive ? _streamSessionFrames : (long?)null,
                streamCurrentSeconds = _streamActive ? Math.Round((Environment.TickCount64 - _streamSessionStartMs) / 1000.0, 1) : (double?)null,
                streamCurrentAvgFps = _streamActive && Environment.TickCount64 > _streamSessionStartMs
                    ? Math.Round(_streamSessionFrames / ((Environment.TickCount64 - _streamSessionStartMs) / 1000.0), 1) : (double?)null,
                lastStreamFrames = _lastStreamFrames,
                lastStreamDurationSeconds = Math.Round(_lastStreamDurationS, 1),
                lastStreamAvgFps = Math.Round(_lastStreamAvgFps, 1),
            });
        }

        if (method == "GET" && path == "/api/model3d/debug")
        {
            var (dbgSlots, dbgChara, dbgPose, dbgColors) = ResolveModelInputs();
            _modelExport.BuildGlb(dbgSlots, dbgChara, bypassCache: true, pose: dbgPose, colors: dbgColors);
            return Json(new { trace = _modelExport.LastTrace, live = dbgPose != null });
        }

        if (method == "GET" && path == "/api/model3d/textures")
        {
            // ponytail: the raw baked/loaded PNGs, unlit and unshaded — for chasing a color bug,
            // read the actual pixel here instead of guessing from a lit 3D render or a lossy
            // screenshot. Index matches "tex=N"/"normal=N" in /api/model3d/debug's trace.
            var (texSlots, texChara, texPose, texColors) = ResolveModelInputs();
            _modelExport.BuildGlb(texSlots, texChara, bypassCache: true, pose: texPose, colors: texColors);
            var sb = new StringBuilder("<html><body style='background:#333;color:#eee;font-family:monospace'>");
            for (var i = 0; i < _modelExport.LastTextures.Count; i++)
            {
                var b64 = Convert.ToBase64String(_modelExport.LastTextures[i]);
                var label = i < _modelExport.LastTextureLabels.Count ? System.Net.WebUtility.HtmlEncode(_modelExport.LastTextureLabels[i]) : "";
                sb.Append($"<div style='margin-bottom:12px'>tex={i} — {label}<br><img src='data:image/png;base64,{b64}' style='max-width:400px;image-rendering:pixelated;border:1px solid #666'></div>");
            }
            return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
        }

        if (method == "GET" && path == "/api/pose/list")
            return Json(new { poses = _savedPoses.Keys.ToArray() });

        if (method == "POST" && path == "/api/action/pose/reset")
        {
            // ponytail: snapshots freeze after first capture (see ResolveModelInputs) — this is
            // the escape hatch for "the frozen one is bad, let me recapture" without a plugin reload.
            var name = query["name"];
            if (string.IsNullOrEmpty(name)) { _savedPoses.Clear(); return Json(new { ok = true, cleared = "all" }); }
            _savedPoses.TryRemove(name, out _);
            return Json(new { ok = true, cleared = name });
        }

        if (method == "GET" && path == "/api/model3d.glb")
        {
            // ponytail: mdl/mtrl/tex parsing is pure file I/O (Lumina + vendored Penumbra parser),
            // safe from any thread — only the skeleton pose read (ResolveModelInputs) touches game
            // memory, and that's already marshalled onto the framework thread.
            try
            {
                var (glbSlots, glbChara, glbPose, glbColors) = ResolveModelInputs();
                // ponytail: default view is the model's own bind pose — no live skeleton capture at
                // all. The self-maintained "idle" snapshot (first-seen-standing, frozen) still
                // inherited every live-capture flakiness (missing physics bones on a transient
                // frame, mid-animation finger curl — "Klauen-Hand" traced back to exactly this).
                // Bind pose needs zero game-memory reads, so none of that can happen: the .mdl's own
                // vertices are already posed, see ModelExportService's own top-of-file comment.
                // ?pose=live forces the raw live capture, ?pose=weapon the saved weapon-drawn
                // snapshot; both still fall back to live if not saved yet.
                var poseName = query["pose"] ?? "idle";
                if (poseName == "idle")
                    glbPose = null;
                else if (poseName != "live" && _savedPoses.TryGetValue(poseName, out var saved))
                    glbPose = saved;
                var glb = _modelExport.BuildGlb(glbSlots, glbChara, bypassCache: true, pose: glbPose, colors: glbColors);
                return glb == null
                    ? ("404 Not Found", "application/octet-stream", Array.Empty<byte>())
                    : ("200 OK", "model/gltf-binary", glb);
            }
            catch (Exception ex)
            {
                _log.Error($"[WebUi] model3d export failed: {ex.Message}");
                return ("500 Internal Server Error", "application/octet-stream", Array.Empty<byte>());
            }
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
            Plugin.BwLockJustToggled = true; // bypass the pin throttle once — see field's doc comment
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
                "GET /", "GET /api/search?q=", "GET /api/item/{id}", "GET /api/snapshot", "GET /api/preview3d/stream",
                "POST /api/action/preview3d/setitem?slot=&itemId=&stain0=&stain1=", "POST /api/action/preview3d/cleargear",
                "POST /api/action/preview3d/reset",
                "GET /api/preview3d/debug",
                "POST /api/action/craftlog/{id}", "POST /api/action/dutyfinder/{cfc}",
                "POST /api/action/map?territory=&map=&x=&y=",
            },
        }, "404 Not Found");
    }

    // ponytail: viewer shows whoever the shell is showing; with no snapshot (nobody clicked yet),
    // fall back to the player's own gear. Body/face/hair always come from the LOCAL player's
    // Customize — Recent snapshots don't store appearance (yet). Framework thread for both.
    private (System.Collections.Generic.IReadOnlyList<GlamSource.Core.EquipmentSlot> Slots, ModelExport.CharacterModelInfo? Chara, ModelExport.SkeletonPose? Pose, ModelExport.CustomizeColors? Colors) ResolveModelInputs()
    {
        var slots = _shell.DebugSnapshot;
        try
        {
            return _framework.RunOnFrameworkThread(() =>
            {
                var effective = slots.Count > 0 ? slots : _glamour.GetSelfEquipment();
                ModelExport.CharacterModelInfo? chara = null;
                ModelExport.SkeletonPose? pose = null;
                ModelExport.CustomizeColors? colors = null;
                if (Plugin.ObjectTable.LocalPlayer is { } pc && pc.Customize is { Length: > 0 } c)
                {
                    var race = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.Race];
                    var tribe = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.Tribe];
                    var gender = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.Gender];
                    var skinColorIdx = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.SkinColor];
                    var hairColorIdx = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.HairColor];
                    // ponytail: the VANILLA customize state is wanted here — explicitly not any
                    // Glamourer-applied design. Glamourer's IPC only serializes ModelData (the
                    // applied design; StateApi.Convert(state.ModelData,...)), its vanilla BaseData
                    // is not IPC-exposed. But pc.Customize IS the vanilla state: proven by three
                    // consecutive live traces whose rawCustomize stayed byte-identical while a Raen
                    // design was applied and removed via Glamourer — the applied design never touches
                    // this array. The earlier 127-vs-112 "mismatch" that sent us down the Glamourer
                    // path was the applied test design, not a wrong read.
                    var debugSrc = $"tribe={tribe} gender={gender} skinIdx={skinColorIdx} hairIdx={hairColorIdx} swatchSrc=own(vanilla) rawCustomize=[{string.Join(",", c.ToArray())}]";
                    try
                    {
                        colors = ModelExport.CmpColorReader.Read(_modelExportGameData, tribe, gender, skinColorIdx, hairColorIdx);
                        if (colors != null) colors = colors.Value with { DebugSource = $"cmp:{debugSrc}" };
                    }
                    catch (Exception ex) { _log.Warning($"[WebUi] human.cmp color read failed: {ex.Message}"); }
                    // ponytail: NO Glamourer Parameters overlay here either — SkinDiffuse/HairDiffuse
                    // from GetState belong to the applied design (ModelData), and this viewer shows
                    // the vanilla character. GlamourerColorIpc stays available if a "show the applied
                    // design" mode is ever wanted.
                    if (colors == null)
                    {
                        try { colors = ModelExport.CustomizeColorsService.Capture(pc.Address); }
                        catch (Exception ex) { _log.Warning($"[WebUi] customize color capture failed: {ex.Message}"); }
                    }
                    // ponytail: highlight color for the hair-mask bake (see ModelExportService's
                    // hair handling) — only meaningful when the player actually enabled highlights
                    // (CustomizeIndex.HasHighlights' 0x80 bit; Dalamud's own doc comment: "negative
                    // to enable"). Best-effort: colors stays usable even if this fails.
                    if (colors != null)
                    {
                        try
                        {
                            var hasHighlights = (c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.HasHighlights] & 0x80) != 0;
                            if (hasHighlights)
                            {
                                var highlightIdx = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.HairColor2];
                                var highlight = ModelExport.CmpColorReader.ReadHighlightColor(_modelExportGameData, highlightIdx);
                                if (highlight != null) colors = colors.Value with { Highlight = highlight };
                            }
                        }
                        catch (Exception ex) { _log.Warning($"[WebUi] highlight color read failed: {ex.Message}"); }
                    }
                    // ponytail: the iris material's own base texture is a neutral grayscale — same
                    // situation as skin's base.tex — and needs this tint or it renders white/gray
                    // instead of the character's actual eye color. Right eye only for now (left can
                    // differ via EyeColor2, but the model's two eye submeshes share one material).
                    if (colors != null)
                    {
                        try
                        {
                            var eyeIdx = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.EyeColor];
                            var eye = ModelExport.CmpColorReader.ReadEyeColor(_modelExportGameData, eyeIdx);
                            if (eye != null) colors = colors.Value with { Eye = eye };
                        }
                        catch (Exception ex) { _log.Warning($"[WebUi] eye color read failed: {ex.Message}"); }
                    }
                    // ponytail: face decals (moles/scars/tattoo lines — CustomizeIndex.FaceFeatures,
                    // a bitmask of which are toggled on) get tinted by FaceFeaturesColor, NOT hair
                    // color — first guess used hairTint, wrong per a real Glamourer readout showing
                    // Feature Color as a distinct value from Hair Color. Only set Feature when the
                    // bitmask is actually nonzero — a character with zero features toggled should
                    // render none, not a guessed fallback.
                    if (colors != null)
                    {
                        try
                        {
                            var featureBits = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.FaceFeatures];
                            colors = colors.Value with { FaceFeatures = featureBits };
                            if (featureBits != 0)
                            {
                                var featureColorIdx = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.FaceFeaturesColor];
                                var feature = ModelExport.CmpColorReader.ReadFeatureColor(_modelExportGameData, featureColorIdx);
                                if (feature != null) colors = colors.Value with { Feature = feature };
                            }
                        }
                        catch (Exception ex) { _log.Warning($"[WebUi] feature color read failed: {ex.Message}"); }
                    }
                    var face = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.FaceType];
                    var hair = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.HairStyle];
                    var tail = c[(int)Dalamud.Game.ClientState.Objects.Enums.CustomizeIndex.RaceFeatureType];
                    chara = new ModelExport.CharacterModelInfo(
                        ModelExport.CharacterModelInfo.ResolveRaceCode(race, tribe, gender), face, hair, tail);
                    // ponytail: whatever the character is doing RIGHT NOW — idle, weapon drawn,
                    // sitting, mid-emote — read straight from the already-computed live skeleton.
                    // No .pap/animation parsing; see SkeletonPoseService for why that's unneeded.
                    try { pose = ModelExport.SkeletonPoseService.Capture(pc.Address); }
                    catch (Exception ex) { _log.Warning($"[WebUi] skeleton pose capture failed: {ex.Message}"); }

                    // ponytail: self-maintaining "idle"/"weapon" snapshots — no user action needed,
                    // and captured ONCE then frozen (not continuously refreshed) so the pose stays
                    // exactly the same across views instead of drifting with the idle animation's
                    // breathing/sway loop every time the character happens to be observed idling.
                    if (pose != null)
                    {
                        var busy = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Emoting]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene];
                        unsafe
                        {
                            var chr = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)pc.Address;
                            if (chr->IsWeaponDrawn && !busy) _savedPoses.TryAdd("weapon", pose);
                            else if (!chr->IsWeaponDrawn && !busy) _savedPoses.TryAdd("idle", pose);
                        }
                    }
                }
                return (effective, chara, pose, colors);
            }).GetAwaiter().GetResult();
        }
        catch { return (slots, null, null, null); }
    }

    private static (string, string, byte[]) Json(object payload, string status = "200 OK")
        => (status, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts)));

    // "hat man das mount oder minion schon unlocked, überall" — only Mount/Minion-kind drops carry a
    // native unlock check (everything else has no such concept), so only those get annotated; skips
    // the check entirely for the rest instead of doing a pointless lookup.
    private DutyDrop Annotate(DutyDrop d) => d.Kind is "Mount" or "Minion"
        ? d with { Unlocked = UnlockCheckService.CheckUnlocked(_detail, d.ItemId) } : d;

    private DutyDetail AnnotateUnlocks(DutyDetail dd) => dd with
    {
        General = dd.General.Select(Annotate).ToList(),
        Featured = dd.Featured.Select(Annotate).ToList(),
        Bosses = dd.Bosses.Select(b => b with
        {
            Drops = b.Drops.Select(Annotate).ToList(),
            Chests = b.Chests.Select(c => c with { Items = c.Items.Select(Annotate).ToList() }).ToList(),
        }).ToList(),
        Exchanges = dd.Exchanges.Select(e => e with
        {
            Token = e.Token == null ? null : Annotate(e.Token),
            Items = e.Items.Select(Annotate).ToList(),
        }).ToList(),
    };

    public void Dispose()
    {
        Stop();
        _imageService.Dispose();
        (_universalis as IDisposable)?.Dispose();
    }
}
