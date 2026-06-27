using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using System;
using GlamSource.Windows;

namespace GlamSource
{
    public sealed class Plugin : IDalamudPlugin
    {
        private readonly DalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IPluginLog pluginLog;
        private readonly WindowSystem windowSystem;
        private readonly MainWindow mainWindow;

        public Plugin(
            DalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog pluginLog)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.pluginLog = pluginLog;

            this.windowSystem = new WindowSystem("GlamSource");
            this.mainWindow = new MainWindow();
            this.windowSystem.AddWindow(this.mainWindow);

            this.commandManager.AddHandler("/glamsource", new CommandInfo(OnCommand)
            {
                HelpMessage = "Öffne das GlamSource Fenster",
                ShowInHelp = true
            });

            this.pluginInterface.UiBuilder.Draw += DrawUI;
            this.pluginInterface.UiBuilder.OpenMainUi += OpenMainUI;

            this.pluginLog.Information("GlamSource Plugin geladen.");
        }

        public string Name => "GlamSource";

        private void OnCommand(string command, string args)
        {
            this.mainWindow.IsOpen = true;
        }

        private void OpenMainUI()
        {
            this.mainWindow.IsOpen = true;
        }

        private void DrawUI()
        {
            this.windowSystem.Draw();
        }

        public void Dispose()
        {
            this.pluginInterface.UiBuilder.Draw -= DrawUI;
            this.pluginInterface.UiBuilder.OpenMainUi -= OpenMainUI;
            this.commandManager.RemoveHandler("/glamsource");
            this.windowSystem.RemoveAllWindows();
        }
    }
}

namespace GlamSource.Windows
{
    public class MainWindow : Dalamud.Window
    {
        public MainWindow(DalamudPluginInterface pluginInterface) : base("GlamSource", pluginInterface)
        {
            this.Size = new System.Drawing.Size(400, 300);
        }

        protected override void Draw()
        {
            ImGui.TextColored(ImVec4.Yellow, "GlamSource: Quellenanzeige");

            if (ImGui.Button("Ausrüstungsquelle prüfen"))
            {
                FetchItemSource("12345"); // Beispiel-Item-ID
            }

            if (ImGui.Button("Mountquelle prüfen"))
            {
                FetchMountSource("67890"); // Beispiel-Mount-ID
            }
        }
    }
}
