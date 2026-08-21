using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace GlamSource.Windows;

/// <summary>
/// IPC bridge to the vnavmesh community plugin (soft dependency — may not be installed/active).
/// Mirrors the try/catch + IsAvailable pattern from GatherBuddyRebornIpc.
/// ponytail: only the endpoints needed for "go to coordinate X" are wrapped here
/// (Nav.IsReady, SimpleMove.PathfindAndMoveTo, Path.Stop, Path.IsRunning) — not GBR's
/// full vnavmesh surface (no mesh build/reload, no raw Nav.Pathfind waypoint list, no
/// fishing-boat or Diadem special cases). Wiring into movement logic is Phase 4.
/// </summary>
public class VNavmeshIpc
{
    private readonly ICallGateSubscriber<bool>? _isReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool>? _pathfindAndMoveTo;
    private readonly ICallGateSubscriber<object>? _stop;
    private readonly ICallGateSubscriber<bool>? _isRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?>? _pointOnFloor;

    public VNavmeshIpc(IDalamudPluginInterface pi)
    {
        try
        {
            _isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
            _stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
            _isRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            _pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        }
        catch
        {
            // ponytail: mock/DalaMock or vnavmesh not installed — survive silently
            _isReady = null;
            _pathfindAndMoveTo = null;
            _stop = null;
            _isRunning = null;
            _pointOnFloor = null;
        }
    }

    /// <summary>
    /// Snap a rough coordinate to the actual mesh floor. Used to resolve a valid Y
    /// after we compute XZ from Lumina map data (which has no altitude).
    /// </summary>
    public Vector3? PointOnFloor(Vector3 near, bool allowUnlandable = true, float halfExtentXZ = 5f)
    {
        if (_pointOnFloor == null) return null;
        try { return _pointOnFloor.HasFunction ? _pointOnFloor.InvokeFunc(near, allowUnlandable, halfExtentXZ) : null; }
        catch { return null; }
    }

    /// <summary>Whether vnavmesh IPC is available and its navmesh is loaded/ready.</summary>
    public bool IsReady
    {
        get
        {
            if (_isReady == null) return false;
            try { return _isReady.HasFunction && _isReady.InvokeFunc(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Requests pathfinding + movement to the given world-space position.
    /// Returns false if vnavmesh is unavailable/not ready or the call fails.
    /// </summary>
    public bool PathfindAndMoveTo(Vector3 destination, bool fly = false)
    {
        if (_pathfindAndMoveTo == null) return false;
        try { return _pathfindAndMoveTo.HasFunction && _pathfindAndMoveTo.InvokeFunc(destination, fly); }
        catch { return false; }
    }

    /// <summary>Cancels any in-progress vnavmesh movement.</summary>
    public void Stop()
    {
        if (_stop == null) return;
        try { if (_stop.HasFunction) _stop.InvokeAction(); }
        catch { /* noop */ }
    }

    /// <summary>Whether vnavmesh is currently moving the player along a path.</summary>
    public bool IsRunning
    {
        get
        {
            if (_isRunning == null) return false;
            try { return _isRunning.HasFunction && _isRunning.InvokeFunc(); }
            catch { return false; }
        }
    }
}
