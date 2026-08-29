using System;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Collections.Generic;
using GlamSource.Core;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

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
    // ponytail: equipment overlay snapshot — body stays self; each Tick this callback returns the
    // resolved EquipmentSlots (ItemId+stains) to write via canonical SetItemSlotData. null = no overlay.
    private Func<IReadOnlyList<EquipmentSlot>?>? _equipmentSnapshot;
    // ponytail: hover-switch flicker guard. ShellWindow flips desired 0 for a frame between two
    // hovers; clearing the overlay immediately drops us back to self-gear until the next snapshot
    // arrives. Grace holds the old snapshot N frames; a new non-null snapshot cancels grace.
    private int _snapshotClearGrace;
    private const int SnapshotClearGraceFrames = 5;

    /// <summary>Suspend/resume per-frame CopyFromCharacter. Set true while direct slot writes own the view.</summary>
    public void SuspendCharacterCopy(bool suspend) => _suspendCharacterCopy = suspend;

    /// <summary>Register a snapshot callback invoked each Tick. Returns EquipmentSlots to overlay on the self body,
    /// or null for pure self view. Framework thread.</summary>
    public void SetEquipmentSnapshot(Func<IReadOnlyList<EquipmentSlot>?>? provider)
    {
        if (provider == null)
        {
            // defer clear: hold old snapshot for N frames so hover-switch through 0 doesn't flash self-gear
            if (_equipmentSnapshot != null) _snapshotClearGrace = SnapshotClearGraceFrames;
            else _equipmentSnapshot = null;
        }
        else
        {
            _snapshotClearGrace = 0;
            _equipmentSnapshot = provider;
        }
    }

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

    /// <summary>Fill CharaView._items[slotId] via the canonical instance API. Render pipeline
    /// reads from _items, not from ModelData._equipmentModelIds, so this is what actually shows.
    /// Does NOT open the Fitting Room addon — that requires AgentTryon.TryOn(openerAddonId,...).
    /// slotId: 0=MainHand, 1=OffHand, 2=Head, 3=Body, 4=Hands, 5=Waist, 6=Legs, 7=Feet,
    /// 8=Earrings, 9=Necklace, 10=Bracelets, 11=RingRight, 12=RingLeft (14 slots total).
    /// Framework thread.</summary>
    public void SetCharaViewItemSlot(byte slotId, uint itemId, byte stain0, byte stain1, uint glamourItemId = 0)
    {
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.SetItemSlotData(slotId, itemId, stain0, stain1, glamourItemId, false);
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

    // ponytail: mirrors GlamSourceShellWindow.MapToCharaViewItemSlot; duplicated to keep Renderer standalone.
    private static int MapEquipmentSlotToCharaView(EquipmentSlotType s) => s switch
    {
        EquipmentSlotType.MainHand => 0,
        EquipmentSlotType.OffHand => 1,
        EquipmentSlotType.Head => 2,
        EquipmentSlotType.Body => 3,
        EquipmentSlotType.Hands => 4,
        EquipmentSlotType.Legs => 6,
        EquipmentSlotType.Feet => 7,
        EquipmentSlotType.Earrings => 8,
        EquipmentSlotType.Necklace => 9,
        EquipmentSlotType.Bracelets => 10,
        EquipmentSlotType.RingRight => 11,
        EquipmentSlotType.RingLeft => 12,
        _ => -1,
    };

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

        // ponytail: grace countdown — hold snapshot for N frames after null-set, absorb hover-switch 0-blip
        if (_snapshotClearGrace > 0 && --_snapshotClearGrace == 0) _equipmentSnapshot = null;

        // ponytail: snapshot check first — overlay active means Copy would fight canonical writes.
        IReadOnlyList<EquipmentSlot>? overlay = null;
        if (_equipmentSnapshot != null)
        {
            try { overlay = _equipmentSnapshot(); } catch { overlay = null; }
        }
        var overlayActive = overlay != null && overlay.Count > 0;

        // ponytail: recopy every tick while a source is set, so live Glamourer edits propagate
        // immediately. Suspend on overlayActive OR explicit suspend — overlay writes into _items,
        // CopyFromCharacter clobbers them otherwise.
        if (_sourceProvider != null && !_suspendCharacterCopy && !overlayActive)
        {
            var addr = _sourceProvider();
            if (addr != nint.Zero)
                agent->CharaView.ModelData.CopyFromCharacter((Character*)addr);
            if (_pendingRecopyFrames > 0) _pendingRecopyFrames--;
        }

        // ponytail: overlay path — canonical SetItemSlotData writes fill _items (what render reads).
        // Body already came from self via prior CopyFromCharacter frames; only equipment slots swap.
        if (overlayActive)
        {
            foreach (var slot in overlay!)
            {
                var slotId = MapEquipmentSlotToCharaView(slot.Slot);
                if (slotId < 0) continue;
                var itemId = slot.GlamourItemId ?? slot.ActualItemId;
                // ponytail: empty slot MUST be written as 0 — otherwise the previous overlay's item
                // sticks in _items when the next Recent doesn't cover that slot.
                agent->CharaView.SetItemSlotData((byte)slotId, itemId, slot.Stain0, slot.Stain1, 0, false);
            }
        }
        else if (!_suspendCharacterCopy && _sourceProvider != null)
        {
            // ponytail: no overlay + no explicit suspend → keep body dressed with self equipment
            // via raw ModelId writes (fallback for the "no target" case).
            var addr = _sourceProvider();
            if (addr != nint.Zero)
            {
                var cand = (Character*)addr;
                if (cand->DrawData.OwnerObject != null)
                {
                    for (var i = 0; i < 10; i++)
                        SetCharaViewEquipmentSlotRaw((byte)i, *(ulong*)Unsafe.AsPointer(ref cand->DrawData.Equipment((DrawDataContainer.EquipmentSlot)i)));
                    for (var i = 0; i < 3; i++)
                        SetCharaViewWeaponSlotRaw((byte)i, *(ulong*)Unsafe.AsPointer(ref cand->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).ModelId));
                }
            }
        }

        // ponytail: re-applied every tick — CopyFromCharacter above mirrors the live character's
        // ModelData wholesale, which stomps this flag back to "drawn" each frame otherwise.
        agent->CharaView.ToggleDrawWeapon(_weaponDrawn);

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
        var target = Math.Clamp(zoom, 0.5f, 6.0f);
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

    /// <summary>Pan the camera by a screen-space delta. Used for zoom-to-cursor. Framework thread.</summary>
    public void PanCamera(float deltaX, float deltaY)
    {
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.SetCameraXAndY(deltaX, deltaY);
    }

    // ponytail: neutral preview default — no weapon/tool in hand until the user opts in.
    private bool _weaponDrawn;

    /// <summary>Show/hide the mainhand weapon model in the preview. Re-applied every Tick.</summary>
    public void SetWeaponDrawn(bool drawn) => _weaponDrawn = drawn;

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

