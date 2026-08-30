using System;
using System.IO;
using System.Linq;
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
    // ponytail: true while a native UI (Fitting Room/Glamour Plate editor) owns the shared
    // AgentTryon/CharaView slot instead of us — see the IsAgentActive() check in Tick().
    private bool _nativeUiOwnsSlot;
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

        // Real conflict, seen live: the game's own Glamour Plate editor (and Fitting Room) drive
        // this SAME AgentTryon instance/CharaView slot. We deliberately keep our own usage
        // INACTIVE (see DoInitialize's comment) specifically so IsAgentActive() staying true means
        // "something else — a native UI — has taken this over". When that happens, back off
        // entirely: don't write ModelData/items (would fight the native UI's own writes) and don't
        // Update/Render (it's already driving that). Otherwise our web capture just keeps serving
        // whatever that native UI put in the shared texture — reported live as "the Character tab
        // showed a stranger's Glamour Plate" with no repro (it's not ours, it's just an honest
        // capture of a slot someone else briefly owns).
        if (agent->AgentInterface.IsAgentActive())
        {
            if (!_nativeUiOwnsSlot) _log.Info("[PreviewRenderer] native UI (Fitting Room/Glamour Plate) took over the CharaView slot — pausing our render/capture until it releases it");
            _nativeUiOwnsSlot = true;
            return;
        }
        if (_nativeUiOwnsSlot)
        {
            _log.Info("[PreviewRenderer] native UI released the CharaView slot — resuming");
            _nativeUiOwnsSlot = false;
            RequestRecopy(3); // force a few re-copies so our own state fully overwrites whatever it left behind
        }

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

