using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using GlamSource.Core;
using GlamSource.Services;
using GlamSource.Windows;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlamSource;

public class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;


    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ITextureProvider _textureProvider;
    private readonly ICommandManager _commandManager;
    private readonly IClientState _clientState;
    private readonly IPlayerState _playerState;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private readonly ITargetManager _targetManager;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly ICondition _condition;

    private const string CommandName = "/glamsource";

    public Configuration Configuration { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("GlamSource");
    public readonly GameDataService GameDataService;

    private readonly GlamSourceShellWindow shellWindow;
    private readonly GlamourPreviewWindow previewWindow;
    private readonly PreviewRenderer previewRenderer;
    private readonly ContextMenuService contextMenuService;
    private readonly ItemDetailWindow itemDetailWindow;
    private readonly UniversalisService? _universalisService;
    public readonly CraftingCostService CraftingCostService;
    public static IGlamourService? GlamourServiceOverride;

    public readonly IGatheringLocationService GatheringLocationService;
    public readonly VNavmeshIpc VNavmeshIpc;
    public readonly TeleporterIpc TeleporterIpc;
    public readonly SimpleGatherService GatherService;
    private readonly DebugApiService debugApiService;
    private readonly WebUiService webUiService;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        ICommandManager commandManager,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager dataManager,
        IPluginLog pluginLog,
        ITargetManager targetManager,
        IGameGui gameGui,
        IObjectTable objectTable,
        IFramework framework,
        ICondition condition,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop)
    {
        _pluginInterface = pluginInterface;
        _textureProvider = textureProvider;
        _commandManager = commandManager;
        _clientState = clientState;
        _playerState = playerState;
        _dataManager = dataManager;
        _log = pluginLog;
        _targetManager = targetManager;
        _gameGui = gameGui;
        _objectTable = objectTable;
        _framework = framework;
        _condition = condition;

        PluginInterface = _pluginInterface;
        TextureProvider = _textureProvider;
        CommandManager = _commandManager;
        ClientState = _clientState;
        PlayerState = _playerState;
        DataManager = _dataManager;
        Log = _log;
        TargetManager = _targetManager;
        GameGui = _gameGui;
        ObjectTable = _objectTable;
        Framework = _framework;
        Condition = _condition;
        SigScanner = sigScanner;
        GameInterop = gameInterop;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var sourceService = new LuminaItemSourceService(DataManager.GameData);
        var gameDataService = new GameDataService(DataManager, TargetManager, ObjectTable, GameGui, sourceService);
        GameDataService = gameDataService;

        GatheringLocationService = new GatheringLocationService(DataManager.GameData);
        VNavmeshIpc = new VNavmeshIpc(PluginInterface);
        TeleporterIpc = new TeleporterIpc(PluginInterface);
        GatherService = new SimpleGatherService(
            GatheringLocationService, VNavmeshIpc, TeleporterIpc, ObjectTable, ClientState, Condition, GameGui, Log, Framework,
            () => Configuration);


        var itemDetailService = new ItemDetailService(DataManager.GameData);
        var universalisHttpClient = new System.Net.Http.HttpClient();
        _universalisService = new UniversalisService(universalisHttpClient, "Shiva", "Light");
        var craftingCostService = new CraftingCostService(itemDetailService, _universalisService!);
        CraftingCostService = craftingCostService;
        itemDetailWindow = new ItemDetailWindow(itemDetailService, sourceService, _universalisService, textureProvider, DataManager);
        itemDetailWindow.SetPlugin(this);
        WindowSystem.AddWindow(itemDetailWindow);

        contextMenuService = new ContextMenuService(ContextMenu, _gameGui, itemId =>
        {
            itemDetailWindow.ShowItem(itemId);
        });

        shellWindow = new GlamSourceShellWindow(
            this,
            GlamourServiceOverride ?? gameDataService,
            itemDetailWindow,
            TextureProvider,
            DataManager,
            ObjectTable,
            PluginInterface,
            Framework,
            Log);
        WindowSystem.AddWindow(shellWindow);

        previewRenderer = new PreviewRenderer(Framework, Log, SigScanner, GameInterop);
        previewWindow = new GlamourPreviewWindow(previewRenderer, Framework, ClientState, ObjectTable, TargetManager, PluginInterface, Log, GlamourServiceOverride ?? gameDataService);
        WindowSystem.AddWindow(previewWindow);
        shellWindow.PreviewWindow = previewWindow;

        debugApiService = new DebugApiService(shellWindow, Log);
        debugApiService.SetEnabled(Configuration.DebugApiEnabled);
        shellWindow.OnDebugApiToggle = enabled => debugApiService.SetEnabled(enabled);

        webUiService = new WebUiService(itemDetailService, GlamourServiceOverride ?? gameDataService, shellWindow, Configuration, Framework, PluginInterface, Log);
        shellWindow.WebUiInlayStatus = () => webUiService.InlayStatus;
        webUiService.SetEnabled(Configuration.WebUiEnabled);
        shellWindow.OnWebUiToggle = enabled => webUiService.SetEnabled(enabled);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Öffnet das GlamSource Fenster"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        // ponytail: web-UI 3D preview capture — deliberately NOT tied to any window's Draw(), so
        // the web UI works with the ImGui window closed (the whole point of it). Still safe: this
        // fires every frame in-line with Present, same as WindowSystem.Draw above — the one place
        // D3D11 readback is allowed to happen. See PreviewRenderer.CaptureFrameForWeb.
        PluginInterface.UiBuilder.Draw += () => previewRenderer.CaptureFrameForWeb(Configuration.WebUiLive3DPreview);
        PluginInterface.UiBuilder.Draw += PinBrowsingwayOverlaySize;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // ponytail: passive target-scan for the Recent list. Runs even when the Character tab
        // isn't drawn; throttled to every 30 frames so it doesn't hammer the sheets.
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        // ponytail: test-harness auto-opens; real plugin stays closed until user opens it.
        if (GlamourServiceOverride != null)
            shellWindow.IsOpen = true;
    }

    public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // ponytail: temp diagnostic — logs never appeared from Dispose() calls below during
        // unload, so wrap the whole body to find where it dies/throws silently.
        _log.Info("[Plugin] DisposeAsync() entered");
        try
        {
            // ponytail: "beim Disablen vom Plugin auch Browsingway das Overlay disablen" — verified
            // via decompile (Browsingway.Settings) that "/bw overlay <name> disabled on|off|toggle"
            // is a real command, not guessed. Best-effort: Browsingway might already be gone by the
            // time we're unloading (e.g. it got disabled first), so don't let this throw and skip
            // the rest of cleanup.
            try
            {
                if (PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Browsingway" && p.IsLoaded))
                    CommandManager.ProcessCommand("/bw overlay glamsource disabled on");
            }
            catch (Exception ex) { _log.Warning($"[Plugin] disabling Browsingway overlay on unload failed: {ex.Message}"); }

            PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            Framework.Update -= OnFrameworkUpdate;

            WindowSystem.RemoveAllWindows();
            _log.Info("[Plugin] DisposeAsync() windows removed, disposing services");

            shellWindow.Dispose();
            _log.Info("[Plugin] DisposeAsync() shellWindow disposed");
            previewWindow.Dispose();
            _log.Info("[Plugin] DisposeAsync() previewWindow disposed");
            previewRenderer.Dispose();
            _log.Info("[Plugin] DisposeAsync() previewRenderer disposed");
            itemDetailWindow.Dispose();
            contextMenuService.Dispose();
            _universalisService?.Dispose();
            CraftingCostService?.Dispose();
            GatherService.Dispose();
            debugApiService.Dispose();
            webUiService.Dispose();

            CommandManager.RemoveHandler(CommandName);
            _log.Info("[Plugin] DisposeAsync() completed");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Plugin] DisposeAsync() threw");
            throw;
        }

        await Task.CompletedTask;
    }

    private int _recentScanFrame;

    // ponytail: Browsingway persists its own "Hidden" flag across game restarts — if a prior
    // session left the overlay visible when the game closed, Browsingway shows it again on every
    // launch regardless of GlamSource's own open/closed state. Force it hidden once, as soon as
    // Browsingway itself has finished loading (retry for a few seconds since plugin load order
    // isn't guaranteed).
    private bool _bwHideDone;
    private int _bwHideRetryFrames = 300; // ~5s at 60fps

    // Hard-pin the Browsingway overlay's ImGui window to the page's fixed content size — the
    // overlay is an ImGui window in the SAME ImGui context as every plugin, so SetWindowSize by
    // its exact window id ("{Name}###{Guid}", read from Browsingway's own config) simply works.
    // Reported live: unlocking made the window "spring" to its old larger stored size; with this
    // pin it can be MOVED while unlocked but never resized away from the content.
    private string? _bwWindowId;
    private int _bwWindowIdRetryFrames = 600;
    private const float BwOverlayWidth = 1190f;
    private const float BwOverlayHeight = 845f; // titlebar + nav + 660px panels + toolbar + paddings
    private const float BwOverlayMinHeight = 42f; // just the titlebar
    // set by the web UI's minimize button (WebUiService /api/action/overlay/minimize) — the page
    // collapses its content AND the actual ImGui window shrinks to the title bar
    public static volatile bool BwOverlayMinimized;
    public static volatile bool BwPinKilled; // stutter-bisect switch for the per-frame SetWindowSize

    private void PinBrowsingwayOverlaySize()
    {
        if (!Configuration.WebUiEnabled || BwPinKilled) return;
        if (_bwWindowId == null)
        {
            if (_bwWindowIdRetryFrames-- % 120 != 0) return; // look up at most every ~2s
            try
            {
                var path = Path.Combine(PluginInterface.ConfigDirectory.Parent!.FullName, "Browsingway.json");
                if (!File.Exists(path)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var inlay in doc.RootElement.GetProperty("Inlays").EnumerateArray())
                {
                    if (!inlay.TryGetProperty("Url", out var url) || url.GetString()?.Contains("127.0.0.1:23424") != true) continue;
                    _bwWindowId = $"{inlay.GetProperty("Name").GetString()}###{inlay.GetProperty("Guid").GetString()}";
                    _log.Info($"[Plugin] Browsingway overlay window id resolved: {_bwWindowId}");
                    break;
                }
            }
            catch (Exception ex) { _log.Warning($"[Plugin] Browsingway window id lookup failed: {ex.Message}"); }
            if (_bwWindowId == null) return;
        }
        var h = BwOverlayMinimized ? BwOverlayMinHeight : BwOverlayHeight;
        Dalamud.Bindings.ImGui.ImGui.SetWindowSize(_bwWindowId, new System.Numerics.Vector2(BwOverlayWidth, h), Dalamud.Bindings.ImGui.ImGuiCond.Always);
    }
    private string _lastRecentKey = "";

    private void OnFrameworkUpdate(IFramework fw)
    {
        // ponytail: reported live — web-UI preview didn't follow a fresh in-game target-click at
        // all, only updating after opening the native ImGui window's Character tab once. That whole
        // sync/dispatch used to live ONLY inside DrawCharacterTab()'s own Draw() call, so with the
        // native window closed (web-UI-only usage) it simply never ran. Every tick now, independent
        // of any window's visibility — cheap (a few field reads unless the target actually changed).
        if (Configuration.WebUiLive3DPreview) shellWindow.SyncPreviewForWeb();

        if (!_bwHideDone)
        {
            if (!Configuration.WebUiEnabled || !Configuration.WebUiAutoOverlay || --_bwHideRetryFrames <= 0)
            {
                _bwHideDone = true;
            }
            else if (PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Browsingway" && p.IsLoaded))
            {
                // ponytail: Browsingway's overlay is a persistent CEF page — it does NOT reload just
                // because the URL string is unchanged, so every GlamSource update otherwise sits
                // stale in an already-open overlay until the user manually reloads it. Force one here.
                CommandManager.ProcessCommand("/bw overlay glamsource reload toggle");
                CommandManager.ProcessCommand("/bw overlay glamsource hidden on");
                // ponytail: "beim Disablen vom Plugin auch Browsingway disablen, beim Anschalten
                // wieder enablen" — mirrors DisposeAsync's "disabled on" call, so a plugin that got
                // disabled last session (leaving its Browsingway overlay disabled too) comes back up
                // clean on next load instead of staying invisible with no obvious reason why.
                CommandManager.ProcessCommand("/bw overlay glamsource disabled off");
                // Freeze the overlay's position AND size as-is ("Fensterwerte festsetzen, nicht
                // größer/kleiner machen"): Browsingway's Locked flag blocks both. The titlebar
                // lock button still toggles it off for repositioning — and locked is required for
                // drag-rotate anyway, so this doubles as the correct default.
                CommandManager.ProcessCommand("/bw overlay glamsource locked on");
                _bwHideDone = true;
            }
        }

        // Throttle: every 30 frames (~0.5s at 60fps).
        if (++_recentScanFrame < 30) return;
        _recentScanFrame = 0;

        var target = TargetManager.Target;
        if (target is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc) return;

        var name = pc.Name.TextValue;
        var world = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";
        var key = $"{name}@{world}";
        if (key == _lastRecentKey) return;

        var slots = GameDataService.TryGetVisibleGlamour(pc.ObjectIndex) ?? GameDataService.GetTargetEquipment();
        if (slots == null || slots.Count == 0) return;

        _lastRecentKey = key;
        var itemIds = new System.Collections.Generic.List<uint>(slots.Count);
        var stain0s = new System.Collections.Generic.List<byte>(slots.Count);
        var stain1s = new System.Collections.Generic.List<byte>(slots.Count);
        foreach (var s in slots)
        {
            itemIds.Add(s.GlamourItemId ?? s.ActualItemId);
            stain0s.Add(s.Stain0);
            stain1s.Add(s.Stain1);
        }
        Configuration.PushRecent(name, world, pc.GameObjectId, itemIds, stain0s, stain1s);
    }

    private void OnCommand(string command, string args)
    {
        var arg = args?.Trim();
        if (string.Equals(arg, "char", StringComparison.OrdinalIgnoreCase))
        {
            shellWindow.SwitchToTab(GlamSourceShellWindow.TabId.Character);
        }
        else if (string.Equals(arg, "settings", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "config", StringComparison.OrdinalIgnoreCase))
        {
            shellWindow.SwitchToTab(GlamSourceShellWindow.TabId.Settings);
        }
        else if (string.Equals(arg, "preview", StringComparison.OrdinalIgnoreCase))
        {
            if (previewWindow.IsOpen)
                previewWindow.IsOpen = false;
            else
                previewWindow.OpenForCurrentTarget();
        }
        else
        {
            shellWindow.Toggle();
        }
    }

    public void OpenConfigUi()
    {
        shellWindow.IsOpen = true;
        shellWindow.SwitchToTab(GlamSourceShellWindow.TabId.Settings);
    }

    public void ToggleMainUi() => shellWindow.Toggle();
}
