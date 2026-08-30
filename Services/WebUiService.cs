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
        _modelExport = new ModelExport.ModelExportService(detail.GameData);
        _modelExportGameData = detail.GameData;
        _glamourerColors = new GlamourerColorIpc(pi);
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

    private void HandleClient(TcpClient client, CancellationToken token)
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

        // ponytail: long-lived multipart stream, not a single (status, body) response — handled
        // separately from Route()'s request/response model. See StreamPreviewMjpeg's doc comment.
        if (method == "GET" && path == "/api/preview3d/stream")
        {
            StreamPreviewMjpeg(stream, token);
            return;
        }

        var (status, contentType, body) = Route(method, path, query);
        // ponytail: no-store — Browsingway's CEF page is long-lived and any caching here means an
        // update ships but the overlay silently keeps serving the old HTML/JS.
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
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
            "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n"));
        stream.Flush();

        var swStart = Environment.TickCount64;
        var framesSent = 0;
        byte[]? lastSent = null;
        _streamActive = true;
        _streamSessionStartMs = swStart;
        _streamSessionFrames = 0;
        _log.Information("[WebUi] preview3d/stream connected");
        try
        {
            while (!token.IsCancellationRequested && _configuration.WebUiLive3DPreview)
            {
                var jpeg = _shell.PreviewWindow?.Renderer.LatestWebJpeg;
                if (jpeg != null && !ReferenceEquals(jpeg, lastSent))
                {
                    lastSent = jpeg;
                    stream.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
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
            _log.Information($"[WebUi] preview3d setitem: slot={setSlot} itemId={setItemId} stain0={setStain0} stain1={setStain1} (now {_webPreviewGear.Count} slot(s) overridden)");
            return Json(new { ok = true, slots = _webPreviewGear.Count });
        }

        if (method == "POST" && path == "/api/action/preview3d/cleargear" && _configuration.WebUiLive3DPreview)
        {
            _webPreviewGear.Clear();
            _shell.PreviewWindow?.SetSnapshotProvider(null);
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
            _log.Information("[WebUi] preview3d/reset — forced Release()+Initialize()");
            return Json(new { ok = true });
        }

        if (method == "GET" && path == "/api/preview3d/debug")
        {
            // ponytail: mirrors /api/model3d/debug's "don't guess, look at the actual counters"
            // philosophy — for the MJPEG pipeline (untested in practice at time of writing), so a bad
            // first live run is diagnosable here instead of squinting at the video feed.
            var renderer = _shell.PreviewWindow?.Renderer;
            var stats = renderer?.GetWebCaptureStats();
            return Json(new
            {
                enabled = _configuration.WebUiLive3DPreview,
                rendererInitialized = renderer?.IsInitialized ?? false,
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

    public void Dispose() => Stop();
}
