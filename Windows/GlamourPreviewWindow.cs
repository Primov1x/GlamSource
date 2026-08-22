using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using GlamSource.Services;

namespace GlamSource.Windows;

// ponytail: Ktisis PreviewNode reduced to a Dalamud Window; no bone plumbing, no pose file logic.
public sealed unsafe class GlamourPreviewWindow : Window, IDisposable
{
    public enum PreviewMode
    {
        TargetGlam,
        CurrentGear,
    }

    private readonly PreviewRenderer _renderer;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targetManager;
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;

    private bool _frameworkHooked;
    private Vector2? _lastDragPos;
    private PreviewMode _mode = PreviewMode.CurrentGear;

    // ponytail: cache target by EntityId, not ObjectIndex (ObjectIndex is flaky during game updates).
    private uint _targetEntityId = 0;
    // ponytail: base64 string, not JObject — JObject crosses IPC unreliably (assembly-identity mismatch on the Newtonsoft type);
    // GetStateBase64/ApplyState(string) are the IPC-safe endpoints the current Glamourer ships.
    private string? _selfSnapshot;

    public GlamourPreviewWindow(
        PreviewRenderer renderer,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    // ponytail: NoScrollWithMouse so mouse wheel goes to zoom handler, not window scroll.
    : base("GlamSource 3D Preview", ImGuiWindowFlags.NoScrollWithMouse)
    {
        _renderer = renderer;
        _framework = framework;
        _clientState = clientState;
        _objectTable = objectTable;
        _targetManager = targetManager;
        _pi = pluginInterface;
        _log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        SizeCondition = ImGuiCond.FirstUseEver;

        _clientState.Logout += OnLogout;
    }

    // ponytail: inline preview lives in the Character tab. Init the renderer + framework tick
    // without flipping IsOpen so the shell can render the texture inside its own BeginChild.
    public void EnsureInitializedForSelf()
    {
        if (_renderer.IsInitialized)
        {
            if (!_frameworkHooked)
            {
                _framework.Update += OnFrameworkTick;
                _frameworkHooked = true;
            }
            return;
        }

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null) return;
        var selfAddr = localPlayer.Address;

        _framework.RunOnFrameworkThread(() =>
        {
            _renderer.Initialize((Character*)selfAddr, () => _objectTable.LocalPlayer?.Address ?? nint.Zero);
        });

        if (!_frameworkHooked)
        {
            _framework.Update += OnFrameworkTick;
            _frameworkHooked = true;
        }
    }

    // ponytail: shell needs to re-apply target glam to CharaView when Recent-hover previews.
    public void ApplyTargetGlamToPreview(uint targetEntityId)
    {
        _targetEntityId = targetEntityId;
        _mode = targetEntityId != 0 ? PreviewMode.TargetGlam : PreviewMode.CurrentGear;
        _framework.RunOnFrameworkThread(ApplyModeState);
    }

    public PreviewRenderer Renderer => _renderer;

    // ponytail: legacy entry-point kept so /glamsource preview + shell button still work.
    public void OpenForCurrentTarget()
    {
        var tgt = _targetManager.Target;
        OpenForTarget(tgt);
    }

