using GlamSource.Core;

namespace Tests;

public class ItemSearchIndexTests
{
    private static ItemSearchRow Row(uint id, string name, int ilvl, EquipmentSlotType slot, params string[] jobs)
        => new(id, name, 0, ilvl, new[] { slot }, jobs.ToHashSet());

    private static readonly ItemSearchRow[] Rows =
    {
        Row(1, "Ironworks Helm of Fending", 120, EquipmentSlotType.Head, "PLD", "WAR"),
        Row(2, "Ironworks Armor of Fending", 120, EquipmentSlotType.Body, "PLD", "WAR"),
        Row(3, "Ironworks Cap of Healing", 120, EquipmentSlotType.Head, "WHM"),
        Row(4, "Cotton Hat", 10, EquipmentSlotType.Head, "PLD", "WHM"),
    };

    [Fact]
    public void Short_query_without_filter_returns_nothing()
        => Assert.Empty(ItemSearchIndex.Filter(Rows, "ir", null, null, null, null, 10));

    [Fact]
    public void Slot_and_job_filter_browse_without_query_sorted_by_ilvl_desc()
    {
        var hits = ItemSearchIndex.Filter(Rows, "", "Head", "PLD", null, null, 10);
        Assert.Equal(new uint[] { 1, 4 }, hits.Select(h => h.Id));
    }

    [Fact]
    public void Query_plus_ilvl_range()
    {
        var hits = ItemSearchIndex.Filter(Rows, "ironworks", null, null, 100, 130, 10);
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(120, h.ItemLevel));
    }

    [Fact]
    public void Unknown_slot_value_is_ignored_not_an_error()
        => Assert.Equal(3, ItemSearchIndex.Filter(Rows, "ironworks", "Bogus", null, null, null, 10).Count);
}
