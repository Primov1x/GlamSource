using GlamSource.Core;

namespace Tests;

public class GlamourServiceTests
{
    [Fact]
    public void GetLocationName_ReturnsNull_ForUnknownTerritory()
    {
        var service = new GlamourService();
        Assert.Null(service.GetLocationName(9999));
    }

    [Fact]
    public void GetLocationName_ReturnsRegisteredName()
    {
        var service = new GlamourService();
        service.RegisterLocationName(1, "Limsa Lominsa");

        Assert.Equal("Limsa Lominsa", service.GetLocationName(1));
    }

    [Fact]
    public void GetItemSource_ReturnsNull_ForUnknownItem()
    {
        var service = new GlamourService();
        Assert.Null(service.GetItemSource(99999));
    }

    [Fact]
    public void GetItemSource_ReturnsRegisteredSource()
    {
        var service = new GlamourService();
        service.RegisterItemSource(100, 200, "Soleil");

        Assert.Equal("Soleil", service.GetItemSource(100));
    }

    [Fact]
    public void HasItemSource_ReturnsTrue_AfterRegistration()
    {
        var service = new GlamourService();
        service.RegisterItemSource(100, 200, "Soleil");

        Assert.True(service.HasItemSource(100));
        Assert.False(service.HasItemSource(101));
    }

    [Fact]
    public void GetMountSource_ReturnsNull_ForUnknownMount()
    {
        var service = new GlamourService();
        Assert.Null(service.GetMountSource(999));
    }

    [Fact]
    public void GetMountSource_ReturnsRegisteredSource()
    {
        var service = new GlamourService();
        service.RegisterMountSource(1, 2, "Golden Saucer");

        Assert.Equal("Golden Saucer", service.GetMountSource(1));
    }

    [Fact]
    public void HasMountSource_ReturnsTrue_AfterRegistration()
    {
        var service = new GlamourService();
        service.RegisterMountSource(1, 2, "Golden Saucer");

        Assert.True(service.HasMountSource(1));
        Assert.False(service.HasMountSource(2));
    }

    [Fact]
    public void MultipleItems_Can_BeRegistered()
    {
        var service = new GlamourService();
        service.RegisterItemSource(1, 10, "Source A");
        service.RegisterItemSource(2, 20, "Source B");
        service.RegisterItemSource(3, 30, "Source C");

        Assert.Equal("Source A", service.GetItemSource(1));
        Assert.Equal("Source B", service.GetItemSource(2));
        Assert.Equal("Source C", service.GetItemSource(3));
    }

    [Fact]
    public void OverwritingSource_UsesNewValue()
    {
        var service = new GlamourService();
        service.RegisterItemSource(1, 10, "Old Source");
        service.RegisterItemSource(1, 20, "New Source");

        Assert.Equal("New Source", service.GetItemSource(1));
    }
}
