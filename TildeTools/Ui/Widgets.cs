using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TildeTools.Modules;

namespace TildeTools.Ui;

internal static class Widgets
{
    internal static readonly Vector4 ErrorColour = new(1f, 0.4f, 0.3f, 1f);
    internal static readonly Vector4 WarningColour = new(1f, 0.5f, 0.3f, 1f);

    // On release: clamping mid-keystroke blocks typing a number that starts below min, and a drag would save every frame
    internal static bool Paced(string label, ref int shown, int actual, int min, int max, Action<int> apply)
    {
        if (shown < 0)
            shown = actual;

        ImGui.SliderInt(label, ref shown, min, max);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            shown = Math.Clamp(shown, min, max);
            apply(shown);
            return true;
        }

        // Follows the setting again once released, so a value clamped elsewhere doesn't sit stale
        if (!ImGui.IsItemActive())
            shown = actual;

        return false;
    }

    // Arguments run left to right, so Apply sees the value ImGui just wrote
    internal static bool Toggle(string label, bool value, Action<bool> set) =>
        Apply(ImGui.Checkbox(label, ref value), value, set);

    internal static bool Edit(string label, string value, int maxLength, Action<string> set) =>
        Apply(ImGui.InputText(label, ref value, maxLength), value, set);

    // Live while dragged, but dirty only on release: a drag would save every frame
    internal static bool Slide(string label, int value, int min, int max, Action<int> set)
    {
        if (ImGui.SliderInt(label, ref value, min, max))
            set(value);

        return ImGui.IsItemDeactivatedAfterEdit();
    }

    internal static bool Choose(string label, IReadOnlyList<string> names, int current, Action<int> set)
    {
        using var combo = ImRaii.Combo(label, names[current]);
        if (!combo.Success)
            return false;

        var picked = current;
        for (var i = 0; i < names.Count; i++)
            if (ImGui.Selectable(names[i], i == current))
                picked = i;

        return Apply(picked != current, picked, set);
    }

    internal static bool ModuleRow(IModule module, ref bool on)
    {
        var unavailable = module.UnavailableReason;

        bool changed;
        using (ImRaii.Disabled(unavailable != null))
            changed = ImGui.Checkbox(module.Name, ref on);

        using (ImRaii.PushIndent())
        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.TextDisabled(module.Description);

            if (unavailable != null)
                ImGui.TextColored(WarningColour, $"Unavailable: {unavailable}");
        }

        return changed;
    }

    private static bool Apply<T>(bool changed, T value, Action<T> set)
    {
        if (changed)
            set(value);

        return changed;
    }
}
