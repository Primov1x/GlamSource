using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
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

    /// <summary>Suspend/resume per-frame CopyFromCharacter. Set true while direct slot writes own the view.
    /// Also flips TryonCharaView.DoUpdate — reported live: suspending our own CopyFromCharacter calls
    /// alone didn't freeze anything visually, the character kept animating. GetCharacter() likely
    /// keeps returning a pointer straight at the LIVE, still-animating player object regardless of
    /// whether WE re-copy from it — Update(counter, ch) then naturally re-syncs from that live object
    /// every call. DoUpdate is a real field on TryonCharaView (found via reflection against the
    /// actual FFXIVClientStructs.dll, not guessed) whose name strongly suggests it gates whether
    /// CharaView advances/re-syncs its own state at all — untested until now whether it actually
    /// achieves a visual freeze or also freezes camera response; that's exactly what this call is for.</summary>
    public void SuspendCharacterCopy(bool suspend)
    {
        _suspendCharacterCopy = suspend;
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.DoUpdate = !suspend;
    }

    // Pose freeze, FOURTH approach — the one that matches how posing plugins actually hook the
    // engine. History: (1) suspending our CopyFromCharacter calls — no effect; (2) DoUpdate=false —
    // no effect; (3) overwriting hkaPose Local/ModelPose arrays from Tick() — no effect. (3) failed
    // because the skeleton update (animation sampling, SyncModelSpace, physics) runs inside the
    // game's render task (Framework.TaskRenderGraphicsRender), AFTER UiBuilder.Draw — whatever we
    // write in Tick() gets recomputed before it's ever rendered. Brio's fix (read from its actual
    // source, Brio/Game/Posing/SkeletonService.cs): hook the engine's UpdateBonePhysics function
    // (their comment: "all the main skeleton stuff like positions, IK and physics is done at this
    // point"), call the original FIRST, then overwrite bones via hkaPose.AccessBoneModelSpace —
    // which also maintains Havok's sync flags, unlike raw array writes. Signature string is Brio's,
    // verbatim. Unlike Brio we freeze exactly ONE skeleton (the CharaView clone), so no entity
    // system — just a pointer refreshed every Tick and compared in the detour.
    private const string UpdateBonePhysicsSig = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4";
    private delegate nint UpdateBonePhysicsDelegate(nint a1);
    private Dalamud.Hooking.Hook<UpdateBonePhysicsDelegate>? _updateBonePhysicsHook;
    private bool _freezePose;
    private nint _freezeSkeleton; // Render.Skeleton* of the CharaView clone, refreshed each Tick while frozen, 0 = don't touch
    private hkQsTransformf[][]? _frozenModel; // per partial skeleton, per bone, model space
    // Freeze is the DEFAULT (user request, once v4 verified live): a static shot is the whole point
    // of the preview, and frozen + idle throttle costs near zero. Deferred a second past init so
    // the snapshot catches a settled idle pose, not a mid-load/T-pose frame.
    private int _autoFreezeCountdown;
    private const int AutoFreezeDelayFrames = 60;

    /// <summary>Freeze the previewed character's pose: snapshot the skeleton once, then stomp the
    /// engine's freshly-computed pose with it every frame from inside the UpdateBonePhysics hook.
    /// Framework thread. Installs the hook lazily on first use; disables (not disposes) on unfreeze.</summary>
    public void SetFreezePose(bool freeze)
    {
        if (!freeze)
        {
            _freezePose = false;
            _freezeSkeleton = 0;
            _frozenModel = null;
            _autoFreezeCountdown = 0; // explicit unfreeze also cancels a pending freeze-by-default
            _updateBonePhysicsHook?.Disable();
            return;
        }
        if (_updateBonePhysicsHook == null)
        {
            try
            {
                var addr = _sigScanner.ScanText(UpdateBonePhysicsSig);
                _updateBonePhysicsHook = _gameInterop.HookFromAddress<UpdateBonePhysicsDelegate>(addr, UpdateBonePhysicsDetour);
            }
            catch (Exception ex)
            {
                // sig broke on a game patch — freeze silently unavailable until the sig's updated
                _lastCaptureError = $"freeze hook sig scan failed: {ex.Message}";
                _log.Warning($"[PreviewRenderer] UpdateBonePhysics sig scan failed, freeze unavailable: {ex.Message}");
                return;
            }
        }
        _frozenModel = null; // fresh snapshot on (re-)freeze
        _freezeDetourErrors = 0; // explicit re-freeze = user gets a fresh set of strikes
        _freezePose = true;
        _updateBonePhysicsHook.Enable();
    }

    // breaker: repeated caught errors in the detour mean our assumptions about the skeleton
    // layout no longer hold (game patch territory) — a caught managed exception today can be an
    // uncatchable AccessViolation tomorrow. Three strikes and freeze turns itself off.
    private int _freezeDetourErrors;

    // set false once per framework tick (in Tick()); the engine calls UpdateBonePhysics once PER
    // SKELETON per frame — without this gate the full clone-bone rewrite ran once per nearby
    // character (50 people in town = 50x the work), which tanked the game's fps in crowds
    // (measured live: drawCalls/s 80 alone vs 34 in town).
    private volatile bool _freezeAppliedThisFrame;

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = _updateBonePhysicsHook!.Original(a1);
        // never let anything escape into the engine's hot path — a bad frame here is a crash
        try
        {
            var skeletonPtr = _freezeSkeleton;
            if (_freezePose && skeletonPtr != 0 && !_freezeAppliedThisFrame)
            {
                _freezeAppliedThisFrame = true;
                ApplyOrCaptureFrozenPose((Skeleton*)skeletonPtr);
            }
        }
        catch (Exception ex)
        {
            _lastCaptureError = $"freeze detour: {ex.Message}";
            if (++_freezeDetourErrors >= 3)
            {
                _freezePose = false; // detour-side only — Disable() must not run on this thread
                _freezeSkeleton = 0;
                _log.Warning($"[PreviewRenderer] freeze disabled itself after {_freezeDetourErrors} detour errors (game patch?): {ex.Message}");
            }
        }
        return result;
    }

    private void ApplyOrCaptureFrozenPose(Skeleton* skeleton)
    {
        if (skeleton == null || skeleton->PartialSkeletonCount == 0) return;
        var partialCount = skeleton->PartialSkeletonCount;

        // local copy — SetFreezePose(false) nulls _frozenModel from the framework thread while
        // this runs on the render thread; using the field directly NRE'd live ("freeze detour:
        // Object reference not set"). The local either sees the whole array or triggers capture.
        var frozenModel = _frozenModel;

        // (re-)capture: first frozen frame, or skeleton shape changed under us (e.g. gear swap)
        var capture = frozenModel == null || frozenModel.Length != partialCount;
        if (!capture)
        {
            for (var p = 0; p < partialCount; p++)
            {
                var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                if (pose != null && pose->ModelPose.Length != frozenModel![p].Length) { capture = true; break; }
            }
        }

        if (capture)
        {
            var frozen = new hkQsTransformf[partialCount][];
            for (var p = 0; p < partialCount; p++)
            {
                var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                var n = pose == null ? 0 : pose->ModelPose.Length;
                frozen[p] = new hkQsTransformf[n];
                for (var i = 0; i < n; i++) frozen[p][i] = pose->ModelPose[i];
            }
            _frozenModel = frozen;
            return; // this frame renders the just-captured pose anyway
        }

        // clamp to the array we actually hold — partialCount is re-read from live memory and can
        // shrink/grow between the capture-check above and here (audit finding: out-of-bounds)
        var applyCount = System.Math.Min(partialCount, frozenModel!.Length);
        for (var p = 0; p < applyCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null) continue;
            var frozenM = frozenModel[p];
            var n = System.Math.Min(frozenM.Length, pose->ModelPose.Length);
            for (var i = 0; i < n; i++)
            {
                // AccessBoneModelSpace (not raw ModelPose[i] writes) so Havok's sync flags stay
                // correct — Brio's ApplySnapshot does exactly this. DontPropagate: we set EVERY
                // bone absolutely, nothing derives from parents.
                var t = pose->AccessBoneModelSpace(i, hkaPose.PropagateOrNot.DontPropagate);
                if (t != null) *t = frozenM[i];
            }
        }
    }

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
        // _equipmentModelIds sits at 0x20 in the CURRENT struct (index 4) — the old index 2 (0x10)
        // silently wrote into CustomizeData (masked by CopyFromCharacter re-stomping every tick)
        basePtr[4 + slotIndex] = value;
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

    public PreviewRenderer(IFramework framework, IPluginLog log, ISigScanner sigScanner, IGameInteropProvider gameInterop)
    {
        _framework = framework;
        _log = log;
        _sigScanner = sigScanner;
        _gameInterop = gameInterop;
    }

    private readonly ISigScanner _sigScanner;
    private readonly IGameInteropProvider _gameInterop;

    public bool IsInitialized => _initialized;

    /// <summary>True when our CharaView lost its camera — seen live after AgentBannerParty slot
    /// takeovers: every camera call silently no-ops from then on. NOT true while a native UI still
    /// owns the slot (temporary, resolves itself). Framework thread. Caller should fully
    /// reinitialize (GlamourPreviewWindow.ForceReinitializeForSelf), not just poke the camera.</summary>
    public bool CameraLost
    {
        get
        {
            if (!_initialized || _nativeUiOwnsSlot) return false;
            var agent = AgentTryon.Instance();
            return agent != null && agent->CharaView.Camera == null;
        }
    }

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
        // hi-res window: the RT family allocates lazily during the upcoming Render() calls —
        // scale exactly those (see CreateTexture2DDetour). ~2s window covers the state machine.
        EnsureCreateTexture2DHook();
        if (_createTexture2DHook != null)
        {
            _scaleCharaViewAllocs = true;
            _scaleWindowTicks = 120;
        }
        // ponytail: agent is intentionally left INACTIVE — Show() opens the game's Fitting Room
        // addon. CharaView renders fine with an inactive agent (Ktisis precedent): Initialize
        // once, then per-tick Update/Render; items are written directly into ModelData.
        agent->CharaView.Initialize(&agent->AgentInterface, CharaViewSlot, 0);
        agent->CharaView.ModelData.CopyFromCharacter(source);
        agent->CharaView.Update(_counter, agent->CharaView.GetCharacter());
        _initialized = true;
        // ponytail: seed a few refresh frames so early renders can't be clobbered by agent activity.
        _pendingRecopyFrames = 3;
        _autoFreezeCountdown = AutoFreezeDelayFrames; // freeze-by-default, see field comment
        _pendingIdleReset = true; // strip any copied mid-action stance (crafting etc.), see field comment
    }

    /// <summary>Request N future Ticks to re-copy from the source provider (fixes ApplyState frame-lag and Examine hijack).</summary>
    public void RequestRecopy(int frames)
    {
        if (frames > _pendingRecopyFrames) _pendingRecopyFrames = frames;
    }

    /// <summary>Per-frame update/render. Must be called on Framework thread.</summary>
    public void Tick()
    {
        _freezeAppliedThisFrame = false; // re-arm the once-per-frame freeze gate (see the detour)
        // Freeze-pointer hygiene, learned from a REAL crash (AccessViolation in
        // ApplyOrCaptureFrozenPose inside the detour, live crash dump): the pointer was only
        // refreshed on the frames that reached the render section at the bottom — every early
        // return below (native UI hijacking the slot and REBUILDING the clone is the killer:
        // the old skeleton gets freed) left the detour stomping freed memory. Clear it FIRST,
        // every Tick; only a frame that actually reaches a valid render re-arms it. A one-frame
        // freeze gap is invisible (the pose is static anyway); a stale pointer is a crash.
        _freezeSkeleton = 0;
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
        //
        // AgentTryon's own flag isn't the only offender: AgentCharaCard (Adventurer Plate) and the
        // AgentBannerParty/AgentBannerMIP family (party "group photo" screens — up to 8 party
        // members, one CharaView slot each, ours included when the party's big enough) are entirely
        // separate Agent instances with their own IsAgentActive(), confirmed via reflection against
        // the real FFXIVClientStructs.dll, not guessed — this is what the earlier "someone else's
        // portrait" reports (Adventure Plate, then a stranger's portrait with no clean repro) were:
        // different agents, same shared-slot mechanism, needing the same guard.
        // Six more agents carry their own CharaView field (found by grepping FFXIVClientStructs for
        // every "...CharaView" struct, not just the ones we'd already tripped over live): dye
        // preview (Colorant), gearset preview (GearSet), the Character Inspect window, and the
        // Mirage Plate ("Glamour Plate") editor itself. Same shared-slot mechanism, same guard.
        var charaCard = AgentCharaCard.Instance();
        var bannerParty = AgentBannerParty.Instance();
        var bannerMip = AgentBannerMIP.Instance();
        var colorant = AgentColorant.Instance();
        var gearSet = AgentGearSet.Instance();
        var inspect = AgentInspect.Instance();
        var miragePlate = AgentMiragePrismMiragePlate.Instance();
        var status = AgentStatus.Instance();
        if (agent->AgentInterface.IsAgentActive()
            || (charaCard != null && charaCard->AgentInterface.IsAgentActive())
            || (bannerParty != null && bannerParty->AgentBannerInterface.AgentInterface.IsAgentActive())
            || (bannerMip != null && bannerMip->AgentBannerInterface.AgentInterface.IsAgentActive())
            || (colorant != null && colorant->AgentInterface.IsAgentActive())
            || (gearSet != null && gearSet->AgentInterface.IsAgentActive())
            || (inspect != null && inspect->AgentInterface.IsAgentActive())
            || (miragePlate != null && miragePlate->AgentInterface.IsAgentActive())
            || (status != null && status->AgentInterface.IsAgentActive()))
        {
            if (!_nativeUiOwnsSlot) _log.Info($"[PreviewRenderer] native UI took over the CharaView slot (tryon={agent->AgentInterface.IsAgentActive()} charaCard={charaCard != null && charaCard->AgentInterface.IsAgentActive()} bannerParty={bannerParty != null && bannerParty->AgentBannerInterface.AgentInterface.IsAgentActive()} bannerMip={bannerMip != null && bannerMip->AgentBannerInterface.AgentInterface.IsAgentActive()} colorant={colorant != null && colorant->AgentInterface.IsAgentActive()} gearSet={gearSet != null && gearSet->AgentInterface.IsAgentActive()} inspect={inspect != null && inspect->AgentInterface.IsAgentActive()} miragePlate={miragePlate != null && miragePlate->AgentInterface.IsAgentActive()} status={status != null && status->AgentInterface.IsAgentActive()}) — pausing our render/capture until it releases it");
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
            // Contamination guard at the SOURCE — the anvil saga's final form: neither the idle
            // pin nor the mode stomp helped, because the craft FACILITY PROP rides in with the
            // copy itself and then sticks to the clone. While the live char is in an occupied
            // mode (crafting/gathering/etc.), simply don't copy — the clone keeps the last clean
            // state; copying resumes the moment the action ends.
            // Also paused while a thaw-for-animation window runs (and no forced recopy is queued):
            // the per-tick copy stomped the clone's own transition mid-play — the unsheathe anim
            // visibly aborted back to idle. The clone is already synced; nothing is lost.
            var thawSettling = _autoFreezeCountdown > 0 && _pendingRecopyFrames == 0;
            if (addr != nint.Zero && !thawSettling)
            {
                var srcMode = ((Character*)addr)->Mode;
                if (srcMode is CharacterModes.Normal or CharacterModes.Mounted or CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
                    agent->CharaView.ModelData.CopyFromCharacter((Character*)addr);
            }
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
            // Weapons only. The equipment half of this fallback is GONE: for months it wrote to a
            // wrong offset (masked garbage), and the moment the offset was fixed it started
            // OVERWRITING ModelData equipment with DrawData MODEL ids every tick — while the
            // renderer dresses the char from _items (ITEM ids, seeded via SetItemSlotData). Result
            // was a naked character after the fix. Equipment never needed this path; weapons do
            // (they live in ModelData._weaponModelIds and CopyFromCharacter's copy of them is
            // stomped by the agent unless DoUpdate is off, see the weapon-mode block above).
            var addr = _sourceProvider();
            if (addr != nint.Zero)
            {
                var cand = (Character*)addr;
                if (cand->DrawData.OwnerObject != null)
                    for (var i = 0; i < 3; i++)
                        SetCharaViewWeaponSlotRaw((byte)i, *(ulong*)Unsafe.AsPointer(ref cand->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).ModelId));
            }
        }

        // Weapon-only showcase v3 ("es soll NUR die Waffe anzeigen"): hide the character's BODY
        // draw object entirely, keep the weapons' own draw objects visible — they're separate
        // scene objects attached to the (now invisible, still animating) skeleton, so a drawn
        // weapon floats in mid-air where the hand is. Gear-slot zeroing (v2) still showed the
        // naked body; the native flag (v1) did nothing on an inactive agent. Re-applied every
        // tick — the engine may reset visibility.
        {
            var cloneCh = agent->CharaView.GetCharacter();
            var bodyDraw = cloneCh != null ? cloneCh->GameObject.DrawObject : null;
            if (bodyDraw != null)
            {
                if (_weaponOnly)
                {
                    bodyDraw->IsVisible = false;
                    for (var i = 0; i < 3; i++)
                    {
                        var w = cloneCh->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).DrawObject;
                        if (w != null) w->IsVisible = true;
                    }
                    _weaponOnlyHidBody = true;
                }
                else if (_weaponOnlyHidBody)
                {
                    _weaponOnlyHidBody = false;
                    bodyDraw->IsVisible = true;
                }
            }
        }

        // ponytail: re-applied every tick — CopyFromCharacter above mirrors the live character's
        // ModelData wholesale, which stomps this flag back to "drawn" each frame otherwise.
        agent->CharaView.ToggleDrawWeapon(_weaponDrawn);
        // ToggleDrawWeapon alone wasn't enough: in combat the live char's drawn weapon still
        // showed up in the preview (reported live). HideWeapon is the real visibility flag on
        // TryonCharaView (separate from the drawn/sheathed stance) — keep the preview weaponless
        // unless explicitly requested.
        var weaponMode = _weaponDrawn || _weaponOnly;
        agent->CharaView.HideWeapon = !weaponMode;
        // ModelData carries its OWN WeaponHidden byte (0x89) that CopyFromCharacter refreshes from
        // the live char every tick — clear it in weapon mode or the copied "sheathed/hidden" state
        // wins. And DoUpdate=false while a weapon shows: vf10's agent-fetch would stomp the
        // ModelData weapon fields with the agent's EMPTY try-on data every Update otherwise.
        agent->CharaView.ModelData.WeaponHidden = !weaponMode ? agent->CharaView.ModelData.WeaponHidden : false;
        agent->CharaView.DoUpdate = !weaponMode;
        // The weapon DrawObject spawns fine but its own IsVisible flag stays FALSE (log-verified:
        // drawObject=non-null, objVisible=False) — force it every tick while a weapon mode is on,
        // same as the weapon-only body-hide loop already did for its case.
        if (_weaponDrawn || _weaponOnly)
        {
            var wch = agent->CharaView.GetCharacter();
            if (wch != null)
                for (var i = 0; i < 3; i++)
                {
                    var wd = wch->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).DrawObject;
                    if (wd != null) wd->IsVisible = true;
                }
        }

        var ch = agent->CharaView.GetCharacter();
        if (ch == null) return;

        // NOTE deliberately NO per-tick mode stomp / idle pin anymore: both were attempted anvil
        // fixes and made it WORSE — force-interrupting the clone's craft animation midway cut off
        // the craft-END path that despawns the facility prop, leaving an ORPHANED anvil in the
        // offscreen scene that survives every CharaView Release/Initialize (observed live: anvil
        // outlived resets and a class switch). The source-side copy gate below (skip copying while
        // the live char is occupied) is the actual prevention; an already-orphaned prop only goes
        // away with a plugin reload / game restart tearing the scene down.

        // pending weapon load (path #5) — once, on a loaded clone
        if (_weaponLoadPending && agent->CharaView.CharacterLoaded)
        {
            _weaponLoadPending = false;
            LoadWeaponsOntoClone(ch);
        }
        if (_weaponVerifyCountdown > 0 && --_weaponVerifyCountdown == 0)
        {
            var mh = ch->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand);
            _log.Info($"[PreviewRenderer] weapon verify (1s later): mainhand model id={mh.ModelId.Id} drawObject={(nint)mh.DrawObject:X} objVisible={(mh.DrawObject != null ? mh.DrawObject->IsVisible : false)} slotHidden={mh.IsHidden} containerHidden={ch->DrawData.IsWeaponHidden}");
        }

        // post-init idle reset (see _pendingIdleReset) — once, as soon as the clone is loaded
        if (_pendingIdleReset && agent->CharaView.CharacterLoaded)
        {
            _pendingIdleReset = false;
            ch->Timeline.BaseOverride = 0;
            ch->Timeline.TimelineSequencer.PlayTimeline(3); // 3 = plain idle
        }

        // one-time upward nudge for the tighter ortho framing (see OrthoLiftY) — the feet sat on
        // the bottom edge, the shrunken frame would cut them without this
        if (_orthoEnabled && !_orthoLiftApplied && agent->CharaView.CharacterLoaded)
        {
            _orthoLiftApplied = true;
            agent->CharaView.SetCameraXAndY(0f, OrthoLiftY);
        }

        // emote pose (see _emoteTimelineId's comment) — BaseOverride re-pinned every tick,
        // PlayTimeline once per selection for the immediate blend
        if (_emoteTimelineId != 0)
        {
            ch->Timeline.BaseOverride = _emoteTimelineId;
            if (_emotePlayPending)
            {
                _emotePlayPending = false;
                ch->Timeline.TimelineSequencer.PlayTimeline(_emoteTimelineId);
            }
        }
        else if (_emoteClearPending)
        {
            _emoteClearPending = false;
            ch->Timeline.BaseOverride = 0;
            ch->Timeline.TimelineSequencer.PlayTimeline(3); // 3 = normal idle (Brio's reset value)
        }
        // (no else: default state leaves the clone's timeline alone — see the orphaned-anvil NOTE
        // above for why force-pinning idle here backfired)

        // smart framing, ease-out half ("nie in einer Box wirken"): if the char clips the RT
        // border (arm swung past the edge mid-rotation, or a zoom fine front-on but not side-on),
        // gently pull the camera back each tick until he's fully in frame. 2%/tick reads as a
        // smooth glide; floor at 1.0 — the default full-body framing may legitimately touch the
        // bottom edge, easing forever below that would fight the native framing.
        if (_charTouchesBorder && _zoom > 1.0f)
        {
            NotifyInteraction(); // full capture rate while auto-framing so the glide streams smoothly
            SetZoom(_zoom * 0.98f);
        }

        // ortho projection — written BEFORE Update (which may rebuild the projection from these
        // fields) AND again after (in case Update overwrites them from its own config). First
        // after-Update-only version rendered "sehr weit weg und Zoom tut nichts" live — the
        // readback in GetWebCaptureStats shows what the engine actually keeps.
        ApplyOrtho(agent);
        agent->CharaView.Update(_counter, ch);
        ApplyOrtho(agent);

        if (_scaleWindowTicks > 0 && --_scaleWindowTicks == 0) _scaleCharaViewAllocs = false;
        // while thawed-for-animation (weapon stance / emote settling), keep the stream at full
        // rate — otherwise the idle throttle turns the visible animation into a 1fps slideshow
        // ("laggt dann frameweise bis zum Ende der Animation")
        if (_autoFreezeCountdown > 0) NotifyInteraction();
        // auto-freeze gate, from a live incident ("char hat grad random T-Pose gemacht"): a banner
        // hijack triggers auto-reinit, and the freeze countdown then fired while the rebuilt clone
        // was STILL LOADING its model — snapshot froze the load/T-pose permanently. The countdown
        // only ticks while the CharaView itself says the character finished loading.
        // countdown ALWAYS ticks — gating the tick itself on CharacterLoaded deadlocked the whole
        // pipeline after a class switch (loaded stays false while the model rebuilds, countdown
        // freezes, and the thaw-settle copy pause waits on the countdown => copies paused forever,
        // stale glam, invisible weapons; reported live). Only the freeze SNAPSHOT still waits for
        // a loaded character (the T-pose guard); if not loaded yet, re-arm and try again shortly.
        if (_autoFreezeCountdown > 0 && --_autoFreezeCountdown == 0 && !_freezePose)
        {
            // snapshot only when the model is loaded AND the post-init idle reset already played
            // (otherwise the reset button still froze a T-pose, reported live) — else retry soon
            if (agent->CharaView.CharacterLoaded && !_pendingIdleReset) SetFreezePose(true);
            else _autoFreezeCountdown = 30;
        }

        // freeze: refresh the clone's skeleton pointer for the UpdateBonePhysics detour (which runs
        // in the game's render task, where the pose is actually computed — writing bones HERE was
        // attempt 3 and did nothing, see the freeze block's comment)
        if (_freezePose)
        {
            var freezeDrawObject = ch->GameObject.DrawObject;
            _freezeSkeleton = freezeDrawObject != null
                ? (nint)((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)freezeDrawObject)->Skeleton
                : 0;
        }
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
        // ponytail: was 6, then 20 — live data confirmed 20 was hitting MY clamp, not a native
        // floor (zoom=20 measured at cameraDistance=5, plenty of room left). Raised again; still
        // untested where it starts looking blocky (model polygon density) or clips into the near
        // plane — tune based on feel, same as before.
        var target = Math.Clamp(zoom, 0.5f, 80.0f);
        var current = _zoom;
        // (an earlier hard "no zoom-in while border touched" gate lived here — killed zooming
        // entirely because the default framing already touches the border; the gentle ease-out
        // in Tick() is the whole smart-framing mechanism now)

        // ortho mode: zoom = projection height (applied in Tick), the camera does NOT move —
        // moving it would only shift the near/far planes for no visual gain
        if (_orthoEnabled) { _zoom = target; return; }
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

    /// <summary>Live camera distance (world units) and whether the last SetZoom actually moved it —
    /// for diagnosing "does it stop at MY 20.0 clamp or hit some native floor first" (reported live:
    /// "nicht ganz nah... stopp wie vanilla?"). Framework thread. Null if not ready to read.</summary>
    public float? GetCameraDistance()
    {
        if (!_initialized) return null;
        var agent = AgentTryon.Instance();
        if (agent == null) return null;
        var cam = agent->CharaView.Camera;
        if (cam == null) return null;
        var d = cam->Position - cam->LookAtVector;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
    }

    /// <summary>Pan the camera by a screen-space delta. Used for zoom-to-cursor. Framework thread.</summary>
    public void PanCamera(float deltaX, float deltaY)
    {
        if (!_initialized) return;
        var agent = AgentTryon.Instance();
        if (agent == null) return;
        agent->CharaView.SetCameraXAndY(deltaX, deltaY);
    }

    // Hi-res render target ("Ingame-Rendering, aber erweitert" research, avenue 2): the CharaView
    // RT family is allocated lazily on first Render() through Device::CreateTexture2D. Hooking
    // that and doubling the 576x960 allocations while OUR reinit is in flight gives a 1152x1920
    // target — the stream carries 2x pixels, the page shows them at the same size, and a client-
    // side loupe ("Lupe") can magnify 2x at native sharpness. The detour passes a stackalloc'd
    // size so the caller's own array is never mutated; the scale window is armed by DoInitialize
    // and disarmed after a countdown, so foreign CharaView allocations outside that window stay
    // untouched. Sig-scan failure = feature silently off, native res, no crash.
    private const string CreateTexture2DSig = "E8 ?? ?? ?? ?? 48 89 07 48 8D 7F 20";
    private const int RtScale = 2;
    private delegate nint CreateTexture2DDelegate(nint device, nint sizePtr, byte mipLevel, uint textureFormat, uint flags, uint unk);
    private Dalamud.Hooking.Hook<CreateTexture2DDelegate>? _createTexture2DHook;
    private volatile bool _scaleCharaViewAllocs;
    private int _scaleWindowTicks;

    private void EnsureCreateTexture2DHook()
    {
        if (_createTexture2DHook != null) return;
        try
        {
            var addr = _sigScanner.ScanText(CreateTexture2DSig);
            _createTexture2DHook = _gameInterop.HookFromAddress<CreateTexture2DDelegate>(addr, CreateTexture2DDetour);
            _createTexture2DHook.Enable();
        }
        catch (Exception ex)
        {
            _lastCaptureError = $"CreateTexture2D sig scan failed — hi-res preview unavailable: {ex.Message}";
            _log.Warning($"[PreviewRenderer] {_lastCaptureError}");
        }
    }

    private nint CreateTexture2DDetour(nint device, nint sizePtr, byte mipLevel, uint textureFormat, uint flags, uint unk)
    {
        try
        {
            if (_scaleCharaViewAllocs && sizePtr != 0)
            {
                var size = (int*)sizePtr;
                if (size[0] == 576 && size[1] == 960)
                {
                    var scaled = stackalloc int[2] { size[0] * RtScale, size[1] * RtScale };
                    return _createTexture2DHook!.Original(device, (nint)scaled, mipLevel, textureFormat, flags, unk);
                }
            }
        }
        catch { /* never disturb the engine's allocator path */ }
        return _createTexture2DHook!.Original(device, sizePtr, mipLevel, textureFormat, flags, unk);
    }

    // Orthographic camera ("Ingame-Rendering, aber erweitert" research, avenue 1): the CharaView's
    // Render.Camera exposes plain writable projection fields (FoV/AspectRatio/Near/Far/IsOrtho/
    // OrthoHeight — verified against FFXIVClientStructs source, no hooks needed). Ortho is what
    // real product viewers use: no perspective distortion, and zoom becomes a projection-height
    // change instead of a camera move — the character CANNOT clip the near plane or swing out of
    // the frustum sideways while zooming. The game recomputes the projection from these fields
    // each frame, so they're reapplied every Tick right before our Render() call.
    private bool _orthoEnabled = true; // default on — the whole point of the viewer
    // "Char allgemein größer, ohne Box-Limit, ohne Riesen-Fenster": the default framing has the
    // feet ON the bottom edge and dead air above the head — the only real gain is squeezing that
    // top gap. Tighter frame (smaller base height; engine default was 2.0241, read back live)
    // plus a one-time upward camera nudge (OrthoLiftY, applied once after load so the feet clear
    // the bottom edge). Both are TUNING constants — pan units are empirical.
    private const float OrthoBaseHeight = 1.9f;
    private const float OrthoLiftY = -14f;
    private bool _orthoLiftApplied;

    /// <summary>Toggle the orthographic projection. Framework thread.</summary>
    public void SetOrtho(bool enabled)
    {
        _orthoEnabled = enabled;
        if (enabled || !_initialized) return;
        // switching back to perspective: clear the flag once, the game re-derives the rest
        var agent = AgentTryon.Instance();
        var cam = agent != null ? agent->CharaView.Camera : null;
        if (cam != null && cam->RenderCamera != null) cam->RenderCamera->IsOrtho = false;
    }

    public bool OrthoEnabled => _orthoEnabled;

    // Wider stage ("Box größer — bei Emotes sind Teile abgehakt, wie die Hand"): the camera's
    // AspectRatio field is writable too. Rendering a WIDER world window into the same 576-wide
    // texture horizontally squeezes the pixels; the client CSS stretches the canvas back out
    // (aspect-ratio + object-fit:fill in WebUiPage). ~33% more room at the sides for a small
    // horizontal sharpness cost. Native aspect is 576/960 = 0.6.
    private const float PreviewAspect = 0.8f;

    private void ApplyOrtho(AgentTryon* agent)
    {
        var sceneCam = agent->CharaView.Camera;
        if (sceneCam == null || sceneCam->RenderCamera == null) return;
        var rc = sceneCam->RenderCamera;
        rc->AspectRatio = PreviewAspect; // both projections — the client always un-stretches
        if (!_orthoEnabled) return;
        rc->IsOrtho = true;
        rc->OrthoHeight = OrthoBaseHeight / _zoom;
    }

    /// <summary>Readback of what the engine actually kept in the render camera — for diagnosing
    /// whether our ortho writes stick or get stomped by CharaView.Update. Framework thread.</summary>
    public (bool isOrtho, float orthoHeight, float fov)? GetRenderCameraState()
    {
        if (!_initialized) return null;
        var agent = AgentTryon.Instance();
        var cam = agent != null ? agent->CharaView.Camera : null;
        if (cam == null || cam->RenderCamera == null) return null;
        var rc = cam->RenderCamera;
        return (rc->IsOrtho, rc->OrthoHeight, rc->FoV);
    }

    // ponytail: neutral preview default — no weapon/tool in hand until the user opts in.
    private bool _weaponDrawn;
    // "Waffe only": weapon-focus mode — weapon drawn + every OTHER equipment piece hidden via the
    // native TryonCharaView.HideOtherEquipment flag (the Fitting Room's own "show only tried-on
    // item" checkbox drives the same field). The body itself stays — CharaView always renders a
    // character — but bare-skinned with just the weapon it reads as a weapon showcase.
    private bool _weaponOnly;
    private bool _weaponOnlyHidBody; // restore flag for the body draw object's visibility

    /// <summary>Weapon showcase mode: weapon DRAWN (glow/effects only show drawn — sheathed rest
    /// position was rejected live), all other gear hidden. Framework thread.</summary>
    public void SetWeaponOnly(bool on)
    {
        _weaponOnly = on;
        SetWeaponDrawn(on);
    }

    // Weapon path #5, the one that actually works (Ktisis' EquipmentEditor does exactly this):
    // DrawData.LoadWeapon() called directly ON THE CLONE spawns the weapon draw object on any
    // Character*. Everything else was verified dead live: ModelData raw writes / SetItemSlotData /
    // SetModelData (weapons ignored), and agent TryOn() opened the real Fitting Room window.
    private bool _weaponLoadPending;

    private int _weaponVerifyCountdown; // diagnostic: check the draw objects a second after loading

    private void LoadWeaponsOntoClone(Character* clone)
    {
        var addr = _sourceProvider?.Invoke() ?? 0;
        if (addr == 0) { _log.Warning("[PreviewRenderer] LoadWeapons: no source"); return; }
        var src = (Character*)addr;
        for (var i = 0; i < 3; i++)
        {
            var model = src->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).ModelId;
            _log.Info($"[PreviewRenderer] LoadWeapon slot {i}: id={model.Id} type={model.Type} variant={model.Variant} stain0={model.Stain0}");
            clone->DrawData.LoadWeapon((DrawDataContainer.WeaponSlot)i, model, 0, 0, 0, 0, false);
            clone->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).IsHidden = false; // spawn ≠ shown
        }
        // also flip the container-level weapon-hidden flag if present
        {
            clone->DrawData.IsWeaponHidden = false;
        }
        _weaponVerifyCountdown = 60;
    }

    /// <summary>Show/hide the mainhand weapon model in the preview. Re-applied every Tick.
    /// Also thaws a frozen pose for a moment: the drawn/sheathed switch plays a stance animation —
    /// with the skeleton frozen the visible pose never changes (reported live: "Waffe zeigen macht
    /// nix"). Unfreeze, let the stance settle (~1.5s via the auto-freeze countdown, which also
    /// waits for CharacterLoaded), re-freeze in the new stance automatically.</summary>
    public void SetWeaponDrawn(bool drawn)
    {
        _weaponDrawn = drawn;
        // poses are mutually exclusive ("besser wäre: Reset + Waffe zeigen"): drawing the weapon
        // mid-emote played the draw animation INSIDE the emote pose — clear the emote back to
        // idle first, the stance change then runs from a clean base
        if (_emoteTimelineId != 0) { _emoteClearPending = true; _emoteTimelineId = 0; _emotePlayPending = false; }
        if (drawn) _weaponLoadPending = true; // LoadWeapon on the clone next Tick — see the path-#5 comment
        ThawForAnimation();
    }

    private void ThawForAnimation()
    {
        // 150 ticks (~2.5s): the unsheathe animation is ~1s and the first refreeze window (90)
        // caught it mid-transition ("Animation bricht irgendwie ab und geht ins Idle zurück")
        if (!_freezePose) { if (_autoFreezeCountdown > 0) _autoFreezeCountdown = 150; return; }
        SetFreezePose(false);
        _autoFreezeCountdown = 150;
    }

    // Emote pose — Brio's mechanism (their ActionTimelineCapability, works on any Character*, no
    // GPose and NO unlock check at this layer: BannerTimeline/emote gating happens in the game's
    // UI when it builds its list, not where the timeline actually plays — unowned emotes render
    // fine, it's purely local): Timeline.BaseOverride pins the loop, TimelineSequencer.PlayTimeline
    // blends into it immediately. BaseOverride is re-applied every Tick because CharaView.Update
    // re-syncs from the live source (same re-stomp pattern as ToggleDrawWeapon above).
    private ushort _emoteTimelineId;
    private bool _emotePlayPending;
    private bool _emoteClearPending;
    // set by DoInitialize: once the clone reports loaded, force it onto the plain idle timeline
    // ONCE — a reset while the source char is mid-action (seen live: crafting = T-pose ON AN
    // ANVIL) copies that action state into the clone; PlayTimeline(3) drops the copied stance
    // and its timeline-bound props before the auto-freeze can snapshot the mess.
    private bool _pendingIdleReset;

    /// <summary>Play a looping ActionTimeline on the preview clone (0 = back to normal idle).
    /// Framework thread. Ids come from the Emote sheet's ActionTimeline column.</summary>
    public void SetEmoteTimeline(ushort timelineId)
    {
        if (timelineId == 0 && _emoteTimelineId != 0) _emoteClearPending = true;
        _emoteTimelineId = timelineId;
        _emotePlayPending = timelineId != 0;
        _weaponDrawn = false; // mutually exclusive with the weapon stance, same reason as above
        _weaponOnly = false;
        ThawForAnimation();
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

// --- web-UI MJPEG preview: async non-blocking readback + JPEG encode ---
    // The old version did a synchronous CopyResource+Map every capture (a real GPU/CPU stall) and
    // bounded its cost with a flat 150ms wall-clock throttle (~6-7fps ceiling, chosen arbitrarily to
    // cap that stall, not because 6-7fps was ever a goal). This version never stalls: Map uses
    // D3D11_MAP_FLAG_DO_NOT_WAIT, so an unfinished GPU copy is simply skipped and retried next
    // Draw() instead of blocking the render thread. Real cadence is now "however fast the GPU
    // actually finishes each copy" (typically 1 frame), only capped by EncodeThrottleMs below since
    // JPEG-encoding isn't free either and nothing needs it faster.
    // Double-buffered: while buffer A's copy is being polled/mapped/encoded, buffer B can already
    // have its OWN CopyResource in flight — measured live, single-buffer (issue-then-wait-a-frame-
    // then-check, strictly alternating) capped throughput at roughly half of Draw()'s own call rate
    // (58 Draw()/s in, ~26 encoded/s out) purely from that serialization, not GPU or encode cost.
    // Two independent buffers remove that serialization: a new copy can start the SAME Draw() call
    // that finished checking the other one.
    private const int WebStagingBufferCount = 2;
    private readonly ComPtr<ID3D11Texture2D>[] _webStaging = new ComPtr<ID3D11Texture2D>[WebStagingBufferCount];
    private readonly bool[] _webCopyPending = new bool[WebStagingBufferCount];
    // ponytail: reported live as "flackert leicht, wie Zucken" — two in-flight GPU copies aren't
    // guaranteed to finish in the order they were issued (DO_NOT_WAIT polling can observe a later
    // copy completing before an earlier one). Without this, whichever buffer happens to finish
    // first wins, so an older frame can occasionally display AFTER a newer one already did — a
    // brief visible rollback. Each buffer remembers which issue-sequence it's carrying; a
    // completed buffer only gets sent if its sequence is newer than the last frame actually shown.
    private readonly long[] _webBufferSeq = new long[WebStagingBufferCount];
    private long _webNextSeq = 1;
    private long _webLastSentSeq;
    private int _webNextIssueSlot; // round-robins which buffer gets the next CopyResource
    private uint _webStagingWidth, _webStagingHeight;
    private DXGI_FORMAT _webStagingFormat;
    private byte[]? _latestWebJpeg;
    private bool _latestWebFrameIsPng;
    private long _lastEncodeTickMs;
    // ponytail: the raw pixel copy + in-flight flag that let actual encoding move off the Draw()
    // thread — see PumpWebCapture's inline comment for why (transparent-backdrop mode was tanking
    // real in-game FPS by encoding synchronously on the game's own render thread).
    private byte[]? _rawFrameBuffer;
    private volatile bool _webEncodeInProgress;
    // ponytail: "mach mal nen Aus/An-Schalter für trans[parenz]" — experimental, off by default.
    // JPEG has no alpha channel at all, so a transparent backdrop needs PNG instead (slower/bigger,
    // only paid while this is actually on). Chroma-key is naive: samples the top-left pixel each
    // frame as "the backdrop color" and keys out anything close to it — CharaView has no real
    // alpha-separated backdrop/character split to read instead (checked the FFXIVClientStructs field
    // dump, nothing like it exists), and dark clothing/hair close to that sampled color WILL get
    // eaten too. Known, accepted limitation — this is for looking at, not a finished feature.
    private bool _transparentBackdropEnabled = true; // default on (user request) — depth mask made it reliable

    // Depth-buffer mask for the transparent mode — the REAL fix for "dark clothing gets eaten".
    // The color target's alpha is flat 255 (probed live, 0.0.0.165) and no backdrop-color field
    // exists, but the CharaView pipeline has its own depth/stencil target (RenderTargetManager
    // +0x360, "Depth/Stencil for CharaView?" — internal field, read via raw offset): the backdrop
    // has NO geometry, so its depth stays at the clear value while every character pixel differs —
    // a perfect cutout mask independent of color (same principle as the GPose community's ReShade
    // depth-alpha screenshots).
    // One depth staging PER color buffer, paired: the first version used a single unpaired staging
    // ("freeze makes pairing moot") — wrong the moment the CAMERA moves: color and mask from
    // different frames put stale backdrop pixels along the previous silhouette edge, reported live
    // as "flickert leicht weiß beim Drehen". The depth copy for a slot is issued immediately BEFORE
    // that slot's color copy on the same immediate context — D3D11 executes those in order, so a
    // finished color copy guarantees its paired depth copy finished too.
    private readonly ComPtr<ID3D11Texture2D>[] _depthStaging = new ComPtr<ID3D11Texture2D>[WebStagingBufferCount];
    private uint _depthWidth, _depthHeight;
    private DXGI_FORMAT _depthFormat;
    private byte[]? _rawDepthBuffer;
    private int _rawDepthRowPitch;
    private bool _rawDepthValid;
    // set when the depth target fails validation (dims mismatch after a game patch) — depth path
    // stays off for the session, transparent mode falls back to the flood-fill
    private bool _depthPathBroken;

    // Smart framing ("der Char soll NIE in einer Box wirken"): true while character pixels touch
    // the render-target border, i.e. the char is being clipped by the invisible RT edge. Fed by
    // the depth mask each encoded frame (background thread write, framework thread read — plain
    // bool, worst case one frame stale). SetZoom refuses to zoom IN while set; Tick eases the
    // camera back OUT until clear (e.g. an arm swinging past the edge mid-rotation).
    private volatile bool _charTouchesBorder;

    /// <summary>Scan the depth buffer's border ring (2px inset) for character pixels.</summary>
    private void UpdateBorderTouch(byte[] depth, int depthPitch, DXGI_FORMAT depthFmt, int depthW, int depthH)
    {
        var isFloat = depthFmt is DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT
            or DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT or DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
        var texelSize = depthFmt is DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT ? 8 : 4;
        const int inset = 2;
        bool touch = false;
        unsafe
        {
            fixed (byte* dpFixed = depth)
            {
                var dp = dpFixed;
                bool CharAt(int x, int y)
                {
                    var p = dp + y * depthPitch + x * texelSize;
                    var d = isFloat ? *(float*)p : (p[0] | (p[1] << 8) | (p[2] << 16)) / 16777215f;
                    return d != 0f && d != 1f; // not the clear value = geometry = character
                }
                // top + sides only — the BOTTOM edge is always touched (feet stand on the frame's
                // lower border even at default framing), counting it froze the zoom entirely
                // ("kann 0 zoomen, springt instant zurück"). Real mid-picture clipping happens at
                // the sides (arms mid-rotation) and top (head).
                for (var x = inset; x < depthW - inset && !touch; x += 2)
                    touch = CharAt(x, inset);
                for (var y = inset; y < depthH - inset && !touch; y += 2)
                    touch = CharAt(inset, y) || CharAt(depthW - 1 - inset, y);
            }
        }
        _charTouchesBorder = touch;
    }

    // ponytail: "es reicht auch eine Standaufnahme, der Char muss sich nicht aktiv bewegen" —
    // don't spend GPU/CPU capturing a smooth video nobody's watching. Full-rate (EncodeThrottleMs)
    // only for a short window after an actual camera action (rotate/pan/zoom/auto-spin); otherwise
    // a static, occasionally-refreshed frame is all that's needed. Auto-spin re-touches this every
    // ~50ms while running, so it naturally stays full-rate throughout a spin.
    private long _lastInteractionMs;
    private const long InteractionWindowMs = 600;
    private const long IdleEncodeThrottleMs = 1000; // ~1fps while nobody's touching the camera

    /// <summary>Call from any camera-changing web action (rotate/pan/zoom/setitem/auto-spin tick) to
    /// keep the capture rate at full speed for a bit — see the fields above for why idle otherwise
    /// drops to ~1fps instead of running a live video nobody asked for.</summary>
    public void NotifyInteraction() => _lastInteractionMs = Environment.TickCount64;

    /// <summary>Toggle the experimental transparent-backdrop chroma-key. See the field's own comment
    /// for why it's naive and what it'll get wrong. Thread-safe plain bool write.</summary>
    public void SetTransparentBackdrop(bool enabled) => _transparentBackdropEnabled = enabled;

    /// <summary>Whether LatestWebJpeg is currently a PNG (transparent-backdrop mode) instead of a
    /// JPEG — WebUiService's stream needs this to set the right per-part Content-Type.</summary>
    public bool LatestWebFrameIsPng => _latestWebFrameIsPng;
    // ponytail: was 33 (~30fps cap) — live data showed only ~22fps sustained even under that cap
    // (JPEG encode itself is 6ms, nowhere near the limiter), so the 30fps ceiling wasn't even being
    // hit. Lowered to try for more headroom; if GPU-copy-readiness is the real ceiling (not this),
    // sustained fps won't move much and that's the answer to "can we get more" without touching the
    // capture/copy side itself.
    private const long EncodeThrottleMs = 16; // ~60fps cap on JPEG re-encode cost

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

    // ponytail: counts every CaptureFrameForWeb call regardless of outcome — i.e. how often Draw()
    // itself fires. If (drawCalls / elapsed seconds) is already well under the game's real FPS, the
    // ceiling is Dalamud/ImGui's own Draw() cadence (out of our control), not anything downstream
    // of it (encode throttle, GPU copy readiness, etc.) — settles "can we get more fps" definitively
    // instead of continuing to tune knobs that were never the actual limiter.
    private long _drawCallCount;
    private long _drawCallWindowStartMs;

    // Alpha-channel probe of the captured render target. The transparent-backdrop flood-fill is a
    // dead end for dark outfits — but IF the CharaView target's alpha channel already distinguishes
    // character from backdrop (e.g. backdrop stays at clear-alpha while character geometry writes
    // opaque), the real mask existed all along and we've just been discarding it in the JPEG path.
    // min/max over sampled pixels answers that in one glance at /api/preview3d/debug:
    // min==max==255 → alpha is useless (all opaque); a spread → there's a real mask to use.
    private byte _alphaMin = 255;
    private byte _alphaMax;

    public readonly record struct WebCaptureStats(
        bool StagingReady, long FramesEncoded, long FramesSkipped, long CaptureErrors,
        long LastFrameBytes, long LastEncodeDurationMs, string? LastError, int StagingWidth, int StagingHeight,
        bool NativeUiOwnsSlot, double DrawCallsPerSecond, byte AlphaMin, byte AlphaMax,
        bool DepthMaskReady, string? DepthFormat, bool CharTouchesBorder);

    /// <summary>Snapshot of the web MJPEG capture pipeline's health — thread-safe plain field reads,
    /// same as LatestWebJpeg.</summary>
    public WebCaptureStats GetWebCaptureStats()
    {
        var elapsedS = (Environment.TickCount64 - _drawCallWindowStartMs) / 1000.0;
        var drawCallsPerSecond = elapsedS > 0 ? _drawCallCount / elapsedS : 0;
        return new(
            _webStaging[0].Get() != null, _webFramesEncoded, _webFramesSkipped, _webCaptureErrors,
            _lastFrameBytes, _lastEncodeDurationMs, _lastCaptureError, (int)_webStagingWidth, (int)_webStagingHeight,
            _nativeUiOwnsSlot, drawCallsPerSecond, _alphaMin, _alphaMax,
            _rawDepthValid, _depthStaging[0].Get() != null ? _depthFormat.ToString() : null, _charTouchesBorder);
    }

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
        if (_drawCallWindowStartMs == 0) _drawCallWindowStartMs = Environment.TickCount64;
        _drawCallCount++;
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
        for (var i = 0; i < WebStagingBufferCount; i++)
        {
            _webStaging[i].Dispose();
            _webStaging[i] = default;
            _webCopyPending[i] = false;
        }
        for (var i = 0; i < WebStagingBufferCount; i++) { _depthStaging[i].Dispose(); _depthStaging[i] = default; }
        _rawDepthValid = false;
        _webNextIssueSlot = 0;
        _webNextSeq = 1;
        _webLastSentSeq = 0;
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

        // (re)create the staging textures on first use or if the source render target resized
        if (_webStaging[0].Get() == null || desc.Width != _webStagingWidth || desc.Height != _webStagingHeight || desc.Format != _webStagingFormat)
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
            for (var i = 0; i < WebStagingBufferCount; i++)
            {
                if (device.Get()->CreateTexture2D(&stagingDesc, null, _webStaging[i].GetAddressOf()).FAILED)
                {
                    _webCaptureErrors++;
                    _lastCaptureError = "CreateTexture2D (staging) failed";
                    return;
                }
            }
            _webStagingWidth = desc.Width;
            _webStagingHeight = desc.Height;
            _webStagingFormat = desc.Format;
            _log.Info($"[PreviewRenderer] web staging textures (re)created: {WebStagingBufferCount}x {desc.Width}x{desc.Height} fmt={desc.Format}");
        }

        var now = Environment.TickCount64;
        var isBgra = desc.Format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM;

        // 1) drain whichever pending buffer's GPU copy has actually finished — at most one encode
        // per Draw() call (matches EncodeThrottleMs's intent: cap encode cost, not skip it entirely).
        // three-tier rate: full during interaction; ~30fps while the pose is UNFROZEN (a live
        // animation at 1fps is a slideshow, and encode is ~1ms since the fast PNG writer — cheap);
        // 1fps only when frozen AND idle (a static image needs no video).
        // transparent (PNG) frames are 10x the bytes of JPEG — pushing 60 of them per second into
        // Browsingway's Chromium made the OVERLAY itself stutter ("kann kaum den Viewer bewegen");
        // 30fps is indistinguishable for a rotate gesture and halves the decode load
        var fullRateMs = _transparentBackdropEnabled ? 33L : EncodeThrottleMs;
        var effectiveThrottleMs = now - _lastInteractionMs < InteractionWindowMs ? fullRateMs
            : !_freezePose ? 33
            : IdleEncodeThrottleMs;
        if (now - _lastEncodeTickMs >= effectiveThrottleMs)
        {
            for (var i = 0; i < WebStagingBufferCount; i++)
            {
                if (!_webCopyPending[i]) continue;
                D3D11_MAPPED_SUBRESOURCE mapped;
                var hr = context.Get()->Map((ID3D11Resource*)_webStaging[i].Get(), 0, D3D11_MAP.D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
                if (hr.Value == DXGI.DXGI_ERROR_WAS_STILL_DRAWING) { _webFramesSkipped++; continue; } // not done yet — leave pending, try again next Draw()
                if (hr.FAILED)
                {
                    _webCopyPending[i] = false;
                    _webCaptureErrors++;
                    _lastCaptureError = $"Map failed: 0x{hr.Value:X8}";
                    continue;
                }
                try
                {
                    // stale — a newer buffer already got shown while this one was still pending.
                    // Drop it silently (not an error, not a skip — the frame just lost the race).
                    if (_webBufferSeq[i] <= _webLastSentSeq) continue;
                    // Previous background encode still running — don't pile up a second one, leave
                    // this buffer's copy pending and retry the whole thing next Draw(). Reported
                    // live: enabling the transparent-backdrop (PNG + flood-fill) mode tanked actual
                    // in-game FPS — because the encode used to run SYNCHRONOUSLY right here, on the
                    // same thread as the game's own Present. Only the raw memcpy + Map/Unmap below
                    // (D3D11 calls, must stay inline with Draw — see this method's own doc comment)
                    // still run on this thread; actual JPEG/PNG encoding is pure CPU, no graphics API
                    // calls, and now runs on a background Task instead of blocking the game.
                    if (_webEncodeInProgress) continue;

                    var w = (int)desc.Width;
                    var h = (int)desc.Height;
                    var rowPitch = (int)mapped.RowPitch;
                    var byteCount = rowPitch * h;
                    if (_rawFrameBuffer == null || _rawFrameBuffer.Length != byteCount) _rawFrameBuffer = new byte[byteCount];
                    var rawFrameBuffer = _rawFrameBuffer;
                    fixed (byte* dst = rawFrameBuffer)
                        Buffer.MemoryCopy((void*)mapped.pData, dst, byteCount, byteCount);

                    _webLastSentSeq = _webBufferSeq[i];
                    _webEncodeInProgress = true;
                    var transparent = _transparentBackdropEnabled;
                    // read this slot's paired depth into _rawDepthBuffer while we're in the
                    // "no encode running" window — the Task below reads it, and the next refresh
                    // can only happen after that Task finished (same lifecycle guarantee
                    // _rawFrameBuffer relies on).
                    DrainDepthCopy(context, i);
                    var depthValid = _rawDepthValid && _rawDepthBuffer != null;
                    var depthBuf = _rawDepthBuffer;
                    var depthPitch = _rawDepthRowPitch;
                    var depthFmt = _depthFormat;
                    var depthW = (int)_depthWidth;
                    var depthH = (int)_depthHeight;
                    var encodeStart = Environment.TickCount64;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // alpha probe (see _alphaMin doc) — every 61st pixel, prime stride so
                            // the sample grid doesn't align with image structure
                            if (isBgra)
                            {
                                byte amin = 255, amax = 0;
                                for (var y2 = 0; y2 < h; y2 += 7)
                                {
                                    var row = y2 * rowPitch;
                                    for (var x2 = 0; x2 < w; x2 += 11)
                                    {
                                        var a = rawFrameBuffer[row + x2 * 4 + 3];
                                        if (a < amin) amin = a;
                                        if (a > amax) amax = a;
                                    }
                                }
                                _alphaMin = amin;
                                _alphaMax = amax;
                            }
                            // smart-framing border check — every frame, every mode (see _charTouchesBorder)
                            if (depthValid) UpdateBorderTouch(depthBuf!, depthPitch, depthFmt, depthW, depthH);
                            var png = transparent;
                            // depth mask when we have one; flood-fill only as fallback (depth copy
                            // not finished yet / depth target unavailable)
                            var encoded = png
                                ? (depthValid
                                    ? EncodeDepthMaskedPngFast(rawFrameBuffer, w, h, rowPitch, isBgra,
                                        depthBuf!, depthPitch, depthFmt, depthW, depthH)
                                    : EncodeChromaKeyedPngFromBuffer(rawFrameBuffer, w, h, rowPitch, isBgra))
                                : EncodeJpegFromBuffer(rawFrameBuffer, w, h, rowPitch, isBgra);
                            _latestWebFrameIsPng = png;
                            _latestWebJpeg = encoded;
                            _lastEncodeDurationMs = Environment.TickCount64 - encodeStart;
                            _lastFrameBytes = encoded.Length;
                            _webFramesEncoded++;
                            if (_webFramesEncoded == 1) _log.Info($"[PreviewRenderer] first web frame encoded: {_lastFrameBytes} bytes in {_lastEncodeDurationMs}ms, isBgra={isBgra}");
                        }
                        catch (Exception ex)
                        {
                            _webCaptureErrors++;
                            _lastCaptureError = $"background encode failed: {ex.Message}";
                        }
                        finally { _webEncodeInProgress = false; }
                    });
                    _lastEncodeTickMs = now;
                }
                finally
                {
                    context.Get()->Unmap((ID3D11Resource*)_webStaging[i].Get(), 0);
                    _webCopyPending[i] = false;
                }
                break; // one copy-and-dispatch per Draw() call is enough; the other buffer (if also ready) waits for the next one
            }
        }

        // 2) issue a fresh copy into whichever buffer is currently free — independent of step 1
        // above, so a new copy can start the SAME Draw() call that just finished checking the other
        // buffer, instead of always waiting a full extra Draw() between "issue" and "check".
        for (var tries = 0; tries < WebStagingBufferCount; tries++)
        {
            var slot = _webNextIssueSlot;
            _webNextIssueSlot = (_webNextIssueSlot + 1) % WebStagingBufferCount;
            if (_webCopyPending[slot]) continue; // still in flight — try the other buffer
            // depth FIRST, color second, same context — color-copy-finished then implies
            // depth-copy-finished, and both are from the same frame (see _depthStaging's comment)
            // depth is copied in EVERY mode now (was transparent-only): the smart-zoom border
            // check needs the char mask even while streaming JPEG. One extra CopyResource+Map
            // per frame, negligible.
            IssueDepthCopy(device, context, slot);
            context.Get()->CopyResource((ID3D11Resource*)_webStaging[slot].Get(), (ID3D11Resource*)tex.Get());
            _webCopyPending[slot] = true;
            _webBufferSeq[slot] = _webNextSeq++;
            break;
        }
    }

    // Offset of RenderTargetManager's internal Unk360 ("Depth/Stencil for CharaView?"), resolved
    // at runtime via reflection from the SHIPPED FFXIVClientStructs assembly instead of a
    // hardcoded 0x360 — a game patch that shifts the struct also ships an updated
    // FFXIVClientStructs, so the resolved offset moves with it. A hardcoded offset reading a
    // then-garbage pointer and dereferencing it is an AccessViolation (flagged in the crash-safety
    // audit). -1 = field not found (renamed/removed upstream) → depth path stays disabled.
    private static readonly int CharaViewDepthOffset = ResolveCharaViewDepthOffset();

    private static int ResolveCharaViewDepthOffset()
    {
        var field = typeof(RenderTargetManager).GetField("Unk360",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var attr = field?.GetCustomAttributes(typeof(System.Runtime.InteropServices.FieldOffsetAttribute), false);
        return attr is [System.Runtime.InteropServices.FieldOffsetAttribute a] ? a.Value : -1;
    }

    /// <summary>The CharaView pipeline's shared depth/stencil target (RenderTargetManager.Unk360,
    /// internal in FFXIVClientStructs — offset resolved via reflection, see CharaViewDepthOffset).
    /// Shared by ALL CharaViews; ours holds our content right after our Render() call, and
    /// _nativeUiOwnsSlot already pauses us whenever anyone else drives the slot.</summary>
    private static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* GetCharaViewDepthTexture()
    {
        if (CharaViewDepthOffset < 0) return null;
        var rtm = RenderTargetManager.Instance();
        return rtm == null ? null : *(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + CharaViewDepthOffset);
    }

    /// <summary>Copy the CharaView depth target into the given slot's depth staging — called right
    /// before that slot's COLOR copy so both land in the same frame (see _depthStaging's comment).</summary>
    private void IssueDepthCopy(ComPtr<ID3D11Device> device, ComPtr<ID3D11DeviceContext> context, int slot)
    {
        if (_depthPathBroken) return;
        var gameTex = GetCharaViewDepthTexture();
        if (gameTex == null || gameTex->D3D11Texture2D == null)
        {
            if (_rawDepthBuffer == null) _lastCaptureError = "CharaView depth target unavailable (RTM+0x360 null)";
            return;
        }
        var depthTex = (ID3D11Texture2D*)gameTex->D3D11Texture2D;
        D3D11_TEXTURE2D_DESC desc;
        depthTex->GetDesc(&desc);
        // patch guard: the depth target MUST match the color RT's dimensions. A shifted struct
        // after a game patch can yield a pointer that happens to be a valid-but-WRONG texture —
        // dims are the cheap tell. Mismatch = permanently disable the depth path this session.
        if (desc.Width != _webStagingWidth || desc.Height != _webStagingHeight)
        {
            _depthPathBroken = true;
            _lastCaptureError = $"depth target dims {desc.Width}x{desc.Height} != color RT {_webStagingWidth}x{_webStagingHeight} — depth path disabled (game patch?)";
            _log.Warning($"[PreviewRenderer] {_lastCaptureError}");
            return;
        }
        if (_depthStaging[0].Get() == null || desc.Width != _depthWidth || desc.Height != _depthHeight || desc.Format != _depthFormat)
        {
            for (var i = 0; i < WebStagingBufferCount; i++) { _depthStaging[i].Dispose(); _depthStaging[i] = default; }
            _rawDepthValid = false;
            var stagingDesc = desc with
            {
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0u,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags = 0u,
                MipLevels = 1,
                ArraySize = 1,
            };
            for (var i = 0; i < WebStagingBufferCount; i++)
            {
                ComPtr<ID3D11Texture2D> staging = default;
                if (device.Get()->CreateTexture2D(&stagingDesc, null, staging.GetAddressOf()).FAILED)
                {
                    _lastCaptureError = $"CreateTexture2D (depth staging, fmt={desc.Format}) failed";
                    return;
                }
                _depthStaging[i] = staging;
            }
            _depthWidth = desc.Width;
            _depthHeight = desc.Height;
            _depthFormat = desc.Format;
            _log.Info($"[PreviewRenderer] depth stagings created: {WebStagingBufferCount}x {desc.Width}x{desc.Height} fmt={desc.Format}");
        }
        context.Get()->CopyResource((ID3D11Resource*)_depthStaging[slot].Get(), (ID3D11Resource*)depthTex);
    }

    /// <summary>Read the given slot's depth staging into _rawDepthBuffer. Called only after that
    /// slot's color Map already succeeded — the depth copy was issued before the color copy on the
    /// same context, so it's guaranteed finished; a plain blocking Map is fine here.</summary>
    private void DrainDepthCopy(ComPtr<ID3D11DeviceContext> context, int slot)
    {
        if (_depthStaging[slot].Get() == null) return;
        D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = context.Get()->Map((ID3D11Resource*)_depthStaging[slot].Get(), 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        if (hr.FAILED) { _lastCaptureError = $"depth Map failed: 0x{hr.Value:X8}"; return; }
        try
        {
            var byteCount = (int)mapped.RowPitch * (int)_depthHeight;
            if (_rawDepthBuffer == null || _rawDepthBuffer.Length != byteCount) _rawDepthBuffer = new byte[byteCount];
            _rawDepthRowPitch = (int)mapped.RowPitch;
            fixed (byte* dst = _rawDepthBuffer)
                Buffer.MemoryCopy((void*)mapped.pData, dst, byteCount, byteCount);
            _rawDepthValid = true;
        }
        finally
        {
            context.Get()->Unmap((ID3D11Resource*)_depthStaging[slot].Get(), 0);
        }
    }

    /// <summary>D3D11's BGRA formats are byte-identical to GDI+'s Format32bppArgb (same B,G,R,A
    /// memory order) — construct the Bitmap directly over the mapped GPU memory, no extra copy,
    /// only for the encode call's duration (Unmap happens right after in the caller).</summary>
    private static System.Drawing.Bitmap EncodeSourceBitmap(int width, int height, int rowPitch, nint scan0, bool isBgra)
        => isBgra
            ? new System.Drawing.Bitmap(width, height, rowPitch, System.Drawing.Imaging.PixelFormat.Format32bppArgb, scan0)
            : SwapToBgraBitmap(width, height, rowPitch, scan0);

    /// <summary>Pins the managed copy and delegates to EncodeJpeg — safe to call from a background
    /// thread/Task, unlike the GPU-mapped pointer this buffer was copied from (which is Unmap()'d
    /// back on the Draw thread before this ever runs).</summary>
    private static unsafe byte[] EncodeJpegFromBuffer(byte[] buffer, int width, int height, int rowPitch, bool isBgra)
    {
        fixed (byte* p = buffer) return EncodeJpeg(width, height, rowPitch, (nint)p, isBgra);
    }

    /// <summary>Same as EncodeJpegFromBuffer, for the chroma-key/PNG path.</summary>
    private unsafe byte[] EncodeChromaKeyedPngFromBuffer(byte[] buffer, int width, int height, int rowPitch, bool isBgra)
    {
        fixed (byte* p = buffer) return EncodeChromaKeyedPng(width, height, rowPitch, (nint)p, isBgra);
    }

    // --- minimal fast PNG writer ---
    // GDI+'s PNG encoder measured 37-65ms per 576x960 frame (max compression, no knob) and was THE
    // stream fps ceiling (~15fps). This writer: filter 0 rows + ZLibStream(Fastest) — bigger files,
    // several times faster, pure background-thread CPU, zero in-game cost.
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WritePngChunk(MemoryStream ms, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        ms.Write(len);
        ms.Write(type);
        ms.Write(data);
        var crc = Crc32(data, Crc32(type, 0xFFFFFFFF)) ^ 0xFFFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        ms.Write(len);
    }

    /// <summary>raw = h rows of (1 filter byte + w*4 RGBA bytes), filter 0 everywhere.</summary>
    internal static byte[] WritePngRgba(byte[] raw, int w, int h)
    {
        using var ms = new MemoryStream(raw.Length / 3);
        ms.Write(stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type RGBA
        WritePngChunk(ms, "IHDR"u8, ihdr);
        using var cms = new MemoryStream(raw.Length / 3);
        using (var z = new System.IO.Compression.ZLibStream(cms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        WritePngChunk(ms, "IDAT"u8, cms.GetBuffer().AsSpan(0, (int)cms.Length));
        WritePngChunk(ms, "IEND"u8, default);
        return ms.ToArray();
    }

    // reused across frames (single encode in flight, guarded by _webEncodeInProgress)
    private byte[]? _pngRawBuffer;
    private bool[]? _erodedScratch; // second mask ring for the anti-aliased silhouette edge

    /// <summary>Fast path of the depth-masked transparent encode: builds the RGBA rows directly
    /// (BGRA swizzle + depth mask + 1px erode) and writes the PNG itself — no GDI+ anywhere.</summary>
    private byte[] EncodeDepthMaskedPngFast(byte[] color, int w, int h, int rowPitch, bool isBgra,
        byte[] depth, int depthPitch, DXGI_FORMAT depthFmt, int depthW, int depthH)
    {
        var isFloat = depthFmt is DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT
            or DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT or DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
        var texelSize = depthFmt is DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS or DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT ? 8 : 4;

        if (_chromaVisited == null || _chromaVisited.Length != w * h) _chromaVisited = new bool[w * h];
        var isBackdrop = _chromaVisited;
        unsafe
        {
            fixed (byte* dpF = depth)
            {
                var dp = dpF;
                for (var y = 0; y < h; y++)
                {
                    var dy = depthH == h ? y : y * depthH / h;
                    var rowBase = y * w;
                    for (var x = 0; x < w; x++)
                    {
                        var dx = depthW == w ? x : x * depthW / w;
                        var p = dp + dy * depthPitch + dx * texelSize;
                        var d = isFloat ? *(float*)p : (p[0] | (p[1] << 8) | (p[2] << 16)) / 16777215f;
                        isBackdrop[rowBase + x] = d == 0f || d == 1f;
                    }
                }
            }
        }

        // pass 1: erode mask — kept pixel touching backdrop (4-neighborhood) goes transparent too
        // (antialiased silhouette pixels blend char+backdrop color, the bright-fringe fix)
        if (_erodedScratch == null || _erodedScratch.Length != w * h) _erodedScratch = new bool[w * h];
        var eroded = _erodedScratch;
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var i = rowBase + x;
                eroded[i] = isBackdrop[i]
                    || (x > 0 && isBackdrop[i - 1]) || (x < w - 1 && isBackdrop[i + 1])
                    || (y > 0 && isBackdrop[i - w]) || (y < h - 1 && isBackdrop[i + w]);
            }
        }

        var rawLen = h * (1 + w * 4);
        if (_pngRawBuffer == null || _pngRawBuffer.Length != rawLen) _pngRawBuffer = new byte[rawLen];
        var raw = _pngRawBuffer;
        for (var y = 0; y < h; y++)
        {
            var src = y * rowPitch;
            var dst = y * (1 + w * 4);
            raw[dst++] = 0; // filter: none
            var rowBase = y * w;
            for (var x = 0; x < w; x++, src += 4, dst += 4)
            {
                var i = rowBase + x;
                if (eroded[i])
                {
                    raw[dst] = 0; raw[dst + 1] = 0; raw[dst + 2] = 0; raw[dst + 3] = 0;
                    continue;
                }
                // anti-aliased edge ("Lupe an den Rändern verpixelt"): kept pixels bordering the
                // eroded ring get half alpha — the binary 0/255 stair-step was the visible jaggy,
                // doubly so magnified in the loupe. One semi ring reads as a smooth silhouette.
                var edge = (x > 0 && eroded[i - 1]) || (x < w - 1 && eroded[i + 1])
                    || (y > 0 && eroded[i - w]) || (y < h - 1 && eroded[i + w]);
                var a = (byte)(edge ? 140 : 255);
                if (isBgra)
                {
                    raw[dst] = color[src + 2]; raw[dst + 1] = color[src + 1]; raw[dst + 2] = color[src]; raw[dst + 3] = a;
                }
                else
                {
                    raw[dst] = color[src]; raw[dst + 1] = color[src + 1]; raw[dst + 2] = color[src + 2]; raw[dst + 3] = a;
                }
            }
        }
        return WritePngRgba(raw, w, h);
    }

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

    /// <summary>Chroma-key + PNG encode — see _transparentBackdropEnabled's doc comment for why this
    /// has no real alpha source to work from. Flood-fills from the image BORDER instead of a single
    /// global reference color: only backdrop connected to the edge gets keyed out, so dark clothing/
    /// hair in the middle of the frame survives even if similarly colored (reported live: a flat
    /// global threshold made "die Kleidung sieht weird aus" — this fixes that class of mistake, a
    /// gradient backdrop is naturally one connected blob touching every edge, a worn character isn't
    /// connected to the edge in a normal centered framing). Each step compares to its OWN neighbor's
    /// color, not a fixed reference, so it tolerates the backdrop's gradient.
    /// EncodeSourceBitmap already normalizes to Format32bppArgb regardless of source BGRA-ness, so
    /// this can just work in that one format without re-deriving isBgra logic itself.</summary>
    // ponytail: reused across frames instead of a fresh `new bool[width*height]` + `new Stack<int>`
    // every single call — real GC pressure at 15-60fps for a ~550k-element array. Cleared, not
    // reallocated, unless the source resized (matches the width/height fields' own resize check).
    private bool[]? _chromaVisited;
    private Stack<int>? _chromaStack;

    private byte[] EncodeChromaKeyedPng(int width, int height, int rowPitch, nint scan0, bool isBgra)
    {
        using var bmp = EncodeSourceBitmap(width, height, rowPitch, scan0, isBgra);
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        const int thresholdSq = 18 * 18; // untuned — per-step tolerance now, not a global one; raise/lower based on how it looks live

        if (_chromaVisited == null || _chromaVisited.Length != width * height)
        {
            _chromaVisited = new bool[width * height];
            _chromaStack = new Stack<int>(width * height / 4);
        }
        else
        {
            Array.Clear(_chromaVisited);
            _chromaStack!.Clear();
        }
        var visited = _chromaVisited;
        var stack = _chromaStack!;

        unsafe
        {
            var basePtr = (byte*)data.Scan0;
            var stride = data.Stride;

            void Seed(int x, int y)
            {
                var i = y * width + x;
                if (visited[i]) return;
                visited[i] = true;
                stack.Push(i);
            }
            for (var x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
            for (var y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }

            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var x = i % width;
                var y = i / width;
                var p = basePtr + y * stride + x * 4;
                p[3] = 0; // keyed out — part of the backdrop blob

                if (x > 0) TryExpand(x - 1, y, p);
                if (x < width - 1) TryExpand(x + 1, y, p);
                if (y > 0) TryExpand(x, y - 1, p);
                if (y < height - 1) TryExpand(x, y + 1, p);
            }

            void TryExpand(int nx, int ny, byte* fromPixel)
            {
                var ni = ny * width + nx;
                if (visited[ni]) return;
                var np = basePtr + ny * stride + nx * 4;
                int db = np[0] - fromPixel[0], dg = np[1] - fromPixel[1], dr = np[2] - fromPixel[2];
                if (db * db + dg * dg + dr * dr >= thresholdSq) return;
                visited[ni] = true;
                stack.Push(ni);
            }
        }
        bmp.UnlockBits(data);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
        // the clone (and its skeleton) is about to die — stop the freeze detour touching it
        _freezeSkeleton = 0;
        _frozenModel = null;
        _orthoLiftApplied = false; // fresh camera after reinit needs the nudge again
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
        // hook first, and unconditionally — it may exist even if CharaView never initialized,
        // and a live detour surviving plugin unload is a guaranteed crash
        _freezePose = false;
        _freezeSkeleton = 0;
        _updateBonePhysicsHook?.Dispose();
        _updateBonePhysicsHook = null;
        _scaleCharaViewAllocs = false;
        _createTexture2DHook?.Dispose();
        _createTexture2DHook = null;
        if (!_initialized) return;
        // Dispose can be called off-frame; hop to Framework thread for the Release.
        try { _framework.RunOnFrameworkThread(Release).Wait(); }
        catch (Exception ex) { _log.Warning($"[PreviewRenderer] Dispose Release failed: {ex.Message}"); }
    }
}
