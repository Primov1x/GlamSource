using Dalamud.Interface.Windowing;
using ImGuiNET;
using System.Numerics;

namespace GlamSource {
    public class GlamSourceWindow : Window {
        public GlamSourceWindow()
            : base("GlamSourceWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) {
            this.SizeConstraints = new WindowSizeConstraints {
                MinimumSize = new Vector2(320, 240),
                MaximumSize = new Vector2(1000, 800)
            };
        }

        public override void Draw() {
            ImGui.Text("Willkommen bei GlamSource!");
            ImGui.Separator();
            ImGui.TextWrapped("Dieses Plugin zeigt dir schnell und einfach die Herkunft deiner Glams, Mounts und anderer Ausstattung – Dungeons, Shops, Crafting, Events und mehr.");
        }
    }
}