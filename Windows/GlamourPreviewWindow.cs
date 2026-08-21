using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource.Services;

namespace GlamSource.Windows;

// ponytail: Ktisis PreviewNode reduced to a Dalamud Window; no bone plumbing, no pose file logic.
public sealed unsafe class GlamourPreviewWindow : Window, IDisposable
{
    private readonly PreviewRenderer _renderer;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ITargetManager _targetManager;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    private bool _frameworkHooked;
    private Vector2? _lastDragPos;

    public GlamourPreviewWindow(
        PreviewRenderer renderer,
        IFramework framework,
        IClientState clientState,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IPluginLog log)
        : base("GlamSource 3D Preview", ImGuiWindowFlags.None)
    {
        _renderer = renderer;
        _framework = framework;
        _clientState = clientState;
        _targetManager = targetManager;
        _objectTable = objectTable;
        _log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        SizeCondition = ImGuiCond.FirstUseEver;

        _clientState.Logout += OnLogout;
    }

    public void OpenForCurrentTarget()
    {
        // Prefer target, fall back to LocalPlayer.
        var src = (IGameObject?)_targetManager.Target ?? _objectTable.LocalPlayer;
        if (src == null)
        {
            _log.Info("[GlamourPreviewWindow] No target or local player; open ignored.");
            return;
        }

        var addr = src.Address;
        _framework.RunOnFrameworkThread(() =>
        {
            _renderer.Release();
            _renderer.Initialize((Character*)addr);
        });

        IsOpen = true;
    }

    public override void OnOpen()
    {
        if (!_frameworkHooked)
        {
            _framework.Update += OnFrameworkTick;
            _frameworkHooked = true;
        }
    }

    public override void OnClose()
    {
        if (_frameworkHooked)
        {
            _framework.Update -= OnFrameworkTick;
            _frameworkHooked = false;
        }
        _framework.RunOnFrameworkThread(_renderer.Release);
    }

    private void OnFrameworkTick(IFramework fw)
    {
        if (!_clientState.IsLoggedIn) return;

        // Pause when game's own inspect agent is up — avoids fighting it for CharaView state.
        var agent = AgentInspect.Instance();
        if (agent != null && agent->AgentInterface.IsAgentActive() && !_renderer.IsInitialized)
            return;

        _renderer.Tick();
    }

    public override void Draw()
    {
        if (!_renderer.IsInitialized)
        {
            ImGui.TextDisabled("Preview not initialized.");
            if (ImGui.Button("Init from Target"))
                OpenForCurrentTarget();
            return;
        }

        if (ImGui.SmallButton("Reset"))
            _framework.RunOnFrameworkThread(_renderer.Reset);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reload from Target"))
            OpenForCurrentTarget();
        ImGui.SameLine();
        ImGui.TextDisabled("Drag image to rotate");

        var handle = _renderer.GetTextureHandle();
        if (handle == 0)
        {
            ImGui.TextDisabled("Waiting for texture...");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        // Portrait-ish aspect matching the CharaView render target (~192:320 = 0.6).
        var w = MathF.Max(160f, avail.X);
        var h = MathF.Max(240f, avail.Y - ImGui.GetFontSize());
        var target = w / h > 0.6f ? new Vector2(h * 0.6f, h) : new Vector2(w, w / 0.6f);

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(handle), target);

        HandleDrag(cursor, target);
    }

    private void HandleDrag(Vector2 imgTopLeft, Vector2 imgSize)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inside = mouse.X >= imgTopLeft.X && mouse.X <= imgTopLeft.X + imgSize.X
                  && mouse.Y >= imgTopLeft.Y && mouse.Y <= imgTopLeft.Y + imgSize.Y;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && (inside || _lastDragPos.HasValue))
        {
            if (_lastDragPos.HasValue)
            {
                var delta = mouse - _lastDragPos.Value;
                if (delta.LengthSquared() > 0f)
                {
                    // Scale mouse delta to camera deltas; Ktisis uses roughly ±50 units per button click.
                    var yaw = delta.X * 0.75f;
                    var pitch = delta.Y * 0.75f;
                    _framework.RunOnFrameworkThread(() => _renderer.SetYawPitch(yaw, pitch));
                }
            }
            _lastDragPos = mouse;
        }
        else
        {
            _lastDragPos = null;
        }
    }

    private void OnLogout(int type, int code)
    {
        _framework.RunOnFrameworkThread(_renderer.Release);
        IsOpen = false;
    }

    public void Dispose()
    {
        _clientState.Logout -= OnLogout;
        if (_frameworkHooked)
        {
            _framework.Update -= OnFrameworkTick;
            _frameworkHooked = false;
        }
        _renderer.Dispose();
    }
}
