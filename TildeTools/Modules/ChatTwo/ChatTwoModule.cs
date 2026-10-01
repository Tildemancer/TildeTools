using System;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using static TildeTools.Ui.Widgets;

namespace TildeTools.Modules.ChatTwo;

internal sealed class ChatTwoModule(ChatTwoSettings settings, Action save, Func<bool> splitterOn)
    : HostedModule<global::ChatTwo.Plugin>("Chat 2", "ChatTwo", HostInOwnFolder, new SetupFile("ChatTwo.json", ShippedSetup.PathOf("ChatTwo.json"), Restarts: true))
{
    private int _limit = -1;

    private static readonly HostedConfig<global::ChatTwo.Configuration> Config = new("Chat 2", ShippedSetup.PathOf("ChatTwo.json"));

    // Piggy-backs off of preexisting history so people can seamlessly switch between actual C2 and TT.
    private static void HostInOwnFolder()
    {
        var folder = new DirectoryInfo(ShippedSetup.PathOf("ChatTwo"));

        global::ChatTwo.Hosting.HostIn(folder, Config.Load, Config.Save);
        Svc.Log.Info($"Chat 2 keeps its data in {folder.FullName}.");
    }

    public override string Id => "chat-two";

    public override string Description => "Replaces the game's chat window. Lets you customize a lot more than the default chat window, like " +
                                          "its size in excess of the default chatbox limits and more than 3 tabs at any given time.";

    // Only raised while something splits what goes past 500, otherwise the game would drop it and clear the box.
    internal int InputByteCap => IsEnabled && splitterOn() ? settings.InputLimitBytes : 500;

    protected override Window? Settings => Instance?.SettingsWindow;

    // When C2 isn't running a tag goes as text, like normal.
    internal byte[] EncodeTags(byte[] bytes)
    {
        if (IsRunning)
            global::ChatTwo.Util.AutoTranslate.ReplaceWithPayload(ref bytes);

        return bytes;
    }

    protected override void AfterCreate()
    {
        Svc.Pi.UiBuilder.OpenConfigUi -= Instance!.SettingsWindow.Toggle;
        Instance.WindowSystem.RemoveWindow(Instance.SettingsWindow);

        // Its constructor only refills the tabs when Reason isn't Boot, and hosted, that's TT's Reason.
        // If the client is logged in this start is a restart, which standalone would refill.
        // Off the framework thread, it's TT's load, where no game reads are allowed, and standalone wouldn't refill there either.
        if (Svc.Pi.Reason is PluginLoadReason.Boot && Svc.Framework.IsInFrameworkUpdateThread && Svc.ClientState.IsLoggedIn)
            Instance.MessageManager.FilterAllTabsAsync();
    }

    // Cleans up what it left on the UiBuilder, since it'll outlive it.
    protected override void AfterDispose()
    {
        var ui = Svc.Pi.UiBuilder;
        (ui.DisableCutsceneUiHide, ui.DisableGposeUiHide, ui.DisableUserUiHide) = (false, false, false);

        // Fonts from failed constructions are out of reach without an instance.
        if (Instance?.FontManager is not { } fonts)
            return;

        foreach (var font in new[] { fonts.Axis, fonts.AxisItalic, fonts.FontAwesome, fonts.RegularFont, fonts.ItalicFont })
            font?.Dispose();
    }

    protected override void DrawBody()
    {
        if (IsRunning)
            ImGui.TextUnformatted($"Chat 2 is running and takes up to {InputByteCap} bytes. Long messages use Emote Splitter's settings.");

        ImGui.Separator();

        if (Paced("Character limit", ref _limit, settings.InputLimitBytes,
                ChatTwoSettings.MinInputLimit, ChatTwoSettings.MaxInputLimit, settings, static (s, v) => s.InputLimitBytes = v))
            save();

        ImGui.TextDisabled("How long Chat 2's text input can be. Technically in bytes, but for our purposes you can think of these as characters.");
    }
}