    /// <summary>Open the preview; if <paramref name="target"/> is non-null, defaults to Target-Glam mode.</summary>
    public void OpenForTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject? target)
    {
        _targetEntityId = target?.EntityId ?? 0;
        _mode = target != null ? PreviewMode.TargetGlam : PreviewMode.CurrentGear;

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            _log.Info("[GlamourPreviewWindow] No local player; open ignored.");
            return;
        }

        var selfAddr = localPlayer.Address;

        _framework.RunOnFrameworkThread(() =>
        {
            _renderer.Release();
            // ponytail: pass a live LocalPlayer-address resolver so Tick can re-copy each frame
            // (ApplyState lands async; without this the viewer shows pre-apply gear).
            _renderer.Initialize((Character*)selfAddr, () => _objectTable.LocalPlayer?.Address ?? nint.Zero);
            ApplyModeState();
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
        // ponytail: always restore snapshot on close — user should not be silently left in target's glam.
        RestoreSnapshotIfAny();
        _framework.RunOnFrameworkThread(_renderer.Release);
    }

    private void OnFrameworkTick(IFramework fw)
    {
        if (!_clientState.IsLoggedIn) return;
        _renderer.Tick();
    }

    public override void PreDraw()  => UiStyle.PushWindow();
    public override void PostDraw() => UiStyle.PopWindow();

    public override void Draw()
    {
        using var _style = UiStyle.Push();

        if (!_renderer.IsInitialized)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiStyle.Muted, "Preview is idle.");
            ImGui.Spacing();
            if (ImGui.Button("Init from Self"))
                OpenForTarget(null);
            return;
        }

        var hasTarget = _targetEntityId != 0;
        if (ImGui.RadioButton("Target Glamour##previewMode", _mode == PreviewMode.TargetGlam)
            && _mode != PreviewMode.TargetGlam && hasTarget)
        {
            _mode = PreviewMode.TargetGlam;
            _framework.RunOnFrameworkThread(ApplyModeState);
        }
        if (!hasTarget && ImGui.IsItemHovered())
            ImGui.SetTooltip("No target stored — reopen with a target to enable.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Aktuelle Ausrustung##previewMode", _mode == PreviewMode.CurrentGear)
            && _mode != PreviewMode.CurrentGear)
        {
            _mode = PreviewMode.CurrentGear;
            _framework.RunOnFrameworkThread(ApplyModeState);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset"))
            _framework.RunOnFrameworkThread(_renderer.Reset);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reload"))
            _framework.RunOnFrameworkThread(ApplyModeState);
        ImGui.SameLine();
        ImGui.TextDisabled("Drag image to rotate");

        var zoom = _renderer.Zoom;
        if (ImGui.SliderFloat("Zoom", ref zoom, 0.5f, 3.0f))
            _framework.RunOnFrameworkThread(() => _renderer.SetZoom(zoom));

        var handle = _renderer.GetTextureHandle();
        if (handle == 0)
        {
            ImGui.TextDisabled("Waiting for texture...");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var w = MathF.Max(160f, avail.X);
        var h = MathF.Max(240f, avail.Y - ImGui.GetFontSize());
        var target = w / h > 0.6f ? new Vector2(h * 0.6f, h) : new Vector2(w, w / 0.6f);

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(handle), target);

        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##previewDrag", target);

        // ponytail: wheel-zoom while hovering the image; SetZoom already clamps 0.5–3.0.
        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                var newZoom = _renderer.Zoom + wheel * 0.1f;
                _framework.RunOnFrameworkThread(() => _renderer.SetZoom(newZoom));
            }
        }

        HandleDrag(cursor, target);
    }

    // Framework thread only.
    private void ApplyModeState()
    {
        if (!IsGlamourerInstalled())
        {
            // ponytail: without Glamourer we can't snapshot/copy; just refresh CharaView from LocalPlayer.
            RefreshCharaViewFromLocalPlayer();
            return;
        }

        try
        {
            if (_selfSnapshot == null)
            {
                GetStateBase64? getStateInstance = new(_pi);
                if (!getStateInstance.Valid)
                {
                    _log.Error("[GlamourPreviewWindow] GetStateBase64 IPC not available");
                    return;
                }

                var (ecGet, state) = getStateInstance.Invoke(0, 0);
                if (ecGet == GlamourerApiEc.Success && state != null)
                    _selfSnapshot = state;
                else
                    _log.Warning($"[GlamourPreviewWindow] GetStateBase64(self) failed: {ecGet}");
            }

            Dalamud.Game.ClientState.Objects.Types.IGameObject? targetObj = null;
            if (_mode == PreviewMode.TargetGlam && _targetEntityId != 0)
            {
                var found = _objectTable.SearchByEntityId(_targetEntityId);
                if (found == null || found == _objectTable.LocalPlayer)
                {
                    _log.Warning($"[GlamourPreviewWindow] Target not found (EntityId={_targetEntityId}), falling back to self");
                    _mode = PreviewMode.CurrentGear;
                }
                else
                {
                    _log.Info($"[GlamourPreviewWindow] Target resolved: EntityId={_targetEntityId} ObjectIndex={found.ObjectIndex}");
                    targetObj = found;
                }
            }

            if (targetObj != null)
            {
                GetStateBase64? getStateInstance = new(_pi);
                if (getStateInstance.Valid)
                {
                    var (ecTgt, tgtState) = getStateInstance.Invoke(targetObj.ObjectIndex, 0);
                    if (ecTgt == GlamourerApiEc.Success && tgtState != null)
                    {
                        var ecApp = new ApplyState(_pi).Invoke(tgtState, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment);
                        if (ecApp != GlamourerApiEc.Success)
                            _log.Warning($"[GlamourPreviewWindow] ApplyState(target->self) failed: {ecApp}");
                    }
                    else
                    {
                        _log.Warning($"[GlamourPreviewWindow] GetState(target={targetObj.ObjectIndex}, EntityId={_targetEntityId}) failed: {ecTgt}");
                    }
                }
            }
            else
            {
                // CurrentGear: restore snapshot if we mutated earlier.
                if (_selfSnapshot != null)
                {
                    var ec = new ApplyState(_pi).Invoke(_selfSnapshot, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment);
                    if (ec != GlamourerApiEc.Success)
                        _log.Warning($"[GlamourPreviewWindow] ApplyState(restore) failed: {ec}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[GlamourPreviewWindow] ApplyModeState error: {ex.Message}");
        }

        // ponytail: no immediate CharaView refresh here — it would copy pre-apply gear, since
        // ApplyState lands in the model a frame or two later. Let Tick re-copy from LocalPlayer
        // until the post-apply model is in (target apply mutates more → longer window).
        _renderer.RequestRecopy(_mode == PreviewMode.TargetGlam ? 15 : 5);
    }

    private void RefreshCharaViewFromLocalPlayer()
    {
        var lp = _objectTable.LocalPlayer;
        if (lp == null) return;
        var agent = AgentTryon.Instance();
        if (agent == null || !_renderer.IsInitialized) return;
        agent->CharaView.ModelData.CopyFromCharacter((Character*)lp.Address);
    }

    private void RestoreSnapshotIfAny()
    {
        var snap = _selfSnapshot;
        _selfSnapshot = null;
        if (snap == null) return;
        if (!IsGlamourerInstalled()) return;

        _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var ec = new ApplyState(_pi).Invoke(snap, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment);
                if (ec != GlamourerApiEc.Success)
                    _log.Warning($"[GlamourPreviewWindow] Restore on close failed: {ec}");
            }
            catch (Exception ex)
            {
                _log.Warning($"[GlamourPreviewWindow] Restore on close error: {ex.Message}");
            }
        });
    }

    private bool IsGlamourerInstalled()
    {
        try
        {
            var (major, _) = new ApiVersion(_pi).Invoke();
            return major > 0;
        }
        catch
        {
            return false;
        }
    }

    private void HandleDrag(Vector2 imgTopLeft, Vector2 imgSize)
    {
        _ = imgTopLeft; _ = imgSize;
        var mouse = ImGui.GetIO().MousePos;

        if (ImGui.IsItemActive())
        {
            if (_lastDragPos.HasValue)
            {
                var delta = mouse - _lastDragPos.Value;
                if (delta.LengthSquared() > 0f)
                {
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
        _selfSnapshot = null;
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
