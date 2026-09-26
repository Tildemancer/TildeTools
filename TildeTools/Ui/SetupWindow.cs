using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TildeTools.Modules;

namespace TildeTools.Ui;

// Opens once for everyone, new install or not
internal sealed class SetupWindow : Window
{
    private readonly ModuleManager _modules;
    private readonly Configuration _config;
    private readonly Action _save;

    private (bool Run, bool Shipped, bool Theirs)[] _choices = [];

    internal SetupWindow(ModuleManager modules, Configuration config, Action save)
        : base("TildeTools setup")
    {
        (_modules, _config, _save) = (modules, config, save);

        Size = new Vector2(520, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    internal void Open()
    {
        _choices = [.. _modules.Modules.Select(module =>
        {
            var theirs = module.Setup?.HasSettings ?? false;
            return (_config.IsModuleEnabled(module.Id), !theirs && module.Setup?.Declined != true, theirs);
        })];

        IsOpen = true;
    }

    public override void OnClose()
    {
        _config.SetupSeen = true;
        _save();
    }

    public override void Draw()
    {
        ImGui.TextWrapped("TildeTools bundles three plugins by other people alongside its own Emote Splitter and Spelling. Pick which to run.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "I've included my settings as default options to help get you started. Otherwise, TildeTools " +
            "will try to read any existing settings you may have, and revert to the plugin defaults if " +
            "not. You can revisit this choice at any time.");

        for (var i = 0; i < _choices.Length; i++)
        {
            var module = _modules.Modules[i];
            ref var choice = ref _choices[i];

            using var id = ImRaii.PushId(module.Id);
            ImGui.Separator();
            Widgets.ModuleRow(module, ref choice.Run);

            if (choice.Run && module.Setup is { } setup)
            {
                using var indent = ImRaii.PushIndent();
                DrawSettings(module, setup, choice.Theirs, ref choice.Shipped);
            }

            ImGui.Spacing();
        }

        ImGui.Separator();

        using (ImRaii.TextWrapPos(0f))
            ImGui.TextDisabled("Switch modules on and off in /tt under Modules, where each one's settings have their own tab. /tt setup opens this again, and the Credits tab says who made each one.");

        if (ImGui.Button("Done"))
        {
            Apply();
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button("Skip"))
            IsOpen = false;
    }

    private static void DrawSettings(IModule module, SetupFile setup, bool theirs, ref bool shipped)
    {
        if (ImGui.RadioButton("Tildemancer's defaults", shipped))
            shipped = true;

        ImGui.SameLine();

        if (ImGui.RadioButton(theirs ? "Keep yours" : $"{module.Name}'s own defaults", !shipped))
            shipped = false;

        if (!shipped)
            return;

        using var wrap = ImRaii.TextWrapPos(0f);

        if (theirs)
            ImGui.TextDisabled("Yours are kept as a .bak file.");

        if (module.IsEnabled && !setup.Restarts)
            ImGui.TextDisabled($"{module.Name} can't restart, so they take over the next time TildeTools loads.");
    }

    // Queued now, so a module switched on below takes it as it starts
    private void Apply()
    {
        for (var i = 0; i < _choices.Length; i++)
        {
            var module = _modules.Modules[i];
            var (run, shipped, _) = _choices[i];

            if (run && module.Setup is { } setup)
            {
                if (shipped)
                {
                    ShippedSetup.Declined.Remove(setup.Name);
                    module.UseShippedSetup();
                }
                else
                {
                    ShippedSetup.Declined.Add(setup.Name);
                }
            }

            // Next tick, see MainWindow.DrawModuleList
            if (run != _config.IsModuleEnabled(module.Id))
                Svc.Framework.RunOnTick(() => _modules.SetEnabled(module, run));
        }
    }
}
