using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using GlamSource.Core;

namespace GlamSource;

[Serializable]
public sealed class RecentTarget
{
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public ulong ContentId { get; set; }
    public List<uint> ItemIds { get; set; } = new();
    // ponytail: parallel to ItemIds; empty list for old configs = stains default to 0.
    public List<byte> Stain0s { get; set; } = new();
    public List<byte> Stain1s { get; set; } = new();
    public DateTime LastSeen { get; set; }
}

[Serializable]
public class Configuration : Core.Configuration, IPluginConfiguration
{
    public const int MaxRecentTargets = 10;

    public List<RecentTarget> RecentTargets { get; set; } = new();

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    public void PushRecent(string name, string world, ulong contentId, IEnumerable<uint> itemIds)
        => PushRecent(name, world, contentId, itemIds, null, null);

    public void PushRecent(string name, string world, ulong contentId,
                           IEnumerable<uint> itemIds,
                           IEnumerable<byte>? stain0s,
                           IEnumerable<byte>? stain1s)
    {
        RecentTargets.RemoveAll(r => r.Name == name && r.World == world);
        RecentTargets.Insert(0, new RecentTarget
        {
            Name = name,
            World = world,
            ContentId = contentId,
            ItemIds = itemIds.ToList(),
            Stain0s = stain0s?.ToList() ?? new List<byte>(),
            Stain1s = stain1s?.ToList() ?? new List<byte>(),
            LastSeen = DateTime.UtcNow,
        });
        while (RecentTargets.Count > MaxRecentTargets)
            RecentTargets.RemoveAt(RecentTargets.Count - 1);
        Save();
    }
}
