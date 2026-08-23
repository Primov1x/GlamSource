using System;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamSource.Services;

// ponytail: minimal Ktisis-CharaView port — no bones, no penumbra, no dialog wiring.
// Owns lifecycle of AgentTryon.CharaView (CharaViewSlot) plus a per-frame counter.
// All game-state calls must run on the Framework thread.
public sealed unsafe class PreviewRenderer : IDisposable
{
    // ponytail: CharaView texture slot. 0=Character, 1=Inspect/CharaCard/Fashion, 2=TryOn/
    // GearSetPreview, 3=Colorant, 4=Banners (see FFXIVClientStructs CharaView header).
    // Slot 1 is what game Examine renders into — that's the conflict we were in.
    // Slot 2 matches the agent we drive (AgentTryon); only lost if the game's TryOn opens.
    private const uint CharaViewSlot = 2;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private uint _counter = 1;
    private bool _initialized;
    private float _zoom = 1.0f;
    // ponytail: source resolver stored across frames — Character* would go stale, a func won't.
    private Func<nint>? _sourceProvider;
    // ponytail: force re-copy for N Ticks after ApplyState / Examine hijacks CharaView.ModelData.
    private int _pendingRecopyFrames;
    // ponytail: when true, Tick() skips CopyFromCharacter so direct ModelData writes
    // drive the CharaView render. Cleared on Recent revert.
    private bool _suspendCharacterCopy;
    // ponytail: pending warmup — Initialize(0) queues it; Tick() resolves a real equipped
    // ItemId from the live source's DrawData each frame until AgentTryon activates.
    private bool _retryWarmup;
    private nint _warmupSource;
    // ponytail: equipment overlay source — body stays self; only 10 equipment + 3 weapon ModelIds
    // come from this live character's DrawData. Character* would go stale, a func won't.
    private Func<nint>? _equipmentProvider;

    /// <summary>Suspend/resume per-frame CopyFromCharacter. Set true while direct slot writes own the view.</summary>
    public void SuspendCharacterCopy(bool suspend) => _suspendCharacterCopy = suspend;

    /// <summary>Set the live character whose equipment/weapon ModelIds overlay the self body, or null for pure self. Framework thread.</summary>
    public void SetEquipmentSource(Func<nint>? provider) => _equipmentProvider = provider;

    /// <summary>Write a pre-packed model value into CharaViewModelData._equipmentModelIds[slotIndex]
    /// (10 entries @ 0x20). <paramref name="modelValue"/> is the raw Item.ModelMain 8-byte sheet
    /// value (Id | Type&lt;&lt;16 | Variant&lt;&lt;32), repacked into the runtime EquipmentModelId
    /// layout (Id | Variant&lt;&lt;16 | Stain0&lt;&lt;24 | Stain1&lt;&lt;32). No agent/CharaView member
    /// functions are called, so the agent stays inactive and the Fitting Room addon never opens.
    /// Must be called on the Framework thread.</summary>
    public void SetCharaViewEquipmentSlot(byte slotIndex, ulong modelValue, byte stain0, byte stain1)
    {
        if (!_initialized || slotIndex >= 10) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        // CharaViewModelData: _equipmentModelIds @ 0x20, 10 × 8B → ulong indices 2..11.
        var basePtr = (ulong*)Unsafe.AsPointer(ref agent->CharaView.ModelData);
        basePtr[2 + slotIndex] = (modelValue & 0xFFFF)
            | (((modelValue >> 32) & 0xFF) << 16)
            | ((ulong)stain0 << 24)
            | ((ulong)stain1 << 32);
    }

    /// <summary>Write a pre-packed model value into CharaViewModelData._weaponModelIds[slotIndex]
    /// (3 entries @ 0x70). <paramref name="modelValue"/> is the raw Item.ModelMain/ModelSub 8-byte
    /// sheet value, which already matches the runtime WeaponModelId layout (Id | Type&lt;&lt;16 |
    /// Variant&lt;&lt;32) — only the stain bytes are appended. No agent/CharaView member functions
    /// are called, so the agent stays inactive and the Fitting Room addon never opens.
    /// Must be called on the Framework thread.</summary>
    public void SetCharaViewWeaponSlot(byte slotIndex, ulong modelValue, byte stain0, byte stain1)
    {
        if (!_initialized || slotIndex >= 3) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        // CharaViewModelData: _weaponModelIds @ 0x70, 3 × 8B → ulong indices 14..16.
        var basePtr = (ulong*)Unsafe.AsPointer(ref agent->CharaView.ModelData);
        basePtr[14 + slotIndex] = (modelValue & 0xFFFF_FFFF_FFFF)
            | ((ulong)stain0 << 48)
            | ((ulong)stain1 << 56);
    }

