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
        _freezePose = true;
        _updateBonePhysicsHook.Enable();
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = _updateBonePhysicsHook!.Original(a1);
        // never let anything escape into the engine's hot path — a bad frame here is a crash
        try
        {
            var skeletonPtr = _freezeSkeleton;
            if (_freezePose && skeletonPtr != 0) ApplyOrCaptureFrozenPose((Skeleton*)skeletonPtr);
        }
        catch (Exception ex) { _lastCaptureError = $"freeze detour: {ex.Message}"; }
        return result;
    }

    private void ApplyOrCaptureFrozenPose(Skeleton* skeleton)
    {
        if (skeleton == null || skeleton->PartialSkeletonCount == 0) return;
        var partialCount = skeleton->PartialSkeletonCount;

        // (re-)capture: first frozen frame, or skeleton shape changed under us (e.g. gear swap)
        var capture = _frozenModel == null || _frozenModel.Length != partialCount;
        if (!capture)
        {
            for (var p = 0; p < partialCount; p++)
            {
                var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                if (pose != null && pose->ModelPose.Length != _frozenModel![p].Length) { capture = true; break; }
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

        for (var p = 0; p < partialCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null) continue;
            var frozenM = _frozenModel![p];
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
    private bool _transparentBackdropEnabled;

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
        bool NativeUiOwnsSlot, double DrawCallsPerSecond, byte AlphaMin, byte AlphaMax);

    /// <summary>Snapshot of the web MJPEG capture pipeline's health — thread-safe plain field reads,
    /// same as LatestWebJpeg.</summary>
    public WebCaptureStats GetWebCaptureStats()
    {
        var elapsedS = (Environment.TickCount64 - _drawCallWindowStartMs) / 1000.0;
        var drawCallsPerSecond = elapsedS > 0 ? _drawCallCount / elapsedS : 0;
        return new(
            _webStaging[0].Get() != null, _webFramesEncoded, _webFramesSkipped, _webCaptureErrors,
            _lastFrameBytes, _lastEncodeDurationMs, _lastCaptureError, (int)_webStagingWidth, (int)_webStagingHeight,
            _nativeUiOwnsSlot, drawCallsPerSecond, _alphaMin, _alphaMax);
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
        var effectiveThrottleMs = now - _lastInteractionMs < InteractionWindowMs ? EncodeThrottleMs : IdleEncodeThrottleMs;
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
                            var png = transparent;
                            var encoded = png
                                ? EncodeChromaKeyedPngFromBuffer(rawFrameBuffer, w, h, rowPitch, isBgra)
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
            context.Get()->CopyResource((ID3D11Resource*)_webStaging[slot].Get(), (ID3D11Resource*)tex.Get());
            _webCopyPending[slot] = true;
            _webBufferSeq[slot] = _webNextSeq++;
            break;
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
        if (!_initialized) return;
        // Dispose can be called off-frame; hop to Framework thread for the Release.
        try { _framework.RunOnFrameworkThread(Release).Wait(); }
        catch (Exception ex) { _log.Warning($"[PreviewRenderer] Dispose Release failed: {ex.Message}"); }
    }
}
