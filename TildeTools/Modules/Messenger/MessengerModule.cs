using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TildeTools.Modules.Messenger;

// HostInOwnFolder runs before construction, or XIM will write its config into TT instead.
// Its settings are EzConfig's file, in the standalone XIM's folder.
internal sealed class MessengerModule() : HostedModule<global::Messenger.Messenger>(
    "XIV Instant Messenger", "Messenger", global::Messenger.Hosting.HostInOwnFolder,
    new SetupFile("Messenger.json", ShippedSetup.PathOf("Messenger", "DefaultConfig.json"), Restarts: false))
{
    public override string Id => "messenger";

    public override string Description =>
        "Each person's tells in their own window, like a messaging app. Long messages are split instead of " +
        "refused. It's basically WoW Instant Messenger, but in FFXIV.";

    public override string? UnavailableReason => base.UnavailableReason
        ?? (global::Messenger.Hosting.IsHosted && !IsEnabled
            ? "It can't restart until TildeTools is reloaded or the game restarts."
            : null);

    protected override Window? Settings => IsRunning ? global::Messenger.Hosting.Settings : null;

    protected override void DrawBody()
    {
        if (IsRunning)
            ImGui.TextUnformatted(
                "Instant Messenger is running. Long messages, in tells or channel windows, use Emote Splitter's " +
                "settings instead of being refused.");
    }
}
