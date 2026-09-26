using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TildeTools.Modules;

namespace TildeTools.Ui;

internal sealed class MainWindow : Window
{
    private readonly ModuleManager _modules;
    private readonly SetupWindow _setup;

    internal MainWindow(ModuleManager modules, SetupWindow setup)
        : base("TildeTools")
    {
        _modules = modules;
        _setup = setup;

        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    internal void Open() => IsOpen = true;

    private IModule? _select;

    // Every frame, open or not: a hosted plugin's own settings button or command opens its tab here instead
    internal void Redirect()
    {
        foreach (var module in _modules.Modules)
        {
            if (!module.TakeSettingsRequest())
                continue;

            (_select, IsOpen) = (module, true);
            BringToFront();
        }
    }

    public override void OnOpen()
    {
        foreach (var module in _modules.Modules)
            module.SettingsOpened();
    }

    public override void OnClose()
    {
        foreach (var module in _modules.Modules)
            module.SettingsClosed();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##tildetools-tabs");
        if (!tabs.Success)
            return;

        foreach (var module in _modules.Modules)
        {
            if (!module.IsEnabled)
                continue;

            using var tab = ImRaii.TabItem(module.Name, module == _select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            if (!tab.Success)
                continue;

            using var id = ImRaii.PushId(module.Id);
            module.DrawTab();

            if (module.TakeCloseRequest())
                IsOpen = false;
        }

        _select = null;

        using (var modules = ImRaii.TabItem("Modules"))
            if (modules.Success)
                DrawModuleList();

        using (var credits = ImRaii.TabItem("Credits"))
            if (credits.Success)
                Credits.Draw();
    }

    private void DrawModuleList()
    {
        ImGui.TextWrapped("Switched off, a module leaves the game as if it never loaded.");

        if (ImGui.Button("First-time setup"))
            _setup.Open();

        ImGui.Separator();

        foreach (var module in _modules.Modules)
        {
            using var id = ImRaii.PushId(module.Id);

            var enabled = module.IsEnabled;

            // Next tick, not mid-draw: a plugin stopped here still gets this frame's Draw after its Dispose
            if (Widgets.ModuleRow(module, ref enabled))
                Svc.Framework.RunOnTick(() => _modules.SetEnabled(module, enabled));

            ImGui.Spacing();
        }
    }
}
