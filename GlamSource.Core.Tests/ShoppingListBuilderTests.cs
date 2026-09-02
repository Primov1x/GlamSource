using GlamSource.Core;

namespace Tests;

public class ShoppingListBuilderTests
{
    private static ItemSourceDetail Vendor(string npc, uint gil) => new(ItemSourceType.Vendor, "Shop: " + npc, npc, "Limsa", 9f, 11f, 129, 11,
        new[] { new CostEntry(0, "Gil", gil, 0) }, null, null, null, null, null, null, null, null);

    private static ItemSourceDetail Craft(params CostEntry[] mats) => new(ItemSourceType.Crafted, "Crafted by Weaver", null, null, null, null, null, null,
        null, mats, null, null, null, null, null, null, null);

    private static ItemSourceDetail Generic() => new(ItemSourceType.Other, "No known current source.", null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null);

    private static readonly Dictionary<uint, ItemDetail> Details = new()
    {
        [1] = new ItemDetail(1, "Hat", 1, false, 0, new[] { Generic(), Vendor("Npc A", 100) }, null, null),
        [2] = new ItemDetail(2, "Coat", 1, false, 0, new[] { Vendor("Npc A", 250) }, null, null),
        [3] = new ItemDetail(3, "Boots", 1, false, 0, new[] { Craft(new CostEntry(7, "Cotton", 2, 0)) }, null, null),
        [4] = new ItemDetail(4, "Ring", 1, false, 0, new[] { Generic() }, null, null),
    };

    private static readonly (uint, string, uint)[] Outfit = { (1, "Hat", 0), (2, "Coat", 0), (3, "Boots", 0), (4, "Ring", 0), (0, "empty slot", 0) };

    [Fact]
    public void Same_npc_becomes_one_stop_with_summed_gil()
    {
        var list = ShoppingListBuilder.Build(Outfit, id => Details.GetValueOrDefault(id));
        var vendor = Assert.Single(list.Lines, l => l.Kind == "Vendor");
        Assert.Equal(new uint[] { 1, 2 }, vendor.Items.Select(i => i.ItemId));
        Assert.Equal(350u, Assert.Single(vendor.Costs).Count);
        Assert.Equal("Npc A", vendor.NpcName);
    }

    [Fact]
    public void Vendor_beats_generic_and_totals_sum_across_stops()
    {
        var list = ShoppingListBuilder.Build(Outfit, id => Details.GetValueOrDefault(id));
        Assert.Equal(new[] { "Vendor", "Craft", "Other" }, list.Lines.Select(l => l.Kind));
        Assert.Equal(350u, Assert.Single(list.Totals).Count);
        Assert.Equal(2u, Assert.Single(list.Lines.Single(l => l.Kind == "Craft").Materials).Count);
    }
}
