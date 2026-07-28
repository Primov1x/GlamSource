using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GlamSource.Core;
using Newtonsoft.Json.Linq;

namespace GlamSource.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly IGlamourService _glamourService;
    private int _lastLoggedIndex = -1;
    private int _lastLoggedEc = -999;
    private bool _lastNoTargetLogged = false;

    public MainWindow(IGlamourService glamourService)
        : base("GlamSource", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _glamourService = glamourService;
    }

    public void Dispose() { }

    public override void Draw()
    {
        try
        {
            var target = Plugin.TargetManager.Target;
            if (target is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter playerChar && playerChar.Address != nint.Zero)
            {
                var objectIndex = (int)target.ObjectIndex;
                var getState = new Glamourer.Api.IpcSubscribers.GetState(Plugin.PluginInterface);
                var (ec, jObject) = getState.Invoke(objectIndex, 0);
                int ecInt = (int)ec;
                _lastNoTargetLogged = false;
                if (objectIndex != _lastLoggedIndex || ecInt != _lastLoggedEc)
                {
                    Plugin.Log.Information("[DEBUG-Glamourer] GetState: objectIndex={ObjectIndex} ec={Ec} json={Json}",
                        objectIndex, ecInt, jObject?.ToString() ?? "(null)");
                    _lastLoggedIndex = objectIndex;
                    _lastLoggedEc = ecInt;
                }
            }
            else
            {
                if (!_lastNoTargetLogged)
                {
                    Plugin.Log.Information("[DEBUG-Glamourer] No valid player target");
                    _lastNoTargetLogged = true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DEBUG-Glamourer] GetState failed");
        }

        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Target Equipment");
        ImGui.Separator();
        ImGui.Spacing();

        var slots = _glamourService.GetTargetEquipment();

        if (slots.Count == 0)
        {
            ImGui.Text("No equipment data available.");
            return;
        }

        if (ImGui.BeginTable("EquipmentTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Worn Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Glamour", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Overlay", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            foreach (var slot in slots)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"{slot.Slot}");

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{slot.ActualItemName} ({slot.ActualItemId})");

                ImGui.TableSetColumnIndex(2);
                if (slot.IsGlamoured)
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), $"{slot.GlamourItemName} ({slot.GlamourItemId})");
                }
                else
                {
                    ImGui.TextDisabled("(none)");
                }

                ImGui.TableSetColumnIndex(3);
                if (slot.IsGlamoured)
                {
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "\u2713");
                }
                else
                {
                    ImGui.TextDisabled("-");
                }
            }

            ImGui.EndTable();
        }
    }
}
