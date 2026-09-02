namespace GlamSource.Core;

[Serializable]
public class Configuration
{
    public int Version { get; set; } = 0;

    // "en" or "de" — UI chrome only (labels/tooltips/buttons), not item/game data, which is
    // already localized for free via Dalamud's IDataManager loading Lumina sheets in the
    // client's own game language. See doku/item-source-detection.md's Localization TODO.
    public string Language { get; set; } = "en";

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

    // ponytail: experimental — live MJPEG preview streamed to the web UI via GPU texture readback
    // (D3D11 CopyResource+Map, JPEG-encoded, served as multipart/x-mixed-replace). Opt-in, off by
    // default: raw COM interop on a live game texture, riskier than the rest of GlamSource. See
    // Services/PreviewRenderer.cs PumpWebCapture and Services/WebUiService.cs StreamPreviewMjpeg.
    public bool WebUiLive3DPreview { get; set; } = false;

    // "wenn ich in examine 'item source' klicke, soll es im webgui aufgehen anstatt im imgui" —
    // opt-in since it depends on WebUiEnabled+the Browsingway overlay actually being set up.
    public bool ContextMenuOpensInWebUi { get; set; } = false;
}