    /// <summary>Write a raw 8-byte runtime model value into CharaViewModelData._equipmentModelIds[slotIndex]. Framework thread.</summary>
    public void SetCharaViewEquipmentSlotRaw(byte slotIndex, ulong value)
    {
        if (!_initialized || slotIndex >= 10) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        var basePtr = (ulong*)Unsafe.AsPointer(ref agent->CharaView.ModelData);
        basePtr[2 + slotIndex] = value;
    }

    /// <summary>Write a raw 8-byte runtime model value into CharaViewModelData._weaponModelIds[slotIndex]. Framework thread.</summary>
    public void SetCharaViewWeaponSlotRaw(byte slotIndex, ulong value)
    {
        if (!_initialized || slotIndex >= 3) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        var basePtr = (ulong*)Unsafe.AsPointer(ref agent->CharaView.ModelData);
        basePtr[14 + slotIndex] = value;
    }

    public PreviewRenderer(IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;
    }

    public bool IsInitialized => _initialized;

    /// <summary>Current camera zoom (1.0 = CharaView default distance).</summary>
    public float Zoom => _zoom;

    /// <summary>Initialize CharaView from a source character. Must be called on Framework thread.
    /// <paramref name="warmupItemId"/> is a real Item RowId used to activate AgentTryon when the
    /// Fitting Room has never been opened. Pass 0 to defer activation: the source address is
    /// queued and Tick() resolves a real equipped ItemId from the live source's DrawData each
    /// frame until the agent activates.</summary>
    public void Initialize(Character* source, uint warmupItemId, Func<nint>? sourceProvider = null)
    {
        if (_initialized) return;
        if (source == null) return;

        _sourceProvider = sourceProvider;
        _warmupSource = (nint)source;
        _retryWarmup = true;

        var agent = AgentTryon.Instance();
        if (agent == null)
        {
            // ponytail: Tryon agent only exists once the Fitting Room has been opened.
            // Warm it with a real equipped ItemId — TryOn(0,0) leaves the agent inactive and the
            // game then hijacks CharaView slot 2 for AgentCharaCard (portraits/adventure plates).
            if (warmupItemId == 0) return; // _retryWarmup stays set — Tick() retries.
            AgentTryon.TryOn(0, warmupItemId, 0, 0, 0, false);
            agent = AgentTryon.Instance();
            if (agent == null) return; // _retryWarmup stays set — Tick() retries.
        }

        DoInitialize(source, agent);
    }

    private void DoInitialize(Character* source, AgentTryon* agent)
    {
        // ponytail: agent is intentionally left INACTIVE — Show() opens the game's Fitting Room
        // addon. CharaView renders fine with an inactive agent (Ktisis precedent): Initialize
        // once, then per-tick Update/Render; items are written directly into ModelData.
        agent->CharaView.Initialize(&agent->AgentInterface, CharaViewSlot, 0);
        agent->CharaView.ModelData.CopyFromCharacter(source);
        agent->CharaView.Update(_counter, agent->CharaView.GetCharacter());
        _initialized = true;
        // ponytail: seed a few refresh frames so early renders can't be clobbered by agent activity.
        _pendingRecopyFrames = 3;
    }

    /// <summary>Request N future Ticks to re-copy from the source provider (fixes ApplyState frame-lag and Examine hijack).</summary>
    public void RequestRecopy(int frames)
    {
        if (frames > _pendingRecopyFrames) _pendingRecopyFrames = frames;
    }

