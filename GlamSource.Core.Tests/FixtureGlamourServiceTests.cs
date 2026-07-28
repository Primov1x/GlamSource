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
        Assert.Equal(12, slots.Count);
        Assert.Equal(EquipmentSlotType.MainHand, slots[0].Slot);
        Assert.Equal("Agonizing Flame of Fury", slots[0].ActualItemName);
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

        var mainHand = glamoured.First(s => s.Slot == EquipmentSlotType.MainHand);
        Assert.Equal((uint?)35678, mainHand.GlamourItemId);
        Assert.Equal("YoRHa Type-No.2 Type S (Body)", mainHand.GlamourItemName);
        Assert.Equal((uint)41234, mainHand.ActualItemId);
        Assert.Equal("Agonizing Flame of Fury", mainHand.ActualItemName);
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
    public void GetTargetEquipment_CachesResult()
    {
        var service = new FixtureGlamourService(TestFixturePath("target-example.json"));
        var first = service.GetTargetEquipment();
        var second = service.GetTargetEquipment();

        Assert.Same(first, second);
    }
}