/// <summary>Raw CPU-readback of the current CharaView frame, for the web-UI 3D preview
    /// (experimental, opt-in). BGRA byte order per pixel unless <see cref="IsBgra"/> is false.</summary>
    public readonly record struct CapturedFrame(int Width, int Height, int RowPitch, byte[] Pixels, bool IsBgra);

    private CapturedFrame? _lastWebFrame;
    private long _lastCaptureTickMs;
    private const long CaptureThrottleMs = 90; // ~10-11 fps ceiling — matches the web UI's fast-poll rate

    /// <summary>Last frame captured for the web-UI 3D preview, or null if never captured / feature off.
    /// Thread-safe to read from anywhere (plain field read) — capture itself only ever runs from
    /// Draw(), never from here.</summary>
    public CapturedFrame? LatestWebFrame => _lastWebFrame;

    /// <summary>Call once per Draw() while the web-UI 3D preview is enabled and this tab is visible —
    /// same call context ImGui.Image already uses safely every frame. Throttled internally; cheap to
    /// call every frame. Do NOT call this from Framework.RunOnFrameworkThread or any other thread —
    /// the immediate D3D11 context must only be touched here, in-line with the game's own Present.</summary>
    public void CaptureFrameForWeb(bool enabled)
    {
        if (!enabled) { _lastWebFrame = null; return; }
        var now = Environment.TickCount64;
        if (now - _lastCaptureTickMs < CaptureThrottleMs) return;
        _lastCaptureTickMs = now;
        _lastWebFrame = TryCapturePixels();
    }

    /// <summary>Copy the CharaView GPU texture to CPU. MUST be called only from Draw() (the same
    /// in-line-with-Present context ImGui.Image already reads this SRV from) — never from
    /// Framework.RunOnFrameworkThread or a background thread. Returns null if not ready or the copy
    /// failed.</summary>
    /// <remarks>ponytail: pattern mirrors Dalamud's own TextureManager.GetRawImageAsync (internal,
    /// not exposed to plugins) — CopyResource into a STAGING texture, Map, read, Unmap. The SRV
    /// ComPtr below is adopted from a borrowed handle (GetTextureHandle owns nothing) and must
    /// NEVER be Dispose()'d — TerraFX's raw-pointer ComPtr ctor does not AddRef, so releasing it
    /// would drop a refcount CharaView still owns. Everything obtained via GetResource/As/GetDevice/
    /// GetImmediateContext DOES own a ref and must be disposed (all four `using`).</remarks>
    private CapturedFrame? TryCapturePixels()
    {
        var srvHandle = GetTextureHandle();
        if (srvHandle == 0) return null;

        // Adopted, not owned — do not Dispose. See remarks above.
        var srv = new ComPtr<ID3D11ShaderResourceView>((ID3D11ShaderResourceView*)srvHandle);

        using ComPtr<ID3D11Resource> res = default;
        srv.Get()->GetResource(res.GetAddressOf());
        if (res.Get() == null) return null;

        using ComPtr<ID3D11Texture2D> tex = default;
        if (res.As(&tex).FAILED) return null;

        using ComPtr<ID3D11Device> device = default;
        tex.Get()->GetDevice(device.GetAddressOf());
        using ComPtr<ID3D11DeviceContext> context = default;
        device.Get()->GetImmediateContext(context.GetAddressOf());
        if (device.Get() == null || context.Get() == null) return null;

        D3D11_TEXTURE2D_DESC desc;
        tex.Get()->GetDesc(&desc);

        var stagingDesc = desc with
        {
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0u,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0u,
            MipLevels = 1,
            ArraySize = 1,
        };
        using ComPtr<ID3D11Texture2D> staging = default;
        if (device.Get()->CreateTexture2D(&stagingDesc, null, staging.GetAddressOf()).FAILED) return null;

        context.Get()->CopyResource((ID3D11Resource*)staging.Get(), (ID3D11Resource*)tex.Get());

        D3D11_MAPPED_SUBRESOURCE mapped;
        if (context.Get()->Map((ID3D11Resource*)staging.Get(), 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).FAILED)
            return null;
        try
        {
            var byteCount = (int)(mapped.RowPitch * desc.Height);
            var bytes = new byte[byteCount];
            fixed (byte* dst = bytes)
                Buffer.MemoryCopy((void*)mapped.pData, dst, byteCount, byteCount);
            var isBgra = desc.Format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM;
            return new CapturedFrame((int)desc.Width, (int)desc.Height, (int)mapped.RowPitch, bytes, isBgra);
        }
        finally
        {
            context.Get()->Unmap((ID3D11Resource*)staging.Get(), 0);
        }
    }

    /// <summary>Release CharaView. Must be called on Framework thread.</summary>
    public void Release()
    {
        _log.Info("[PreviewRenderer] Release() called");
        if (!_initialized) { _log.Info("[PreviewRenderer] Release() no-op, not initialized"); return; }
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
            // ponytail: AgentTryon.TryOn(...) itself opens the Fitting Room addon regardless of the
            // trailing bool — confirmed via log: clean CharaView.Release() above, then this call still
            // popped the addon on plugin unload. Removed; CharaView.Release() alone is enough cleanup,
            // and the primed item state is irrelevant next time the player opens Fitting Room for real.
            _initialized = false;
            _counter = 1;
            _zoom = 1.0f;
            _sourceProvider = null;
            _equipmentSnapshot = null;
            _pendingRecopyFrames = 0;
            _retryWarmup = false;
            _warmupSource = nint.Zero;
        }
    }

    public void Dispose()
    {
        _log.Info($"[PreviewRenderer] Dispose() called, _initialized={_initialized}");
        if (!_initialized) return;
        // Dispose can be called off-frame; hop to Framework thread for the Release.
        try { _framework.RunOnFrameworkThread(Release).Wait(); }
        catch (Exception ex) { _log.Warning($"[PreviewRenderer] Dispose Release failed: {ex.Message}"); }
    }
}
