namespace GlamSource.Core;

[Serializable]
public class Configuration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;
    public bool ShowCraftingSavings { get; set; } = false;
}
