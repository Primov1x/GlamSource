using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace GlamSource.Services;

/// <summary>
/// IPC bridge to the Teleporter plugin. Same soft-dep pattern as <see cref="VNavmeshIpc"/>.
/// ponytail: only Teleport(aetheryteId, 0) + availability probe — no per-plugin caching,
/// no shortcuts, no cost checks. Enough for "get me to the right zone".
/// </summary>
public class TeleporterIpc
{
    private readonly ICallGateSubscriber<uint, byte, bool>? _teleport;
    private readonly ICallGateSubscriber<bool>? _chatMessage;

    public TeleporterIpc(IDalamudPluginInterface pi)
    {
        try
        {
            _teleport = pi.GetIpcSubscriber<uint, byte, bool>("Teleport");
            _chatMessage = pi.GetIpcSubscriber<bool>("Teleport.ChatMessage");
        }
        catch
        {
            _teleport = null;
            _chatMessage = null;
        }
    }

    /// <summary>True if Teleporter plugin exposes its IPC (i.e. installed &amp; loaded).</summary>
    public bool IsAvailable
    {
        get
        {
            if (_chatMessage == null) return false;
            try { return _chatMessage.HasFunction; }
            catch { return false; }
        }
    }

    /// <summary>Casts Teleport to the given aetheryte. Returns false if IPC missing/failed.</summary>
    public bool Teleport(uint aetheryteId)
    {
        if (_teleport == null) return false;
        try { return _teleport.HasFunction && _teleport.InvokeFunc(aetheryteId, 0); }
        catch { return false; }
    }
}
