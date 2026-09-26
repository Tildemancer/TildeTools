using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TildeTools.Modules.Wordsmith;

// See Hosting.HostInOwnFile
internal sealed class WordsmithModule() : HostedModule<global::Wordsmith.Wordsmith>("Wordsmith", "Wordsmith", () => global::Wordsmith.Hosting.HostInOwnFile(Config.Load, Config.Save, LogColours.Of))
{
    private static readonly HostedConfig<global::Wordsmith.Configuration> Config = new("Wordsmith", ShippedSetup.PathOf("Wordsmith.json"));

    public override string Id => "wordsmith";

    public override string Description => "A roleplay scratch pad. Its spellcheck and thesaurus come from the Spelling module.";

    protected override void AfterCreate() => global::Wordsmith.Hosting.ReleaseInstallerButtons();

    protected override Window? Settings => IsRunning ? global::Wordsmith.Hosting.Settings : null;

    protected override void DrawBody()
    {
        if (!IsRunning)
            return;

        ImGui.TextUnformatted("Wordsmith is running. The scratch pad's copy button sends through Emote Splitter instead.");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "Breaks and markers come from Emote Splitter's tab, so the pad previews exactly what's sent. " +
            "Wordsmith's own markers and OOC tags stay off.");
        ImGui.Spacing();

        // WordsmithUI is internal
        if (ImGui.Button("Scratch pad"))
            Svc.Commands.ProcessCommand("/scratchpad");

        ImGui.SameLine();

        if (ImGui.Button("Thesaurus"))
            Svc.Commands.ProcessCommand("/thesaurus");
    }
}
