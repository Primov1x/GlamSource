using GlamSource.Core;

namespace Tests;

public class ConfigurationTests
{
    [Fact]
    public void Default_Version_ShouldBeZero()
    {
        var config = new Configuration();
        Assert.Equal(0, config.Version);
    }

    [Fact]
    public void Default_IsConfigWindowMovable_ShouldBeTrue()
    {
        var config = new Configuration();
        Assert.True(config.IsConfigWindowMovable);
    }

    [Fact]
    public void Default_SomePropertyToBeSavedAndWithADefault_ShouldBeTrue()
    {
        var config = new Configuration();
        Assert.True(config.SomePropertyToBeSavedAndWithADefault);
    }

    [Fact]
    public void Can_Set_And_Read_Properties()
    {
        var config = new Configuration();
        config.Version = 5;
        config.IsConfigWindowMovable = false;
        config.SomePropertyToBeSavedAndWithADefault = false;

        Assert.Equal(5, config.Version);
        Assert.False(config.IsConfigWindowMovable);
        Assert.False(config.SomePropertyToBeSavedAndWithADefault);
    }

    [Fact]
    public void Configuration_IsSerializable()
    {
        var config = new Configuration { Version = 3, IsConfigWindowMovable = false };
        var serialized = System.Text.Json.JsonSerializer.Serialize(config);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Configuration>(serialized)!;

        Assert.Equal(3, deserialized.Version);
        Assert.False(deserialized.IsConfigWindowMovable);
    }
}