    /// <summary>Per-frame update/render. Must be called on Framework thread.</summary>
    public void Tick()
    {
        if (!_initialized)
        {
            // ponytail: deferred warmup — Initialize(0) queued a source; resolve a real
            // equipped ItemId from the live source's DrawData and activate AgentTryon.
            if (!_retryWarmup) return;
            var addr = _sourceProvider?.Invoke() ?? _warmupSource;
            if (addr == nint.Zero) return;
            var src = (Character*)addr;
            uint warmupItemId = 0;
            foreach (var slot in src->DrawData.EquipmentModelIds)
                if (slot.Id != 0) { warmupItemId = slot.Id; break; }
            if (warmupItemId == 0) return; // not drawn yet — retry next frame
            AgentTryon.TryOn(0, warmupItemId, 0, 0, 0, false);
            var warmupAgent = AgentTryon.Instance();
            if (warmupAgent == null) return;
            _retryWarmup = false;
            DoInitialize(src, warmupAgent);
            return;
        }
        var agent = AgentTryon.Instance();
        if (agent == null) return;

        // ponytail: recopy every tick while a source is set, so live Glamourer edits propagate
        // immediately. _pendingRecopyFrames stays for callers that want to force extra ticks after ApplyState.
        if (_sourceProvider != null && !_suspendCharacterCopy)
        {
            var addr = _sourceProvider();
            if (addr != nint.Zero)
                agent->CharaView.ModelData.CopyFromCharacter((Character*)addr);
            if (_pendingRecopyFrames > 0) _pendingRecopyFrames--;
        }

        // ponytail: equipment overlay — body stays self, only the 10+3 ModelIds come from the overlay
        // source's DrawData. Whole 8-byte copies: both sides are the same runtime structs
        // (EquipmentModelId / WeaponModelId), so no field repacking.
        if (_equipmentProvider != null)
        {
            var addr = _equipmentProvider();
            if (addr != nint.Zero)
            {
                var src = (Character*)addr;
                if (src->DrawData.OwnerObject != null)
                {
                    for (var i = 0; i < 10; i++)
                        SetCharaViewEquipmentSlotRaw((byte)i, *(ulong*)Unsafe.AsPointer(ref src->DrawData.Equipment((DrawDataContainer.EquipmentSlot)i)));
                    for (var i = 0; i < 3; i++)
                        SetCharaViewWeaponSlotRaw((byte)i, *(ulong*)Unsafe.AsPointer(ref src->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).ModelId));
                }
            }
        }

        var ch = agent->CharaView.GetCharacter();
        if (ch == null) return;

        agent->CharaView.Update(_counter, ch);
        agent->CharaView.Render(_counter++);
    }

    /// <summary>Switch the rendered source character. Must be called on Framework thread.</summary>
    public void SetSource(nint address, uint warmupItemId, Func<nint> sourceProvider)
    {
        _sourceProvider = sourceProvider;
        if (!_initialized)
        {
            Initialize(address != nint.Zero ? (Character*)address : null, warmupItemId, sourceProvider);
            return;
        }

        var agent = AgentTryon.Instance();
        if (agent == null) return;
        if (address != nint.Zero)
        {
            agent->CharaView.ModelData.CopyFromCharacter((Character*)address);
            // ponytail: few forced re-copies so the new character lands even without an active TryOn agent.
            RequestRecopy(5);
        }
    }

    public void SetYawPitch(float yaw, float pitch)
    {
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.SetCameraYawAndPitch(yaw, pitch);
    }

    /// <summary>Set camera distance. 1.0 = CharaView default distance. Must be called on the Framework thread.</summary>
    public void SetZoom(float zoom)
    {
        var target = Math.Clamp(zoom, 0.5f, 3.0f);
        var current = _zoom;
        _zoom = target;
        if (!_initialized || target == current) return;

        var agent = AgentTryon.Instance();
        if (agent == null) return;
        var cam = agent->CharaView.Camera;
        if (cam == null) return;

        // ponytail: assumes positive SetCameraDistance delta = camera moves away; flip the sign
        // if it feels inverted in-game. Distance ∝ 1/zoom ⇒ delta = dist·(z₀/z₁−1), dist read live.
        var d = cam->Position - cam->LookAtVector;
        var dist = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        agent->CharaView.SetCameraDistance(dist * (current / target - 1f));
    }

    public void Reset()
    {
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.ResetPositions();
        _zoom = 1.0f;
    }

    /// <summary>Get the SRV handle for ImGui.Image, or 0 if not ready.</summary>
    public nint GetTextureHandle()
    {
        if (!_initialized) return 0;
        var rtm = RenderTargetManager.Instance();
        if (rtm == null) return 0;
        var tex = rtm->GetCharaViewTexture(CharaViewSlot);
        if (tex == null) return 0;
        return (nint)tex->D3D11ShaderResourceView;
    }

    /// <summary>Release CharaView. Must be called on Framework thread.</summary>
    public void Release()
    {
        if (!_initialized) return;
        try
        {
            var agent = AgentTryon.Instance();
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
            _zoom = 1.0f;
            _sourceProvider = null;
            _equipmentProvider = null;
            _pendingRecopyFrames = 0;
            _retryWarmup = false;
            _warmupSource = nint.Zero;
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
