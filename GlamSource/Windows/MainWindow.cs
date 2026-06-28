using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using GlamSource.Windows.Helpers;
using GlamSource;

namespace GlamSource.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string goatImagePath;
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin, string goatImagePath)
        : base("My Amazing Window", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.goatImagePath = goatImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text($"The random config bool is {plugin.Configuration.SomePropertyToBeSavedAndWithADefault}");

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.Spacing();

        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            if (!child.Success)
                return;

            ImGui.Text("Have a goat:");
            var goatImage = Plugin.TextureProvider.GetFromFile(goatImagePath).GetWrapOrDefault();
            if (goatImage != null)
            {
                using (ImRaii.PushIndent(55f))
                {
                    ImGui.Image(goatImage.Handle, goatImage.Size);
                }
            }
            else
            {
                ImGui.Text("Image not found.");
            }

            ImGuiHelpers.ScaledDummy(20.0f);

            JobInfoRenderer.Render();
            MainWindowHelpers.RenderLocationInfo();
        }
    }
}