// --- web-UI MJPEG preview: async non-blocking readback + JPEG encode ---
    // The old version did a synchronous CopyResource+Map every capture (a real GPU/CPU stall) and
    // bounded its cost with a flat 150ms wall-clock throttle (~6-7fps ceiling, chosen arbitrarily to
    // cap that stall, not because 6-7fps was ever a goal). This version never stalls: Map uses
    // D3D11_MAP_FLAG_DO_NOT_WAIT, so an unfinished GPU copy is simply skipped and retried next
    // Draw() instead of blocking the render thread. Real cadence is now "however fast the GPU
    // actually finishes each copy" (typically 1 frame), only capped by EncodeThrottleMs below since
    // JPEG-encoding isn't free either and nothing needs it faster.
    private ComPtr<ID3D11Texture2D> _webStaging;
    private uint _webStagingWidth, _webStagingHeight;
    private DXGI_FORMAT _webStagingFormat;
    private bool _webCopyPending;
    private byte[]? _latestWebJpeg;
    private long _lastEncodeTickMs;
    private const long EncodeThrottleMs = 33; // ~30fps cap on JPEG re-encode cost

    // --- debug counters, all untested-in-practice as of writing — exposed via GetWebCaptureStats()
    // (see WebUiService's GET /api/preview3d/debug) so a bad first live run is diagnosable from the
    // web UI instead of guessing. FramesSkipped is expected/healthy (DO_NOT_WAIT doing its job, not
    // an error); CaptureErrors and LastError are the ones to actually look at.
    private long _webFramesEncoded;
    private long _webFramesSkipped;
    private long _webCaptureErrors;
    private long _lastFrameBytes;
    private long _lastEncodeDurationMs;
    private string? _lastCaptureError;

    public readonly record struct WebCaptureStats(
        bool StagingReady, long FramesEncoded, long FramesSkipped, long CaptureErrors,
        long LastFrameBytes, long LastEncodeDurationMs, string? LastError, int StagingWidth, int StagingHeight,
        bool NativeUiOwnsSlot);

    /// <summary>Snapshot of the web MJPEG capture pipeline's health — thread-safe plain field reads,
    /// same as LatestWebJpeg.</summary>
    public WebCaptureStats GetWebCaptureStats() => new(
        _webStaging.Get() != null, _webFramesEncoded, _webFramesSkipped, _webCaptureErrors,
        _lastFrameBytes, _lastEncodeDurationMs, _lastCaptureError, (int)_webStagingWidth, (int)_webStagingHeight,
        _nativeUiOwnsSlot);

    /// <summary>Latest JPEG-encoded frame for the web-UI MJPEG stream (see WebUiService's
    /// /api/preview3d/stream), or null if never captured / feature off. Thread-safe to read from
    /// anywhere (plain field read) — capture/encode only ever runs from Draw(), never from here.</summary>
    public byte[]? LatestWebJpeg => _latestWebJpeg;

    /// <summary>Call once per UiBuilder.Draw frame (Plugin.cs wires this, unconditionally — not tied
    /// to any window's visibility, so the web UI keeps working with the ImGui window closed) — same
    /// call context ImGui.Image already uses safely every frame. Cheap to call every frame; internally
    /// throttled/non-blocking. Returns immediately (no-op) if CharaView was never initialized. Do NOT
    /// call this from Framework.RunOnFrameworkThread or any other thread — the immediate D3D11
    /// context must only be touched here, in-line with the game's own Present (an earlier version
    /// that broke this rule corrupted D3D11 state badly enough to crash the game — see
    /// WebUiService's /api/preview3d/stream doc comment).</summary>
    public void CaptureFrameForWeb(bool enabled)
    {
        if (!enabled) { _latestWebJpeg = null; ReleaseWebStaging(); return; }
        // Native UI (Fitting Room/Glamour Plate) currently owns the shared CharaView slot (see
        // Tick()'s IsAgentActive() check) — capturing it would just serve their content over our
        // stream. Stop pushing new frames (the MJPEG stream just freezes on our last real frame
        // instead of switching to theirs); RequestRecopy(3) in Tick() catches the display back up
        // to our own state once the native UI releases it.
        if (_nativeUiOwnsSlot) return;
        try { PumpWebCapture(); }
        catch (Exception ex)
        {
            // best-effort — a bad frame just stalls the stream one tick, not a crash — but record it
            // so /api/preview3d/debug can actually show WHY instead of just "no frame yet".
            _webCaptureErrors++;
            _lastCaptureError = ex.Message;
            _log.Warning($"[PreviewRenderer] web capture failed: {ex.Message}");
        }
    }

    private void ReleaseWebStaging()
    {
        _webStaging.Dispose();
        _webStaging = default;
        _webCopyPending = false;
    }

    /// <remarks>ponytail: pattern mirrors Dalamud's own TextureManager.GetRawImageAsync (internal,
    /// not exposed to plugins) — CopyResource into a STAGING texture, Map, read, Unmap — but with a
    /// persistent staging texture (recreated only on resize) and DO_NOT_WAIT instead of a fresh
    /// blocking staging texture every call. The SRV ComPtr below is adopted from a borrowed handle
    /// (GetTextureHandle owns nothing) and must NEVER be Dispose()'d — TerraFX's raw-pointer ComPtr
    /// ctor does not AddRef, so releasing it would drop a refcount CharaView still owns. Everything
    /// obtained via GetResource/As/GetDevice/GetImmediateContext DOES own a ref and must be disposed
    /// (all four `using`).</remarks>
    private void PumpWebCapture()
    {
        var srvHandle = GetTextureHandle();
        if (srvHandle == 0) return;

        // Adopted, not owned — do not Dispose. See remarks above.
        var srv = new ComPtr<ID3D11ShaderResourceView>((ID3D11ShaderResourceView*)srvHandle);

        using ComPtr<ID3D11Resource> res = default;
        srv.Get()->GetResource(res.GetAddressOf());
        if (res.Get() == null) return;

        using ComPtr<ID3D11Texture2D> tex = default;
        if (res.As(&tex).FAILED) return;

        using ComPtr<ID3D11Device> device = default;
        tex.Get()->GetDevice(device.GetAddressOf());
        using ComPtr<ID3D11DeviceContext> context = default;
        device.Get()->GetImmediateContext(context.GetAddressOf());
        if (device.Get() == null || context.Get() == null) return;

        D3D11_TEXTURE2D_DESC desc;
        tex.Get()->GetDesc(&desc);

        // (re)create the staging texture on first use or if the source render target resized
        if (_webStaging.Get() == null || desc.Width != _webStagingWidth || desc.Height != _webStagingHeight || desc.Format != _webStagingFormat)
        {
            ReleaseWebStaging();
            var stagingDesc = desc with
            {
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0u,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags = 0u,
                MipLevels = 1,
                ArraySize = 1,
            };
            if (device.Get()->CreateTexture2D(&stagingDesc, null, _webStaging.GetAddressOf()).FAILED)
            {
                _webCaptureErrors++;
                _lastCaptureError = "CreateTexture2D (staging) failed";
                return;
            }
            _webStagingWidth = desc.Width;
            _webStagingHeight = desc.Height;
            _webStagingFormat = desc.Format;
            _log.Info($"[PreviewRenderer] web staging texture (re)created: {desc.Width}x{desc.Height} fmt={desc.Format}");
        }

        if (!_webCopyPending)
        {
            context.Get()->CopyResource((ID3D11Resource*)_webStaging.Get(), (ID3D11Resource*)tex.Get());
            _webCopyPending = true;
            return; // give the GPU at least one Draw() before polling Map — an instant DO_NOT_WAIT check would just miss every time
        }

        var now = Environment.TickCount64;
        if (now - _lastEncodeTickMs < EncodeThrottleMs) return; // copy stays pending; poll again next Draw()

        D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = context.Get()->Map((ID3D11Resource*)_webStaging.Get(), 0, D3D11_MAP.D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (hr.Value == DXGI.DXGI_ERROR_WAS_STILL_DRAWING) { _webFramesSkipped++; return; } // GPU not done yet — copy stays pending, retry next Draw()
        if (hr.FAILED)
        {
            _webCopyPending = false;
            _webCaptureErrors++;
            _lastCaptureError = $"Map failed: 0x{hr.Value:X8}";
            return; // real failure — drop this cycle, next Draw() starts a fresh copy
        }

        try
        {
            var isBgra = desc.Format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM;
            var encodeStart = Environment.TickCount64;
            _latestWebJpeg = EncodeJpeg((int)desc.Width, (int)desc.Height, (int)mapped.RowPitch, (nint)mapped.pData, isBgra);
            _lastEncodeDurationMs = Environment.TickCount64 - encodeStart;
            _lastFrameBytes = _latestWebJpeg.Length;
            _webFramesEncoded++;
            if (_webFramesEncoded == 1) _log.Info($"[PreviewRenderer] first web frame encoded: {_lastFrameBytes} bytes in {_lastEncodeDurationMs}ms, isBgra={isBgra}");
            _lastEncodeTickMs = now;
        }
        finally
        {
            context.Get()->Unmap((ID3D11Resource*)_webStaging.Get(), 0);
            _webCopyPending = false; // next Draw() starts a fresh CopyResource for the following frame
        }
    }

    /// <summary>D3D11's BGRA formats are byte-identical to GDI+'s Format32bppArgb (same B,G,R,A
    /// memory order) — construct the Bitmap directly over the mapped GPU memory, no extra copy,
    /// only for the encode call's duration (Unmap happens right after in the caller).</summary>
    private static System.Drawing.Bitmap EncodeSourceBitmap(int width, int height, int rowPitch, nint scan0, bool isBgra)
        => isBgra
            ? new System.Drawing.Bitmap(width, height, rowPitch, System.Drawing.Imaging.PixelFormat.Format32bppArgb, scan0)
            : SwapToBgraBitmap(width, height, rowPitch, scan0);

    private static byte[] EncodeJpeg(int width, int height, int rowPitch, nint scan0, bool isBgra)
    {
        using var bmp = EncodeSourceBitmap(width, height, rowPitch, scan0, isBgra);
        using var ms = new MemoryStream();
        var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var encParams = new System.Drawing.Imaging.EncoderParameters(1);
        encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
        bmp.Save(ms, encoder, encParams);
        return ms.ToArray();
    }

    /// <summary>Rare fallback — every CharaView format observed in practice is BGRA — kept as a
    /// correctness path instead of silently assuming, since a wrong assumption here would swap
    /// red/blue in the whole preview.</summary>
    private static System.Drawing.Bitmap SwapToBgraBitmap(int width, int height, int rowPitch, nint scan0)
    {
        var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        unsafe
        {
            var src = (byte*)scan0;
            var dst = (byte*)data.Scan0;
            for (var y = 0; y < height; y++)
            {
                var srcRow = src + y * rowPitch;
                var dstRow = dst + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    dstRow[x * 4 + 0] = srcRow[x * 4 + 2]; // B
                    dstRow[x * 4 + 1] = srcRow[x * 4 + 1]; // G
                    dstRow[x * 4 + 2] = srcRow[x * 4 + 0]; // R
                    dstRow[x * 4 + 3] = srcRow[x * 4 + 3]; // A
                }
            }
        }
        bmp.UnlockBits(data);
        return bmp;
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
