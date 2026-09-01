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
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;


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
    private readonly ItemDetailService itemDetailService;
    private readonly ItemImageService itemImageService;
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
        IGameInteropProvider gameInterop,
        IContextMenu contextMenu)
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
        // ponytail: real bug, not just a Mock workaround — every other [PluginService] property here
        // gets manually re-assigned from a constructor param (so this class doesn't actually depend
        // on Dalamud's own reflection-based [PluginService] injection at all), but ContextMenu was
        // missing from that list. In the real game [PluginService] populated it anyway so it went
        // unnoticed; DalaMock's Autofac-based plugin loader has no such reflection step, so
        // `ContextMenu` stayed permanently null and crashed the very first `new ContextMenuService(
        // ContextMenu, ...)` call below with an NRE.
        ContextMenu = contextMenu;

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


        itemDetailService = new ItemDetailService(DataManager.GameData);
        var universalisHttpClient = new System.Net.Http.HttpClient();
        _universalisService = new UniversalisService(universalisHttpClient, "Shiva", "Light");
        var craftingCostService = new CraftingCostService(itemDetailService, _universalisService!);
        CraftingCostService = craftingCostService;
        // ponytail: separate instance from WebUiService's own — cheap (in-memory cache, no shared
        // state needed), avoids reshaping WebUiService's constructor for this.
        itemImageService = new ItemImageService(new System.Net.Http.HttpClient());
        itemDetailWindow = new ItemDetailWindow(itemDetailService, sourceService, _universalisService, textureProvider, DataManager, itemImageService);
        itemDetailWindow.SetPlugin(this);
        WindowSystem.AddWindow(itemDetailWindow);

        contextMenuService = new ContextMenuService(ContextMenu, _gameGui, itemId => OpenItemDetail(itemId), gameDataService, itemDetailService);

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
            itemImageService.Dispose();
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
    private int _retainerCacheFrame;

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
    private long _bwLookupStartedMs;
    private bool _bwOverlayMissingWarned;
    private const float BwOverlayWidth = 1190f;
    // titlebar + nav + 660px panels + toolbar + paddings + #recentsFooter. 925 STILL clipped
    // ("hab noch scrollränder, bissl größer darf schon sein") — bumped further with extra margin
    // instead of hunting the exact pixel again.
    private const float BwOverlayHeight = 990f;
    private const float BwOverlayMinHeight = 42f; // just the titlebar
    // compact height for the Item-Search tab before any results are showing — "darf gern klein
    // bleiben bis man sachen sucht": titlebar + nav + search input + margins, no results list yet
    private const float BwOverlayCompactHeight = 260f;
    // set by the web UI's minimize button (WebUiService /api/action/overlay/minimize) — the page
    // collapses its content AND the actual ImGui window shrinks to the title bar
    public static volatile bool BwOverlayMinimized;
    // set by the web UI whenever the Item-Search tab has no results/detail showing yet (WebUiService
    // /api/action/overlay/compact) — full height only once there's actually content to show
    public static volatile bool BwOverlayCompact;
    public static volatile bool BwPinKilled; // stutter-bisect switch for the per-frame SetWindowSize
    // set by the web UI's lock button (WebUiService /api/action/overlay/lock) — bypasses the 2s
    // pin throttle below for one immediate re-pin. Without this, unlocking let Browsingway's own
    // persisted ImGui window size flash through for up to 2s before our next scheduled pin call
    // corrected it ("wenn man das lock wegmacht ... nicht umspringen auf was anderes").
    public static volatile bool BwLockJustToggled;

    private long _lastBwPinMs;
    private bool _lastBwMinimized;
    private bool _lastBwCompact;

    private void PinBrowsingwayOverlaySize()
    {
        if (!Configuration.WebUiEnabled || BwPinKilled) return;
        // Throttled to every 2s (was per-frame): a per-frame Always-pin makes the overlay window
        // re-layout continuously — needless work, and it muddied a live stutter bisection (the
        // actual culprit that day was a sick Browsingway CEF process, cured by toggling
        // Browsingway off/on — documented in doku/character-preview.md).
        var minimizedChanged = BwOverlayMinimized != _lastBwMinimized;
        var compactChanged = BwOverlayCompact != _lastBwCompact;
        var bypassThrottle = minimizedChanged || compactChanged || BwLockJustToggled;
        if (!bypassThrottle && Environment.TickCount64 - _lastBwPinMs < 2000) return;
        _lastBwPinMs = Environment.TickCount64;
        _lastBwMinimized = BwOverlayMinimized;
        _lastBwCompact = BwOverlayCompact;
        BwLockJustToggled = false;
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
            if (_bwWindowId == null)
            {
                // ponytail: GlamSource never CREATES the Browsingway overlay itself — every
                // "/bw overlay glamsource ..." call above assumes one named "glamsource" already
                // exists. That's always been a manual, one-time step in Browsingway's own settings
                // UI (Dalamud has no chat command to add one — checked Browsingway's source,
                // Settings.HandleOverlayCommand only edits EXISTING overlays), just never
                // documented anywhere. Live report: "bei ihr wird kein overlay angelegt" — a fresh
                // install with no matching overlay retries silently forever. One clear chat
                // message after 15s of failed lookups, not a silent no-op.
                if (_bwLookupStartedMs == 0) _bwLookupStartedMs = Environment.TickCount64;
                else if (!_bwOverlayMissingWarned && Environment.TickCount64 - _bwLookupStartedMs > 15_000)
                {
                    _bwOverlayMissingWarned = true;
                    ChatGui.PrintError(
                        "[GlamSource] Kein Browsingway-Overlay gefunden. Einmalig manuell einrichten: " +
                        "Browsingway-Einstellungen öffnen (/bw config) -> Overlay hinzufügen -> " +
                        "Name \"glamsource\", URL \"http://127.0.0.1:23424/\".");
                }
                return;
            }
        }
        var h = BwOverlayMinimized ? BwOverlayMinHeight : BwOverlayCompact ? BwOverlayCompactHeight : BwOverlayHeight;
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

        // "man sieht welches mat wo liegt und wie viel" — RetainerManager only ever loads ONE
        // retainer's inventory at a time (whichever's open at the bell); snapshot it into
        // RetainerInventoryCache the moment it's open so each retainer's contents stay browsable
        // for the rest of the session, not just while that specific retainer window is up. Cheap
        // no-op call when no retainer is open — every ~1s is plenty, this isn't time-critical.
        if (++_retainerCacheFrame >= 60) { _retainerCacheFrame = 0; RetainerInventoryCache.UpdateFromCurrentRetainer(); }

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
                // ponytail: was "locked on" by default (needed for drag-rotate, and freezes
                // position/size) — flipped per request: "bei start/öffnen nicht locked als
                // standard, damit man verschieben kann". Size still stays pinned regardless (see
                // PinBrowsingwayOverlaySize, unrelated to this flag); only position becomes
                // draggable on open now. The titlebar lock button still locks it for drag-rotate.
                CommandManager.ProcessCommand("/bw overlay glamsource locked off");
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
        else if (string.Equals(arg, "mount", StringComparison.OrdinalIgnoreCase))
        {
            OpenTargetMount();
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

    // ponytail: target-based twin of the "Check Mount" context-menu entry — same resolution
    // (GameDataService.GetMountId -> ItemDetailService.ResolveMountItemId -> ItemDetailWindow.ShowItem),
    // just reading the current target instead of the right-clicked actor. Silently no-ops if there's
    // no target, it's not mounted, or the mount isn't in the scraped dataset — same as the context
    // menu entry simply not appearing in those cases.
    private void OpenTargetMount()
    {
        var target = TargetManager.Target;
        if (target == null) return;

        var mountId = GameDataService.GetMountId(target);
        if (mountId is not > 0) return;

        var mountItemId = itemDetailService.ResolveMountItemId(mountId.Value);
        if (mountItemId is not > 0) return;

        OpenItemDetail(mountItemId.Value);
    }

    // ponytail: shared by the context-menu callback ("Check Mount", "Item Source") and
    // OpenTargetMount (/glamsource mount twin) — was duplicated inline in the context menu
    // callback only, so OpenTargetMount bypassed ContextMenuOpensInWebUi entirely (live: a mount
    // opened via examine still popped ImGui despite the web-UI setting being on).
    private void OpenItemDetail(uint itemId)
    {
        // "wenn ich in examine 'item source' klicke, soll es im webgui aufgehen anstatt im
        // imgui" — opt-in setting; falls back to ImGui if the web UI itself isn't even on
        // (opening nothing would be worse than the old behavior).
        if (Configuration.ContextMenuOpensInWebUi && Configuration.WebUiEnabled)
        {
            // the web page already switches to Suche + opens the item on its own poll (see
            // WebUiPage's pendingitem interval) — just make sure the overlay is actually
            // VISIBLE, since Browsingway's own hidden/minimized state is otherwise entirely
            // user/hotkey-managed and the push would silently land in a hidden overlay.
            BwOverlayMinimized = false;
            CommandManager.ProcessCommand("/bw overlay glamsource hidden off");
        }
        else
        {
            itemDetailWindow.ShowItem(itemId);
        }
        // ponytail: webUiService is constructed further below the fields it's assigned in — fine,
        // this only runs on an actual click, long after the constructor finishes.
        webUiService?.PushItemToWeb(itemId);
    }
}
