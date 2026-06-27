using Dalamud.Game.ClientState;
using Dalamud.Plugin;
using System;
using System.Net.Http;
using System.Text.Json;
using Dalamud.Interface;

namespace GlamSource
{
    [PluginEntryPoint("GlamSource", "1.0.0", "Sir Clankerton der Dritte")]
    public class Plugin : IDalamudPlugin
    {
        private readonly DalamudPluginInterface pluginInterface;
        private readonly ClientState clientState;

        public Plugin(DalamudPluginInterface pluginInterface, ClientState clientState)
        {
            this.pluginInterface = pluginInterface;
            this.clientState = clientState;
        }

        public void Initialize()
        {
            // Plugin initialisierung
            pluginInterface.PluginLogger.Info("GlamSource: Initialisiert");

            // UI Fenster registrieren
            pluginInterface.WindowManager.AddWindow(new MainWindow(pluginInterface));

            // Slash-Befehle registrieren
            pluginInterface.CommandManager.AddHandler("/glamsource", (sender, args) => {
                pluginInterface.WindowManager.ShowWindow<MainWindow>();
                return true;
            });
        }

        public void Dispose()
        {
            // Plugin Cleanup
            pluginInterface.WindowManager.RemoveWindow<MainWindow>();
            pluginInterface.CommandManager.RemoveHandler("/glamsource");
            pluginInterface.PluginLogger.Info("GlamSource: Beendet");
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
