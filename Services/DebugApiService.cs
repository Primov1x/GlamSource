using Dalamud.Plugin.Services;
using GlamSource.Windows;
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlamSource.Services;

// ponytail: dev-only, read-only. No endpoint writes state or simulates input.
public sealed class DebugApiService : IDisposable
{
    private const string Prefix = "http://127.0.0.1:23423/";

    private readonly GlamSourceShellWindow _shellWindow;
    private readonly IPluginLog _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public DebugApiService(GlamSourceShellWindow shellWindow, IPluginLog log)
    {
        _shellWindow = shellWindow;
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
            _log.Information($"[DebugApi] listening on {Prefix}");
        }
        catch (Exception ex)
        {
            _log.Error($"[DebugApi] failed to start: {ex.Message}");
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
            catch { return; } // listener stopped/disposed

            try { HandleRequest(ctx); }
            catch (Exception ex) { _log.Error($"[DebugApi] request error: {ex.Message}"); }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        object payload = ctx.Request.Url?.AbsolutePath switch
        {
            "/debug/snapshot" => new
            {
                slots = _shellWindow.DebugSnapshot,
                activeRecentName = _shellWindow.DebugActiveRecentName,
                isRecentOverrideActive = _shellWindow.DebugIsRecentOverrideActive,
            },
            _ => new { error = "unknown endpoint", available = new[] { "/debug/snapshot" } },
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose() => Stop();
}
