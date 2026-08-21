using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamSource.Services;

// ponytail: minimal Ktisis-CharaView port — no bones, no penumbra, no dialog wiring.
// Owns lifecycle of AgentInspect.CharaView (clientObjectIndex=1) plus a per-frame counter.
// All game-state calls must run on the Framework thread.
public sealed unsafe class PreviewRenderer : IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private uint _counter = 1;
    private bool _initialized;

    public PreviewRenderer(IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;
    }

    public bool IsInitialized => _initialized;

    /// <summary>Initialize CharaView from a source character. Must be called on Framework thread.</summary>
    public void Initialize(Character* source)
    {
        if (_initialized) return;
        if (source == null) return;

        var agent = AgentInspect.Instance();
        if (agent == null) return;

        agent->CharaView.Initialize(&agent->AgentInterface, 1, 0);
        agent->CharaView.ModelData.CopyFromCharacter(source);
        agent->CharaView.Update(_counter, agent->CharaView.GetCharacter());
        _initialized = true;
    }

    /// <summary>Per-frame update/render. Must be called on Framework thread.</summary>
    public void Tick()
    {
        if (!_initialized) return;
        var agent = AgentInspect.Instance();
        if (agent == null) return;

        var ch = agent->CharaView.GetCharacter();
        if (ch == null) return;

        agent->CharaView.Update(_counter, ch);
        agent->CharaView.Render(_counter++);
    }

    public void SetYawPitch(float yaw, float pitch)
    {
        if (!_initialized) return;
        var agent = AgentInspect.Instance();
        if (agent == null) return;
        agent->CharaView.SetCameraYawAndPitch(yaw, pitch);
    }

    public void Reset()
    {
        if (!_initialized) return;
        var agent = AgentInspect.Instance();
        if (agent == null) return;
        agent->CharaView.ResetPositions();
    }

    /// <summary>Get the SRV handle for ImGui.Image, or 0 if not ready.</summary>
    public nint GetTextureHandle()
    {
        if (!_initialized) return 0;
        var rtm = RenderTargetManager.Instance();
        if (rtm == null) return 0;
        var tex = rtm->GetCharaViewTexture(1);
        if (tex == null) return 0;
        return (nint)tex->D3D11ShaderResourceView;
    }

    /// <summary>Release CharaView. Must be called on Framework thread.</summary>
    public void Release()
    {
        if (!_initialized) return;
        try
        {
            var agent = AgentInspect.Instance();
            if (agent != null)
                agent->CharaView.Release();
        }
        catch (Exception ex)
        {
            _log.Warning($"[PreviewRenderer] Release failed: {ex.Message}");
        }
        finally
        {
            _initialized = false;
            _counter = 1;
        }
    }

    public void Dispose()
    {
        if (!_initialized) return;
        // Dispose can be called off-frame; hop to Framework thread for the Release.
        try { _framework.RunOnFrameworkThread(Release).Wait(); }
        catch (Exception ex) { _log.Warning($"[PreviewRenderer] Dispose Release failed: {ex.Message}"); }
    }
}
