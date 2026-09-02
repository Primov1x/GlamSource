using Lumina.Excel.Sheets;

namespace GlamSource.Core;

/// One row of the web search index: everything the slot / job / iLvl filters need, read once.
public sealed record ItemSearchRow(uint Id, string Name, uint IconId, int ItemLevel, EquipmentSlotType[] Slots, IReadOnlySet<string> Jobs);

public sealed record ItemSearchResult(uint Id, string Name, uint IconId, int ItemLevel);

/// Web UI item search with slot / job / iLvl filters. The name-only search used to be a plain
/// substring scan over the Item sheet capped at 20 — a filter on top of that cap would have been
/// useless, so this scans a prebuilt index instead (built lazily on the first filtered request,
/// ~40k rows, a few MB).
public sealed class ItemSearchIndex
{
    private readonly Lumina.GameData _gameData;
    private List<ItemSearchRow>? _rows;

    public ItemSearchIndex(Lumina.GameData gameData) => _gameData = gameData;

    /// (abbreviation, localized name) for the job dropdown, in the game's own UI order.
    public IReadOnlyList<(string abbr, string name)> Jobs()
    {
        var sheet = _gameData.GetExcelSheet<ClassJob>();
        if (sheet == null) return Array.Empty<(string, string)>();
        return sheet
            .Where(j => j.RowId > 0 && !j.Abbreviation.IsEmpty)
            .OrderBy(j => j.UIPriority).ThenBy(j => j.RowId)
            .Select(j => (j.Abbreviation.ToString(), j.Name.ToString()))
            .ToList();
    }

    public IReadOnlyList<ItemSearchResult> Search(string query, string? slot, string? job, int? ilvlMin, int? ilvlMax, int take)
        => Filter(_rows ??= Build(), query, slot, job, ilvlMin, ilvlMax, take);

    /// Pure filter (unit-tested). No filter + query under 3 chars = nothing, same rule as before.
    /// A filter alone (empty query) browses — that's the whole point of a slot filter. Filtered
    /// results sort by iLvl descending; a pure name search keeps sheet order like it always did.
    public static IReadOnlyList<ItemSearchResult> Filter(IEnumerable<ItemSearchRow> rows, string query, string? slot, string? job, int? ilvlMin, int? ilvlMax, int take)
    {
        var q = query.Trim();
        EquipmentSlotType? slotType = Enum.TryParse<EquipmentSlotType>(slot, out var st) ? st : null;
        var hasFilter = slotType != null || !string.IsNullOrEmpty(job) || ilvlMin != null || ilvlMax != null;
        if (q.Length < 3 && !hasFilter) return Array.Empty<ItemSearchResult>();

        var hits = rows.Where(r =>
            (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            && (slotType == null || r.Slots.Contains(slotType.Value))
            && (string.IsNullOrEmpty(job) || r.Jobs.Contains(job))
            && (ilvlMin == null || r.ItemLevel >= ilvlMin)
            && (ilvlMax == null || r.ItemLevel <= ilvlMax));
        if (hasFilter) hits = hits.OrderByDescending(r => r.ItemLevel).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return hits.Take(take).Select(r => new ItemSearchResult(r.Id, r.Name, r.IconId, r.ItemLevel)).ToList();
    }

    private List<ItemSearchRow> Build()
    {
        // ClassJobCategory is one bool column per job, named by abbreviation (PLD, WAR, ...) —
        // verified against game data 2026-09-02 (category 59 "GLA MRD PLD WAR DRK GNB" -> exactly
        // those six props true). Reflection once per category (~200 rows), not per item.
        var jobProps = typeof(ClassJobCategory).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && !p.Name.StartsWith("Unknown", StringComparison.Ordinal))
            .ToArray();
        var jobsByCategory = new Dictionary<uint, IReadOnlySet<string>>();
        foreach (var cat in _gameData.GetExcelSheet<ClassJobCategory>() ?? Enumerable.Empty<ClassJobCategory>())
            jobsByCategory[cat.RowId] = jobProps.Where(p => p.GetValue(cat) is true).Select(p => p.Name).ToHashSet();

        var none = new HashSet<string>();
        var list = new List<ItemSearchRow>();
        foreach (var item in _gameData.GetExcelSheet<Item>() ?? Enumerable.Empty<Item>())
        {
            var name = item.Name.ToString();
            if (name.Length == 0) continue;
            var slots = new List<EquipmentSlotType>(2);
            var esc = item.EquipSlotCategory;
            if (esc.IsValid && esc.RowId > 0)
            {
                var c = esc.Value;
                if (c.MainHand > 0) slots.Add(EquipmentSlotType.MainHand);
                if (c.OffHand > 0) slots.Add(EquipmentSlotType.OffHand);
                if (c.Head > 0) slots.Add(EquipmentSlotType.Head);
                if (c.Body > 0) slots.Add(EquipmentSlotType.Body);
                if (c.Gloves > 0) slots.Add(EquipmentSlotType.Hands);
                if (c.Legs > 0) slots.Add(EquipmentSlotType.Legs);
                if (c.Feet > 0) slots.Add(EquipmentSlotType.Feet);
                if (c.Ears > 0) slots.Add(EquipmentSlotType.Earrings);
                if (c.Neck > 0) slots.Add(EquipmentSlotType.Necklace);
                if (c.Wrists > 0) slots.Add(EquipmentSlotType.Bracelets);
                if (c.FingerR > 0) slots.Add(EquipmentSlotType.RingRight);
                if (c.FingerL > 0) slots.Add(EquipmentSlotType.RingLeft);
            }
            var jobs = jobsByCategory.TryGetValue(item.ClassJobCategory.RowId, out var j) ? j : none;
            list.Add(new ItemSearchRow(item.RowId, name, item.Icon, (int)item.LevelItem.RowId, slots.ToArray(), jobs));
        }
        return list;
    }
}
