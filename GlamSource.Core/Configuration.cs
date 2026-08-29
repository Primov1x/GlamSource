namespace GlamSource.Core;

[Serializable]
public class Configuration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool ShowCraftingSavings { get; set; } = false;

    // Auto-Gather settings (GBR-style)
    public uint AutoGatherMountId { get; set; } = 0;      // 0 = Mount Roulette (GeneralAction 24)
    public float MountUpDistance { get; set; } = 15f;      // skip mount if node closer than this
    public string MinerSetName { get; set; } = "";
    public string BotanistSetName { get; set; } = "";
    public string FisherSetName { get; set; } = "";

    // Shell window: last active tab index (0=Lookup, 1=Character, 2=Settings).

    // ponytail: dev-only read-only state inspector (localhost HTTP), off by default.
    public bool DebugApiEnabled { get; set; } = false;

    // ponytail: optional HTML alternative UI (localhost:23424), off by default.
    public bool WebUiEnabled { get; set; } = false;

    // ponytail: auto-drive a Browsingway overlay named "GlamSource" (show on open, hide on close).
    public bool WebUiAutoOverlay { get; set; } = true;

    // ponytail: experimental — live 3D preview streamed to the web UI via GPU texture readback
    // (D3D11 CopyResource+Map). Opt-in, off by default: raw COM interop on a live game texture,
    // riskier than the rest of GlamSource. See Services/PreviewRenderer.cs TryCapturePixels.
    public bool WebUiLive3DPreview { get; set; } = false;
}
