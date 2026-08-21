using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamSource.Core;

namespace GlamSource.Windows;

public enum GatherState
{
    Idle,
    Teleporting,
    SwappingJob,
    Mounting,
    FlyingToArea,
    MovingToNode,
    Interacting,
    WaitingForGatherWindow,
    Gathering,
    Done,
    Failed,
}

/// <summary>
/// Independent Mining/Botany gather flow: nearest node for one ItemId -> move -> interact -> click slot.
/// Decoupled from GatherBuddyReborn's list/enabled system entirely — no FallbackItems, no list mutation.
/// ponytail: no mount/fly handling (vnavmesh's PathfindAndMoveTo already walks the player there), no
/// fishing/collectables/Diadem — scope is plain ground-based Mining/Botany nodes only.
/// Drives itself off IFramework.Update; caller starts it via StartGathering(itemId) and polls State/IsDone.
/// </summary>
public sealed unsafe class SimpleGatherService : IDisposable
{
    private const float InteractRange = 3.0f;

    private readonly IGatheringLocationService _locations;
    private readonly VNavmeshIpc _nav;
    private readonly TeleporterIpc _teleporter;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly Func<Configuration> _configAccessor;

    public GatherState State { get; private set; } = GatherState.Idle;
    public bool IsDone => State is GatherState.Done or GatherState.Failed;

    private uint _targetItemId;
    private IGameObject? _targetNode;
    private uint _targetTerritoryId;
    private GatheringLocation? _targetLocation;
    private Vector3 _targetWorld;
    private DateTime _teleportDeadline;
    private DateTime _stateDeadline;
    private uint _wantedClassJobId; // 0 = no swap wanted
    // Mount Roulette (id 24 GeneralAction). Used when config MountId = 0.
    private const uint MountRouletteGeneralAction = 24;
    private const float FlyArriveRange = 20f;

    public SimpleGatherService(
        IGatheringLocationService locations,
        VNavmeshIpc nav,
        TeleporterIpc teleporter,
        IObjectTable objectTable,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog log,
        IFramework framework,
        Func<Configuration> configAccessor)
    {
        _locations = locations;
        _nav = nav;
        _teleporter = teleporter;
        _objectTable = objectTable;
        _clientState = clientState;
        _condition = condition;
        _gameGui = gameGui;
        _log = log;
        _framework = framework;
        _configAccessor = configAccessor;

        _framework.Update += OnUpdate;
    }

    // ClassJob ids: Miner=16, Botanist=17, Fisher=18
    private const uint JobMiner = 16;
    private const uint JobBotanist = 17;
    private const uint JobFisher = 18;

    private static uint WantedJobFor(string gatheringTypeName) => gatheringTypeName switch
    {
        "Mining" or "Quarrying" => JobMiner,
        "Logging" or "Harvesting" => JobBotanist,
        "Spearfishing" => JobFisher,
        _ => 0,
    };

    private string? GearsetNameFor(uint jobId) => jobId switch
    {
        JobMiner => _configAccessor().MinerSetName,
        JobBotanist => _configAccessor().BotanistSetName,
        JobFisher => _configAccessor().FisherSetName,
        _ => null,
    };

