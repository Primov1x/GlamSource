using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GlamSource.Windows;

// ponytail: central style helper so every window agrees on rounding, palette, and section headers.
// Everything is expressed relative to ImGui.GetFontSize() so user Dalamud scale still owns sizing.
internal static class UiStyle
{
    // ---- Palette (kept close to the plugin's historical accent so nothing looks alien) ----
    public static readonly Vector4 Accent      = new(0.90f, 0.70f, 0.20f, 1.00f); // warm gold
    public static readonly Vector4 AccentDim   = new(0.90f, 0.70f, 0.20f, 0.35f);
    public static readonly Vector4 Success     = new(0.30f, 0.80f, 0.30f, 1.00f);
    public static readonly Vector4 Warning     = new(1.00f, 0.60f, 0.00f, 1.00f);
    public static readonly Vector4 Muted       = new(0.55f, 0.58f, 0.62f, 1.00f);
    public static readonly Vector4 PanelBorder = new(1.00f, 1.00f, 1.00f, 0.06f);

    /// <summary>
    /// Push a soft-rounded style scope for the current draw pass. Auto-pops via IDisposable.
    /// Use at the very top of Window.Draw so tables/child windows inherit the rounding.
    /// </summary>
    public static Scope Push()
    {
        var r = MathF.Max(4f, ImGui.GetFontSize() * 0.35f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,      r);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,       r);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,       r * 0.75f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,       r);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding,   r);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,        r * 0.75f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding,         r * 0.75f);
        ImGui.PushStyleColor(ImGuiCol.Border, PanelBorder);
        return new Scope(varCount: 7, colorCount: 1);
    }

    /// <summary>
    /// Consistent section header: accent dot + label + thin separator.
    /// Replaces the copy-pasted TextColored(...) + Separator() pairs.
    /// </summary>
    public static void SectionHeader(string label)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var fs = ImGui.GetFontSize();
        var dot = fs * 0.35f;
        var cy = p.Y + fs * 0.55f;
        dl.AddCircleFilled(new Vector2(p.X + dot, cy), dot, ImGui.ColorConvertFloat4ToU32(Accent));
        ImGui.Dummy(new Vector2(dot * 2f + fs * 0.3f, fs));
        ImGui.SameLine();
        ImGui.TextColored(Accent, label);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>Small right-aligned muted hint on the current line.</summary>
    public static void MutedHint(string text)
    {
        ImGui.SameLine();
        ImGui.TextColored(Muted, text);
    }

    /// <summary>
    /// A rounded, bordered panel — useful for grouping results, snapshots, etc.
    /// Returns an IDisposable so `using var _ = UiStyle.BeginCard(...)` is the whole call.
    /// If BeginChild returned false the disposer is a no-op.
    /// </summary>
    public static CardScope BeginCard(string id, Vector2 size)
    {
        var opened = ImGui.BeginChild(id, size, true);
        return new CardScope(opened);
    }

    public readonly ref struct Scope
    {
        private readonly int _varCount;
        private readonly int _colorCount;
        public Scope(int varCount, int colorCount) { _varCount = varCount; _colorCount = colorCount; }
        public void Dispose()
        {
            if (_colorCount > 0) ImGui.PopStyleColor(_colorCount);
            if (_varCount   > 0) ImGui.PopStyleVar(_varCount);
        }
    }

    public readonly ref struct CardScope
    {
        public readonly bool Opened;
        public CardScope(bool opened) { Opened = opened; }
        public void Dispose() => ImGui.EndChild();
    }
}
