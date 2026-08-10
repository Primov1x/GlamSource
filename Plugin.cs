using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using GlamSource.Core;
using GlamSource.Services;
using GlamSource.Windows;

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

    private const string CommandName = "/glamsource";

    public Configuration Configuration { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("GlamSource");
    public readonly GameDataService GameDataService;

    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;
    private readonly ContextMenuService contextMenuService;
    private readonly ItemDetailWindow itemDetailWindow;
    private readonly UniversalisService? _universalisService;
    public static IGlamourService? GlamourServiceOverride;

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
        IObjectTable objectTable)
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

        PluginInterface = _pluginInterface;
        TextureProvider = _textureProvider;
        CommandManager = _commandManager;
        ClientState = _clientState;
        PlayerState = _playerState;
        DataManager = _dataManager;
        Log = _log;
        TargetManager = _targetManager;
        GameGui = _gameGui;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var sourceService = new LuminaItemSourceService(DataManager.GameData);
        var gameDataService = new GameDataService(DataManager, TargetManager, ObjectTable, GameGui, sourceService);
        GameDataService = gameDataService;

        var goatImagePath = Path.Join(PluginInterface.AssemblyLocation.Directory?.FullName, "goat.png");

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(gameDataService, itemDetailWindow);

        var itemDetailService = new ItemDetailService(DataManager.GameData);
        var universalisHttpClient = new System.Net.Http.HttpClient();
        _universalisService = new UniversalisService(universalisHttpClient, "Shiva", "Light");
        itemDetailWindow = new ItemDetailWindow(itemDetailService, sourceService, _universalisService, textureProvider);
        WindowSystem.AddWindow(itemDetailWindow);

        contextMenuService = new ContextMenuService(ContextMenu, _gameGui, itemId =>
        {
            itemDetailWindow.ShowItem(itemId);
        });

        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Offnet das GlamSource Fenster"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        if (GlamourServiceOverride != null)
            mainWindow.IsOpen = true;
    }

    public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        configWindow.Dispose();
        mainWindow.Dispose();
        itemDetailWindow.Dispose();
        contextMenuService.Dispose();
        _universalisService?.Dispose();

        CommandManager.RemoveHandler(CommandName);

        await Task.CompletedTask;
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void ToggleConfigUi() => configWindow.Toggle();
    public void ToggleMainUi() => mainWindow.Toggle();
}