    /// <summary>
    /// Equips a gearset for the requested ClassJob directly via RaptureGearsetModule.
    /// Prefers the user-configured set name (lets user pin a specific loadout); falls back
    /// to the first existing gearset matching the ClassJob. Returns false if none found.
    /// Pattern lifted from Questionable/Data/ClassJobUtils.cs:229 (SwitchClassJob).
    /// </summary>
    private bool TryEquipGearsetForJob(uint classJobId, string? preferredName)
    {
        var mod = RaptureGearsetModule.Instance();
        if (mod == null) return false;

        int? fallbackId = null;
        for (var i = 0; i < 100; i++)
        {
            var e = mod->GetGearset(i);
            if (e == null || !e->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            if (e->ClassJob != (byte)classJobId) continue;

            if (!string.IsNullOrWhiteSpace(preferredName) && e->NameString == preferredName)
            {
                mod->EquipGearset(e->Id);
                return true;
            }
            fallbackId ??= e->Id;
        }

        if (fallbackId is int id)
        {
            mod->EquipGearset(id);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Starts gathering the given ItemId. Returns false immediately if no gathering node/game object
    /// could be found nearby (player must already be in the right territory — no zone travel here).
    /// </summary>
    /// <summary>
    /// Structured start result — tells caller *which* precondition failed instead of one lumpy bool.
    /// </summary>
    public readonly record struct StartResult(bool Started, string Reason)
    {
        public static StartResult Ok() => new(true, string.Empty);
        public static StartResult Fail(string reason) => new(false, reason);
    }

    public StartResult TryStartGathering(uint itemId)
    {
        if (State is GatherState.Teleporting or GatherState.Mounting or GatherState.FlyingToArea or GatherState.MovingToNode or GatherState.Interacting or GatherState.WaitingForGatherWindow or GatherState.Gathering)
            return StartResult.Fail("Already gathering — stop current run first");

        var locations = _locations.GetLocations(itemId);
        if (locations.Count == 0)
        {
            _log.Warning($"[SimpleGatherService] No gathering location known for item {itemId}");
            State = GatherState.Failed;
            return StartResult.Fail("Item has no known gathering location (not gatherable, or missing from data)");
        }

        var currentTerritory = _clientState.TerritoryType;
        _targetItemId = itemId;

        if (!locations.Any(l => l.TerritoryId == currentTerritory))
        {
            // Wrong zone — pick first location with a known aetheryte and teleport.
            var wanted = string.Join(", ", locations.Select(l => l.TerritoryName ?? l.TerritoryId.ToString()).Distinct());
            if (!_teleporter.IsAvailable)
            {
                _log.Warning($"[SimpleGatherService] Wrong zone (in {currentTerritory}), Teleporter IPC unavailable. Need: {wanted}");
                State = GatherState.Failed;
                return StartResult.Fail($"Wrong zone — install/enable Teleporter plugin, or travel manually to: {wanted}");
            }

            foreach (var loc in locations)
            {
                var aetheryteId = _locations.GetAetheryteFor(loc.TerritoryId);
                if (aetheryteId is null)
                    continue;
                if (!_teleporter.Teleport(aetheryteId.Value))
                    continue;
                _targetTerritoryId = loc.TerritoryId;
                _targetLocation = loc;
                _teleportDeadline = DateTime.UtcNow.AddSeconds(30);
                State = GatherState.Teleporting;
                _log.Information($"[SimpleGatherService] Teleporting to aetheryte {aetheryteId} in territory {loc.TerritoryId} ({loc.TerritoryName}) — target XZ ({loc.WorldX:F1}, {loc.WorldZ:F1})");
                return StartResult.Ok();
            }

            _log.Warning($"[SimpleGatherService] Wrong zone but no aetheryte known for any candidate: {wanted}");
            State = GatherState.Failed;
            return StartResult.Fail($"Wrong zone and no known aetheryte — travel manually to: {wanted}");
        }

        // Same-zone: fly to the known coord first (ObjectTable's ~50m radius won't see
        // a node from a random spawn point). ScanForNodeThenMove kicks in on arrival.
        _targetLocation = locations.First(l => l.TerritoryId == currentTerritory);
        _targetTerritoryId = currentTerritory;
        BeginJobSwapOrMount();
        return StartResult.Ok();
    }

    /// <summary>
    /// Gate before mounting: swap gearset if wrong job, else go straight to mount decision.
    /// </summary>
    private void BeginJobSwapOrMount()
    {
        _wantedClassJobId = _targetLocation != null ? WantedJobFor(_targetLocation.GatheringTypeName) : 0;
        if (_wantedClassJobId == 0 || _objectTable.LocalPlayer?.ClassJob.RowId == _wantedClassJobId)
        {
            DecideMountOrWalk();
            return;
        }

        var setName = GearsetNameFor(_wantedClassJobId);
        if (!TryEquipGearsetForJob(_wantedClassJobId, setName))
        {
            _log.Warning($"[SimpleGatherService] No gearset found for ClassJob {_wantedClassJobId} — create one in-game (name in config is optional)");
            State = GatherState.Failed;
            return;
        }

        _log.Information($"[SimpleGatherService] Equipping gearset for ClassJob {_wantedClassJobId} (preferred name: \"{setName}\")");
        State = GatherState.SwappingJob;
        _stateDeadline = DateTime.UtcNow.AddSeconds(10);
    }

    /// <summary>
    /// Skips mounting entirely when the target coord is within MountUpDistance; otherwise mounts up.
    /// </summary>
    private void DecideMountOrWalk()
    {
        if (_targetLocation == null)
        {
            State = GatherState.Failed;
            return;
        }
        var player = _objectTable.LocalPlayer;
        if (player != null)
        {
            var flatDx = _targetLocation.WorldX - player.Position.X;
            var flatDz = _targetLocation.WorldZ - player.Position.Z;
            var flat = MathF.Sqrt(flatDx * flatDx + flatDz * flatDz);
            if (flat <= _configAccessor().MountUpDistance)
            {
                _log.Information($"[SimpleGatherService] Node within {flat:F1}m — walking, no mount");
                BeginFlyingToArea();
                return;
            }
        }
        BeginMounting();
    }

    private static unsafe bool IsMountUnlocked(uint mountId)
    {
        var ps = PlayerState.Instance();
        return ps != null && ps->IsMountUnlocked(mountId);
    }

    private void BeginMounting()
    {
        if (_condition[ConditionFlag.Mounted] || _condition[ConditionFlag.InFlight])
        {
            BeginFlyingToArea();
            return;
        }

        var am = ActionManager.Instance();
        var mountId = _configAccessor().AutoGatherMountId;

        // GBR pattern: specific mount if unlocked & ready → else Roulette → else give up.
        if (mountId != 0 && IsMountUnlocked(mountId) && am->GetActionStatus(ActionType.Mount, mountId) == 0)
        {
            am->UseAction(ActionType.Mount, mountId);
        }
        else if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralAction) == 0)
        {
            am->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
        }
        else
        {
            _log.Information("[SimpleGatherService] No usable mount available — walking");
            BeginFlyingToArea();
            return;
        }

        State = GatherState.Mounting;
        _stateDeadline = DateTime.UtcNow.AddSeconds(5);
    }

    private void BeginFlyingToArea()
    {
        if (_targetLocation == null)
        {
            State = GatherState.Failed;
            return;
        }

        // Try to snap Y to the actual floor near the target — vnavmesh needs a real 3D coord for flying.
        var approx = _targetLocation.ApproxWorld with { Y = 1024f };
        _targetWorld = _nav.PointOnFloor(approx, allowUnlandable: true, halfExtentXZ: 20f)
                       ?? _targetLocation.ApproxWorld;

        State = GatherState.FlyingToArea;
        _stateDeadline = DateTime.UtcNow.AddSeconds(120);
        _nav.PathfindAndMoveTo(_targetWorld, fly: true);
        _log.Information($"[SimpleGatherService] Flying to node area at ({_targetWorld.X:F1}, {_targetWorld.Y:F1}, {_targetWorld.Z:F1})");
    }

    // ponytail: keep bool overload so existing callers compile; new callers should prefer TryStartGathering for the reason.
    public bool StartGathering(uint itemId) => TryStartGathering(itemId).Started;

    public void Stop()
    {
        _nav.Stop();
        State = GatherState.Idle;
        _targetNode = null;
    }

    private IGameObject? FindNearestNodeObject()
    {
        // ponytail: "gathering node" objects have ObjectKind.GatheringPoint; closest one to the player is
        // our best guess without a full ID->object-name lookup table. Good enough for the reduced scope.
        var player = _objectTable.LocalPlayer;
        if (player == null)
            return null;

        IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint)
                continue;

            var dist = Vector3.DistanceSquared(player.Position, obj.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = obj;
            }
        }

