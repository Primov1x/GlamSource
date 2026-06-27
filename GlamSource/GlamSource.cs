using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace GlamSource {
    [PluginName("GlamSource")]
    [PluginDescription("Zeigt woher Glams und Mounts kommen.")]
    public sealed class GlamSourcePlugin : IDalamudPlugin {
        private readonly string name = "GlamSource";
        private readonly WindowSystem windowSystem;
        private readonly DalamudPluginInterface pluginInterface;

        public GlamSourcePlugin(DalamudPluginInterface pluginInterface) {
            this.pluginInterface = pluginInterface;
            windowSystem = new WindowSystem(name);

            // Register our custom window using Dalamud.Interface.Windowing.Window
            var window = new GlamSourceWindow();
            windowSystem.AddWindow(window);

            pluginInterface.UiBuilder.Draw += this.OnDraw;
            pluginInterface.UiBuilder.OpenMainUi += this.OnOpenMainUi;
            pluginInterface.CommandManager.AddHandler("/glamsource", new CommandInfo(OnCommand) {
                HelpMessage = "Öffnet das GlamSource Fenster"
            });
        }

        public string Name => name;

        private void OnCommand(CommandInfo info) {
            windowSystem.GetWindow("GlamSourceWindow").IsOpen = true;
        }

        private void OnOpenMainUi() {
            windowSystem.GetWindow("GlamSourceWindow").IsOpen = true;
        }

        private void OnDraw() {
            windowSystem.Draw();
        }

        public void Dispose() {
            pluginInterface?.CommandManager.RemoveHandler("/glamsource");
            pluginInterface?.UiBuilder.Draw -= this.OnDraw;
            pluginInterface?.UiBuilder.OpenMainUi -= this.OnOpenMainUi;
        }
    }
}