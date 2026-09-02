namespace GlamSource.Core;

/// One stop of an outfit shopping list: a vendor (NPC + location, summed costs), one craft
/// (materials), one duty (Duty Finder id) or a "somewhere else" note — with the outfit items it
/// covers. Items carry Count = 1 so the existing inventory annotation ("owned / missing") applies.
public sealed record ShoppingLine(string Kind, string Title, string? NpcName, string? ZoneName, float? MapX, float? MapY,
    uint? TerritoryTypeId, uint? MapId, uint? CfcRowId,
    IReadOnlyList<CostEntry> Items, IReadOnlyList<CostEntry> Costs, IReadOnlyList<CostEntry> Materials);

public sealed record ShoppingList(IReadOnlyList<ShoppingLine> Lines, IReadOnlyList<CostEntry> Totals);

/// Outfit shopping list (prototype): picks ONE best source per item (vendor with a location >
/// craft > nameless exchange > duty > quest > gathering > everything else), merges items that share
/// a stop (same NPC = one visit with summed costs, same duty = one run), and sums the vendor costs
/// per currency across the whole outfit.
public static class ShoppingListBuilder
{
    private sealed class Stop
    {
        public string Key = "", Kind = "", Title = "";
        public ItemSourceDetail? Src;
        public readonly List<CostEntry> Items = new();
        public readonly Dictionary<uint, CostEntry> Costs = new();
        public readonly List<CostEntry> Materials = new();
    }

    public static ShoppingList Build(IEnumerable<(uint itemId, string name, uint iconId)> items, Func<uint, ItemDetail?> getDetail)
    {
        var stops = new List<Stop>();
        foreach (var (itemId, name, iconId) in items.Where(i => i.itemId != 0).DistinctBy(i => i.itemId))
        {
            var src = getDetail(itemId)?.Sources.OrderBy(Rank).FirstOrDefault();
            var (kind, key, title) = Classify(src, itemId);
            var stop = stops.FirstOrDefault(s => s.Key == key);
            if (stop == null)
                stops.Add(stop = new Stop { Key = key, Kind = kind, Title = title, Src = src });
            stop.Items.Add(new CostEntry(itemId, name, 1, iconId));
            foreach (var c in src?.Costs ?? Array.Empty<CostEntry>())
                stop.Costs[c.ItemId] = stop.Costs.TryGetValue(c.ItemId, out var prev) ? prev with { Count = prev.Count + c.Count } : c;
            foreach (var m in src?.Materials ?? Array.Empty<CostEntry>())
            {
                var idx = stop.Materials.FindIndex(x => x.ItemId == m.ItemId);
                if (idx >= 0) stop.Materials[idx] = stop.Materials[idx] with { Count = stop.Materials[idx].Count + m.Count };
                else stop.Materials.Add(m);
            }
        }

        var totals = new Dictionary<uint, CostEntry>();
        foreach (var c in stops.SelectMany(s => s.Costs.Values))
            totals[c.ItemId] = totals.TryGetValue(c.ItemId, out var prev) ? prev with { Count = prev.Count + c.Count } : c;

        static IReadOnlyList<CostEntry> Ordered(IEnumerable<CostEntry> e) => e.OrderBy(c => c.ItemId == 0 ? 0 : 1).ThenBy(c => c.Name).ToList();
        var lines = stops
            .OrderBy(s => KindOrder(s.Kind)).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ShoppingLine(s.Kind, s.Title, s.Src?.NpcName, s.Src?.ZoneName, s.Src?.MapX, s.Src?.MapY,
                s.Src?.TerritoryTypeId, s.Src?.MapId, s.Src?.CfcRowId, s.Items, Ordered(s.Costs.Values), s.Materials))
            .ToList();
        return new ShoppingList(lines, Ordered(totals.Values));
    }

    private static int Rank(ItemSourceDetail s) => s.Type switch
    {
        ItemSourceType.Vendor or ItemSourceType.Shop => s.NpcName != null ? 0 : 2,
        ItemSourceType.Crafted => 1,
        ItemSourceType.Dungeon or ItemSourceType.Trial or ItemSourceType.Raid => 3,
        ItemSourceType.Quest => 4,
        ItemSourceType.Gathering => 5,
        ItemSourceType.MogStation => 8,
        ItemSourceType.Other or ItemSourceType.Unknown => 9,
        _ => 6,
    };

    private static int KindOrder(string kind) => kind switch { "Vendor" => 0, "Craft" => 1, "Duty" => 2, _ => 3 };

    private static (string kind, string key, string title) Classify(ItemSourceDetail? s, uint itemId)
    {
        if (s == null) return ("Other", "none", "No known source");
        switch (s.Type)
        {
            case ItemSourceType.Vendor:
            case ItemSourceType.Shop:
                if (s.NpcName == null) return ("Vendor", $"v|{s.Description}", s.Description);
                return ("Vendor", $"v|{s.NpcName}|{s.ZoneName}", string.IsNullOrEmpty(s.ZoneName) ? s.NpcName : $"{s.NpcName}, {s.ZoneName}");
            case ItemSourceType.Crafted:
                return ("Craft", $"c|{itemId}", s.Description); // one craft per item, materials stay per item
            case ItemSourceType.Dungeon:
            case ItemSourceType.Trial:
            case ItemSourceType.Raid:
                return ("Duty", $"d|{s.CfcRowId ?? 0}|{s.Description}", s.CfcName ?? s.Description);
            default:
                return ("Other", $"o|{s.Description}", s.Description);
        }
    }
}
