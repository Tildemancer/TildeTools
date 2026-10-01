using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TildeTools.Modules.Wordsmith;

// See Hosting.HostInOwnFile
internal sealed class WordsmithModule() : HostedModule<global::Wordsmith.Wordsmith>("Wordsmith", "Wordsmith", () => global::Wordsmith.Hosting.HostInOwnFile(Config.Load, Config.Save, LogColors.Of))
{
    private static readonly HostedConfig<global::Wordsmith.Configuration> Config = new("Wordsmith", ShippedSetup.PathOf("Wordsmith.json"));

    public override string Id => "wordsmith";

    public override string Description => "Ye olde classic roleplay scratch pad. Its spellcheck and thesaurus come from the Spelling module " +
                                          "now; most of it has been gutted and replaced with our functionality, but the workspace is nice and " +
                                          "well known.";

    protected override void AfterCreate() => global::Wordsmith.Hosting.ReleaseInstallerButtons();

    protected override Window? Settings => IsRunning ? global::Wordsmith.Hosting.Settings : null;

    protected override void DrawBody()
    {
        if (!IsRunning)
            return;

        ImGui.TextUnformatted("Wordsmith is running. The scratch pad's copy button sends through Emote Splitter instead, and its " +
                              "spellcheck/define/thesaurus functions use the Spelling module. This replaces almost all its " +
                              "functionality.");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "Breaks and markers come from Emote Splitter's tab, so the pad previews exactly what's sent.");
        ImGui.Spacing();

        // WordsmithUI is internal
        if (ImGui.Button("Scratch pad"))
            Svc.Commands.ProcessCommand("/scratchpad");

        ImGui.SameLine();

        if (ImGui.Button("Thesaurus"))
            Svc.Commands.ProcessCommand("/thesaurus");
    }
}
