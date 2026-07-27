using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using GlamSource.Windows;
using GlamSource.Windows.Helpers;

namespace GlamSource;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private const string CommandName = "/glamsource";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("GlamSource");
    public readonly GameDataService GameDataService;
    public readonly MainWindowHelpers MainWindowHelpers;

    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GameDataService = new GameDataService(DataManager);
        MainWindowHelpers = new MainWindowHelpers(GameDataService);

        var goatImagePath = Path.Join(PluginInterface.AssemblyLocation.Directory?.FullName, "goat.png");

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this, goatImagePath, MainWindowHelpers);

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
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        configWindow.Dispose();
        mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void ToggleConfigUi() => configWindow.Toggle();
    public void ToggleMainUi() => mainWindow.Toggle();
}
