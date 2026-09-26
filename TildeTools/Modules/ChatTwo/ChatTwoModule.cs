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

    // Its own folder, so an existing install's history still turns up
    private static void HostInOwnFolder()
    {
        var folder = new DirectoryInfo(ShippedSetup.PathOf("ChatTwo"));

        global::ChatTwo.Hosting.HostIn(folder, Config.Load, Config.Save);
        Svc.Log.Info($"Chat 2 keeps its data in {folder.FullName}.");
    }

    public override string Id => "chat-two";

    public override string Description => "Replaces the game's chat window, with tabs you set up. Its input takes long messages for Emote Splitter.";

    // Only while something splits what goes past 500, or the game drops it and the box clears
    internal int InputByteCap => IsEnabled && splitterOn() ? settings.InputLimitBytes : 500;

    protected override Window? Settings => Instance?.SettingsWindow;

    // Not running, a tag goes as text, as the game's own box sends one
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

        // Its constructor refills the tabs only when Reason isn't Boot, and hosted that's TildeTools' Reason
        // Logged in, this start is a restart, which standalone would refill
        // Off the framework thread it's TildeTools' own load: no game read there, and standalone wouldn't refill either
        if (Svc.Pi.Reason is PluginLoadReason.Boot && Svc.Framework.IsInFrameworkUpdateThread && Svc.ClientState.IsLoggedIn)
            Instance.MessageManager.FilterAllTabsAsync();
    }

    // What it left on our UiBuilder, which outlives it
    protected override void AfterDispose()
    {
        var ui = Svc.Pi.UiBuilder;
        (ui.DisableCutsceneUiHide, ui.DisableGposeUiHide, ui.DisableUserUiHide) = (false, false, false);

        var fonts = Instance!.FontManager;
        foreach (var font in new[] { fonts.Axis, fonts.AxisItalic, fonts.FontAwesome, fonts.RegularFont, fonts.ItalicFont })
            font?.Dispose();
    }

    protected override void DrawBody()
    {
        if (IsRunning)
            ImGui.TextUnformatted($"Chat 2 is running and takes up to {InputByteCap} bytes. Long messages use Emote Splitter's settings.");

        ImGui.Separator();

        if (Paced("Chat limit (bytes)", ref _limit, settings.InputLimitBytes,
                ChatTwoSettings.MinInputLimit, ChatTwoSettings.MaxInputLimit, v => settings.InputLimitBytes = v))
            save();

        ImGui.TextDisabled("How long Chat 2's text input can be.");
    }
}
