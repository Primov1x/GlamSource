namespace GlamSource.Core;

[Serializable]
public class Configuration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;
    public bool ShowCraftingSavings { get; set; } = false;

    // Auto-Gather settings (GBR-style)
    public uint AutoGatherMountId { get; set; } = 0;      // 0 = Mount Roulette (GeneralAction 24)
    public float MountUpDistance { get; set; } = 15f;      // skip mount if node closer than this
    public string MinerSetName { get; set; } = "";
    public string BotanistSetName { get; set; } = "";
    public string FisherSetName { get; set; } = "";
}
