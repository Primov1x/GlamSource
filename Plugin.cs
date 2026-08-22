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
        ICondition condition)
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

        var goatImagePath = Path.Join(PluginInterface.AssemblyLocation.Directory?.FullName, "goat.png");

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

        previewRenderer = new PreviewRenderer(Framework, Log);
        previewWindow = new GlamourPreviewWindow(previewRenderer, Framework, ClientState, ObjectTable, TargetManager, PluginInterface, Log);
        WindowSystem.AddWindow(previewWindow);
        shellWindow.PreviewWindow = previewWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Offnet das GlamSource Fenster"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // ponytail: passive target-scan for the Recent list. Runs even when the Character tab
        // isn't drawn; throttled to every 30 frames so it doesn't hammer the sheets.
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        if (GlamourServiceOverride != null)
            shellWindow.IsOpen = true;
        else
            shellWindow.SwitchToTab((GlamSourceShellWindow.TabId)Configuration.SelectedTab);
    }

    public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Framework.Update -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();

        shellWindow.Dispose();
        previewWindow.Dispose();
        previewRenderer.Dispose();
        itemDetailWindow.Dispose();
        contextMenuService.Dispose();
        _universalisService?.Dispose();
        CraftingCostService?.Dispose();
        GatherService.Dispose();

        CommandManager.RemoveHandler(CommandName);

        await Task.CompletedTask;
    }

    private int _recentScanFrame;
    private string _lastRecentKey = "";

    private void OnFrameworkUpdate(IFramework fw)
    {
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
        foreach (var s in slots) itemIds.Add(s.GlamourItemId ?? s.ActualItemId);
        Configuration.PushRecent(name, world, pc.GameObjectId, itemIds);
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
