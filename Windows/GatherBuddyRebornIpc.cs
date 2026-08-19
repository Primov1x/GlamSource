using System;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace GlamSource.Windows;

/// <summary>
/// IPC bridge to GatherBuddy Reborn.
/// </summary>
public class GatherBuddyRebornIpc
{
    private readonly IDalamudPluginInterface? _pi;
    private readonly ICallGateSubscriber<string, uint>? _identify;

    public GatherBuddyRebornIpc(IDalamudPluginInterface pi)
    {
        _pi = pi;
        try
        {
            _identify = pi.GetIpcSubscriber<string, uint>("GatherBuddyReborn.Identify");
        }
        catch
        {
            // ponytail: mock/DalaMock has no real IPC — survive silently
            _identify = null;
        }
    }

    /// <summary>
    /// Whether GatherBuddy IPC is available (plugin installed and registered).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (_identify == null) return false;
            try { return _identify.HasFunction; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Whether the GatherBuddy Reborn assembly is loaded in the current process.
    /// </summary>
    public static bool IsGbrAssemblyLoaded
    {
        get
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == "GatherBuddyReborn");
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Resolve item name → game ItemId (0 = not a known gatherable).
    /// </summary>
    public uint IdentifyItem(string name)
    {
        if (!IsAvailable) return 0;
        try { return _identify!.InvokeFunc(name); }
        catch { return 0; }
    }

    /// <summary>
    /// Whether auto-gather is currently enabled.
    /// </summary>
    public bool IsAutoGatherEnabled()
    {
        if (!IsAvailable) return false;
        try
        {
            var ipc = _pi!.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
            return ipc?.HasFunction == true ? ipc.InvokeFunc() : false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Toggle auto-gather on/off.
    /// </summary>
    public void SetAutoGatherEnabled(bool enabled)
    {
        if (!IsAvailable) return;
        try
        {
            var ipc = _pi!.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled");
            if (ipc?.HasFunction == true)
                ipc.InvokeAction(enabled);
        }
        catch { /* noop in mock */ }
    }

    /// <summary>
    /// Human-readable status of GatherBuddy Reborn's auto-gather feature.
    /// </summary>
    public string GetAutoGatherStatusText()
    {
        if (!IsAvailable) return string.Empty;
        try
        {
            var ipc = _pi!.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
            return ipc?.HasFunction == true ? ipc.InvokeFunc() : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Create or update a persistent GatherBuddy Reborn gather list via assembly reflection.
    /// List name format: "GlamSource: {itemName}" (truncated to ~50 chars).
    /// </summary>
    public bool CreatePersistentGatherList(string itemName, System.Collections.Generic.Dictionary<uint, int> materials)
    {
        // ponytail: reflection call — no compile-time dependency on GBR assembly
        var gbrAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "GatherBuddyReborn");
        if (gbrAssembly == null) return false;

        var bridgeType = gbrAssembly.GetType("GatherBuddy.Crafting.CraftingGatherBridge");
        if (bridgeType == null) return false;

        var listName = $"GlamSource: {itemName}";
        if (listName.Length > 50)
            listName = listName.Substring(0, 47) + "...";

        try
        {
            bridgeType.InvokeMember(
                "CreatePersistentGatherList",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.InvokeMethod,
                null, null,
                new object?[] { listName, materials });
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log?.Error(ex, "[GBR] CreatePersistentGatherList failed for '{ListName}'", listName);
            return false;
        }
    }
}
