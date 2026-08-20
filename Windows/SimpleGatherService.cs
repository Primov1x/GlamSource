using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GlamSource.Core;

namespace GlamSource.Windows;

public enum GatherState
{
    Idle,
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
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;
    private readonly IFramework _framework;

    public GatherState State { get; private set; } = GatherState.Idle;
    public bool IsDone => State is GatherState.Done or GatherState.Failed;

    private uint _targetItemId;
    private IGameObject? _targetNode;

    public SimpleGatherService(
        IGatheringLocationService locations,
        VNavmeshIpc nav,
        IObjectTable objectTable,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog log,
        IFramework framework)
    {
        _locations = locations;
        _nav = nav;
        _objectTable = objectTable;
        _clientState = clientState;
        _condition = condition;
        _gameGui = gameGui;
        _log = log;
        _framework = framework;

        _framework.Update += OnUpdate;
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
        if (State is GatherState.MovingToNode or GatherState.Interacting or GatherState.WaitingForGatherWindow or GatherState.Gathering)
            return StartResult.Fail("Already gathering — stop current run first");

        var locations = _locations.GetLocations(itemId);
        if (locations.Count == 0)
        {
            _log.Warning($"[SimpleGatherService] No gathering location known for item {itemId}");
            State = GatherState.Failed;
            return StartResult.Fail("Item has no known gathering location (not gatherable, or missing from data)");
        }

        var currentTerritory = _clientState.TerritoryType;
        var wanted = string.Join(", ", locations.Select(l => l.TerritoryName ?? l.TerritoryId.ToString()).Distinct());
        if (!locations.Any(l => l.TerritoryId == currentTerritory))
        {
            _log.Warning($"[SimpleGatherService] Player in territory {currentTerritory}, item {itemId} needs one of: {wanted}");
            State = GatherState.Failed;
            return StartResult.Fail($"Wrong zone — teleport to: {wanted}");
        }

        var node = FindNearestNodeObject();
        if (node == null)
        {
            _log.Warning($"[SimpleGatherService] In correct territory {currentTerritory} but no GatheringPoint object in ObjectTable for item {itemId}");
            State = GatherState.Failed;
            return StartResult.Fail("In correct zone but no node visible — move closer to a node area");
        }

        _targetItemId = itemId;
        _targetNode = node;
        State = GatherState.MovingToNode;
        _nav.PathfindAndMoveTo(node.Position);
        return StartResult.Ok();
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
