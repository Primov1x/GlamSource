using Dalamud.Plugin;
using Dalamud.Interface.Windowing;

namespace GlamSource {
    [PluginName("GlamSource")]
    public class GlamSourcePlugin : Plugin {
        private readonly ImGuiWindow window;

        public GlamSourcePlugin(DalamudPluginInterface pluginInterface) {
            this.window = new ImGuiWindow("GlamSource");
            pluginInterface.CommandManager.AddHandler("/glamsource", this.OnSlashCommand);
        }

        public void OnSlashCommand(string command, string arguments) {
            window.IsOpen = true;
        }

        public override void Draw() {
            if (window.IsOpen) {
                ImGui.Begin("GlamSource");
                ImGui.Text("GlamSource läuft!");
                ImGui.End();
            }
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            if (disposing) {
                this.window?.Dispose();
            }
        }
    }
}
