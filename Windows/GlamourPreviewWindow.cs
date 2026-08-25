using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using GlamSource.Core;
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
    private readonly IGlamourService _glamour;

    private bool _frameworkHooked;
    private Vector2? _lastDragPos;
    private PreviewMode _mode = PreviewMode.CurrentGear;

    // ponytail: cache target by EntityId, not ObjectIndex (ObjectIndex is flaky during game updates).
    private uint _targetEntityId = 0;
    // Who the preview is currently showing (0 = self).
    private uint _sourceEntityId;
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
        IPluginLog log,
        IGlamourService glamour)
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
        _glamour = glamour;

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
        _sourceEntityId = 0;
        if (_renderer.IsInitialized)
        {
            if (!_frameworkHooked)
            {
                _framework.Update += OnFrameworkTick;
                _frameworkHooked = true;
            }
            // ponytail: keep the recopy pump alive so the CharaView refreshes each frame
            // even when the game's TryOn agent is inactive (Fitting Room never opened).
            _renderer.RequestRecopy(2);
            return;
        }

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null) return;
        var selfAddr = localPlayer.Address;

        _framework.RunOnFrameworkThread(() =>
        {
            _renderer.Initialize((Character*)selfAddr, 0, () => _objectTable.LocalPlayer?.Address ?? nint.Zero);
        });

        if (!_frameworkHooked)
        {
            _framework.Update += OnFrameworkTick;
            _frameworkHooked = true;
        }
    }

    // ponytail: any equipped ItemId works to activate AgentTryon. Pick the first non-zero slot
    // from the live self read; returns 0 if nothing is equipped (caller retries next frame).
    private uint ResolveWarmupItemId()
    {
        try
        {
            var eq = _glamour.GetSelfEquipment();
            foreach (var slot in eq)
            {
                var id = slot.GlamourItemId ?? slot.ActualItemId;
                if (id != 0) return id;
            }
        }
        catch (Exception ex) { _log.Warning($"[GlamourPreviewWindow] ResolveWarmupItemId: {ex.Message}"); }
        return 0;
    }

    // ponytail: body stays self; equipment overlay is a Shell-owned provider. One owner per Tick:
    // Shell resolves recent/pinned/target/self and hands us the callback; Renderer writes via canonical
    // SetItemSlotData into _items (what render pipeline actually reads — CopyFromCharacter only touches
    // ModelData @0x48 and can't fill _items @0xF8 → warmup-item + zeros bug without an explicit overlay).
    public void SetSnapshotProvider(Func<IReadOnlyList<EquipmentSlot>?>? provider)
    {
        if (!_renderer.IsInitialized)
            EnsureInitializedForSelf();
        _framework.RunOnFrameworkThread(() => _renderer.SetEquipmentSnapshot(provider));
    }

    // ponytail: legacy entry — kept as a thin wrapper so external callsites (Plugin.cs etc.) still resolve.
    public void ShowCharacterInPreview(uint entityId)
    {
        if (entityId == 0) { SetSnapshotProvider(null); return; }
        uint id = entityId;
        SetSnapshotProvider(() =>
        {
            var obj = _objectTable.SearchByEntityId(id);
            return obj == null ? null : _glamour.TryGetVisibleGlamour(obj.ObjectIndex);
        });
    }

    // ponytail: delta orbit for the shell's inline preview; mirrors HandleDrag's dispatch.
    public void SetYawPitch(float dx, float dy)
    {
        _framework.RunOnFrameworkThread(() => _renderer.SetYawPitch(dx, dy));
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

        // ponytail: Target-Glam renders the target's own character (live per-tick copy) instead of
        // ApplyState-ing its glam onto LocalPlayer — no async propagation, no Fitting Room needed.
        _framework.RunOnFrameworkThread(() =>
        {
            _renderer.Release();

            uint sourceEntityId = 0;
            if (_mode == PreviewMode.TargetGlam && _targetEntityId != 0)
            {
                var found = _objectTable.SearchByEntityId(_targetEntityId);
                if (found != null && found != localPlayer)
                    sourceEntityId = _targetEntityId;
            }

            if (sourceEntityId == 0)
            {
                _sourceEntityId = 0;
                _renderer.Initialize((Character*)localPlayer.Address, 0,
                    () => _objectTable.LocalPlayer?.Address ?? nint.Zero);
            }
            else
            {
                var targetObj = _objectTable.SearchByEntityId(sourceEntityId);
                if (targetObj == null)
                {
                    _log.Warning($"[GlamourPreviewWindow] Target not found (EntityId={sourceEntityId}), showing self");
                    _sourceEntityId = 0;
                    _renderer.Initialize((Character*)localPlayer.Address, ResolveWarmupItemId(),
                        () => _objectTable.LocalPlayer?.Address ?? nint.Zero);
                    return;
                }

                _log.Info($"[GlamourPreviewWindow] Show target: EntityId={sourceEntityId}");
                _sourceEntityId = sourceEntityId;
                // Live provider: Tick re-copies the target each frame, so the preview tracks the target live.
                _renderer.Initialize((Character*)targetObj.Address, 0,
                    () => _objectTable.SearchByEntityId(_sourceEntityId)?.Address ?? nint.Zero);
            }

            // Keep a self snapshot so Recent/close paths can still restore own glam.
            if (_mode == PreviewMode.TargetGlam)
                TrySnapshotSelfOnce();
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

        // If we were showing the target, switch back to self before releasing and restore
        // own glam if a snapshot exists (defensive — Target-Glam itself no longer mutates self).
        if (_mode == PreviewMode.TargetGlam && _sourceEntityId != 0)
        {
            var lp = _objectTable.LocalPlayer;
            if (lp != null)
            {
                _framework.RunOnFrameworkThread(() => _renderer.SetSource(
                    lp.Address, ResolveWarmupItemId(),
                    () => _objectTable.LocalPlayer?.Address ?? nint.Zero));
            }

            var snap = _selfSnapshot;
            if (snap != null)
            {
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
        }

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

    // Framework thread only. Mode switch = source switch; no glam mutation of LocalPlayer.
    private void ApplyModeState()
    {
        if (!IsGlamourerInstalled() || _mode == PreviewMode.CurrentGear)
        {
            if (_sourceEntityId != 0)
            {
                _sourceEntityId = 0;
                var lp = _objectTable.LocalPlayer;
                if (lp != null)
                {
                    _framework.RunOnFrameworkThread(() => _renderer.SetSource(
                        lp.Address, ResolveWarmupItemId(),
                        () => _objectTable.LocalPlayer?.Address ?? nint.Zero));
                }
            }
            return;
        }

        if (_targetEntityId != 0 && _sourceEntityId != _targetEntityId)
        {
            _sourceEntityId = _targetEntityId;
            var targetObj = _objectTable.SearchByEntityId(_targetEntityId);
            if (targetObj != null)
            {
                _framework.RunOnFrameworkThread(() => _renderer.SetSource(
                    targetObj.Address, ResolveWarmupItemId(),
                    () => _objectTable.SearchByEntityId(_sourceEntityId)?.Address ?? nint.Zero));
            }
        }
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
                else
                    _log.Info("[GlamourPreviewWindow] Restored own glamour.");
            }
            catch (Exception ex)
            {
                _log.Warning($"[GlamourPreviewWindow] Restore on close error: {ex.Message}");
            }
        });
    }

    // ponytail: capture LocalPlayer glamour once, so the shell can mutate self via SetItem
    // and later restore. Silent no-op if Glamourer missing or snapshot already stored.
    public void TrySnapshotSelfOnce()
    {
        if (_selfSnapshot != null) return;
        if (!IsGlamourerInstalled())
        {
            _log.Warning("[GlamourPreviewWindow] TrySnapshotSelfOnce: Glamourer not installed; skipping.");
            return;
        }
        try
        {
            var (ec, state) = new GetStateBase64(_pi).Invoke(0, 0);
            if (ec == GlamourerApiEc.Success && state != null)
                _selfSnapshot = state;
            else
                _log.Warning($"[GlamourPreviewWindow] TrySnapshotSelfOnce: GetStateBase64 failed: {ec}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[GlamourPreviewWindow] TrySnapshotSelfOnce error: {ex.Message}");
        }
    }

    // ponytail: public entry for the shell to restore LocalPlayer's original glamour
    // when the user clears/leaves the Recent preview.
    public void RestoreSelf() => RestoreSnapshotIfAny();

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
        // ponytail: always restore before tearing down — otherwise LocalPlayer stays glamoured
        // if the plugin is disposed while a Recent preview is active.
        RestoreSnapshotIfAny();
        _clientState.Logout -= OnLogout;
        if (_frameworkHooked)
        {
            _framework.Update -= OnFrameworkTick;
            _frameworkHooked = false;
        }
        _renderer.Dispose();
    }
}
