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

    // Shell window: last active tab index (0=Lookup, 1=Character, 2=Settings).
    public int SelectedTab { get; set; } = 0;

    // ponytail: dev-only read-only state inspector (localhost HTTP), off by default.
    public bool DebugApiEnabled { get; set; } = false;
}
