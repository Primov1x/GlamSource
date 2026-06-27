using Dalamud.Interface.Windowing;
using ImGuiNET;
using System.Numerics;

namespace GlamSource.Windows;

public class MainWindow : Window
{
    public MainWindow() : base("GlamSource")
    {
        this.Size = new Vector2(400, 300);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "GlamSource: Quellenanzeige");

        if (ImGui.Button("Ausrüstungsquelle prüfen"))
        {
            FetchItemSource("12345"); // Beispiel-Item-ID
        }

        if (ImGui.Button("Mountquelle prüfen"))
        {
            FetchMountSource("67890"); // Beispiel-Mount-ID
        }
    }

    private void FetchItemSource(string itemId)
    {
        ImGui.Text($"Überprüfe Quelle für Item: {itemId}");
        // TODO: Implementiere die Logik zum Abrufen der Item-Quelle
    }

    private void FetchMountSource(string mountId)
    {
        ImGui.Text($"Überprüfe Quelle für Mount: {mountId}");
        // TODO: Implementiere die Logik zum Abrufen der Mount-Quelle
    }
}