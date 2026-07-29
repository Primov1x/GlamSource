using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GlamSource.Core;
using Newtonsoft.Json.Linq;

namespace GlamSource.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly IGlamourService _glamourService;
    private int _lastLoggedIndex = -1;
    private int _lastLoggedEc = -999;
    private bool _lastNoTargetLogged = false;
    private int _retryFrame = -1;
    private int _retryObjectIndex = -1;
    private const int RetryCooldownFrames = 5;
    private int _drawFrame = 0;

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

    private static string FormatSource(ItemSource src)
    {
        return src.Type switch
        {
            ItemSourceType.Crafted => src.Description,
            ItemSourceType.Vendor => src.Description,
            ItemSourceType.Quest => "Quest",
            ItemSourceType.Dungeon or ItemSourceType.Trial or ItemSourceType.Raid => src.Description,
            _ => src.Description
        };
    }

    private static Vector4 GetSourceColor(ItemSourceType type)
    {
        return type switch
        {
            ItemSourceType.Crafted => new Vector4(1f, 0.5f, 0f, 1f),
            ItemSourceType.Vendor => new Vector4(0.5f, 0.5f, 1f, 1f),
            ItemSourceType.Quest => new Vector4(0.5f, 1f, 0.5f, 1f),
            ItemSourceType.Dungeon => new Vector4(1f, 0.3f, 0.3f, 1f),
            ItemSourceType.Trial => new Vector4(1f, 0.8f, 0f, 1f),
            ItemSourceType.Raid => new Vector4(0.8f, 0f, 1f, 1f),
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1f)
        };
    }

    private bool IsValidTarget(IGameObject obj)
    {
        if (obj is not ICharacter)
            return false;

        var ok = (int)obj.ObjectKind;
        return ok == (int)ObjectKind.Pc
            || ok == (int)ObjectKind.BattleNpc
            || ok == (int)ObjectKind.EventNpc;
    }

    private (Glamourer.Api.Enums.GlamourerApiEc ec, JObject? json) CallGlamourerState(int objectIndex, bool useNameFallback)
    {
        if (useNameFallback)
        {
            var stateName = new Glamourer.Api.IpcSubscribers.GetStateName(Plugin.PluginInterface);
            var playerChar = Plugin.TargetManager.Target as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
            string? playerName = playerChar?.Name.ToString();
            if (string.IsNullOrEmpty(playerName))
                return (Glamourer.Api.Enums.GlamourerApiEc.ActorNotFound, null);

            return stateName.Invoke(playerName, 0);
        }
        else
        {
            var getState = new Glamourer.Api.IpcSubscribers.GetState(Plugin.PluginInterface);
            return getState.Invoke(objectIndex, 0);
        }
    }

    public override void Draw()
    {
        System.Console.WriteLine($"[DIAG] Draw() called, slots={_glamourService.GetTargetEquipment().Count}");
        try
        {
            var target = Plugin.TargetManager?.Target;
            if (target is not null && IsValidTarget(target) && target.Address != nint.Zero)
            {
                _lastNoTargetLogged = false;
                var objectIndex = (int)target.ObjectIndex;
                var getState = new Glamourer.Api.IpcSubscribers.GetState(Plugin.PluginInterface);
                var (ec, jObject) = getState.Invoke(objectIndex, 0);
                int ecInt = (int)ec;

                if (ec == Glamourer.Api.Enums.GlamourerApiEc.ActorNotFound)
                {
                    _drawFrame++;
                    if (_retryFrame == -1 || _drawFrame - _retryFrame >= RetryCooldownFrames)
                    {
                        var playerChar = target as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
                        if (playerChar is not null && playerChar.Address != nint.Zero)
                        {
                            Plugin.Log?.Information("[DEBUG-Glamourer] ActorNotFound, retrying with GetStateName for '{Name}'",
                                playerChar.Name.ToString());
                            var (fallbackEc, fallbackJson) = CallGlamourerState(objectIndex, true);
                            int fallbackEcInt = (int)fallbackEc;
                            if (objectIndex != _lastLoggedIndex || fallbackEcInt != _lastLoggedEc)
                            {
                                Plugin.Log?.Information("[DEBUG-Glamourer] GetStateName fallback: ec={Ec} json={Json}",
                                    fallbackEcInt, fallbackJson?.ToString() ?? "(null)");
                                _lastLoggedIndex = objectIndex;
                                _lastLoggedEc = fallbackEcInt;
                            }
                            _retryFrame = _drawFrame;
                            _retryObjectIndex = objectIndex;
                        }
                        else
                        {
                            if (objectIndex != _lastLoggedIndex || ecInt != _lastLoggedEc)
                            {
                                Plugin.Log?.Information("[DEBUG-Glamourer] ActorNotFound for non-player (ObjectKind={ObjectKind})",
                                    target.ObjectKind);
                                _lastLoggedIndex = objectIndex;
                                _lastLoggedEc = ecInt;
                            }
                        }
                    }
                }
                else
                {
                    _retryFrame = -1;
                    _retryObjectIndex = -1;
                    if (objectIndex != _lastLoggedIndex || ecInt != _lastLoggedEc)
                    {
                        Plugin.Log?.Information("[DEBUG-Glamourer] GetState: objectIndex={ObjectIndex} ec={Ec} json={Json}",
                            objectIndex, ecInt, jObject?.ToString() ?? "(null)");
                        _lastLoggedIndex = objectIndex;
                        _lastLoggedEc = ecInt;
                    }
                }

                if (ec == Glamourer.Api.Enums.GlamourerApiEc.ActorNotHuman)
                {
                    Plugin.Log?.Warning("[DEBUG-Glamourer] Target is not human (ObjectKind={ObjectKind}, name={Name})",
                        target.ObjectKind, target.Name.ToString());
                }
            }
            else
            {
                if (!_lastNoTargetLogged)
                {
                    Plugin.Log?.Information("[DEBUG-Glamourer] No valid target (null/invalid/not character)");
                    _lastNoTargetLogged = true;
                }
                _retryFrame = -1;
                _retryObjectIndex = -1;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log?.Error(ex, "[DEBUG-Glamourer] GetState failed");
            _retryFrame = -1;
            _retryObjectIndex = -1;
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

        if (ImGui.BeginTable("EquipmentTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Worn Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Glamour", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
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
                var hasActualSources = slot.ActualItemSources != null && slot.ActualItemSources.Count > 0;
                var hasGlamourSources = slot.GlamourItemSources != null && slot.GlamourItemSources.Count > 0;

                if (!hasActualSources && !hasGlamourSources)
                {
                    ImGui.TextDisabled("Unknown");
                }
                else
                {
                    if (hasActualSources)
                    {
                        foreach (var src in slot.ActualItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Worn: {FormatSource(src)}");
                        }
                    }

                    if (hasGlamourSources)
                    {
                        foreach (var src in slot.GlamourItemSources)
                        {
                            var color = GetSourceColor(src.Type);
                            ImGui.TextColored(color, $"Glam: {FormatSource(src)}");
                        }
                    }
                }

                ImGui.TableSetColumnIndex(4);
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
