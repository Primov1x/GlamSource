using System.Text.Json;

namespace GlamSource.Core.Tests;

public class FixtureGlamourServiceTests
{
    private string TestFixturePath(string fileName)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
        return Path.Combine(dir, fileName);
    }

    [Fact]
    public void Load_ValidFixture_ReturnsSlots()
    {
        var service = new FixtureGlamourService(TestFixturePath("target-example.json"));
        var slots = service.GetTargetEquipment();

        Assert.NotEmpty(slots);
        Assert.Equal(14, slots.Count);
        Assert.Equal(EquipmentSlotType.MainHand, slots[0].Slot);
        Assert.Equal("Iron Ingot", slots[0].ActualItemName);
    }

    [Fact]
    public void Load_EmptySlots_HandledCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "empty-slots.json");
        File.WriteAllText(tempFile, "[]");

        try
        {
            var service = new FixtureGlamourService(tempFile);
            var slots = service.GetTargetEquipment();

            Assert.Empty(slots);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_GlamouredSlots_ExposesBothIds()
    {
        var service = new FixtureGlamourService(TestFixturePath("target-example.json"));
        var slots = service.GetTargetEquipment();

        var glamoured = slots.Where(s => s.IsGlamoured).ToList();
        Assert.NotEmpty(glamoured);

        var body = glamoured.First(s => s.Slot == EquipmentSlotType.Body);
        Assert.Equal((uint?)35637, body.GlamourItemId);
        Assert.Equal("Diamond Zeta Sword", body.GlamourItemName);
        Assert.Equal((uint)3278, body.ActualItemId);
        Assert.Equal("Hempen Shirt", body.ActualItemName);
    }

    [Fact]
    public void Load_MalformedJson_Throws()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "malformed.json");
        File.WriteAllText(tempFile, "{not valid json!!!");

        try
        {
            var service = new FixtureGlamourService(tempFile);
            Assert.Throws<JsonException>(() => service.GetTargetEquipment());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var service = new FixtureGlamourService(Path.Combine(Path.GetTempPath(), "nonexistent.json"));
        Assert.Throws<FileNotFoundException>(() => service.GetTargetEquipment());
    }

    [Fact]
    public void Load_WithSourceService_ExposesSources()
    {
        var mockService = new MockItemSourceService();
        var service = new FixtureGlamourService(TestFixturePath("target-example.json"), mockService);
        var slots = service.GetTargetEquipment();

        var ironIngot = slots.First(s => s.Slot == EquipmentSlotType.MainHand);
        Assert.NotNull(ironIngot.ActualItemSources);
        Assert.NotEmpty(ironIngot.ActualItemSources!);
        Assert.Equal(ItemSourceType.Crafted, ironIngot.ActualItemSources![0].Type);

        var hempenShirt = slots.First(s => s.Slot == EquipmentSlotType.Body);
        Assert.NotNull(hempenShirt.ActualItemSources);
        Assert.NotEmpty(hempenShirt.ActualItemSources!);
        Assert.Equal(ItemSourceType.Vendor, hempenShirt.ActualItemSources![0].Type);

        var glamourSword = slots.First(s => s.Slot == EquipmentSlotType.Body);
        Assert.NotNull(glamourSword.GlamourItemSources);
        Assert.NotEmpty(glamourSword.GlamourItemSources!);

        var coffer = slots.First(s => s.Slot == EquipmentSlotType.Ear);
        Assert.NotNull(coffer.ActualItemSources);
        Assert.NotEmpty(coffer.ActualItemSources!);
        Assert.Equal(ItemSourceType.Coffer, coffer.ActualItemSources![0].Type);

        var dungeonChest = slots.First(s => s.Slot == EquipmentSlotType.Waist);
        Assert.NotNull(dungeonChest.ActualItemSources);
        Assert.NotEmpty(dungeonChest.ActualItemSources!);
        Assert.Equal(ItemSourceType.Coffer, dungeonChest.ActualItemSources![0].Type);
    }

    [Fact]
    public void GetSources_Fate_ReturnsFateSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38904);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Fate, sources[0].Type);
        Assert.Contains("Lost City of Amdapor", sources[0].Description);
    }

    [Fact]
    public void GetSources_Mob_ReturnsMobSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38905);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Mob, sources[0].Type);
        Assert.Contains("Bomb", sources[0].Description);
    }

    [Fact]
    public void GetSources_HouseVendor_ReturnsVendorSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38906);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Vendor, sources[0].Type);
        Assert.Contains("Sastasha", sources[0].Description);
    }

    [Fact]
    public void GetSources_Coffer_ReturnsCofferSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38907);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Coffer, sources[0].Type);
        Assert.Contains("Loose Fit Attire Coffer", sources[0].Description);
    }

    [Fact]
    public void GetSources_DungeonBossChest_ReturnsCofferSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38908);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Coffer, sources[0].Type);
        Assert.Contains("The Stone Vigil (Savage)", sources[0].Description);
    }

    [Fact]
    public void GetSources_DungeonDrop_ReturnsCofferSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38909);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.Coffer, sources[0].Type);
        Assert.Contains("The Stone Vigil", sources[0].Description);
    }

    [Fact]
    public void GetSources_PvP_ReturnsPvpSource()
    {
        var mock = new MockItemSourceService();
        var sources = mock.GetSources(38901);
        Assert.Single(sources);
        Assert.Equal(ItemSourceType.PvP, sources[0].Type);
        Assert.Contains("PvP Reward", sources[0].Description);
    }

    private sealed class MockItemSourceService : IItemSourceService
    {
        // ponytail: one source per fixture item so the UI can test rendering all types
        private static readonly Dictionary<uint, IReadOnlyList<ItemSource>> _map = new()
        {
            [3278]  = new[] { new ItemSource(ItemSourceType.Vendor, "Vendor") }.AsReadOnly(),
            [5057]  = new[] { new ItemSource(ItemSourceType.Crafted, "Crafted") }.AsReadOnly(),
            [4554]  = new[] { new ItemSource(ItemSourceType.Vendor, "Vendor") }.AsReadOnly(),
            [33422] = new[] { new ItemSource(ItemSourceType.Dungeon, "Duty Drop") }.AsReadOnly(),
            [35637] = new[] { new ItemSource(ItemSourceType.Raid, "Raid Drop") }.AsReadOnly(),
            [2]     = new[] { new ItemSource(ItemSourceType.Vendor, "Vendor") }.AsReadOnly(),
            [33155] = new[] { new ItemSource(ItemSourceType.Trial, "Trial Reward") }.AsReadOnly(),
            [41229] = new[] { new ItemSource(ItemSourceType.Achievement, "Achievement") }.AsReadOnly(),
            [38901] = new[] { new ItemSource(ItemSourceType.PvP, "PvP Reward") }.AsReadOnly(),
            [38902] = new[] { new ItemSource(ItemSourceType.TreasureHunt, "Treasure Hunt") }.AsReadOnly(),
            [38903] = new[] { new ItemSource(ItemSourceType.Shop, "Shop") }.AsReadOnly(),
            [38904] = new[] { new ItemSource(ItemSourceType.Fate, "Fate Drop: The Lost City of Amdapor") }.AsReadOnly(),
            [38905] = new[] { new ItemSource(ItemSourceType.Mob, "Mob Drop: Bomb") }.AsReadOnly(),
            [38906] = new[] { new ItemSource(ItemSourceType.Vendor, "House Vendor: Sastasha") }.AsReadOnly(),
            [38907] = new[] { new ItemSource(ItemSourceType.Coffer, "Coffer: Loose Fit Attire Coffer") }.AsReadOnly(),
            [38908] = new[] { new ItemSource(ItemSourceType.Coffer, "Dungeon Boss Chest: The Stone Vigil (Savage)") }.AsReadOnly(),
            [38909] = new[] { new ItemSource(ItemSourceType.Coffer, "Dungeon Drop: The Stone Vigil") }.AsReadOnly(),
        };

        public IReadOnlyList<ItemSource> GetSources(uint itemId)
        {
            return _map.GetValueOrDefault(itemId) ?? Array.Empty<ItemSource>();
        }
    }

    [Fact]
    public void GetTargetEquipment_CachesResult()
    {
        var service = new FixtureGlamourService(TestFixturePath("target-example.json"));
        var first = service.GetTargetEquipment();
        var second = service.GetTargetEquipment();

        Assert.Same(first, second);
    }
}