        return best;
    }

    private void OnUpdate(IFramework framework)
    {
        switch (State)
        {
            case GatherState.Teleporting:
                TickTeleporting();
                break;
            case GatherState.SwappingJob:
                TickSwappingJob();
                break;
            case GatherState.Mounting:
                TickMounting();
                break;
            case GatherState.FlyingToArea:
                TickFlyingToArea();
                break;
            case GatherState.MovingToNode:
                TickMoving();
                break;
            case GatherState.Interacting:
                TickInteracting();
                break;
            case GatherState.WaitingForGatherWindow:
                TickWaitingForGatherWindow();
                break;
            case GatherState.Gathering:
                TickGathering();
                break;
        }
    }

    private void TickTeleporting()
    {
        if (_clientState.TerritoryType == _targetTerritoryId
            && !_condition[ConditionFlag.BetweenAreas]
            && !_condition[ConditionFlag.BetweenAreas51])
        {
            // Landed. Find node in the new zone.
            var node = FindNearestNodeObject();
            if (node == null)
            {
                _log.Warning($"[SimpleGatherService] Teleport done, but no GatheringPoint object visible in territory {_targetTerritoryId} for item {_targetItemId}");
                State = GatherState.Failed;
                return;
            }
            // Teleport landed — but the node likely spawns outside ObjectTable's ~50m radius
            // from the aetheryte. Job swap → mount + fly to the known Lumina coord first, then scan.
            BeginJobSwapOrMount();
            return;
        }

        if (DateTime.UtcNow > _teleportDeadline)
        {
            _log.Warning($"[SimpleGatherService] Teleport timed out waiting for territory {_targetTerritoryId} (still in {_clientState.TerritoryType})");
            State = GatherState.Failed;
        }
    }

    private void TickSwappingJob()
    {
        if (_objectTable.LocalPlayer?.ClassJob.RowId == _wantedClassJobId)
        {
            DecideMountOrWalk();
            return;
        }
        if (DateTime.UtcNow > _stateDeadline)
        {
            _log.Warning($"[SimpleGatherService] Gearset swap to ClassJob {_wantedClassJobId} timed out — check the configured gearset name");
            State = GatherState.Failed;
        }
    }

    private void TickMounting()
    {
        if (_condition[ConditionFlag.Mounted] || _condition[ConditionFlag.InFlight])
        {
            BeginFlyingToArea();
            return;
        }

        if (DateTime.UtcNow > _stateDeadline)
        {
            // Mount didn't take (no flying mount unlocked, on-cooldown, in combat zone).
            // ponytail: fall through to ground nav — vnavmesh still tries, worst case: fails cleanly.
            _log.Information("[SimpleGatherService] Mount did not engage — proceeding on foot");
            BeginFlyingToArea();
        }
    }

    private void TickFlyingToArea()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null)
            return;

        var dist = Vector3.Distance(player.Position, _targetWorld);
        var arrived = dist <= FlyArriveRange || !_nav.IsRunning;
        if (!arrived)
        {
            if (DateTime.UtcNow > _stateDeadline)
            {
                _log.Warning($"[SimpleGatherService] Fly-to-area timed out (dist {dist:F1})");
                _nav.Stop();
                State = GatherState.Failed;
            }
            return;
        }

        _nav.Stop();
        var node = FindNearestNodeObject();
        if (node == null)
        {
            _log.Warning($"[SimpleGatherService] Reached target coord ({_targetWorld.X:F1},{_targetWorld.Z:F1}) but no GatheringPoint visible in ObjectTable");
            State = GatherState.Failed;
            return;
        }

        _targetNode = node;
        State = GatherState.MovingToNode;
        _nav.PathfindAndMoveTo(node.Position);
    }

    private void TickMoving()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null || _targetNode == null)
        {
            State = GatherState.Failed;
            return;
        }

        var dist = Vector3.Distance(player.Position, _targetNode.Position);
        if (dist <= InteractRange)
        {
            _nav.Stop();
            // Dismount before interact — gathering fails while mounted.
            if (_condition[ConditionFlag.Mounted] || _condition[ConditionFlag.InFlight])
            {
                ActionManager.Instance()->UseAction(ActionType.Mount, 0); // 0 = dismount (per GBR)
                return; // wait a frame for dismount to complete
            }
            var target = (GameObject*)_targetNode.Address;
            TargetSystem.Instance()->OpenObjectInteraction(target);
            State = GatherState.Interacting;
        }
        else if (!_nav.IsRunning)
        {
            // vnavmesh stopped moving before we arrived (blocked/unreachable) — fail rather than guess.
            _log.Warning("[SimpleGatherService] vnavmesh stopped before reaching the node");
            State = GatherState.Failed;
        }
    }

    private void TickInteracting()
    {
        if (_condition[ConditionFlag.Gathering])
            State = GatherState.WaitingForGatherWindow;
    }

    private void TickWaitingForGatherWindow()
    {
        var addon = GatheringAddonReader.GetAddon(_gameGui);
        if (addon == null)
            return; // still opening

        var slot = GatheringAddonReader.FindItemSlot(addon, _targetItemId);
        if (slot < 0)
        {
            _log.Warning($"[SimpleGatherService] Item {_targetItemId} not offered by this node");
            State = GatherState.Failed;
            return;
        }

        GatheringAddonReader.ClickSlot(addon, slot, _log);
        State = GatherState.Gathering;
    }

    private void TickGathering()
    {
        if (!_condition[ConditionFlag.Gathering])
            State = GatherState.Done; // node interaction ended — no list bookkeeping, caller decides next step
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
    }
}
